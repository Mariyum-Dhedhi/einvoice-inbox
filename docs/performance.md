# Performance

What happens when the database has a year of invoices in it and several clerks work at once? Package 15a measured
it with a load test, found a slow query and a deadlock, fixed both behind failing tests, and measured again.

**Short version:** at about 105,000 invoices and 10 clerk actions per second, the system before this package dropped
requests: 60 deadlocks, 129 lock-wait timeouts, 63 failed requests, and uploads that took up to 10 seconds. After the
fixes, every one of 7,268 requests succeeded, a re-match took 28 ms (95th percentile) and an upload 76 ms.

## What was measured

| | |
|---|---|
| Date | 2026-11-16 |
| Code | commit `f4170a5`; the states below are switched with `tests/load/state.sql` on the same build |
| Machine | one laptop: Intel Core i7-13650HX (14 cores, 20 threads), 32 GB RAM, Windows 11 |
| Database | SQL Server 2022 CU27 in Docker Desktop 4.99.0 (20 CPUs, 16 GB for the VM) |
| App | Release build, logging at Warning, HTTPS on localhost, sign-in through the real login form |
| Load | k6 2.3.0 in Docker: 10 clerk turns per second for 3 minutes (1,800 turns, about 7,300 requests) |
| Data | a fresh database per state: 100,000 invoices from the past year, plus a busy order (see below) |

One clerk turn is what a clerk does with a new invoice:
1. Upload an XRechnung invoice.
2. Open it.
3. Re-match it.
4. Look at the first page of the exception queue.

Every upload is a valid invoice for the same order. `LoadTemplateTests` proves the template k6 fills in is exactly what the tested invoice builder makes.

**The busy order.** PO-0101 stands for a framework order billed in parts: it starts with 5,000 earlier invoices and
gets every upload of the run (up to about 6,800 invoices). Matching adds up what all the other invoices of an order
have billed, so this is the worst case for matching. Most orders have one or a few invoices; the second table shows one of those.

Everything ran on one machine, so the load generator, the app and the database compete for the same CPUs. The
numbers compare states on this machine; they are not a promise for a server.

## Results

Three states, each on a fresh load database, in the order shown:

- **Before:** the schema before package 15a.
- **Order index only:** the first fix, `IX_Invoice_PurchaseOrderNumber`.
- **After:** both indexes plus snapshot reads, as the migrations and `db/database.sql` now build it.

| | Before | Order index only | After |
|---|---:|---:|---:|
| Failed requests | **63** of 6,725 | 0 of 7,268 | **0** of 7,268 |
| Deadlocks / lock-wait timeouts (app log) | **60 / 129** | 0 / 0 | **0 / 0** |
| Turns completed (of 1,800) | 1,633 | 1,801 | 1,801 |
| Upload, 95th percentile | 9.90 s | 121 ms | **76 ms** |
| Re-match, 95th percentile | 9.81 s | 74 ms | **28 ms** |
| Open invoice, 95th percentile | 15 ms | 14 ms | 13 ms |
| Exception queue, 95th percentile | 17 ms | 13 ms | 13 ms |
| Pages read per match, busy order | 38,320 | 36,816 | **17,972** |
| CPU per match | 44.0 ms | 32.4 ms | **14.0 ms** |
| Time per match, including waits | 1,662 ms | 39.9 ms | **14.6 ms** |

Per match: SQL Server's own counters for `usp_MatchInvoice` over the whole run (`sys.dm_exec_procedure_stats`),
averaged over about 3,200 to 3,600 matches. Every match in the run was on the busy order. "Turns completed": in the
before state the app fell behind and k6 dropped 167 turns (k6 counts 1,801 when one extra turn starts in the last
moment).

A match on an ordinary order (seeded invoice RE-2026-0001, `match-reads.sql`, same database):

| | Before | After |
|---|---:|---:|
| Pages read by one match | 2,794 (2,689 of them scanning `dbo.Invoice`) | **93** |

Raw results: [`tests/load/results`](../tests/load/results) (k6 summaries and SQL Server counters).

## What the load test found, and the fixes

### 1. Matching read the whole invoice table

The match adds up what every *other* invoice of the same order has already billed. Without an index on the order
number, SQL Server read all invoices to find them, so every match got slower as invoices piled up.

**Test first:** `QueryPlanTests.Matching_reads_only_the_invoices_of_its_order` reads the actual execution plan and
fails if `dbo.Invoice` is read by anything but a seek.

**Fix:** `IX_Invoice_PurchaseOrderNumber (PurchaseOrderNumber) INCLUDE (Id, Status)`. It contains every column the query needs, so there is no second read per invoice.

**Effect:** an ordinary match goes from 2,794 to 93 pages.

On the busy order, this index alone barely helped: 36,816 pages instead of 38,320. The next finding explains why.

### 2. A deadlock between matching and uploading

On an earlier run with only the order index, the busy order had grown to about 7,000 invoices. 108 requests failed,
re-matches took up to 10 seconds, and the app log showed deadlocks. SQL Server's deadlock record showed the cycle:

| | holds | waits for |
|---|---|---|
| a match on PO-0101 | a lock on the **whole** `InvoiceLine` table | the new invoice row of an upload |
| an upload for PO-0101 | its new, not yet committed invoice row | room to insert its lines into `InvoiceLine` |

The match read so many invoice lines that SQL Server replaced thousands of row locks with one lock on the whole table. SQL Server
ended the deadlock by cancelling one side, and the clerk got a 500. Matches of one order also run one after the other by design (an app lock,
so two invoices cannot both claim the same delivery). Once matches slowed down, the rest queued until the
10-second lock wait ran out ("Timed out waiting for another match on the same purchase order").

Whether a run deadlocks depends on timing. The fresh order-index-only run above happened to finish without one.
So the fixes are proven by tests, not by load runs:

**Test first:** `LockingTests.A_match_does_not_wait_for_an_upload_that_is_not_committed_yet`. One connection
writes an invoice for an order and does not commit. Another matches an invoice of the same order with a 3-second
lock timeout. Before the fix the match waited, every time.

**Fix: snapshot reads** (`READ_COMMITTED_SNAPSHOT`, `db/database.sql`, run by db-init and the test fixture).
- A read sees the last committed version of a row instead of waiting for a writer. Writers still lock each other.
- The deliberate locks keep working: the app locks of matching and the audit chain, and `UPDLOCK, READPAST` in the posting queue. All 343 database tests pass with it.
- Azure SQL Database, the planned cloud target, has this setting on by default.
- Correctness: a match does not count an invoice that is not yet committed. That invoice's own match runs after it commits and counts this one, so the two are matched as if one came after the other.

**Test first:** `QueryPlanTests.Matching_reads_the_lines_of_other_invoices_from_one_index`. It gives the order 300
more invoices and reads the actual plan's run-time counters. It fails if `InvoiceLine` is scanned, or if any read in the table
runs more often than the invoice has lines. Before the fix: 600 table reads for an invoice with 2 lines.

**Fix:** `IX_InvoiceLine_Quantities (InvoiceId, LineNumber) INCLUDE (Quantity, PurchaseOrderLineNumber)`.
- The lines of the other invoices are read from this index alone, without a second read in the table for each line.
- Pages per match on the busy order halve: 36,816 to 17,972.
- CPU per match: 32.4 to 14.0 ms.

**Plan tests on a grown table.** On the six seeded invoices any plan is cheap, and SQL Server may scan where it would
seek on a grown table. So the plan tests first add 5,000 invoices inside their rolled-back transaction and compile
afresh (`WITH RECOMPILE`). For each fix, a planted bug (fix removed) makes its test fail.

## Known limit

Matching an invoice still reads every other invoice of its order: the cost grows with the size of the order, not with
the size of the table. For the busy order with about 6,800 invoices that is 17,972 pages and 14 ms of CPU per
match, fine at 10 per second. An order with tens of thousands of invoices would need a running total per order
line (what has been billed so far), kept in step with every invoice's status. That is a bigger change to the
matching rules, which exist twice (C# and SQL), and it is not part of this package.

## Not measured

- **The KoSIT check.** The load app uses the local KoSIT container like the demo does, so each upload includes it. It is not
  measured on its own.
- **AI extraction.** It is off: PDF reading speed depends on the GPU, see the README.
- **The dashboard and the Blazor pages.** They run over a WebSocket connection; the load test drives the API.
- **The volume data.** It has no audit events and only placeholder documents, and no query under test reads either.

## Reproduce

[`tests/load/README.md`](../tests/load/README.md). A slow query found there gets a failing plan test first, then the
fix. Numbers in this file and the README come only from runs saved in `tests/load/results`.
