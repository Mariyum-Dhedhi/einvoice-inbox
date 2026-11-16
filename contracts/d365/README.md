# ERP posting contract

`POST data/VendorInvoices` with the header `Idempotency-Key: <invoice id>` and a body exactly like
`vendor-invoice.example.json` (camelCase, no field more, none missing; `purchaseOrder` and `purchaseOrderLine` may be
`null`).

| Answer | Meaning |
|---|---|
| `201 {"documentNumber":"VI-000123"}` | booked |
| `200` with the same number | this key was booked before (a retry after a lost reply) |
| `400` | the body is not this contract, or the key is missing |
| `422` | the ERP refuses the invoice (for example a currency the company does not book): final |
| `408`, `429`, `5xx` | temporary: the application tries again later |

Both sides test against this file: the application's connector (`HttpErpConnector`) and the mock ERP
(`tests/EInvoice.MockErp`). Change it only together with both.
