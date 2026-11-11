# E-Invoice Inbox

[![CI](https://github.com/Mariyum-Dhedhi/einvoice-inbox/actions/workflows/ci.yml/badge.svg)](https://github.com/Mariyum-Dhedhi/einvoice-inbox/actions/workflows/ci.yml)

Accounts-payable automation for German e-invoices: receives XRechnung, ZUGFeRD and PDF invoices,
matches them against purchase orders and goods receipts, approves the clean ones automatically,
and sends the rest to a human with a stated reason.

> **Status:** in development. Week 1 (MVP) is being built in public, one
> [work package](https://github.com/Mariyum-Dhedhi/einvoice-inbox/issues) per pull request.

## Run it

Requirements: .NET 10 SDK and Docker.

```bash
cp .env.example .env          # then set MSSQL_SA_PASSWORD in .env
docker compose up -d          # SQL Server 2022 on localhost:1433
dotnet build --warnaserror
dotnet test
```

## Stack

C# on .NET 10, ASP.NET Core, EF Core, SQL Server 2022, Blazor, xUnit. Modular monolith:

```
src/EInvoice.Domain          entities, value objects, matching rules, state machine (no dependencies)
src/EInvoice.Application     use cases, interfaces, validation
src/EInvoice.Infrastructure  EF Core, SQL, parsers, AI client, ERP connector
src/EInvoice.Web             API controllers and Blazor UI
tests/                       automated tests
```

## Deliberately out of scope

- Creating outbound invoices and running payments
- Posting to a real ERP system
- Sending or receiving over the Peppol network
- OCR of scanned paper images
- Multi-company and multi-currency support
- Re-implementing the EN 16931 rule set; the official KoSIT validator does that

## License

MIT. All data in this repository is synthetic.
