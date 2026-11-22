# ERP sync contract (app side)

The ERP sync (`sync/erp-sync`, package 16c) reads master data from the ERP and hands it to the app through this API.
It never writes to the app's database itself: the app keeps its rules, its audit trail and its transactions.

## Who may call

Only the sync, with its API key:

```
Authorization: ApiKey <key>
```

The app stores only the key's SHA-256 (`Sync:ApiKeySha256`, 64 hex digits, from user secrets or an environment
variable). The right key signs the request in as `erp-sync` with the `Integration` role. That role is not an account
role: no user can hold it and no person can sign in with it. Without a configured hash, no key works. A wrong or missing
key gets `401` problem details with `WWW-Authenticate: ApiKey`; the key may call nothing but `/api/sync` (`403`).

## Records: `PUT /api/sync/{entity}/{erpId}`

`entity` is `vendors`, `purchase-orders` or `goods-receipts`. `erpId` is the ERP's id: 1 to 50 letters, digits or
`. _ : -`, starting with a letter or digit. Bodies are `application/json` and read strictly: a field more or one
missing is `400`.

```json
// vendors/{erpId}
{ "number": "K-00001", "name": "Nordlicht Papier GmbH", "vatId": "DE000000123", "status": "Active" }

// purchase-orders/{erpId}
{ "number": "PO-26-000001", "vendorErpId": "V-00001", "orderDate": "2026-11-02", "currency": "EUR", "status": "Open",
  "lines": [ { "lineNumber": 1, "itemNumber": "PAPER-A4", "description": "Copy paper A4", "quantity": 10,
               "unitPrice": 4.5, "taxRatePercent": 19 } ] }

// goods-receipts/{erpId}
{ "number": "GR-26-000001", "purchaseOrderErpId": "PO-00001", "receivedDate": "2026-11-03",
  "lines": [ { "purchaseOrderLineNumber": 1, "quantity": 4 } ] }
```

`status` is `Active` or `Blocked` for a vendor (blocked means inactive), and `Open` or `Closed` for an order. Lines
are replaced as a whole; their order does not matter.

| Answer | `outcome` | What the sync does |
|---|---|---|
| `201` | `Created` | counts it as created |
| `200` | `Updated` | counts it as updated (audited, with the new state) |
| `200` | `Unchanged` | counts it as unchanged: the same values again write nothing, so re-reading an overlap is harmless |
| `409` | `VendorNotFound`, `OrderNotFound` | a record it depends on is not there yet: keep it as a failure and try again next run |
| `422` | `InvalidRecord`, `NumberTaken`, `NumberChanged`, `VendorChanged`, `OrderChanged`, `OrderBelowReceived`, `OverReceived` | the record itself is refused: log it with its ERP id and skip it |
| `400` | none | the request is not this contract (body or ERP id) |
| `415` | none | the body is not `application/json` |

Every refusal is problem details (`application/problem+json`) with the `outcome` and a safe `detail` that never
repeats the record. The app's rules still apply:
- Orders are in EUR, dated no later than today, 1 to 100 lines.
- An order's number, vendor and currency never change, and a receipt's number and order never change.
- A line never goes below what was received, and no receipt exceeds what was ordered.
- A number that belongs to a record made in the app is refused.

Records from the ERP are marked as such. Only the sync changes them: the buyer cannot add a receipt to an ERP order
(its receipts come from the ERP), and records made in the app are never touched by the sync.

Every created or updated record writes an audit event (`VendorSyncedFromErp`, `PurchaseOrderSyncedFromErp`,
`GoodsReceiptSyncedFromErp`). The event's actor is `erp-sync`, its source `"ERP sync"`, and it holds the ERP id and the
new state.

## Watermarks: `GET` / `PUT /api/sync/watermarks/{entity}`

```json
GET  → { "entity": "purchase-orders", "value": "2026-11-21T09:00:00+00:00" }   // value null before the first run
PUT  ← { "value": "2026-11-21T09:00:00+00:00" }                                  // 204
```

The latest change time a complete run has read. The sync saves it only after the whole run for that entity succeeded.

## Failures: `/api/sync/failures/{entity}[/{erpId}]`

| Call | Meaning |
|---|---|
| `GET /api/sync/failures/{entity}` | open failures, oldest first: `[{ "erpId", "reason", "firstSeen", "lastSeen", "tries" }]` |
| `PUT /api/sync/failures/{entity}/{erpId}` `{ "reason": "..." }` | record a failure, or count one more try of a known one (reason: 1 to 200 characters, one line) |
| `DELETE /api/sync/failures/{entity}/{erpId}` | the record went through: forget the failure (`204` also when there was none) |

Each run first reads its open failures again from the ERP by id, then reads the changes since the watermark.
