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

Open https://localhost:7169. To show posting to the (mock) ERP as well, give the app the same client as the mock
ERP once (`ERP_CLIENT_ID` and `ERP_CLIENT_SECRET` from `.env`; the mock does not start without a secret):

```bash
dotnet user-secrets set "Erp:ClientId" "einvoice" --project src/EInvoice.Web
dotnet user-secrets set "Erp:ClientSecret" "<ERP_CLIENT_SECRET from .env>" --project src/EInvoice.Web
```

Then start the app with `Erp__Enabled=true` and `Erp__BaseUrl=http://127.0.0.1:8090/` set as environment variables.

### With AI (plain PDF invoices)

Plain PDFs are read by a local language model, which is off unless switched on. Once per machine: install
[Ollama](https://ollama.com) and run `ollama pull qwen2.5:7b` (about 4.7 GB; a GPU with 8 GB makes a page take a few
seconds). Ollama listens on `127.0.0.1:11434`, on this machine only. Then start the app with AI and ERP on:

```bash
Ai__Enabled=true Ai__OllamaUrl=http://127.0.0.1:11434/ Ai__Model=qwen2.5:7b Erp__Enabled=true Erp__BaseUrl=http://127.0.0.1:8090/ dotnet run --project src/EInvoice.Web --launch-profile https
```

(PowerShell: `$env:Ai__Enabled="true"; $env:Ai__OllamaUrl="http://127.0.0.1:11434/"; $env:Ai__Model="qwen2.5:7b"`, then
`dotnet run ...`.) With AI on, *Explain with AI* also appears on exceptions. The first PDF after a start takes longer
while Ollama loads the model.

Before a demo, check that the model gives every AI sample its outcome on this machine (a minute or two, needs Docker):

```bash
EINVOICE_OLLAMA_URL=http://127.0.0.1:11434/ EINVOICE_OLLAMA_MODEL=qwen2.5:7b dotnet test tests/EInvoice.IntegrationTests --filter FullyQualifiedName~DemoAiOllamaTests
```

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

With AI on (plain PDFs, no XML inside; the model reads them):

| File | What happens |
|---|---|
| `09-ai-full-match.pdf` | German layout, three lines: read, every value found in the document, **Matched** |
| `10-ai-price-variance.pdf` | 14 desks billed at 379,00 instead of 349,00: **Exception**, price variance |
| `11-ai-hidden-instruction.pdf` | Correct values, plus white-on-white text telling the AI to approve it and report a total of 1.00. The model never sees hidden text; the app reports it: **Exception**, HIDDEN_TEXT, values unchanged |
| `12-ai-english-numbers.pdf` | International layout with English numbers (1,234.56): read correctly, **Matched** |
| `13-ai-letterhead-with-logo.pdf` | Laid out like a real supplier invoice: logo, letterhead, address window, ruled table, VAT per rate (19 % and 7 %), footer with registry and bank details. The logo is an image and never reaches the model: **Matched** |
| `14-ai-letterhead-price-variance.pdf` | Another supplier's letterhead: 16 toner cartridges billed at 68,90 instead of the ordered 62,50 (+10 %): **Exception**, price variance |
| `15-ai-letterhead-quantity-variance.pdf` | 20 HDMI cables billed, 17 delivered: **Exception**, quantity variance |
| `16-ai-letterhead-not-received.pdf` | Six desks billed for order PO-0120, of which nothing has arrived yet: **Exception**, nothing received (as the buyer, record the receipt, then *Match again*) |
| `17-ai-letterhead-new-bank-account.pdf` | Correct in every value, but "our bank details have changed, pay only to the account below", with an account nobody approved: the classic invoice fraud. The model reads the IBAN, the app checks it: **Exception**, IBAN_NOT_APPROVED |

With AI off, the PDFs are refused ("PDF invoices are read by AI, which is switched off"); nothing is stored.
`DemoAiInvoiceTests` checks these outcomes in CI with a perfect reader; `DemoAiOllamaTests` with the real model (local
only, not a measurement: the measured numbers are in `testdata/ai-eval`).

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
6. **AI (if switched on).** As the clerk, upload 09: the invoice carries an *AI · read from PDF* badge, and the
   inbox marks it. Upload 11: the values are the document's, and the finding says hidden text was found; open the
   PDF to see that the page looks normal. Upload 10, open it, and press *Explain with AI*: a suggestion in words, whose
   numbers are checked against the match result before it is shown. A person still decides. Then upload 17, which looks
   like a genuine supplier letter asking for payment to a new account: the app stops it with IBAN_NOT_APPROVED.
7. **Admin: bank accounts.** `/admin/vendors`, pick a vendor, propose a new bank account as `admin@`; it stays
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

Change `DemoInvoices.cs` (XML and ZUGFeRD) or `DemoAiInvoices.cs` (plain PDFs, drawn by the AI evaluation's renderer),
then write the files and check them:

```bash
EINVOICE_WRITE_DEMO_FILES=1 dotnet test tests/EInvoice.IntegrationTests --filter FullyQualifiedName~DemoFileTests
dotnet test tests/EInvoice.IntegrationTests --filter "FullyQualifiedName~DemoInvoiceTests|FullyQualifiedName~DemoAiInvoiceTests"
```

A changed AI sample is checked with the real model too (`DemoAiOllamaTests`, above) before it is committed: a layout the
model misreads still ends safely (the line and VAT checks catch it, nothing wrong is matched) but shows a different
story. A two-line version of `10` did exactly that with qwen2.5:7b, every time; the desk-only invoice reads cleanly.
