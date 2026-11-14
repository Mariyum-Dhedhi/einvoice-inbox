# E-Invoice Inbox

Accounts-payable automation for German e-invoices (XRechnung, ZUGFeRD, PDF):
parse, validate, three-way match, approve, post, audit.

## Stack
C# on .NET 10, ASP.NET Core, EF Core, SQL Server 2022, Blazor with MudBlazor,
xUnit, Testcontainers, Playwright. Docker Compose for local runs.

## Layout
src/EInvoice.Domain          no dependencies; entities, rules, state machine
src/EInvoice.Application     use cases and interfaces
src/EInvoice.Infrastructure  EF Core, SQL, parsers, AI client, ERP connector
src/EInvoice.Web             API and Blazor UI
tests/                       unit, integration, E2E, AI evaluation
db/                          stored procedures, views, seed data

## Rules
- Domain references nothing. Dependencies point inward only.
- Money is decimal, never float or double.
- All SQL is parameterised. No string-built queries.
- XML is read with DTD processing prohibited and no external resolver.
- No secrets in the repository. Use user secrets and environment variables.
- AI output is untrusted input: validate against the schema and re-check with rules.
- Tests first. Every bug fix starts with a failing test.
- Small commits, one concern each, imperative commit messages.
- Do not add a NuGet package without asking.
- Synthetic data only.

## Conventions
- Shared build settings live in Directory.Build.props; do not repeat them in a .csproj.
- NuGet versions live only in Directory.Packages.props (central package management).
- Package lock files are committed; CI restores with --locked-mode. Run `dotnet restore` after changing packages.
- Warnings are errors, including vulnerable-package (NuGetAudit) warnings.
- Layering is enforced by tests/EInvoice.UnitTests/ArchitectureTests.cs.
- GitHub Actions are pinned to full commit SHAs with the version in a comment.
- Docker images are pinned to a specific tag; database ports bind to 127.0.0.1 only.
- One branch and one pull request per work package; the PR closes its issue.
- Schema changes: add an EF migration AND regenerate db/migrations/schema.sql (see db/README.md); CI checks both.
- Every decimal column has explicit precision; every string has a max length; enums get a CHECK constraint (DatabaseModelTests).
- Tables: Guid nonclustered PK, clustered on a hidden bigint IDENTITY RowId (MappingConventions.UseClusteredRowId).
- Collections exposed by entities are returned in a defined order; the database never guarantees row order.
- The app connects as einvoice_app (no schema rights); only db-init uses sa.
- Tests that need SQL Server carry [Trait("Category", "Database")] and [Collection(SqlServerCollection.Name)]; SqlServerFixture builds the database from the db/ scripts, so a broken deployment script fails the tests.
- Database tests must not assume they are alone: create data under unique numbers, and roll back unless the test needs commits.
- Matching rules exist twice (ThreeWayMatcher in C#, dbo.usp_MatchInvoice in SQL). Change both together; MatchProcedureTests compares them field by field.
- Stored procedures live in db/procedures as CREATE OR ALTER scripts, deployed by db-init after the migrations.
- Uploads: file type comes from the bytes (ContentSniffer), never the name; file names are display-only (UploadNames.Sanitize). Duplicates are checked first and backed by unique indexes for races.
- Every accepted upload becomes an invoice: Matched, Exception (with findings or match reasons) or Failed (unreadable, original kept). Refusals (empty, too large, wrong type, duplicate) store nothing.
- Values longer than a database column are refused with a reason before saving, never truncated where they identify something (invoice number, order reference).
- API errors are problem details (application/problem+json) with a safe detail: no exception text, no stack trace. Refusals carry an "outcome" name.
- Settings are bound to options classes and validated at startup (ValidateOnStart); a wrong value stops the app. Connection strings come from user secrets or environment variables.
- Every response sends X-Content-Type-Options: nosniff; API responses are no-store. Stored files are only served as attachments.
- OpenAPI (/openapi/v1.json) is mapped in Development only.
- Re-match only clears match problems: an invoice in Exception with validation findings stays for a human.
- Committed test invoices for the same vendor need distinct amounts or dates, or the POSSIBLE_DUPLICATE rule (correctly) flags invoices in other tests.
- All XML is read through Infrastructure/Parsing/SafeXml (DTD prohibited, no resolver, size limit). Never create an XmlReader or XDocument from untrusted input anywhere else.
- Parse errors name the business term (BT-n) and never repeat document content.
- testdata/**/kosit holds official files byte-exact; never edit them. Build modified variants in the test instead.
- Validation rules live in Application/Validation as pure functions with stable codes (RuleCodes); data comes through IValidationDataSource. Every rule needs a passing and a failing test, and every document rule must accept all 66 KoSIT samples.
- Findings never contain free text from the document; IBANs only via the masked Iban.ToString().
- External checks fail closed: an unreachable or misbehaving service gives Unavailable / a human review, never "accepted".
- Tests against the KoSIT validator container carry [Trait("Category", "Kosit")] and need EINVOICE_KOSIT_URL (docker compose up -d kosit-validator).
- Downloads baked into images are pinned by URL and SHA-256 (ADD --checksum); see docker/kosit-validator/README.md.

## Commands
dotnet build --warnaserror
dotnet test --filter "Category!=Database&Category!=Kosit"   # fast, no containers
dotnet test --filter Category=Database      # needs Docker running; Testcontainers starts SQL Server itself
dotnet test --filter Category=Kosit         # needs the kosit-validator service and EINVOICE_KOSIT_URL
docker compose up -d
