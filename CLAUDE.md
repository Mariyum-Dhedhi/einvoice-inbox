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
- The database runs with READ_COMMITTED_SNAPSHOT (db/database.sql, run by db-init and SqlServerFixture): reads never wait for writers (LockingTests). Explicit locks (app locks, UPDLOCK/READPAST) still apply.
- Query plan tests (tests/.../Performance/QueryPlanTests) add 5,000 invoices in their rolled-back transaction and run WITH RECOMPILE: plans on six seeded rows say nothing.
- Load test (tests/load, k6 in the pinned grafana/k6 image) runs only locally against the separate database EInvoiceLoad (db-init with EINVOICE_DB). Performance numbers in docs/performance.md and the README come only from runs saved in tests/load/results, quoted with machine, date and commit.
- Every response gets SecurityHeaders (one strict CSP: script-src 'self', no inline scripts, so no <ImportMap />; isolation, permissions and referrer policies), pinned by SecurityHeaderTests. Antiforgery cookie __Host-einvoice-antiforgery (Secure, SameSite=Strict). HSTS a year outside Development only.
- Browser journeys run over HTTPS (Kestrel with the development certificate; CI runs dotnet dev-certs https).
- ZAP (tests/zap, CI job zap): baseline scans public and signed in as the clerk. Any alert not in tests/zap/rules.tsv with a reason fails; add an IGNORE only with the reason or the test that pins it.
- Mutation testing: dotnet stryker (local tool, tests/EInvoice.UnitTests/stryker-config.json), run by hand, never in CI. Summaries in tests/mutation/results (tests/mutation/summarize.py); quote scores only from there, with date and config. A surviving mutant that matters gets a test; an equivalent one is noted, not pinned.
- Stored procedures live in db/procedures as CREATE OR ALTER scripts, deployed by db-init after the migrations.
- Uploads: file type comes from the bytes (ContentSniffer), never the name; file names are display-only (UploadNames.Sanitize). Duplicates are checked first and backed by unique indexes for races.
- Every accepted upload becomes an invoice: Matched, Exception (with findings or match reasons) or Failed (unreadable, original kept). Refusals (empty, too large, wrong type, duplicate) store nothing.
- Values longer than a database column are refused with a reason before saving, never truncated where they identify something (invoice number, order reference).
- API errors are problem details (application/problem+json) with a safe detail: no exception text, no stack trace. Refusals carry an "outcome" name.
- Settings are bound to options classes and validated at startup (ValidateOnStart); a wrong value stops the app. Connection strings come from user secrets or environment variables.
- Every response sends X-Content-Type-Options: nosniff; API responses are no-store. Stored files are only served as attachments.
- OpenAPI (/openapi/v1.json) is mapped in Development only.
- Sign-in is ASP.NET Core Identity with the cookie __Host-einvoice (HttpOnly, Secure, SameSite=Lax, 8 h, not sliding). Every endpoint requires sign-in (fallback policy); only the login page, static files and error pages are [AllowAnonymous]. The API answers 401/403 as problem details, never a redirect.
- Roles (Application/Security/Roles): Clerk, Approver, Auditor, Admin, Buyer. Separated duties: the uploader cannot approve, the auditor only reads, the admin and the buyer never touch invoices. A new role needs its Ids entry in IdentityConfigurations (seeded by migration), a Role.* text, and a column in PolicyTableTests and AccessTable.Callers.
- Endpoints and pages name a policy (Web/Security/Policies), never a role list; every one declares its access ([Authorize(Policy = ...)] or [AllowAnonymous]). A new endpoint must be added to tests/.../Api/AccessTable.cs (hand-written, independent of Policies) or EndpointInventoryTests fails; RoleMatrixTests calls each one as every role, no role and nobody.
- Wrong role: API 403 problem details; pages go to /account/access-denied. Buttons appear only for roles that may use them, and every action checks its policy again on the server (MayAsync).
- Approval (Application/Approval): only the Approver role decides (DecideInvoices); Matched and Exception both wait for a person. Nobody decides their own upload (user names compared ignoring case). Blocking findings allow only rejection. Approving needs the gross amount in EUR within the approver's limit: the account claim approval_limit_eur, else Approval:DefaultLimitEur; an unreadable or doubled claim is 0 (ApproverOf). Reasons: required to reject and to approve an Exception; max 200 characters, refused not cut.
- ApprovalRules is the one place for the approval checks: InvoiceApproval enforces them, the /approvals queue and the decision panel show them in advance. Never re-implement a rule in a page.
- Invoice has a rowversion (shadow property "Version"). The detail returns it as an opaque string; a decision sends it back and is saved only against it (EfApprovalStore sets it as the original value): the second of two simultaneous decisions gets 409 Conflict. Stores turn DbUpdateConcurrencyException into ConcurrentStatusChangeException.
- Audit trail (Application/Audit, Infrastructure/Audit): AuditEvent rows form a SHA-256 hash chain (AuditHash: every field length-prefixed, time in UTC, plus the previous hash). Append only through AuditChain (exclusive sp_getapplock in the transaction, unique Sequence). EInvoiceDbContext.SaveChangesAsync writes one event per new invoice status change in the same transaction; synchronous SaveChanges refuses status changes. The app user has DENY UPDATE, DELETE on dbo.AuditEvent.
- AuditActions codes are part of every hash: never rename one. Audit details never contain document content or a password; a failed sign-in is recorded under the name as typed.
- AuditVerifier names the first event that is missing (SequenceGap), unlinked (ChainBroken) or changed (HashMismatch). A chain cannot show that its newest events were cut off: the /audit page says to note down count and latest hash.
- Tests that act like a database administrator (editing audit rows) use SqlServerFixture.CreateAdminContext inside a rolled-back transaction, so the shared chain stays intact.
- ERP posting (Application/Posting, Infrastructure/Posting): every new Approved status change gets a PostingJob in the same save (transactional outbox, EInvoiceDbContext hook). PostingWorker (only when Erp:Enabled) claims due jobs with a lease (UPDLOCK, READPAST) and posts with the invoice id as Idempotency-Key. Posted: invoice Posted by "erp-connector" with the document number. 408/5xx/timeout/unreachable/unreadable answer: retry after RetryPolicy's wait (default 30 s, 2 min, 10 min, 30 min, 1 h: six tries). 4xx, or the last try failed: invoice Failed with the reason, job Dead.
- A busy ERP (package 16a): 429 is Throttled and uses up no try. Retry-After (429 and 503; seconds or HTTP date, a past date ignored) replaces the policy's wait, clamped to 1 s to 1 h in InvoicePosting (the connector passes it uncapped). At most Erp:MaxConcurrentCalls (4) calls in flight; store calls stay one at a time (one DbContext). ErpCircuitBreaker is a singleton: Erp:BreakerFailures (5) temporary failures in a row (429 included; a 4xx is an answer) open it for Erp:BreakerOpenSeconds (120), then one trial call. Jobs it holds back are released (ReleaseAsync) with tries unchanged. IPostingLog logs throttling and breaker changes with the job id only.
- ERP sign-in (package 16b, Infrastructure/Posting/ErpTokens.cs): OAuth 2.0 client credentials. ErpTokenSource (singleton) keeps one token and renews it 60 s before expiry, one renewal at a time; RenewAsync(rejected) renews once however many calls report the same token. ErpAuthHandler on the "erp" client sets the Bearer token; a 401 renews once and resends once; a second 401 is Rejected "ERP refused the credentials." (HttpErpConnector.CredentialsRefused), and so is a token endpoint answering 400/401. Token endpoint down or nonsense: Transient "Could not get a token from the ERP.". Erp:ClientId and Erp:ClientSecret are required when posting is on (user secrets or environment variables; ERP_CLIENT_SECRET in .env for compose); messages never repeat a value. Secret and tokens never in logs, errors, reasons or audit (ErpCredentialLeakTests captures every log at Trace).
- Mock ERP auth: always on. Auth:ClientId/Auth:ClientSecret (required; it refuses to start without a secret)/Auth:TokenLifetimeSeconds; POST oauth/token; data calls need its unexpired Bearer token. POST _mock/auth {expireAll, rejectAll, tokenEndpointDown}, GET _mock/tokens. In tests MockErpFactory.Client() goes through the real ErpAuthHandler; AnonymousClient() for control calls.
- Mock ERP chaos mode (Chaos:* or POST _mock/chaos, off by default): a share of postings gets throttle, error, unavailable, slow or malformed (booked, answer broken); GET _mock/stats counts requests, most in flight and chaos by kind.
- KPIs (db/views, Application/Kpi, /dashboard for invoice readers): four hand-written views (CREATE OR ALTER, deployed by db-init and SqlServerFixture after the procedures), specified by KpiViewTests. Counts and sums are 0, never NULL; months are Berlin months of receipt; "straight through" means approved and never an Exception. Views have no order: EfKpiQueries orders them and fills the twelve months with zeros. A view reads only; a new view column means a change in the view, its test and the query record together.
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
- Public demo (Web/Security/Demo.cs, Demo:Enabled, off by default, meant for Production behind the tunnel): POST /account/demo (always mapped, 404 when off, antiforgery) makes a NEW account per click, "demo-<role>-<8 chars>@example.com", one role from DemoSignIn.Roles, no password; visitors never share an account (sign-out renews the stamp). Banner on every page. Limits: sign-in POSTs (password form and demo buttons) per address and hour (rate limiter), uploads per person and sliding hour (UploadLimit decorator, UploadCounter singleton). Not the Development demo users: DemoUsers:Password stays Development-only.
- Public stack (docs/self-hosting.md, compose profile "public"): the app (docker/web/Dockerfile, chiseled-extra for ICU, user 1654, read-only, no caps) shares the network of a pinned tailscale container (Funnel, docker/tailscale/serve.json); nothing of it is published on the PC. Tunnel:Enabled believes X-Forwarded-For/Proto from loopback only, last hop only (Configuration/Hosting.cs). Plain HTTP from anywhere else is not HTTPS, so antiforgery refuses it: check the stack from inside the tunnel's network (tests/public-stack/check.sh, CI job public-stack). DataProtection:KeysPath (absolute) keeps sign-in keys in a volume.
- Nightly reset (db/demo, service demo-reset in the public profile): reset.sh drops the database (ROLLBACK IMMEDIATE) and runs db/init.sh, so there is no table list to maintain; nightly.sh waits for RESET_AT (03:00) in Europe/Berlin, checking the clock every minute. DemoResetTests runs the real scripts in a SQL Server container of its own and compares row counts of every table with a fresh build. Never run the reset against the shared test database.
- API tests sign in with ApiFactory.CreateApiClient(user, roles) through a header scheme that only the test project has; CreateApiClient(user: null) is anonymous and uses the real cookie. Real sign-in is tested in SignInTests against the database.
- Re-match only clears match problems: an invoice in Exception with validation findings stays for a human.
- Committed test invoices for the same vendor need distinct amounts or dates, or the POSSIBLE_DUPLICATE rule (correctly) flags invoices in other tests.
- UI pages run operations through Operations (one DI scope per operation): never inject DbContext-backed services straight into a Blazor Server component.
- UI texts live in src/EInvoice.Web/Resources/Texts.resx (English) and Texts.de.resx (German); pages read them as T["Key"], helpers as t["Key"]. Both files get every key (TextResourcesTests); no page writes words of its own (HardCodedTextTests). Codes get words by key: Status.*, Outcome.*, Severity.*, Rule.<CODE>.Title/.Step, Intake.*, Decision.*, Rematch.*, BankAccount.*, AuditAction.*, Role.*: a new enum value, rule code, audit action or role needs both texts.
- Language: cookie __Host-einvoice-culture (set only by POST /account/language with a token), else Accept-Language, else German (Languages). Formats stay German in both languages. Recorded English (finding messages, failure reasons, AI explanations) is shown as recorded with lang="en". The API is not translated.
- Test clients from ApiFactory.CreateApiClient send Accept-Language: en; browser journeys use the en-US locale unless they test the language.
- UI shows status and match outcome as text + icon (Labels), never colour alone. Amounts and dates use Formats (German formats, Berlin time).
- No third-party web fonts or CDNs in the UI (Google Fonts sends visitor IPs to Google); system fonts and packaged assets only.
- Filter and page state lives in the address (query string) so every view can be linked; address values are user input and are validated.
- Component tests use bUnit (tests/EInvoice.ComponentTests). Browser tests use Playwright with [Trait("Category", "E2E")]: the five journeys in tests/.../E2E/Journeys.cs (base class BrowserJourney) are package 12's "done when" and run in CI.
- README screenshot (docs/images/review-match.png) comes only from the journey Readme_screenshot_an_approver_reviews_a_price_variance: run it with EINVOICE_SCREENSHOT_DIR=docs/images and commit the file; CI puts its copy in the e2e-test-results artifact. ReadmeTests checks the image, the Progress checklist and every relative link.
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
- Purchasing (Application/Purchasing, Infrastructure/Purchasing, /purchasing pages): orders and goods receipts are ADD ONLY (nothing recorded is edited), by the Buyer (ManagePurchasing); invoice readers see them (ReadPurchasing). Each addition is saved with its audit event (PurchaseOrderCreated, GoodsReceiptRecorded); a refusal stores nothing and names the line (PurchasingResult.Position). Receipts never exceed what was ordered. The buyer's order page does not load invoices (GetAsync withInvoices: false).
- Numbers typed into forms are German (Formats.TryParseNumber: digits and one decimal comma); a point or thousands separator is refused, never guessed.

## Commands
dotnet build --warnaserror
dotnet test --filter "Category!=Database&Category!=Kosit&Category!=E2E&Category!=Ollama"   # fast, no containers
dotnet test --filter Category=Database      # needs Docker running; Testcontainers starts SQL Server itself
dotnet test --filter Category=Kosit         # needs the kosit-validator service and EINVOICE_KOSIT_URL
dotnet test --filter Category=E2E           # needs Docker and Chromium (once: powershell tests/EInvoice.IntegrationTests/bin/Debug/net10.0/playwright.ps1 install chromium)
dotnet test --filter Category=Ollama        # real model, local only: EINVOICE_OLLAMA_URL=http://127.0.0.1:11434/ EINVOICE_OLLAMA_MODEL=qwen2.5:7b
dotnet run --project tests/EInvoice.AiEvaluation -- run --provider ollama --model qwen2.5:7b   # AI evaluation (Docker + Ollama)
docker compose up -d
