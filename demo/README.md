# Demo

Everything here is synthetic: fictional vendors, orders and invoices from `db/seed/seed.sql`, bank code `00000000`
(which does not exist), and invoices built in code (`tests/EInvoice.IntegrationTests/TestData/DemoInvoices.cs`).

## Start

Once per machine (Docker and the .NET 10 SDK installed):

```bash
cp .env.example .env    # set MSSQL_SA_PASSWORD and EINVOICE_APP_PASSWORD
# The app signs in to SQL Server as einvoice_app with EINVOICE_APP_PASSWORD from .env:
dotnet user-secrets set "ConnectionStrings:EInvoice" "Server=127.0.0.1,1433;Database=EInvoice;User Id=einvoice_app;Password=<EINVOICE_APP_PASSWORD>;Encrypt=True;TrustServerCertificate=True" --project src/EInvoice.Web
dotnet user-secrets set "DemoUsers:Password" "<a passphrase of at least 12 characters>" --project src/EInvoice.Web
```

Every time:

```bash
docker compose up -d                                   # SQL Server, KoSIT validator, mock ERP
dotnet run --project src/EInvoice.Web --launch-profile https
```

Open https://localhost:7169. To show posting to the (mock) ERP as well, start the app with
`Erp__Enabled=true` and `Erp__BaseUrl=http://127.0.0.1:8090/` set as environment variables.

## Sign in

All demo accounts share the password set in `DemoUsers:Password`
(`dotnet user-secrets list --project src/EInvoice.Web` shows it). They exist only in Development.

| Account | Role | Can |
|---|---|---|
| `clerk@example.com` | Clerk | upload, re-match, read invoices, ask for an explanation |
| `approver@example.com` | Approver | approve or reject (never their own upload), read invoices |
| `auditor@example.com` | Auditor | read everything, verify the audit trail; changes nothing |
| `admin@example.com`, `admin2@example.com` | Admin | users, vendors, bank accounts; never touches invoices |
| `buyer@example.com` | Buyer | purchase orders and goods receipts (what invoices are matched against); never touches invoices |

## The files

Upload them from `demo/invoices` as the clerk. Each one uses its own seeded order, so the order does not matter.

| File | What happens |
|---|---|
| `01-full-match.xml` | XRechnung, prices and quantities agree with order and goods receipt: **Matched** |
| `02-zugferd-full-match.pdf` | ZUGFeRD PDF; its embedded XML is the invoice (no AI): **Matched** |
| `03-price-variance.xml` | Desk billed at 389.00, ordered at 349.00: **Exception**, price variance |
| `04-quantity-variance.xml` | 25 toner cartridges billed, 20 delivered: **Exception**, quantity variance |
| `05-no-goods-receipt.xml` | Nothing received yet: **Exception**, nothing received |
| `06-no-purchase-order.xml` | Refers to order PO-9998, which does not exist: **Exception**, no purchase order |
| `07-unknown-bank-account.xml` | Payment to a bank account nobody approved: **Exception**, IBAN_NOT_APPROVED |
| `08-unreadable.xml` | Breaks off in the middle: **Failed**, the original file is kept |
| `01-full-match.xml` again | Refused as a duplicate; nothing is stored |

`DemoInvoiceTests` checks every one of these outcomes against the seeded database, and the XML passes the
official KoSIT validator.

## A ten-minute demo

0. **Buyer: what invoices are matched against.** As `buyer@example.com`, `/purchasing` lists the seeded orders
   with what arrived. Open PO-0110: ordered, nothing received. Record a goods receipt (the open quantities are
   filled in). Later, as the clerk, *Match again* on `05-no-goods-receipt.xml` turns it **Matched**. A new order
   (*New purchase order*) works the same way; nothing recorded can be edited afterwards, and every addition is in
   the audit trail. The buyer cannot open the inbox.
1. **Clerk: intake.** Upload 01, 03, 07 and 02. Each lands with a status in words and an icon. Open 03: the
   three-way match panel shows order price, invoiced price and the tolerance; *Explain* says why in plain words.
   Open 07: the bank account is shown masked, and the finding says it is not an approved account of this vendor.
2. **Clerk: what is refused.** Upload 01 again (duplicate, nothing stored) and 08 (Failed, original kept).
3. **Approver: decisions.** `/approvals` lists what waits, oldest first, and says in advance what is allowed.
   Approve 01 (no reason needed). Approve 03 (a reason is required for an exception). Reject 07 with a reason.
   With the ERP switched on, 01 turns **Posted** within seconds, with the ERP's document number.
4. **Auditor: proof.** `/audit` verifies the hash chain of every status change and sign-in; note the count and the
   latest hash.
5. **Dashboard.** `/dashboard` shows invoices by status, what blocks the exception queue, the straight-through rate
   per month and the ERP posting health.
6. **Admin: bank accounts.** `/admin/vendors`, pick a vendor, propose a new bank account as `admin@`; it stays
   Pending until `admin2@` confirms it (two-person rule), and the history keeps every version.

The six seeded invoices (RE-2026-0001 to 0006) start as *Parsed*; as the clerk, *Re-match* on one of them shows each
match outcome without uploading anything.

## Reset after a demo

The files have fixed invoice numbers, so they can be uploaded once per database. To start again, throw the local
database away and let db-init build it fresh. **This deletes everything in your local database**:

```bash
docker compose down -v      # -v removes the database volume
docker compose up -d
```

Then **restart the app**: it creates the demo accounts when it starts, so a running app does not put them into the new
database, and every sign-in fails until it is restarted.

A local database created before package 11 needs this reset too: its bank accounts became *Pending* when the
two-person rule arrived (an account nobody confirmed is not trusted), so every invoice gets `IBAN_NOT_APPROVED`.

## Changing the files

Change `DemoInvoices.cs`, then write the files and check them:

```bash
EINVOICE_WRITE_DEMO_FILES=1 dotnet test tests/EInvoice.IntegrationTests --filter FullyQualifiedName~DemoFileTests
dotnet test tests/EInvoice.IntegrationTests --filter FullyQualifiedName~DemoInvoiceTests
```
