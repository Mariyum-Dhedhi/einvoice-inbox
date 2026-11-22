package erp

import (
	"os"
	"path/filepath"
	"testing"
	"time"
)

// The committed examples (contracts/d365/*.page.example.json) are what the mock ERP serves; the sync's strict reader
// must take them as they are. The mock ERP's side is MockErpMasterDataTests.

func example(t *testing.T, name string) []byte {
	t.Helper()
	b, err := os.ReadFile(filepath.Join("..", "..", "..", "..", "contracts", "d365", name))
	if err != nil {
		t.Fatalf("the contract example %s: %v", name, err)
	}
	return b
}

func decodePage[T Record](t *testing.T, name string) ([]T, string) {
	t.Helper()
	var p page
	if err := strictInto(example(t, name), &p); err != nil || p.Value == nil {
		t.Fatalf("%s is not a page: %v", name, err)
	}
	var out []T
	for i, raw := range *p.Value {
		var r T
		if err := decodeStrict(raw, &r); err != nil {
			t.Fatalf("%s entry %d: %v", name, i+1, err)
		}
		out = append(out, r)
	}
	return out, p.NextLink
}

func TestTheVendorExampleIsRead(t *testing.T) {
	vendors, next := decodePage[Vendor](t, "vendors.page.example.json")

	if len(vendors) != 2 || next != "" || vendors[0].AccountNumber != "K-00001" || *vendors[0].VatNumber != "DE123456789" ||
		vendors[1].VatNumber != nil || vendors[1].Status != "Blocked" || !vendors[0].Modified.Equal(time.Date(2026, 8, 20, 7, 12, 45, 0, time.UTC)) {
		t.Fatalf("read %+v, next %q", vendors, next)
	}
}

func TestThePurchaseOrderExampleIsReadWithItsExactAmounts(t *testing.T) {
	orders, next := decodePage[PurchaseOrder](t, "purchase-orders.page.example.json")

	if len(orders) != 1 || next == "" {
		t.Fatalf("read %+v, next %q", orders, next)
	}
	o := orders[0]
	if o.Number != "PO-26-000001" || o.VendorID != "V-00001" || o.OrderDate != "2026-10-28" || len(o.Lines) != 2 ||
		o.Lines[0].UnitPrice.String() != "4.50" || o.Lines[1].Quantity.String() != "12.5" {
		t.Fatalf("read %+v", o)
	}
}

func TestTheProductReceiptExampleIsRead(t *testing.T) {
	receipts, _ := decodePage[ProductReceipt](t, "product-receipts.page.example.json")

	if len(receipts) != 1 || receipts[0].PurchaseOrderID != "PO-00001" || receipts[0].Lines[0].Quantity.String() != "60" {
		t.Fatalf("read %+v", receipts)
	}
}
