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
- Sign-in is ASP.NET Core Identity with the cookie __Host-einvoice (HttpOnly, Secure, SameSite=Lax, 8 h, not sliding). Every endpoint requires sign-in (fallback policy); only the login page, static files and error pages are [AllowAnonymous]. The API answers 401/403 as problem details, never a redirect.
- Roles (Application/Security/Roles): Clerk, Approver, Auditor, Admin. Separated duties: the uploader cannot approve, the auditor only reads, the admin never touches invoices.
- Endpoints and pages name a policy (Web/Security/Policies), never a role list; every one declares its access ([Authorize(Policy = ...)] or [AllowAnonymous]). A new endpoint must be added to tests/.../Api/AccessTable.cs (hand-written, independent of Policies) or EndpointInventoryTests fails; RoleMatrixTests calls each one as every role, no role and nobody.
- Wrong role: API 403 problem details; pages go to /account/access-denied. Buttons appear only for roles that may use them, and every action checks its policy again on the server (MayAsync).
- Approval (Application/Approval): only the Approver role decides (DecideInvoices); Matched and Exception both wait for a person. Nobody decides their own upload (user names compared ignoring case). Blocking findings allow only rejection. Approving needs the gross amount in EUR within the approver's limit: the account claim approval_limit_eur, else Approval:DefaultLimitEur; an unreadable or doubled claim is 0 (ApproverOf). Reasons: required to reject and to approve an Exception; max 200 characters, refused not cut.
- ApprovalRules is the one place for the approval checks: InvoiceApproval enforces them, the /approvals queue and the decision panel show them in advance. Never re-implement a rule in a page.
- Invoice has a rowversion (shadow property "Version"). The detail returns it as an opaque string; a decision sends it back and is saved only against it (EfApprovalStore sets it as the original value): the second of two simultaneous decisions gets 409 Conflict. Stores turn DbUpdateConcurrencyException into ConcurrentStatusChangeException.
- Audit trail (Application/Audit, Infrastructure/Audit): AuditEvent rows form a SHA-256 hash chain (AuditHash: every field length-prefixed, time in UTC, plus the previous hash). Append only through AuditChain (exclusive sp_getapplock in the transaction, unique Sequence). EInvoiceDbContext.SaveChangesAsync writes one event per new invoice status change in the same transaction; synchronous SaveChanges refuses status changes. The app user has DENY UPDATE, DELETE on dbo.AuditEvent.
- AuditActions codes are part of every hash: never rename one. Audit details never contain document content or a password; a failed sign-in is recorded under the name as typed.
- AuditVerifier names the first event that is missing (SequenceGap), unlinked (ChainBroken) or changed (HashMismatch). A chain cannot show that its newest events were cut off: the /audit page says to note down count and latest hash.
- Tests that act like a database administrator (editing audit rows) use SqlServerFixture.CreateAdminContext inside a rolled-back transaction, so the shared chain stays intact.
- ERP posting (Application/Posting, Infrastructure/Posting): every new Approved status change gets a PostingJob in the same save (transactional outbox, EInvoiceDbContext hook). PostingWorker (only when Erp:Enabled) claims due jobs with a lease (UPDLOCK, READPAST) and posts with the invoice id as Idempotency-Key. Posted: invoice Posted by "erp-connector" with the document number. 408/429/5xx/timeout/unreachable: retry after RetryPolicy's wait (default 30 s, 2 min, 10 min, 30 min, 1 h: six tries). 4xx, or the last try failed: invoice Failed with the reason, job Dead.
- The ERP contract is contracts/d365/vendor-invoice.example.json, tested from both sides: HttpErpConnector (strict JSON: no unknown, no missing fields) and the mock ERP tests/EInvoice.MockErp, which references none of our projects. Change the contract only together with both. The mock's _mock/failures plans failures per invoice number, so tests sharing it do not use up each other's failures.
- Bank accounts (two-person rule): VendorBankAccount.Propose makes it Pending; Confirm by a different person (ignoring case) makes it Active; only Active accounts reach validation. Removal needs one admin and a reason. The column default is Pending (fail closed); seed.sql inserts its accounts as Active.
- VendorBankAccount is a SQL Server temporal table (history in VendorBankAccountHistory, periods ValidFrom/ValidTo); the history cannot be edited while versioning is on. It has a rowversion: two admins changing the same account get a conflict.
- Audit events for non-invoice changes are queued with EInvoiceDbContext.QueueAudit and written by the next SaveChangesAsync in its transaction. A store that clears the change tracker after a failed save also calls ClearQueuedAudit.
- The admin bank page shows the full IBAN (to compare with a trusted source); history, audit details and findings show only the masked Iban.ToString().
- No [Consumes] on endpoints: it is checked before authorization and answers 415 to callers who should get 403. Actions check the content type themselves.
- Every failed sign-in gets the same message (unknown user, wrong password, locked). Lockout: 5 failures, 15 minutes. Return addresses are local only (SignInSetup.SafeReturnUrl).
- The security stamp is checked on every request (ValidationInterval zero) and open pages re-check every minute (SignInRevalidator). Sign-out is a POST with an antiforgery token and renews the stamp, so every copy of the cookie stops working.
- The login page is static server rendering ([ExcludeFromInteractiveRouting]); only a normal HTTP response can set the cookie. The password field is never bound for output.
- The actor recorded on invoices is the signed-in user name (SignedInUser.Name); there is no placeholder user.
- Demo users (clerk@, approver@, auditor@, admin@example.com) exist only in Development, only when DemoUsers:Password is set in user secrets; set outside Development, the app does not start.
- API tests sign in with ApiFactory.CreateApiClient(user, roles) through a header scheme that only the test project has; CreateApiClient(user: null) is anonymous and uses the real cookie. Real sign-in is tested in SignInTests against the database.
- Re-match only clears match problems: an invoice in Exception with validation findings stays for a human.
- Committed test invoices for the same vendor need distinct amounts or dates, or the POSSIBLE_DUPLICATE rule (correctly) flags invoices in other tests.
- UI pages run operations through Operations (one DI scope per operation): never inject DbContext-backed services straight into a Blazor Server component.
- UI shows status and match outcome as text + icon (Labels), never colour alone. Amounts and dates use Formats (German formats, Berlin time).
- No third-party web fonts or CDNs in the UI (Google Fonts sends visitor IPs to Google); system fonts and packaged assets only.
- Filter and page state lives in the address (query string) so every view can be linked; address values are user input and are validated.
- Component tests use bUnit (tests/EInvoice.ComponentTests). Browser tests use Playwright with [Trait("Category", "E2E")]: the five journeys in tests/.../E2E/Journeys.cs (base class BrowserJourney) are package 12's "done when" and run in CI.
- Journeys sign in through the real login form with their own accounts (TestUsers), use their own data (own orders, own vendor; never change seeded rows), and fail on any browser console error. Before clicking, wait for the layout's hidden [data-testid=app-ready] marker (shown after the first interactive render); after changing a page, wait for something the new page shows, not only the address.
- AI extraction (Application/Extraction, Infrastructure/Extraction): the model only reads. It gets visible PDF text (hidden text is left out and reported), masked (IBAN, e-mail, phone, labelled contact names), fenced with a random marker, and no tools.
- A model reply is untrusted input: ModelOutput accepts exactly the schema (one extra property rejects everything) and nothing is repaired. Error messages never repeat document or model text.
- Field confidence is computed by EvidenceChecker (value found in the visible text as a whole token + arithmetic agrees); a model's own confidence is never used. IBANs enter a document only via placeholders masked out of that same document.
- Extracted invoices become the same ParsedDocument as XML and go through the same rules and matcher; the VAT breakdown is recomputed from the lines.
- FakeInvoiceModel is for tests and CI plumbing only; numbers from it are never reported as model accuracy (the evaluation in package 13c uses a real model).
- Test PDFs are built in code (SyntheticPdf), never downloaded or taken from real suppliers.
- ZUGFeRD / Factur-X: a PDF with an embedded invoice (only factur-x.xml, zugferd-invoice.xml or xrechnung.xml, exactly one; PdfEmbeddedInvoiceReader) is SourceFormat.Zugferd. Its XML is the invoice: parsed like any XML (SafeXml), never sent to AI, so it works with AI off. Profiles (BT-24, ZugferdProfiles): BASIC, EN 16931, EXTENDED, XRECHNUNG accepted; MINIMUM, BASIC WL and unknown are refused (nothing stored). Only XRECHNUNG goes to KoSIT (the container knows XRechnung only); the others get the Review finding CONFORMANCE_NOT_CHECKED. Ambiguous, too large or unreadable embedded XML: Failed with the reason. The original PDF is stored.
- Test ZUGFeRD PDFs are written by tests/.../TestData/ZugferdPdf (byte by byte, real xref table); their XML comes from CiiInvoiceBuilder, built from the official KoSIT CII sample.
- PDFs are read by AI only when Ai:Enabled is true (off by default). Off: PDF refused (415). Model down: 503, nothing stored, the same file can be sent again. Unreadable/invalid reply: stored as Failed.
- AI-read invoices have SourceFormat.Pdf and get Review findings: EXTRACTION_UNCONFIRMED per field without evidence (names the BT, never the value) and HIDDEN_TEXT. They skip the KoSIT check (no XML) but get every document rule.
- Never write "flag ? null : bytes" for a ReadOnlyMemory<byte>?: a null byte[] becomes an EMPTY memory, not "no value".
- Ollama runs natively (GPU), bound to 127.0.0.1:11434; model qwen2.5:7b. Real-model tests ([Trait("Category", "Ollama")]) are never in CI and never reported as accuracy.
- AI evaluation: testdata/ai-eval/cases.json is the answer key (approved case by case); the PDFs are generated from it and never edited (EvalPdfTests compares bytes). Change cases.json, then run the generate command.
- The oracle run (OracleRunTests, CI) must reach all expected answers; a failure there means the answer key or the scoring is wrong, not a model.
- README AI numbers come only from full real-model runs saved in testdata/ai-eval/results/, quoted with model, digest, Ollama version, date and the number of runs.
- gitattributes has no {a,b} brace patterns: one line per extension.
- Exception explanations (Application/Explanation): RuleBasedExplainer covers every match outcome and every RuleCodes constant; a new rule code needs a step there (a test reads RuleCodes).
- The AI explainer sees ExplanationFacts only (codes, numbers, rule messages; never invoice number, item names or other supplier text). Any number in its reply that is not in the facts discards the reply (rule-based text shown, with the reason). It is a suggestion; a person decides.
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
dotnet test --filter "Category!=Database&Category!=Kosit&Category!=E2E&Category!=Ollama"   # fast, no containers
dotnet test --filter Category=Database      # needs Docker running; Testcontainers starts SQL Server itself
dotnet test --filter Category=Kosit         # needs the kosit-validator service and EINVOICE_KOSIT_URL
dotnet test --filter Category=E2E           # needs Docker and Chromium (once: powershell tests/EInvoice.IntegrationTests/bin/Debug/net10.0/playwright.ps1 install chromium)
dotnet test --filter Category=Ollama        # real model, local only: EINVOICE_OLLAMA_URL=http://127.0.0.1:11434/ EINVOICE_OLLAMA_MODEL=qwen2.5:7b
dotnet run --project tests/EInvoice.AiEvaluation -- run --provider ollama --model qwen2.5:7b   # AI evaluation (Docker + Ollama)
docker compose up -d
