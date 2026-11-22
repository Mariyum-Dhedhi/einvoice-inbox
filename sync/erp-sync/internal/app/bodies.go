package app

import "encoding/json"

// The bodies of the sync API's upserts, field for field as contracts/sync/README.md says (the app reads them strictly:
// a field more or one missing is 400). Amounts are json.Number so the ERP's exact text arrives.

type VendorBody struct {
	Number string  `json:"number"`
	Name   string  `json:"name"`
	VatID  *string `json:"vatId"`
	Status string  `json:"status"` // Active or Blocked
}

type OrderBody struct {
	Number      string          `json:"number"`
	VendorErpID string          `json:"vendorErpId"`
	OrderDate   string          `json:"orderDate"`
	Currency    string          `json:"currency"`
	Status      string          `json:"status"` // Open or Closed
	Lines       []OrderLineBody `json:"lines"`
}

type OrderLineBody struct {
	LineNumber     int         `json:"lineNumber"`
	ItemNumber     string      `json:"itemNumber"`
	Description    string      `json:"description"`
	Quantity       json.Number `json:"quantity"`
	UnitPrice      json.Number `json:"unitPrice"`
	TaxRatePercent json.Number `json:"taxRatePercent"`
}

type ReceiptBody struct {
	Number             string            `json:"number"`
	PurchaseOrderErpID string            `json:"purchaseOrderErpId"`
	ReceivedDate       string            `json:"receivedDate"`
	Lines              []ReceiptLineBody `json:"lines"`
}

type ReceiptLineBody struct {
	PurchaseOrderLineNumber int         `json:"purchaseOrderLineNumber"`
	Quantity                json.Number `json:"quantity"`
}
