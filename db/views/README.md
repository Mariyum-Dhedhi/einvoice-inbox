# KPI views

One `CREATE OR ALTER VIEW` script per view in this folder (for example `vw_InvoicesByStatus.sql`). db-init runs them
after the stored procedures and before the app user and seed; the database tests build their database the same way,
so a broken script fails the tests. The specification is `tests/EInvoice.IntegrationTests/Kpi/KpiViewTests.cs`:
make those tests pass.

Rules for every view: schema `dbo`, explicit column names, no `SELECT *`, no `ORDER BY` (the reader sorts), and every
count or sum is `0`, never `NULL`. The app user reads them (`db_datareader`); it cannot change them.

## 1. `dbo.vw_InvoicesByStatus`

| Column | Type | Meaning |
|---|---|---|
| `Status` | int | `dbo.Invoice.Status` |
| `InvoiceCount` | int | invoices in that status |
| `GrossTotalEur` | decimal(18,2) | sum of `GrossTotal_Amount` of those invoices whose `GrossTotal_Currency` is `EUR`; `0` if none |

One row per status that has at least one invoice.

## 2. `dbo.vw_MonthlyThroughput`

| Column | Type | Meaning |
|---|---|---|
| `Month` | date | first day of the month in which the invoice was received, **in Berlin time** |
| `Received` | int | invoices received in that month |
| `StraightThrough` | int | of those: reached `Approved` (6) and were **never** in `Exception` (5) |
| `Exceptions` | int | of those: were in `Exception` at least once |
| `Failed` | int | of those: were `Failed` (9) at least once |

"Received" is the invoice's first status change (`dbo.InvoiceStatusChange`, `Sequence = 1`, column `At`, a
`datetimeoffset`). Berlin time: `At AT TIME ZONE 'W. Europe Standard Time'` (it knows summer time). "Ever" means any
row in `dbo.InvoiceStatusChange` for the invoice with that `ToStatus`. One row per month that has received invoices.

## 3. `dbo.vw_OpenExceptionReasons`

| Column | Type | Meaning |
|---|---|---|
| `Kind` | varchar | `'Finding'` or `'Match'` |
| `Reason` | varchar | the finding's `RuleCode`, or the match outcome's name |
| `OpenInvoices` | int | invoices **now** in `Exception` (5) with that reason, each invoice counted once |

Findings come from `dbo.InvoiceFinding`; match reasons from `dbo.MatchResult.Outcome` except `FullMatch`, named as the
application names them: 2 `PriceVariance`, 3 `QuantityVariance`, 4 `NoPurchaseOrder`, 5 `NoGoodsReceipt`.
One row per kind and reason that occurs.

## 4. `dbo.vw_PostingHealth`

Always exactly **one** row, also when there are no posting jobs at all.

| Column | Type | Meaning |
|---|---|---|
| `WaitingToPost` | int | jobs `Pending` (1) with `Attempts = 0` |
| `Retrying` | int | jobs `Pending` (1) with `Attempts > 0` |
| `Failed` | int | jobs `Dead` (3) |
| `Posted` | int | jobs `Done` (2) |
| `OldestWaitingSince` | datetimeoffset, nullable | the earliest `CreatedAt` of all `Pending` jobs; `NULL` when none waits |
