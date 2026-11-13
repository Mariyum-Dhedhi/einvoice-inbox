# Database

SQL Server 2022 in Docker. `docker compose up -d` starts it and runs the one-shot `db-init` service,
which applies everything in this folder in order. Every step is idempotent.

| Step | File | What it does |
|---|---|---|
| 1 | (inline) | Creates the `EInvoice` database |
| 2 | `migrations/schema.sql` | Schema, generated from the EF Core migrations (do not edit by hand) |
| 3 | `security/app-user.sql` | Least-privilege login `einvoice_app`: read/write data, execute procedures, no schema changes |
| 4 | `seed/seed.sql` | Synthetic data: 20 vendors with one approved IBAN each, 200 purchase orders, receipts, 6 invoices covering all match outcomes |

`tests/verify.sql` runs as `einvoice_app` and checks the seed counts and the security controls
(cannot create or drop tables, cannot change migration history, duplicate invoice number and unknown status rejected).

## Passwords

Both come from `.env` (git-ignored; copy `.env.example`): `MSSQL_SA_PASSWORD` for setup only,
`EINVOICE_APP_PASSWORD` for the application.

## Changing the schema

```bash
dotnet tool restore
E="--project src/EInvoice.Infrastructure --startup-project src/EInvoice.Infrastructure"
dotnet ef migrations add <Name> $E --output-dir Persistence/Migrations
dotnet ef migrations script --idempotent $E -o db/migrations/schema.sql
```

CI fails if the model has changes without a migration, or if `schema.sql` was not regenerated.

## Running the database tests locally

```bash
docker compose up -d
# PowerShell:  $env:EINVOICE_TEST_CONNECTION = "Server=127.0.0.1,1433;Database=EInvoice;User Id=einvoice_app;Password=<EINVOICE_APP_PASSWORD>;Encrypt=True;TrustServerCertificate=True"
dotnet test --filter Category=Database
```

Use `127.0.0.1`, not `localhost`: the port is bound to IPv4 loopback only.
`TrustServerCertificate=True` is for the local self-signed certificate only, never for a deployed database.

## Design notes

- **Keys:** Guids created by the domain, as nonclustered primary keys. Each table is clustered on a
  `bigint IDENTITY` column `RowId` that the domain never sees, avoiding page splits from random Guids.
- **Money:** `decimal(18,2)`; unit prices `decimal(18,6)`; quantities `decimal(18,4)`. No `float`.
- **Duplicates:** unique index `UX_Invoice_Vendor_InvoiceNumber`, filtered to rows that have both values.
  A rejected invoice still blocks its number.
- **Status:** stored as `int` with a CHECK constraint generated from the C# enum.
- **Deletes:** only a document's own lines cascade; deleting a vendor or order never deletes documents.
- **sqlcmd needs `-I`** (QUOTED_IDENTIFIER ON); filtered indexes fail without it.

## Seed data changes

The seed runs only into an empty database (it skips when vendors exist). After pulling a change to
`seed/seed.sql`, reset the local database: `docker compose down -v && docker compose up -d`.
CI always starts from an empty database.
