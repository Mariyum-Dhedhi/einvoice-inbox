package app

import (
	"context"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"github.com/Mariyum-Dhedhi/einvoice-inbox/sync/erp-sync/internal/retry"
)

const apiKey = "key-for-tests-0123456789"

type seen struct {
	method, path, auth, contentType, body string
}

// fakeApp answers every request with status and body, and remembers what it was asked.
func fakeApp(t *testing.T, status int, body string) (*Client, *[]seen) {
	t.Helper()
	var calls []seen
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		b, _ := io.ReadAll(r.Body)
		calls = append(calls, seen{r.Method, r.URL.EscapedPath(), r.Header.Get("Authorization"), r.Header.Get("Content-Type"), string(b)})
		if strings.HasPrefix(body, "{") || strings.HasPrefix(body, "[") {
			if status >= 400 {
				w.Header().Set("Content-Type", "application/problem+json")
			} else {
				w.Header().Set("Content-Type", "application/json")
			}
		}
		w.WriteHeader(status)
		_, _ = io.WriteString(w, body)
	}))
	t.Cleanup(srv.Close)
	return newClient(t, srv), &calls
}

func newClient(t *testing.T, srv *httptest.Server) *Client {
	t.Helper()
	p := retry.Default()
	p.Sleep = func(ctx context.Context, _ time.Duration) error { return ctx.Err() }
	c, err := New(Config{BaseURL: srv.URL + "/", APIKey: apiKey, HTTP: srv.Client(), Retry: p})
	if err != nil {
		t.Fatal(err)
	}
	return c
}

func TestAnUpsertIsAPutWithTheKeyAndTheBody(t *testing.T) {
	c, calls := fakeApp(t, 201, `{"outcome":"Created","id":"7b0c1f5e-0000-4000-8000-000000000001"}`)

	result, err := c.Upsert(context.Background(), PurchaseOrders, "PO-00001", map[string]any{"number": "PO-26-000001"})

	if err != nil || result.Kind != Created {
		t.Fatalf("got %+v, %v", result, err)
	}
	got := (*calls)[0]
	if got.method != "PUT" || got.path != "/api/sync/purchase-orders/PO-00001" || got.auth != "ApiKey "+apiKey ||
		got.contentType != "application/json" || got.body != `{"number":"PO-26-000001"}` {
		t.Fatalf("sent %+v", got)
	}
}

func TestEveryAnswerHasItsKind(t *testing.T) {
	for _, tc := range []struct {
		status  int
		body    string
		kind    Kind
		outcome string
	}{
		{201, `{"outcome":"Created","id":"7b0c1f5e-0000-4000-8000-000000000001"}`, Created, "Created"},
		{200, `{"outcome":"Updated","id":"7b0c1f5e-0000-4000-8000-000000000001"}`, Updated, "Updated"},
		{200, `{"outcome":"Unchanged","id":"7b0c1f5e-0000-4000-8000-000000000001"}`, Unchanged, "Unchanged"},
		{409, `{"status":409,"outcome":"VendorNotFound","detail":"The vendor V-1 is not there yet."}`, Waits, "VendorNotFound"},
		{422, `{"status":422,"outcome":"OverReceived","detail":"Line 1 would exceed the order."}`, Refused, "OverReceived"},
		{400, `{"status":400,"detail":"The body is not a Vendor of the agreed format."}`, Refused, ""},
	} {
		c, _ := fakeApp(t, tc.status, tc.body)

		result, err := c.Upsert(context.Background(), Vendors, "V-1", map[string]any{})

		if err != nil || result.Kind != tc.kind || result.Outcome != tc.outcome || result.Status != tc.status {
			t.Errorf("%d %s: got %+v, %v", tc.status, tc.body, result, err)
		}
		if tc.status >= 400 && result.Detail == "" {
			t.Errorf("%d: the detail is lost", tc.status)
		}
	}
}

func TestAnUnknownSuccessOutcomeIsAnError(t *testing.T) {
	c, _ := fakeApp(t, 200, `{"outcome":"Merged","id":"7b0c1f5e-0000-4000-8000-000000000001"}`)

	if _, err := c.Upsert(context.Background(), Vendors, "V-1", map[string]any{}); err == nil {
		t.Fatal("no error")
	}
}

func TestAWrongOrMissingKeyStopsEverythingAndNeverRepeatsTheKey(t *testing.T) {
	for _, status := range []int{401, 403} {
		c, calls := fakeApp(t, status, `{"status":401}`)

		_, err := c.Upsert(context.Background(), Vendors, "V-1", map[string]any{})

		if !errors.Is(err, ErrNotAllowed) || strings.Contains(err.Error(), apiKey) || len(*calls) != 1 {
			t.Errorf("%d: got %v after %d calls", status, err, len(*calls))
		}
	}
}

func TestATemporaryAppErrorIsRetried(t *testing.T) {
	var calls atomic.Int32
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if calls.Add(1) < 3 {
			w.WriteHeader(503)
			return
		}
		w.WriteHeader(200)
		_, _ = io.WriteString(w, `{"outcome":"Unchanged","id":"7b0c1f5e-0000-4000-8000-000000000001"}`)
	}))
	defer srv.Close()

	result, err := newClient(t, srv).Upsert(context.Background(), Vendors, "V-1", map[string]any{})

	if err != nil || result.Kind != Unchanged || calls.Load() != 3 {
		t.Fatalf("got %+v, %v after %d calls", result, err, calls.Load())
	}
}

func TestAnErpIdIsEscapedInThePath(t *testing.T) {
	c, calls := fakeApp(t, 400, `{"status":400}`)

	_, _ = c.Upsert(context.Background(), Vendors, "V 1/../x", map[string]any{})

	if got := (*calls)[0].path; got != "/api/sync/vendors/V%201%2F..%2Fx" {
		t.Fatalf("path %q", got)
	}
}

// ---- Watermarks ------------------------------------------------------------------------------------------------------

func TestNoWatermarkYet(t *testing.T) {
	c, calls := fakeApp(t, 200, `{"entity":"vendors","value":null}`)

	_, ok, err := c.Watermark(context.Background(), Vendors)

	if err != nil || ok || (*calls)[0].method != "GET" || (*calls)[0].path != "/api/sync/watermarks/vendors" {
		t.Fatalf("got %v, %v; asked %+v", ok, err, (*calls)[0])
	}
}

func TestAWatermarkIsReadAndSavedInUtc(t *testing.T) {
	c, _ := fakeApp(t, 200, `{"entity":"goods-receipts","value":"2026-11-21T10:00:00+01:00"}`)

	value, ok, err := c.Watermark(context.Background(), GoodsReceipts)

	if err != nil || !ok || !value.Equal(time.Date(2026, 11, 21, 9, 0, 0, 0, time.UTC)) {
		t.Fatalf("got %v, %v, %v", value, ok, err)
	}

	saver, calls := fakeApp(t, 204, "")
	if err := saver.SetWatermark(context.Background(), GoodsReceipts, time.Date(2026, 11, 21, 10, 0, 0, 123456700, time.FixedZone("CET", 3600))); err != nil {
		t.Fatal(err)
	}
	got := (*calls)[0]
	if got.method != "PUT" || got.path != "/api/sync/watermarks/goods-receipts" || got.body != `{"value":"2026-11-21T09:00:00.1234567Z"}` {
		t.Fatalf("sent %+v", got)
	}
}

// ---- Failures --------------------------------------------------------------------------------------------------------

func TestOpenFailuresAreRead(t *testing.T) {
	c, _ := fakeApp(t, 200, `[{"erpId":"PO-7","reason":"VendorNotFound","firstSeen":"2026-11-21T09:00:00Z","lastSeen":"2026-11-21T09:05:00Z","tries":2}]`)

	failures, err := c.Failures(context.Background(), PurchaseOrders)

	if err != nil || len(failures) != 1 || failures[0].ErpID != "PO-7" || failures[0].Tries != 2 {
		t.Fatalf("got %+v, %v", failures, err)
	}
}

func TestAFailureIsRecordedAndCleared(t *testing.T) {
	c, calls := fakeApp(t, 204, "")

	if err := c.RecordFailure(context.Background(), PurchaseOrders, "PO-7", "409 VendorNotFound: The vendor is not there yet."); err != nil {
		t.Fatal(err)
	}
	if err := c.ClearFailure(context.Background(), PurchaseOrders, "PO-7"); err != nil {
		t.Fatal(err)
	}

	put, del := (*calls)[0], (*calls)[1]
	if put.method != "PUT" || put.path != "/api/sync/failures/purchase-orders/PO-7" || put.body != `{"reason":"409 VendorNotFound: The vendor is not there yet."}` {
		t.Fatalf("sent %+v", put)
	}
	if del.method != "DELETE" || del.path != "/api/sync/failures/purchase-orders/PO-7" {
		t.Fatalf("sent %+v", del)
	}
}

func TestAReasonIsMadeToFitTheAppsRule(t *testing.T) {
	long := strings.Repeat("ä", 250) + "\nsecond line"
	c, calls := fakeApp(t, 204, "")

	if err := c.RecordFailure(context.Background(), Vendors, "V-1", long); err != nil {
		t.Fatal(err)
	}

	body := (*calls)[0].body
	reason := strings.TrimSuffix(strings.TrimPrefix(body, `{"reason":"`), `"}`)
	if n := len([]rune(reason)); n != 200 || strings.ContainsAny(reason, "\n\r") || strings.Contains(body, `\n`) {
		t.Fatalf("reason of %d characters: %q", n, reason)
	}
}

func TestTheKeyIsNeverInTheConfigsText(t *testing.T) {
	cfg := Config{BaseURL: "http://app/", APIKey: apiKey}

	for _, text := range []string{fmt.Sprint(cfg), fmt.Sprintf("%+v", cfg), fmt.Sprintf("%#v", cfg)} {
		if strings.Contains(text, apiKey) {
			t.Fatalf("key in %q", text)
		}
	}
}
