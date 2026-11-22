package erp

import (
	"bytes"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"time"
)

// The ERP's records as its OData sets give them (the mock ERP's contract: contracts/d365/README.md, "Master data contract").
// Amounts are json.Number: the exact text the ERP sent goes on to the app, never through a float.

// Vendor is one entry of data/Vendors.
type Vendor struct {
	ID            string    `json:"id"`
	AccountNumber string    `json:"vendorAccountNumber"`
	Name          string    `json:"name"`
	VatNumber     *string   `json:"vatNumber"`
	Status        string    `json:"status"` // Active or Blocked
	Modified      time.Time `json:"modifiedDateTime"`
}

// PurchaseOrder is one entry of data/PurchaseOrders, with its lines.
type PurchaseOrder struct {
	ID        string      `json:"id"`
	Number    string      `json:"purchaseOrderNumber"`
	VendorID  string      `json:"vendorId"`
	OrderDate string      `json:"orderDate"` // yyyy-mm-dd, passed on as it is
	Currency  string      `json:"currencyCode"`
	Status    string      `json:"status"` // Open or Closed
	Modified  time.Time   `json:"modifiedDateTime"`
	Lines     []OrderLine `json:"lines"`
}

// OrderLine is one line of a purchase order.
type OrderLine struct {
	LineNumber     int         `json:"lineNumber"`
	ItemNumber     string      `json:"itemNumber"`
	Description    string      `json:"description"`
	Quantity       json.Number `json:"quantity"`
	UnitPrice      json.Number `json:"unitPrice"`
	TaxRatePercent json.Number `json:"taxRatePercent"`
}

// ProductReceipt is one entry of data/ProductReceipts (a goods receipt), with its lines.
type ProductReceipt struct {
	ID              string        `json:"id"`
	Number          string        `json:"productReceiptNumber"`
	PurchaseOrderID string        `json:"purchaseOrderId"`
	ReceivedDate    string        `json:"receivedDate"`
	Modified        time.Time     `json:"modifiedDateTime"`
	Lines           []ReceiptLine `json:"lines"`
}

// ReceiptLine is the quantity received of one order line.
type ReceiptLine struct {
	PurchaseOrderLineNumber int         `json:"purchaseOrderLineNumber"`
	Quantity                json.Number `json:"quantity"`
}

// Record is what every set's entries have: the ERP's id and the time of their last change.
type Record interface {
	Key() string
	Changed() time.Time
}

func (v Vendor) Key() string                { return v.ID }
func (v Vendor) Changed() time.Time         { return v.Modified }
func (o PurchaseOrder) Key() string         { return o.ID }
func (o PurchaseOrder) Changed() time.Time  { return o.Modified }
func (r ProductReceipt) Key() string        { return r.ID }
func (r ProductReceipt) Changed() time.Time { return r.Modified }

// Set names an OData entity set and the record type its entries decode into.
type Set[T Record] struct{ Name string }

var (
	Vendors         = Set[Vendor]{"Vendors"}
	PurchaseOrders  = Set[PurchaseOrder]{"PurchaseOrders"}
	ProductReceipts = Set[ProductReceipt]{"ProductReceipts"}
)

// decodeStrict reads exactly one JSON value into v: an unknown field, a missing one (null or absent where a value is
// required) or trailing data is an error. Like the app's connector: a renamed field must fail, not go missing.
func decodeStrict(data []byte, v any) error {
	if err := strictInto(data, v); err != nil {
		return err
	}
	return complete(v)
}

func strictInto(data []byte, v any) error {
	dec := json.NewDecoder(bytes.NewReader(data))
	dec.DisallowUnknownFields()
	dec.UseNumber()
	if err := dec.Decode(v); err != nil {
		return err
	}
	if _, err := dec.Token(); !errors.Is(err, io.EOF) {
		return errors.New("data after the JSON value")
	}
	return nil
}

// requiredFields lists, per record type, the JSON fields that must be present (encoding/json cannot say "required").
var requiredFields = map[string][]string{
	"vendor":  {"id", "vendorAccountNumber", "name", "vatNumber", "status", "modifiedDateTime"},
	"order":   {"id", "purchaseOrderNumber", "vendorId", "orderDate", "currencyCode", "status", "modifiedDateTime", "lines"},
	"oline":   {"lineNumber", "itemNumber", "description", "quantity", "unitPrice", "taxRatePercent"},
	"receipt": {"id", "productReceiptNumber", "purchaseOrderId", "receivedDate", "modifiedDateTime", "lines"},
	"rline":   {"purchaseOrderLineNumber", "quantity"},
}

// complete checks what decoding cannot: every required value is there (not zero where zero means "missing").
func complete(v any) error {
	switch r := v.(type) {
	case *Vendor:
		return need("vendor", r.ID != "" && r.AccountNumber != "" && r.Name != "" && r.Status != "" && !r.Modified.IsZero())
	case *PurchaseOrder:
		if err := need("order", r.ID != "" && r.Number != "" && r.VendorID != "" && r.OrderDate != "" && r.Currency != "" &&
			r.Status != "" && !r.Modified.IsZero() && r.Lines != nil); err != nil {
			return err
		}
		for _, l := range r.Lines {
			if err := need("oline", l.LineNumber != 0 && l.ItemNumber != "" && l.Description != "" && l.Quantity != "" && l.UnitPrice != "" && l.TaxRatePercent != ""); err != nil {
				return err
			}
		}
	case *ProductReceipt:
		if err := need("receipt", r.ID != "" && r.Number != "" && r.PurchaseOrderID != "" && r.ReceivedDate != "" && !r.Modified.IsZero() && r.Lines != nil); err != nil {
			return err
		}
		for _, l := range r.Lines {
			if err := need("rline", l.PurchaseOrderLineNumber != 0 && l.Quantity != ""); err != nil {
				return err
			}
		}
	}
	return nil
}

func need(kind string, ok bool) error {
	if ok {
		return nil
	}
	return fmt.Errorf("a %s record lacks one of %v", kind, requiredFields[kind])
}
