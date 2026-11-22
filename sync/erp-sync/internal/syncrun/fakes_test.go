package syncrun

import (
	"bytes"
	"context"
	"encoding/json"
	"fmt"
	"io"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"sort"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/Mariyum-Dhedhi/einvoice-inbox/sync/erp-sync/internal/app"
	"github.com/Mariyum-Dhedhi/einvoice-inbox/sync/erp-sync/internal/erp"
	"github.com/Mariyum-Dhedhi/einvoice-inbox/sync/erp-sync/internal/retry"
)

const (
	erpSecret = "erp-secret-for-tests"
	appKey    = "app-key-for-tests"
	token     = "erp-token-for-tests"
)

var base = time.Date(2026, 11, 21, 9, 0, 0, 0, time.UTC)

func at(minute int) time.Time { return base.Add(time.Duration(minute) * time.Minute) }

// ---- Fake ERP --------------------------------------------------------------------------------------------------------

// fakeERP serves three sets in change order, two records a page, honouring "modifiedDateTime ge".
type fakeERP struct {
	srv       *httptest.Server
	mu        sync.Mutex
	sets      map[string][]any // "Vendors", "PurchaseOrders", "ProductReceipts"
	filters   map[string][]string
	failPage  map[string]int // set → page number (1-based) that always answers 500
	byIDCalls []string
}

func newFakeERP(t *testing.T) *fakeERP {
	t.Helper()
	f := &fakeERP{sets: map[string][]any{}, filters: map[string][]string{}, failPage: map[string]int{}}
	f.srv = httptest.NewServer(http.HandlerFunc(f.serve))
	t.Cleanup(f.srv.Close)
	return f
}

func (f *fakeERP) add(set string, records ...any) {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.sets[set] = append(f.sets[set], records...)
}

func changed(record any) time.Time {
	switch r := record.(type) {
	case erp.Vendor:
		return r.Modified
	case erp.PurchaseOrder:
		return r.Modified
	case erp.ProductReceipt:
		return r.Modified
	}
	panic("unknown record")
}

func key(record any) string {
	switch r := record.(type) {
	case erp.Vendor:
		return r.ID
	case erp.PurchaseOrder:
		return r.ID
	case erp.ProductReceipt:
		return r.ID
	}
	panic("unknown record")
}

func (f *fakeERP) serve(w http.ResponseWriter, r *http.Request) {
	if r.URL.Path == "/oauth/token" {
		_ = r.ParseForm()
		if r.PostForm.Get("client_secret") != erpSecret {
			w.WriteHeader(401)
			return
		}
		_, _ = fmt.Fprintf(w, `{"access_token":%q,"token_type":"Bearer","expires_in":3600}`, token)
		return
	}
	if r.Header.Get("Authorization") != "Bearer "+token {
		w.WriteHeader(401)
		return
	}
	path := strings.TrimPrefix(r.URL.Path, "/data/")
	f.mu.Lock()
	defer f.mu.Unlock()
	if i := strings.Index(path, "('"); i > 0 {
		set, id := path[:i], strings.TrimSuffix(path[i+2:], "')")
		f.byIDCalls = append(f.byIDCalls, set+":"+id)
		for _, rec := range f.sets[set] {
			if key(rec) == id {
				_ = json.NewEncoder(w).Encode(rec)
				return
			}
		}
		w.WriteHeader(404)
		return
	}
	set := path
	q := r.URL.Query()
	page := 1
	if p := q.Get("page"); p != "" {
		_, _ = fmt.Sscan(p, &page)
	} else {
		f.filters[set] = append(f.filters[set], q.Get("$filter"))
	}
	if f.failPage[set] == page {
		w.WriteHeader(500)
		return
	}
	records := append([]any(nil), f.sets[set]...)
	sort.SliceStable(records, func(i, j int) bool {
		if !changed(records[i]).Equal(changed(records[j])) {
			return changed(records[i]).Before(changed(records[j]))
		}
		return key(records[i]) < key(records[j])
	})
	if filter := q.Get("$filter"); filter != "" {
		since, err := time.Parse(time.RFC3339Nano, strings.TrimPrefix(filter, "modifiedDateTime ge "))
		if err != nil {
			w.WriteHeader(400)
			return
		}
		var kept []any
		for _, rec := range records {
			if !changed(rec).Before(since) {
				kept = append(kept, rec)
			}
		}
		records = kept
	}
	const size = 2
	from := (page - 1) * size
	to := min(from+size, len(records))
	body := map[string]any{"value": []any{}}
	if from < len(records) {
		body["value"] = records[from:to]
	}
	if to < len(records) {
		next := *r.URL
		next.Scheme, next.Host = "http", r.Host
		nq := next.Query()
		nq.Set("page", fmt.Sprint(page+1))
		next.RawQuery = nq.Encode()
		body["@odata.nextLink"] = next.String()
	}
	_ = json.NewEncoder(w).Encode(body)
}

func vendor(id string, minute int) erp.Vendor {
	return erp.Vendor{ID: id, AccountNumber: "K-" + id, Name: "Vendor " + id, Status: "Active", Modified: at(minute)}
}

func order(id, vendorID string, minute int) erp.PurchaseOrder {
	return erp.PurchaseOrder{
		ID: id, Number: "N-" + id, VendorID: vendorID, OrderDate: "2026-11-02", Currency: "EUR", Status: "Open", Modified: at(minute),
		Lines: []erp.OrderLine{{LineNumber: 1, ItemNumber: "PAPER-A4", Description: "Copy paper A4", Quantity: "10", UnitPrice: "4.5", TaxRatePercent: "19"}},
	}
}

func receipt(id, orderID string, minute int) erp.ProductReceipt {
	return erp.ProductReceipt{ID: id, Number: "GR-" + id, PurchaseOrderID: orderID, ReceivedDate: "2026-11-03", Modified: at(minute),
		Lines: []erp.ReceiptLine{{PurchaseOrderLineNumber: 1, Quantity: "1"}}}
}

// ---- Fake app --------------------------------------------------------------------------------------------------------

type call struct{ method, path, body string }

// fakeApp keeps records by entity and id, answers Created / Updated / Unchanged like the real one, and answers what
// a test planned for single ids instead.
type fakeApp struct {
	srv         *httptest.Server
	mu          sync.Mutex
	records     map[string]string // "entity/id" → body
	planned     map[string]int    // "entity/id" → status to answer every time
	watermarks  map[string]string
	failures    map[string]map[string]int // entity → id → tries
	calls       []call
	delay       time.Duration
	block       bool // upserts wait until the request is cancelled
	inFlight    int
	maxInFlight int
	perOrder    map[string]int // receipts in flight per order
	orderClash  bool
}

func newFakeApp(t *testing.T) *fakeApp {
	t.Helper()
	a := &fakeApp{records: map[string]string{}, planned: map[string]int{}, watermarks: map[string]string{},
		failures: map[string]map[string]int{}, perOrder: map[string]int{}}
	a.srv = httptest.NewServer(http.HandlerFunc(a.serve))
	t.Cleanup(a.srv.Close)
	return a
}

func (a *fakeApp) serve(w http.ResponseWriter, r *http.Request) {
	if r.Header.Get("Authorization") != "ApiKey "+appKey {
		w.WriteHeader(401)
		return
	}
	b, _ := io.ReadAll(r.Body)
	body := string(b)
	parts := strings.Split(strings.TrimPrefix(r.URL.Path, "/api/sync/"), "/")
	a.mu.Lock()
	a.calls = append(a.calls, call{r.Method, r.URL.Path, body})
	a.mu.Unlock()

	w.Header().Set("Content-Type", "application/json")
	switch {
	case parts[0] == "watermarks" && r.Method == "GET":
		a.mu.Lock()
		value, ok := a.watermarks[parts[1]]
		a.mu.Unlock()
		if !ok {
			_, _ = fmt.Fprintf(w, `{"entity":%q,"value":null}`, parts[1])
			return
		}
		_, _ = fmt.Fprintf(w, `{"entity":%q,"value":%q}`, parts[1], value)
	case parts[0] == "watermarks" && r.Method == "PUT":
		var v struct{ Value string }
		_ = json.Unmarshal(b, &v)
		a.mu.Lock()
		a.watermarks[parts[1]] = v.Value
		a.mu.Unlock()
		w.WriteHeader(204)
	case parts[0] == "failures" && r.Method == "GET":
		a.mu.Lock()
		var list []map[string]any
		ids := make([]string, 0, len(a.failures[parts[1]]))
		for id := range a.failures[parts[1]] {
			ids = append(ids, id)
		}
		sort.Strings(ids)
		for _, id := range ids {
			list = append(list, map[string]any{"erpId": id, "reason": "planned", "firstSeen": base, "lastSeen": base, "tries": a.failures[parts[1]][id]})
		}
		a.mu.Unlock()
		if list == nil {
			list = []map[string]any{}
		}
		_ = json.NewEncoder(w).Encode(list)
	case parts[0] == "failures" && r.Method == "PUT":
		a.mu.Lock()
		if a.failures[parts[1]] == nil {
			a.failures[parts[1]] = map[string]int{}
		}
		a.failures[parts[1]][parts[2]]++
		a.mu.Unlock()
		w.WriteHeader(204)
	case parts[0] == "failures" && r.Method == "DELETE":
		a.mu.Lock()
		delete(a.failures[parts[1]], parts[2])
		a.mu.Unlock()
		w.WriteHeader(204)
	case r.Method == "PUT":
		a.upsert(w, r, parts[0], parts[1], body)
	default:
		w.WriteHeader(404)
	}
}

func (a *fakeApp) upsert(w http.ResponseWriter, r *http.Request, entity, id, body string) {
	var receiptOf struct{ PurchaseOrderErpID string }
	_ = json.Unmarshal([]byte(body), &receiptOf)
	a.mu.Lock()
	a.inFlight++
	a.maxInFlight = max(a.maxInFlight, a.inFlight)
	if entity == "goods-receipts" {
		a.perOrder[receiptOf.PurchaseOrderErpID]++
		if a.perOrder[receiptOf.PurchaseOrderErpID] > 1 {
			a.orderClash = true
		}
	}
	delay, block := a.delay, a.block
	a.mu.Unlock()
	defer func() {
		a.mu.Lock()
		a.inFlight--
		if entity == "goods-receipts" {
			a.perOrder[receiptOf.PurchaseOrderErpID]--
		}
		a.mu.Unlock()
	}()
	if block {
		<-r.Context().Done()
		return
	}
	time.Sleep(delay)

	a.mu.Lock()
	defer a.mu.Unlock()
	k := entity + "/" + id
	if status, ok := a.planned[k]; ok {
		w.Header().Set("Content-Type", "application/problem+json")
		w.WriteHeader(status)
		outcome := map[int]string{409: "VendorNotFound", 422: "InvalidRecord", 400: ""}[status]
		_, _ = fmt.Fprintf(w, `{"status":%d,"outcome":%q,"detail":"Planned by the test."}`, status, outcome)
		return
	}
	old, exists := a.records[k]
	a.records[k] = body
	switch {
	case !exists:
		w.WriteHeader(201)
		_, _ = io.WriteString(w, `{"outcome":"Created","id":"7b0c1f5e-0000-4000-8000-000000000001"}`)
	case old == body:
		_, _ = io.WriteString(w, `{"outcome":"Unchanged","id":"7b0c1f5e-0000-4000-8000-000000000001"}`)
	default:
		_, _ = io.WriteString(w, `{"outcome":"Updated","id":"7b0c1f5e-0000-4000-8000-000000000001"}`)
	}
}

func (a *fakeApp) upserts(entity string) []string {
	a.mu.Lock()
	defer a.mu.Unlock()
	var out []string
	for _, c := range a.calls {
		if c.method == "PUT" && strings.HasPrefix(c.path, "/api/sync/"+entity+"/") {
			out = append(out, strings.TrimPrefix(c.path, "/api/sync/"+entity+"/"))
		}
	}
	sort.Strings(out)
	return out
}

// ---- Runner ----------------------------------------------------------------------------------------------------------

func noWait() retry.Policy {
	p := retry.Default()
	p.Sleep = func(ctx context.Context, _ time.Duration) error { return ctx.Err() }
	return p
}

func runner(t *testing.T, e *fakeERP, a *fakeApp, log *bytes.Buffer) *Runner {
	t.Helper()
	erpClient, err := erp.New(erp.Config{
		BaseURL: e.srv.URL + "/", TokenURL: e.srv.URL + "/oauth/token", ClientID: "erp-sync", ClientSecret: erpSecret,
		PageSize: 2, HTTP: e.srv.Client(), Retry: noWait(),
	})
	if err != nil {
		t.Fatal(err)
	}
	appClient, err := app.New(app.Config{BaseURL: a.srv.URL + "/", APIKey: appKey, HTTP: a.srv.Client(), Retry: noWait()})
	if err != nil {
		t.Fatal(err)
	}
	if log == nil {
		log = &bytes.Buffer{}
	}
	return &Runner{
		ERP:            erpClient,
		App:            appClient,
		Workers:        4,
		Overlap:        5 * time.Minute,
		Log:            slog.New(slog.NewTextHandler(log, &slog.HandlerOptions{Level: slog.LevelDebug})),
		NewCorrelation: func() string { return "run-1" },
	}
}

func mustApp(t *testing.T, url, key string) *app.Client {
	t.Helper()
	c, err := app.New(app.Config{BaseURL: url + "/", APIKey: key, Retry: noWait()})
	if err != nil {
		t.Fatal(err)
	}
	return c
}
