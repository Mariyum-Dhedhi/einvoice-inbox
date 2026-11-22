package erp

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"net/url"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/Mariyum-Dhedhi/einvoice-inbox/sync/erp-sync/internal/retry"
)

const secret = "s3cret-for-tests"

// fakeERP is a token endpoint plus data endpoints whose pages the test writes by hand.
type fakeERP struct {
	srv           *httptest.Server
	tokenRequests atomic.Int32
	tokenLifetime int
	tokenForms    []url.Values
	data          http.HandlerFunc // the data handler under test; sees only requests with a valid token
	mu            sync.Mutex
	tokens        map[string]bool
	dataRequests  []*http.Request
}

func newFakeERP(t *testing.T, data http.HandlerFunc) *fakeERP {
	t.Helper()
	f := &fakeERP{tokenLifetime: 3600, data: data, tokens: map[string]bool{}}
	f.srv = httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path == "/oauth/token" {
			n := f.tokenRequests.Add(1)
			_ = r.ParseForm()
			token := fmt.Sprintf("token-%d", n)
			f.mu.Lock()
			f.tokenForms = append(f.tokenForms, r.PostForm)
			f.tokens[token] = true
			f.mu.Unlock()
			if r.PostForm.Get("client_secret") != secret {
				w.WriteHeader(401)
				_, _ = io.WriteString(w, `{"error":"invalid_client"}`)
				return
			}
			w.Header().Set("Content-Type", "application/json")
			_, _ = fmt.Fprintf(w, `{"access_token":%q,"token_type":"Bearer","expires_in":%d}`, token, f.tokenLifetime)
			return
		}
		f.mu.Lock()
		f.dataRequests = append(f.dataRequests, r)
		valid := f.tokens[strings.TrimPrefix(r.Header.Get("Authorization"), "Bearer ")]
		f.mu.Unlock()
		if !valid {
			w.WriteHeader(401)
			return
		}
		f.data(w, r)
	}))
	t.Cleanup(f.srv.Close)
	return f
}

func (f *fakeERP) expireAll() {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.tokens = map[string]bool{}
}

func (f *fakeERP) dataCalls() int {
	f.mu.Lock()
	defer f.mu.Unlock()
	return len(f.dataRequests)
}

func fastRetry(waits *[]time.Duration) retry.Policy {
	p := retry.Default()
	p.Sleep = func(ctx context.Context, d time.Duration) error {
		if waits != nil {
			*waits = append(*waits, d)
		}
		return ctx.Err()
	}
	return p
}

func client(t *testing.T, f *fakeERP, configure ...func(*Config)) *Client {
	t.Helper()
	cfg := Config{
		BaseURL:      f.srv.URL + "/",
		TokenURL:     f.srv.URL + "/oauth/token",
		ClientID:     "erp-sync",
		ClientSecret: secret,
		Scope:        "api://mock-erp/.default",
		PageSize:     2,
		HTTP:         f.srv.Client(),
		Retry:        fastRetry(nil),
	}
	for _, c := range configure {
		c(&cfg)
	}
	c, err := New(cfg)
	if err != nil {
		t.Fatal(err)
	}
	return c
}

func vendorJSON(id string, minute int) string {
	return fmt.Sprintf(`{"id":%q,"vendorAccountNumber":"K-%s","name":"Vendor %s","vatNumber":null,"status":"Active","modifiedDateTime":"2026-11-21T09:%02d:00Z"}`, id, id, id, minute)
}

// pages answers the first request with pages[0], and each nextLink ("?page=n") with pages[n].
func pages(f **fakeERP, pages ...[]string) http.HandlerFunc {
	return func(w http.ResponseWriter, r *http.Request) {
		n := 0
		if p := r.URL.Query().Get("page"); p != "" {
			_, _ = fmt.Sscan(p, &n)
		}
		body := `{"value":[` + strings.Join(pages[n], ",") + `]`
		if n+1 < len(pages) {
			body += fmt.Sprintf(`,"@odata.nextLink":%q`, fmt.Sprintf("%s/data/Vendors?page=%d", (*f).srv.URL, n+1))
		}
		w.Header().Set("Content-Type", "application/json")
		_, _ = io.WriteString(w, body+"}")
	}
}

func readAll(t *testing.T, c *Client, since time.Time) ([][]Vendor, error) {
	t.Helper()
	var got [][]Vendor
	err := Changes(context.Background(), c, Vendors, since, func(page []Vendor) error {
		got = append(got, page)
		return nil
	})
	return got, err
}

func ids(pages [][]Vendor) string {
	var out []string
	for _, p := range pages {
		var page []string
		for _, v := range p {
			page = append(page, v.ID)
		}
		out = append(out, strings.Join(page, ","))
	}
	return strings.Join(out, "|")
}

// ---- Paging ----------------------------------------------------------------------------------------------------------

func TestItFollowsNextLinkUntilThereIsNone(t *testing.T) {
	var f *fakeERP
	f = newFakeERP(t, pages(&f, []string{vendorJSON("V1", 1), vendorJSON("V2", 2)}, []string{vendorJSON("V3", 3), vendorJSON("V4", 4)}, []string{vendorJSON("V5", 5)}))

	got, err := readAll(t, client(t, f), time.Time{})

	if err != nil || ids(got) != "V1,V2|V3,V4|V5" {
		t.Fatalf("got %q, %v", ids(got), err)
	}
}

func TestAnEmptyFirstPageIsFine(t *testing.T) {
	var f *fakeERP
	f = newFakeERP(t, pages(&f, []string{}))

	got, err := readAll(t, client(t, f), time.Time{})

	if err != nil || len(got) > 1 || (len(got) == 1 && len(got[0]) != 0) {
		t.Fatalf("got %v, %v", got, err)
	}
}

func TestAFullLastPageIsFineWithOrWithoutALinkToAnEmptyPage(t *testing.T) {
	full := []string{vendorJSON("V1", 1), vendorJSON("V2", 2)} // PageSize is 2
	for name, set := range map[string][][]string{
		"no link":            {full},
		"link to empty page": {full, {}},
	} {
		var f *fakeERP
		f = newFakeERP(t, pages(&f, set...))

		got, err := readAll(t, client(t, f), time.Time{})

		if err != nil || !strings.HasPrefix(ids(got), "V1,V2") || strings.Count(ids(got), "V") != 2 {
			t.Fatalf("%s: got %q, %v", name, ids(got), err)
		}
	}
}

func TestItAsksForChangesSinceTheTimeInChangeOrder(t *testing.T) {
	var f *fakeERP
	f = newFakeERP(t, pages(&f, []string{}))
	since := time.Date(2026, 11, 21, 8, 55, 0, 0, time.FixedZone("CET", 3600))

	if _, err := readAll(t, client(t, f), since); err != nil {
		t.Fatal(err)
	}

	q := f.dataRequests[0].URL.Query()
	if f.dataRequests[0].URL.Path != "/data/Vendors" ||
		q.Get("$filter") != "modifiedDateTime ge 2026-11-21T07:55:00Z" ||
		q.Get("$orderby") != "modifiedDateTime,id" ||
		q.Get("$top") != "2" {
		t.Fatalf("asked %s", f.dataRequests[0].URL)
	}
}

func TestWithoutATimeItAsksForEverything(t *testing.T) {
	var f *fakeERP
	f = newFakeERP(t, pages(&f, []string{}))

	if _, err := readAll(t, client(t, f), time.Time{}); err != nil {
		t.Fatal(err)
	}

	if f.dataRequests[0].URL.Query().Has("$filter") {
		t.Fatalf("asked %s", f.dataRequests[0].URL)
	}
}

func TestANextLinkToAnotherServerIsRefusedAndGetsNoToken(t *testing.T) {
	var elsewhere atomic.Int32
	evil := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		elsewhere.Add(1)
		w.WriteHeader(200)
	}))
	defer evil.Close()
	f := newFakeERP(t, func(w http.ResponseWriter, r *http.Request) {
		_, _ = fmt.Fprintf(w, `{"value":[%s],"@odata.nextLink":%q}`, vendorJSON("V1", 1), evil.URL+"/data/Vendors?page=1")
	})

	_, err := readAll(t, client(t, f), time.Time{})

	if err == nil || elsewhere.Load() != 0 {
		t.Fatalf("got %v; the other server was called %d times", err, elsewhere.Load())
	}
}

func TestAnErrorFromThePageCallbackStopsReading(t *testing.T) {
	var f *fakeERP
	f = newFakeERP(t, pages(&f, []string{vendorJSON("V1", 1)}, []string{vendorJSON("V2", 2)}))
	stop := errors.New("stop")

	err := Changes(context.Background(), client(t, f), Vendors, time.Time{}, func([]Vendor) error { return stop })

	if !errors.Is(err, stop) || f.dataCalls() != 1 {
		t.Fatalf("got %v after %d data calls", err, f.dataCalls())
	}
}

// Well-formed JSON that is not the contract will not get better by asking again: an error at once.
func TestAPageThatIsNotTheContractIsAnErrorAndNotReadAgain(t *testing.T) {
	for name, body := range map[string]string{
		"unknown field":  `{"value":[{"id":"V1","vendorAccountNumber":"K-1","name":"A","vatNumber":null,"status":"Active","modifiedDateTime":"2026-11-21T09:00:00Z","colour":"red"}]}`,
		"missing field":  `{"value":[{"id":"V1","name":"A","vatNumber":null,"status":"Active","modifiedDateTime":"2026-11-21T09:00:00Z"}]}`,
		"no value":       `{"items":[]}`,
		"no change time": `{"value":[{"id":"V1","vendorAccountNumber":"K-1","name":"A","vatNumber":null,"status":"Active","modifiedDateTime":null}]}`,
	} {
		f := newFakeERP(t, func(w http.ResponseWriter, r *http.Request) { _, _ = io.WriteString(w, body) })

		_, err := readAll(t, client(t, f), time.Time{})

		if err == nil || f.dataCalls() != 1 {
			t.Errorf("%s: got %v after %d data calls, want an error after 1", name, err, f.dataCalls())
		}
	}
}

// A page that broke on the way (cut off, not JSON at all) is a temporary failure: a GET may be asked again.
func TestABrokenPageIsReadAgain(t *testing.T) {
	var calls atomic.Int32
	var waits []time.Duration
	f := newFakeERP(t, func(w http.ResponseWriter, r *http.Request) {
		if calls.Add(1) <= 2 {
			_, _ = io.WriteString(w, `{"value":[`)
			return
		}
		_, _ = io.WriteString(w, `{"value":[`+vendorJSON("V1", 1)+`]}`)
	})

	got, err := readAll(t, client(t, f, func(cfg *Config) { cfg.Retry = fastRetry(&waits) }), time.Time{})

	if err != nil || ids(got) != "V1" || f.dataCalls() != 3 || len(waits) != 2 {
		t.Fatalf("got %q, %v after %d data calls and waits %v", ids(got), err, f.dataCalls(), waits)
	}
}

func TestAPageThatStaysBrokenGivesUpAfterTheTries(t *testing.T) {
	for name, body := range map[string]string{"cut off": `{"value":[`, "not json": `<html>busy</html>`, "empty": ``} {
		f := newFakeERP(t, func(w http.ResponseWriter, r *http.Request) { _, _ = io.WriteString(w, body) })

		_, err := readAll(t, client(t, f), time.Time{})

		if err == nil || f.dataCalls() != 5 {
			t.Errorf("%s: got %v after %d data calls, want an error after 5", name, err, f.dataCalls())
		}
	}
}

func TestABrokenSingleRecordIsReadAgain(t *testing.T) {
	var calls atomic.Int32
	f := newFakeERP(t, func(w http.ResponseWriter, r *http.Request) {
		if calls.Add(1) == 1 {
			_, _ = io.WriteString(w, `{"id":"V1",`)
			return
		}
		_, _ = io.WriteString(w, vendorJSON("V1", 1))
	})

	v, found, err := One(context.Background(), client(t, f), Vendors, "V1")

	if err != nil || !found || v.ID != "V1" || f.dataCalls() != 2 {
		t.Fatalf("got %+v, %v, %v after %d data calls", v, found, err, f.dataCalls())
	}
}

func TestAmountsKeepTheirExactText(t *testing.T) {
	order := `{"id":"PO-1","purchaseOrderNumber":"PO-26-1","vendorId":"V1","orderDate":"2026-11-02","currencyCode":"EUR","status":"Open",` +
		`"modifiedDateTime":"2026-11-21T09:00:00Z","lines":[{"lineNumber":1,"itemNumber":"A","description":"B",` +
		`"quantity":0.3333,"unitPrice":12345678901234.567891,"taxRatePercent":19.00}]}`
	f := newFakeERP(t, func(w http.ResponseWriter, r *http.Request) { _, _ = io.WriteString(w, `{"value":[`+order+`]}`) })

	var got []PurchaseOrder
	err := Changes(context.Background(), client(t, f), PurchaseOrders, time.Time{}, func(p []PurchaseOrder) error {
		got = append(got, p...)
		return nil
	})

	if err != nil || len(got) != 1 {
		t.Fatalf("got %v, %v", got, err)
	}
	line := got[0].Lines[0]
	if line.Quantity.String() != "0.3333" || line.UnitPrice.String() != "12345678901234.567891" || line.TaxRatePercent.String() != "19.00" {
		t.Fatalf("amounts changed on the way: %+v", line)
	}
}

// ---- Tokens ----------------------------------------------------------------------------------------------------------

func TestOneTokenIsFetchedAndReusedForEveryPage(t *testing.T) {
	var f *fakeERP
	f = newFakeERP(t, pages(&f, []string{vendorJSON("V1", 1)}, []string{vendorJSON("V2", 2)}, []string{vendorJSON("V3", 3)}))

	if _, err := readAll(t, client(t, f), time.Time{}); err != nil {
		t.Fatal(err)
	}

	form := f.tokenForms[0]
	if f.tokenRequests.Load() != 1 || form.Get("grant_type") != "client_credentials" || form.Get("client_id") != "erp-sync" ||
		form.Get("client_secret") != secret || form.Get("scope") != "api://mock-erp/.default" {
		t.Fatalf("%d token requests, first form %v", f.tokenRequests.Load(), form)
	}
	if got := f.dataRequests[0].Header.Get("Authorization"); got != "Bearer token-1" {
		t.Fatalf("Authorization %q", got)
	}
}

func TestATokenWithinAMinuteOfExpiryIsRenewedFirst(t *testing.T) {
	var f *fakeERP
	f = newFakeERP(t, pages(&f, []string{}))
	f.tokenLifetime = 120
	now := time.Date(2026, 11, 22, 9, 0, 0, 0, time.UTC)
	c := client(t, f, func(cfg *Config) { cfg.Now = func() time.Time { return now } })

	_, _ = readAll(t, c, time.Time{})
	now = now.Add(59 * time.Second) // 61 s left: still good
	_, _ = readAll(t, c, time.Time{})
	if f.tokenRequests.Load() != 1 {
		t.Fatalf("%d token requests with 61 s left, want 1", f.tokenRequests.Load())
	}
	now = now.Add(2 * time.Second) // 59 s left: renew before the call
	_, _ = readAll(t, c, time.Time{})

	if f.tokenRequests.Load() != 2 || f.dataCalls() != 3 {
		t.Fatalf("%d token requests and %d data calls, want 2 and 3 (no 401 first)", f.tokenRequests.Load(), f.dataCalls())
	}
}

func TestTwentyCallsAtOnceShareOneTokenRequest(t *testing.T) {
	var f *fakeERP
	f = newFakeERP(t, pages(&f, []string{}))
	c := client(t, f)
	var wg sync.WaitGroup
	for range 20 {
		wg.Add(1)
		go func() {
			defer wg.Done()
			_, _ = readAll(t, c, time.Time{})
		}()
	}
	wg.Wait()

	if f.tokenRequests.Load() != 1 {
		t.Fatalf("%d token requests, want 1", f.tokenRequests.Load())
	}
}

func TestA401RenewsTheTokenOnceAndRepeatsTheCall(t *testing.T) {
	var f *fakeERP
	f = newFakeERP(t, pages(&f, []string{vendorJSON("V1", 1)}))
	c := client(t, f)
	_, _ = readAll(t, c, time.Time{})
	f.expireAll()

	got, err := readAll(t, c, time.Time{})

	if err != nil || ids(got) != "V1" || f.tokenRequests.Load() != 2 || f.dataCalls() != 3 {
		t.Fatalf("got %q, %v; %d token requests, %d data calls", ids(got), err, f.tokenRequests.Load(), f.dataCalls())
	}
}

func TestA401AgainAfterRenewingIsRefusedCredentialsNotALoop(t *testing.T) {
	f := newFakeERP(t, func(w http.ResponseWriter, r *http.Request) { w.WriteHeader(401) })

	_, err := readAll(t, client(t, f), time.Time{})

	if !errors.Is(err, ErrCredentialsRefused) || f.dataCalls() != 2 {
		t.Fatalf("got %v after %d data calls, want ErrCredentialsRefused after 2", err, f.dataCalls())
	}
}

func TestAWrongSecretIsRefusedCredentialsAndNeverRepeated(t *testing.T) {
	var f *fakeERP
	f = newFakeERP(t, pages(&f, []string{}))

	_, err := readAll(t, client(t, f, func(cfg *Config) { cfg.ClientSecret = "wrong-secret" }), time.Time{})

	if !errors.Is(err, ErrCredentialsRefused) || strings.Contains(err.Error(), "wrong-secret") {
		t.Fatalf("got %v", err)
	}
}

func TestATokenEndpointThatIsDownIsRetried(t *testing.T) {
	var down atomic.Int32
	var waits []time.Duration
	var f *fakeERP
	f = newFakeERP(t, pages(&f, []string{}))
	inner := f.srv.Config.Handler
	f.srv.Config.Handler = http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path == "/oauth/token" && down.Add(1) <= 2 {
			w.WriteHeader(503)
			return
		}
		inner.ServeHTTP(w, r)
	})

	_, err := readAll(t, client(t, f, func(cfg *Config) { cfg.Retry = fastRetry(&waits) }), time.Time{})

	if err != nil || len(waits) != 2 {
		t.Fatalf("got %v with waits %v", err, waits)
	}
}

// ---- Temporary trouble and single records ----------------------------------------------------------------------------

func TestAThrottledPageIsAskedAgainAfterRetryAfter(t *testing.T) {
	var calls atomic.Int32
	var waits []time.Duration
	f := newFakeERP(t, func(w http.ResponseWriter, r *http.Request) {
		if calls.Add(1) == 1 {
			w.Header().Set("Retry-After", "3")
			w.WriteHeader(429)
			return
		}
		_, _ = io.WriteString(w, `{"value":[`+vendorJSON("V1", 1)+`]}`)
	})

	got, err := readAll(t, client(t, f, func(cfg *Config) { cfg.Retry = fastRetry(&waits) }), time.Time{})

	if err != nil || ids(got) != "V1" || len(waits) != 1 || waits[0] != 3*time.Second {
		t.Fatalf("got %q, %v, waits %v", ids(got), err, waits)
	}
}

func TestOneRecordByIdOrNotFound(t *testing.T) {
	f := newFakeERP(t, func(w http.ResponseWriter, r *http.Request) {
		switch r.URL.EscapedPath() {
		case "/data/Vendors('V1')":
			_, _ = io.WriteString(w, vendorJSON("V1", 1))
		default:
			w.WriteHeader(404)
		}
	})
	c := client(t, f)

	v, found, err := One(context.Background(), c, Vendors, "V1")
	_, missing, errMissing := One(context.Background(), c, Vendors, "V2")

	if err != nil || !found || v.ID != "V1" || v.AccountNumber != "K-V1" {
		t.Fatalf("got %+v, %v, %v", v, found, err)
	}
	if errMissing != nil || missing {
		t.Fatalf("unknown id: found %v, %v", missing, errMissing)
	}
}

func TestAnIdIsEscapedInThePath(t *testing.T) {
	var path, raw string
	f := newFakeERP(t, func(w http.ResponseWriter, r *http.Request) {
		path, raw = r.URL.Path, r.URL.EscapedPath()
		w.WriteHeader(404)
	})

	_, _, _ = One(context.Background(), client(t, f), Vendors, "V'1/x")

	// OData doubles a quote inside a key; the slash stays part of the key, not a new path segment.
	if path != "/data/Vendors('V''1/x')" || !strings.Contains(raw, "%2F") {
		t.Fatalf("path %q (raw %q)", path, raw)
	}
}

func TestRecordsDecodeIntoTheirFields(t *testing.T) {
	receipt := `{"id":"PR-1","productReceiptNumber":"GR-26-1","purchaseOrderId":"PO-1","receivedDate":"2026-11-03",` +
		`"modifiedDateTime":"2026-11-21T09:00:00.1234567Z","lines":[{"purchaseOrderLineNumber":2,"quantity":4}]}`
	var r ProductReceipt
	if err := decodeStrict([]byte(receipt), &r); err != nil {
		t.Fatal(err)
	}
	b, _ := json.Marshal(r.Lines)

	if r.ID != "PR-1" || r.Number != "GR-26-1" || r.PurchaseOrderID != "PO-1" || r.ReceivedDate != "2026-11-03" ||
		!r.Changed().Equal(time.Date(2026, 11, 21, 9, 0, 0, 123456700, time.UTC)) || string(b) != `[{"purchaseOrderLineNumber":2,"quantity":4}]` {
		t.Fatalf("decoded %+v", r)
	}
	if r.Key() != "PR-1" {
		t.Fatalf("key %q", r.Key())
	}
}

func TestTheSecretIsNeverInTheConfigsText(t *testing.T) {
	cfg := Config{ClientID: "erp-sync", ClientSecret: secret}

	for _, text := range []string{fmt.Sprint(cfg), fmt.Sprintf("%+v", cfg), fmt.Sprintf("%#v", cfg)} {
		if strings.Contains(text, secret) {
			t.Fatalf("secret in %q", text)
		}
	}
}
