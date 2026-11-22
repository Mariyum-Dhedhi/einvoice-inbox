# erp-sync

Reads master data from the ERP and hands it to the E-Invoice Inbox: vendors, then purchase orders, then goods
receipts. Go, standard library only (`net/http`, `encoding/json`, `log/slog`, `context`), its own `go.mod`.

```
ERP (OData, OAuth 2.0)  --paged GET, changes since watermark-->  erp-sync  --PUT /api/sync/... (ApiKey)-->  app
```

- **Source:** `GET data/Vendors | PurchaseOrders | ProductReceipts` with
  `$filter=modifiedDateTime ge <watermark - 5 min>`, `$orderby=modifiedDateTime,id` and `$top`, following
  `@odata.nextLink` (which must stay on the ERP's server). Contract: [contracts/d365/README.md](../../contracts/d365/README.md#master-data-contract-read-by-the-erp-sync-package-16c).
- **Target:** the app's sync API, never its database. Contract: [contracts/sync/README.md](../../contracts/sync/README.md).
- **Per entity:**
  1. Read the open failures again by id. A record that goes through clears its failure; one the ERP no longer has is
     forgotten.
  2. Read the changes since the watermark minus 5 minutes of overlap. Clocks differ, and the app's upsert makes a
     second read harmless.
  3. Upsert on `SYNC_WORKERS` goroutines. Records are spread over the workers by ERP id, and receipts by their
     order's id, so two receipts of one order never race the app's over-receipt check.
  4. Save the newest change time read as the new watermark, and only now, when everything went through.
- **Answers:**
  - `409` (a vendor or order not there yet): kept as a failure for the next run.
  - `422`/`400` (refused): logged with the ERP id and skipped.
  - Temporary trouble (timeouts, `408`, `429` with `Retry-After`, `5xx`): retried up to 5 tries, with doubling
    backoff and jitter.
  - When the tries run out, the run stops and the old watermark stays.
  - `401`/`403` from the app stops everything.
- **Log:** JSON (`log/slog`). Each entity writes one line `sync summary` with entity, read, retried, created,
  updated, unchanged, failed, duration, correlation and outcome. Secrets, tokens and record contents are never
  logged.
- **Money:** amounts stay `json.Number`. The ERP's exact digits reach the app and are never read into a float.

## Settings (environment variables only)

| Variable | |
|---|---|
| `ERP_BASE_URL` | required, e.g. `http://mock-erp:8080/` |
| `ERP_TOKEN_URL` | default `<ERP_BASE_URL>oauth/token` |
| `ERP_CLIENT_ID`, `ERP_CLIENT_SECRET` | required |
| `ERP_SCOPE` | optional |
| `APP_BASE_URL` | required |
| `SYNC_API_KEY` | required; the app keeps only its SHA-256 (`Sync:ApiKeySha256`) |
| `SYNC_INTERVAL` | unset or `0`: one run, exit 0 or 1. `5m`: a run every 5 minutes until SIGTERM |
| `SYNC_RUN_TIMEOUT` | default `4m`, shorter than the interval |
| `SYNC_WORKERS` | 1 to 32, default 4 |
| `SYNC_PAGE_SIZE` | 1 to 1000, default 1000 |

A missing or wrong setting stops it with exit code 2. The message names the variable and never repeats its value.

## Test and build

Go is used only from the pinned image, the same one in CI and in the Dockerfile. Run from the repository root,
because the tests read the contract examples in `contracts/d365`:

```
docker run --rm -v "$PWD:/repo" -w /repo/sync/erp-sync golang:1.27.3-bookworm@sha256:dc0963f32053e4bc451d24d87e3892d42d01021b61f093e98eb95c0352bb84b0 sh test.sh
docker build -t erp-sync sync/erp-sync
```

The image is the static binary on `scratch` (CA certificates only), user 65532. Run it with `--read-only`.

## Known limit: deletes

A watermark finds changes, not deletions. A record deleted in the ERP is never read again, so the app keeps it. The
mock ERP never deletes: a vendor is `Blocked` and an order `Closed`, and the sync carries the status over. The fix
for real deletes is a weekly full comparison of ids, which is out of scope here.
