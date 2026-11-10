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

## Commands
dotnet build --warnaserror
dotnet test
docker compose up -d
