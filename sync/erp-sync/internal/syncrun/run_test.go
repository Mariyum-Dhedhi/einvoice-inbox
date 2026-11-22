package syncrun

import (
	"bytes"
	"context"
	"errors"
	"fmt"
	"strings"
	"testing"
	"time"

	"github.com/Mariyum-Dhedhi/einvoice-inbox/sync/erp-sync/internal/erp"
)

func summaryOf(t *testing.T, summaries []Summary, entity string) Summary {
	t.Helper()
	for _, s := range summaries {
		if s.Entity == entity {
			return s
		}
	}
	t.Fatalf("no summary for %s in %+v", entity, summaries)
	return Summary{}
}

func seed(e *fakeERP) {
	e.add("Vendors", vendor("V1", 1), vendor("V2", 2), vendor("V3", 3))
	e.add("PurchaseOrders", order("PO1", "V1", 4), order("PO2", "V2", 5), order("PO3", "V1", 6), order("PO4", "V3", 7), order("PO5", "V2", 8))
	e.add("ProductReceipts", receipt("PR1", "PO1", 9), receipt("PR2", "PO2", 10))
}

// ---- Watermarks ------------------------------------------------------------------------------------------------------

func TestAFirstRunReadsEverythingAndSavesTheNewestChangeAsWatermark(t *testing.T) {
	e, a := newFakeERP(t), newFakeApp(t)
	seed(e)

	summaries, err := runner(t, e, a, nil).Run(context.Background())

	if err != nil {
		t.Fatal(err)
	}
	if e.filters["Vendors"][0] != "" {
		t.Fatalf("first run filtered %q", e.filters["Vendors"][0])
	}
	if s := summaryOf(t, summaries, "purchase-orders"); s.Read != 5 || s.Created != 5 || s.Outcome != "ok" {
		t.Fatalf("orders: %+v", s)
	}
	for entity, want := range map[string]time.Time{"vendors": at(3), "purchase-orders": at(8), "goods-receipts": at(10)} {
		if a.watermarks[entity] != want.Format(time.RFC3339Nano) {
			t.Errorf("%s watermark %q, want %v", entity, a.watermarks[entity], want)
		}
	}
}

func TestTheNextRunAsksFromTheWatermarkMinusFiveMinutes(t *testing.T) {
	e, a := newFakeERP(t), newFakeApp(t)
	seed(e)
	a.watermarks["purchase-orders"] = "2026-11-21T09:10:00Z"

	if _, err := runner(t, e, a, nil).Run(context.Background()); err != nil {
		t.Fatal(err)
	}

	if got := e.filters["PurchaseOrders"][0]; got != "modifiedDateTime ge 2026-11-21T09:05:00Z" {
		t.Fatalf("filter %q", got)
	}
}

func TestARunRightAfterAnotherReadsOnlyTheOverlapAndChangesNothing(t *testing.T) {
	e, a := newFakeERP(t), newFakeApp(t)
	seed(e)
	r := runner(t, e, a, nil)
	if _, err := r.Run(context.Background()); err != nil {
		t.Fatal(err)
	}

	summaries, err := r.Run(context.Background())

	if err != nil {
		t.Fatal(err)
	}
	orders := summaryOf(t, summaries, "purchase-orders") // watermark 09:08, read from 09:03: PO1..PO5 (04..08)
	if orders.Read != 5 || orders.Unchanged != 5 || orders.Created+orders.Updated != 0 {
		t.Fatalf("orders: %+v", orders)
	}
	if a.watermarks["purchase-orders"] != at(8).Format(time.RFC3339Nano) {
		t.Fatalf("watermark moved to %q", a.watermarks["purchase-orders"])
	}
}

func TestAChangedRecordIsUpdated(t *testing.T) {
	e, a := newFakeERP(t), newFakeApp(t)
	seed(e)
	r := runner(t, e, a, nil)
	_, _ = r.Run(context.Background())
	changed := order("PO2", "V2", 30)
	changed.Status = "Closed"
	e.mu.Lock()
	e.sets["PurchaseOrders"][1] = changed
	e.mu.Unlock()

	summaries, err := r.Run(context.Background())

	if s := summaryOf(t, summaries, "purchase-orders"); err != nil || s.Updated != 1 {
		t.Fatalf("got %+v, %v", s, err)
	}
	if a.watermarks["purchase-orders"] != at(30).Format(time.RFC3339Nano) {
		t.Fatalf("watermark %q", a.watermarks["purchase-orders"])
	}
}

func TestTheWatermarkIsSavedOnlyAfterTheLastUpsert(t *testing.T) {
	e, a := newFakeERP(t), newFakeApp(t)
	seed(e)

	if _, err := runner(t, e, a, nil).Run(context.Background()); err != nil {
		t.Fatal(err)
	}

	lastUpsert, watermark := -1, -1
	for i, c := range a.calls {
		if c.method == "PUT" && strings.HasPrefix(c.path, "/api/sync/purchase-orders/") {
			lastUpsert = i
		}
		if c.method == "PUT" && c.path == "/api/sync/watermarks/purchase-orders" {
			watermark = i
		}
	}
	if watermark < 0 || watermark < lastUpsert {
		t.Fatalf("watermark saved at call %d, last order upsert at %d", watermark, lastUpsert)
	}
}

func TestAFailureOnPageThreeLeavesTheOldWatermarkAndStopsTheRun(t *testing.T) {
	e, a := newFakeERP(t), newFakeApp(t)
	seed(e)
	e.failPage["PurchaseOrders"] = 3 // pages: PO1,PO2 | PO3,PO4 | PO5
	a.watermarks["purchase-orders"] = "2026-11-01T00:00:00Z"

	summaries, err := runner(t, e, a, nil).Run(context.Background())

	if err == nil {
		t.Fatal("no error")
	}
	if a.watermarks["purchase-orders"] != "2026-11-01T00:00:00Z" {
		t.Fatalf("watermark moved to %q", a.watermarks["purchase-orders"])
	}
	if s := summaryOf(t, summaries, "purchase-orders"); s.Outcome != "failed" || s.Read != 4 {
		t.Fatalf("orders: %+v", s)
	}
	if got := a.upserts("goods-receipts"); len(got) != 0 {
		t.Fatalf("receipts ran after orders failed: %v", got)
	}
}

// ---- Order and records -----------------------------------------------------------------------------------------------

func TestVendorsThenOrdersThenReceipts(t *testing.T) {
	e, a := newFakeERP(t), newFakeApp(t)
	seed(e)

	if _, err := runner(t, e, a, nil).Run(context.Background()); err != nil {
		t.Fatal(err)
	}

	rank := map[string]int{"vendors": 0, "purchase-orders": 1, "goods-receipts": 2}
	last := 0
	for _, c := range a.calls {
		parts := strings.Split(strings.TrimPrefix(c.path, "/api/sync/"), "/")
		if c.method != "PUT" || parts[0] == "watermarks" || parts[0] == "failures" {
			continue
		}
		if rank[parts[0]] < last {
			t.Fatalf("%s after a later entity", c.path)
		}
		last = rank[parts[0]]
	}
}

func TestARefusedRecordIsLoggedWithItsIdAndSkipped(t *testing.T) {
	e, a := newFakeERP(t), newFakeApp(t)
	seed(e)
	a.planned["purchase-orders/PO3"] = 422
	var log bytes.Buffer

	summaries, err := runner(t, e, a, &log).Run(context.Background())

	if err != nil {
		t.Fatal(err)
	}
	if s := summaryOf(t, summaries, "purchase-orders"); s.Created != 4 || s.Failed != 1 || s.Outcome != "ok" {
		t.Fatalf("orders: %+v", s)
	}
	if a.watermarks["purchase-orders"] != at(8).Format(time.RFC3339Nano) {
		t.Fatalf("watermark %q", a.watermarks["purchase-orders"])
	}
	if !strings.Contains(log.String(), "erp_id=PO3") || !strings.Contains(log.String(), "outcome=InvalidRecord") {
		t.Fatalf("log:\n%s", log.String())
	}
	if len(a.failures["purchase-orders"]) != 0 {
		t.Fatalf("a refused record is not retried, but was kept: %v", a.failures)
	}
}

func TestARecordThatWaitsIsKeptAsAFailureForTheNextRun(t *testing.T) {
	e, a := newFakeERP(t), newFakeApp(t)
	seed(e)
	a.planned["purchase-orders/PO4"] = 409

	summaries, err := runner(t, e, a, nil).Run(context.Background())

	if err != nil {
		t.Fatal(err)
	}
	if s := summaryOf(t, summaries, "purchase-orders"); s.Failed != 1 {
		t.Fatalf("orders: %+v", s)
	}
	if a.failures["purchase-orders"]["PO4"] != 1 {
		t.Fatalf("failures %v", a.failures)
	}
	if a.watermarks["purchase-orders"] != at(8).Format(time.RFC3339Nano) {
		t.Fatalf("the watermark should move on: the failure is retried by id, got %q", a.watermarks["purchase-orders"])
	}
}

func TestOpenFailuresAreReadAgainByIdFirstAndClearedWhenTheyGoThrough(t *testing.T) {
	e, a := newFakeERP(t), newFakeApp(t)
	seed(e)
	a.watermarks["purchase-orders"] = "2026-11-21T10:00:00Z" // no order changed since
	a.failures["purchase-orders"] = map[string]int{"PO2": 1}

	summaries, err := runner(t, e, a, nil).Run(context.Background())

	if err != nil {
		t.Fatal(err)
	}
	if !contains(e.byIDCalls, "PurchaseOrders:PO2") || len(a.failures["purchase-orders"]) != 0 {
		t.Fatalf("by id %v, failures %v", e.byIDCalls, a.failures)
	}
	if s := summaryOf(t, summaries, "purchase-orders"); s.Retried != 1 || s.Created != 1 || s.Read != 0 {
		t.Fatalf("orders: %+v", s)
	}
}

func TestAFailureThatStillWaitsCountsAnotherTry(t *testing.T) {
	e, a := newFakeERP(t), newFakeApp(t)
	seed(e)
	a.watermarks["purchase-orders"] = "2026-11-21T10:00:00Z"
	a.failures["purchase-orders"] = map[string]int{"PO2": 1}
	a.planned["purchase-orders/PO2"] = 409

	if _, err := runner(t, e, a, nil).Run(context.Background()); err != nil {
		t.Fatal(err)
	}

	if a.failures["purchase-orders"]["PO2"] != 2 {
		t.Fatalf("failures %v", a.failures)
	}
}

func TestAFailureTheErpNoLongerHasIsForgotten(t *testing.T) {
	e, a := newFakeERP(t), newFakeApp(t)
	seed(e)
	a.failures["purchase-orders"] = map[string]int{"PO-GONE": 3}
	var log bytes.Buffer

	if _, err := runner(t, e, a, &log).Run(context.Background()); err != nil {
		t.Fatal(err)
	}

	if len(a.failures["purchase-orders"]) != 0 || !strings.Contains(log.String(), "erp_id=PO-GONE") {
		t.Fatalf("failures %v; log:\n%s", a.failures, log.String())
	}
}

func TestEveryRecordIsSentAsTheContractSays(t *testing.T) {
	e, a := newFakeERP(t), newFakeApp(t)
	vat := "DE000000123"
	blocked := vendor("V1", 1)
	blocked.VatNumber, blocked.Status = &vat, "Blocked"
	e.add("Vendors", blocked, vendor("V2", 2))
	o := order("PO1", "V1", 3)
	o.Status = "Closed"
	o.Lines = append(o.Lines, erp.OrderLine{LineNumber: 2, ItemNumber: "TONER", Description: "Toner", Quantity: "0.3333", UnitPrice: "12345678901234.567891", TaxRatePercent: "7.00"})
	e.add("PurchaseOrders", o)
	r := receipt("PR1", "PO1", 4)
	r.Lines = append(r.Lines, erp.ReceiptLine{PurchaseOrderLineNumber: 2, Quantity: "0.25"})
	e.add("ProductReceipts", r)

	if _, err := runner(t, e, a, nil).Run(context.Background()); err != nil {
		t.Fatal(err)
	}

	for k, want := range map[string]string{
		"vendors/V1": `{"number":"K-V1","name":"Vendor V1","vatId":"DE000000123","status":"Blocked"}`,
		"vendors/V2": `{"number":"K-V2","name":"Vendor V2","vatId":null,"status":"Active"}`,
		"purchase-orders/PO1": `{"number":"N-PO1","vendorErpId":"V1","orderDate":"2026-11-02","currency":"EUR","status":"Closed","lines":[` +
			`{"lineNumber":1,"itemNumber":"PAPER-A4","description":"Copy paper A4","quantity":10,"unitPrice":4.5,"taxRatePercent":19},` +
			`{"lineNumber":2,"itemNumber":"TONER","description":"Toner","quantity":0.3333,"unitPrice":12345678901234.567891,"taxRatePercent":7.00}]}`,
		"goods-receipts/PR1": `{"number":"GR-PR1","purchaseOrderErpId":"PO1","receivedDate":"2026-11-03","lines":[` +
			`{"purchaseOrderLineNumber":1,"quantity":1},{"purchaseOrderLineNumber":2,"quantity":0.25}]}`,
	} {
		if got := a.records[k]; got != want {
			t.Errorf("%s:\n got %s\nwant %s", k, got, want)
		}
	}
}

// ---- Workers ---------------------------------------------------------------------------------------------------------

func TestUpsertsRunOnAtMostTheWorkersAndReceiptsOfOneOrderNeverAtOnce(t *testing.T) {
	e, a := newFakeERP(t), newFakeApp(t)
	e.add("Vendors", vendor("V1", 1))
	for i := range 6 {
		e.add("PurchaseOrders", order(fmt.Sprintf("PO%d", i), "V1", 2))
	}
	for i := range 24 {
		e.add("ProductReceipts", receipt(fmt.Sprintf("PR%02d", i), fmt.Sprintf("PO%d", i%3), 3+i))
	}
	a.delay = 20 * time.Millisecond

	if _, err := runner(t, e, a, nil).Run(context.Background()); err != nil {
		t.Fatal(err)
	}

	if a.maxInFlight < 2 || a.maxInFlight > 4 {
		t.Fatalf("at most %d upserts at once, want 2 to 4", a.maxInFlight)
	}
	if a.orderClash {
		t.Fatal("two receipts of the same order were sent at the same time")
	}
	if len(a.upserts("goods-receipts")) != 24 {
		t.Fatalf("receipts %v", a.upserts("goods-receipts"))
	}
}

// ---- Stopping and the summary ----------------------------------------------------------------------------------------

func TestATimeoutStopsTheRunCleanlyAndSavesNoWatermark(t *testing.T) {
	e, a := newFakeERP(t), newFakeApp(t)
	seed(e)
	a.block = true
	var log bytes.Buffer
	ctx, cancel := context.WithTimeout(context.Background(), 200*time.Millisecond)
	defer cancel()
	started := time.Now()

	summaries, err := runner(t, e, a, &log).Run(ctx)

	if !errors.Is(err, context.DeadlineExceeded) {
		t.Fatalf("got %v, want the deadline", err)
	}
	if time.Since(started) > 3*time.Second {
		t.Fatalf("took %v to stop", time.Since(started))
	}
	if len(a.watermarks) != 0 {
		t.Fatalf("watermarks saved: %v", a.watermarks)
	}
	if s := summaryOf(t, summaries, "vendors"); s.Outcome != "cancelled" {
		t.Fatalf("vendors: %+v", s)
	}
	if !strings.Contains(log.String(), "outcome=cancelled") {
		t.Fatalf("log:\n%s", log.String())
	}
}

func TestEachEntityLogsOneSummaryLineWithItsCountsAndNoSecret(t *testing.T) {
	e, a := newFakeERP(t), newFakeApp(t)
	seed(e)
	a.planned["purchase-orders/PO5"] = 409
	var log bytes.Buffer

	if _, err := runner(t, e, a, &log).Run(context.Background()); err != nil {
		t.Fatal(err)
	}

	var lines []string
	for _, l := range strings.Split(log.String(), "\n") {
		if strings.Contains(l, `msg="sync summary"`) {
			lines = append(lines, l)
		}
	}
	if len(lines) != 3 {
		t.Fatalf("%d summary lines:\n%s", len(lines), log.String())
	}
	orders := lines[1]
	for _, want := range []string{"entity=purchase-orders", "read=5", "retried=0", "created=4", "updated=0", "unchanged=0", "failed=1", "duration=", "correlation=run-1", "outcome=ok"} {
		if !strings.Contains(orders, want) {
			t.Errorf("summary lacks %q: %s", want, orders)
		}
	}
	for _, secret := range []string{erpSecret, appKey, token} {
		if strings.Contains(log.String(), secret) {
			t.Fatalf("a secret reached the log:\n%s", log.String())
		}
	}
}

func TestTheAppRefusingTheKeyStopsTheRun(t *testing.T) {
	e, a := newFakeERP(t), newFakeApp(t)
	seed(e)
	r := runner(t, e, a, nil)
	r.App = mustApp(t, a.srv.URL, "wrong-key")

	summaries, err := r.Run(context.Background())

	if err == nil || len(a.upserts("vendors")) != 0 || len(summaries) != 1 {
		t.Fatalf("got %v, %d summaries", err, len(summaries))
	}
}

func contains(list []string, s string) bool {
	for _, x := range list {
		if x == s {
			return true
		}
	}
	return false
}
