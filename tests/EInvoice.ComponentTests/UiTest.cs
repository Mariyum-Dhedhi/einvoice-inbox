using EInvoice.Application.Approval;
using EInvoice.Application.Audit;
using EInvoice.Application.Explanation;
using EInvoice.Application.Intake;
using EInvoice.Application.Kpi;
using EInvoice.Application.Matching;
using EInvoice.Application.Purchasing;
using EInvoice.Application.Queries;
using EInvoice.Application.Security;
using EInvoice.Application.Vendors;
using EInvoice.Application.Validation;
using EInvoice.Domain.Invoices;
using EInvoice.Domain.Matching;
using EInvoice.Web.Api;
using EInvoice.Web.Components.Shared;
using EInvoice.Web.Configuration;
using EInvoice.Web.Security;
using Microsoft.Extensions.DependencyInjection;
using Bunit.TestDoubles;
using MudBlazor;
using MudBlazor.Services;

namespace EInvoice.ComponentTests;

/// <summary>
/// Base for component tests: MudBlazor services, the real <see cref="Operations"/> helper, and fakes for the read side
/// and the two use cases. Pages are rendered with bUnit; no database, no browser.
/// </summary>
public abstract class UiTest : BunitContext, IAsyncLifetime
{
    public const long MaxBytes = 1024;

    protected UiTest()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        UseLanguage("en");
        Services.AddMudServices();
        Services.AddLocalization(o => o.ResourcesPath = "Resources");
        Services.AddSingleton<IInvoiceQueries>(Queries);
        Services.AddSingleton<IInvoiceIntake>(Intake);
        Services.AddSingleton<IInvoiceRematch>(Rematch);
        Services.AddSingleton<IExceptionExplainer>(Explainer);
        Services.AddSingleton(new IntakeOptions(MaxBytes));
        Services.AddSingleton<IUserDirectory>(Users);
        Services.AddSingleton<IInvoiceApproval>(Approval);
        Services.AddSingleton<IAuditLog>(Audit);
        Services.AddSingleton<IKpiQueries>(Kpis);
        Services.AddSingleton<IBankAccountQueries>(BankAccounts);
        Services.AddSingleton<IBankAccountAdministration>(BankAccountAdmin);
        Services.AddSingleton<IPurchasingQueries>(PurchasingQueries);
        Services.AddSingleton<IPurchasing>(Purchasing);
        Services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new ApprovalSettings { DefaultLimitEur = DefaultLimit }));
        Services.AddSingleton<Microsoft.AspNetCore.Components.Forms.AntiforgeryStateProvider>(new FixedAntiforgery());
        Auth = AddAuthorization();
        Auth.SetAuthorized(SignedInUser);
        Auth.SetRoles(Roles.Clerk);
        Auth.SetPolicies(ClerkPolicies);
        Services.AddSingleton<Operations>();
        SetRendererInfo(new Microsoft.AspNetCore.Components.RendererInfo("Server", isInteractive: true));
        Render<MudPopoverProvider>();
    }

    /// <summary>Pages read the actor from the sign-in, as in the app (cascading authentication state).</summary>
    public const string SignedInUser = "clerk@test";

    /// <summary>
    /// What a clerk may do. bUnit does not map roles to policies; that mapping is tested end to end in RoleMatrixTests.
    /// Here a test sets the policies of the role it plays.
    /// </summary>
    public static readonly string[] ClerkPolicies = [Policies.ReadInvoices, Policies.UploadInvoices, Policies.RematchInvoices, Policies.ExplainWithAi, Policies.ReadPurchasing];

    public static readonly string[] ApproverPolicies = [Policies.ReadInvoices, Policies.ExplainWithAi, Policies.DecideInvoices, Policies.ReadPurchasing];

    /// <summary>Records orders and goods receipts; reads no invoices.</summary>
    public static readonly string[] BuyerPolicies = [Policies.ReadPurchasing, Policies.ManagePurchasing];

    /// <summary>The approval limit when the account has none (Approval:DefaultLimitEur).</summary>
    public const decimal DefaultLimit = 5_000m;

    public static readonly string[] AuditorPolicies = [Policies.ReadInvoices, Policies.ReadPurchasing];

    public const string AntiforgeryToken = "test-antiforgery-token";

    protected BunitAuthorizationContext Auth { get; }

    /// <summary>The texts as the pages read them, in the language the test set.</summary>
    protected Microsoft.Extensions.Localization.IStringLocalizer<EInvoice.Web.Texts> T =>
        Services.GetRequiredService<Microsoft.Extensions.Localization.IStringLocalizer<EInvoice.Web.Texts>>();

    protected FakeUserDirectory Users { get; } = new();

    protected FakeApproval Approval { get; } = new();

    protected FakeAuditLog Audit { get; } = new();

    protected FakeKpiQueries Kpis { get; } = new();

    protected FakeBankAccountQueries BankAccounts { get; } = new();

    protected FakeBankAccountAdministration BankAccountAdmin { get; } = new();

    protected FakeQueries Queries { get; } = new();

    protected FakePurchasingQueries PurchasingQueries { get; } = new();

    protected FakePurchasing Purchasing { get; } = new();

    protected FakeIntake Intake { get; } = new();

    protected FakeRematch Rematch { get; } = new();

    protected FakeExplainer Explainer { get; } = new();

    protected string CurrentUri => Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri;

    protected void NavigateTo(string uri) => Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo(uri);

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>
    /// The language the page is rendered in, as the app's request localization would set it ("de" or "en"). English unless
    /// a test says otherwise, whatever the language of the machine running the tests.
    /// </summary>
    public static void UseLanguage(string language)
    {
        var culture = System.Globalization.CultureInfo.GetCultureInfo(language);
        System.Globalization.CultureInfo.CurrentCulture = culture;
        System.Globalization.CultureInfo.CurrentUICulture = culture;
    }

    /// <summary>MudBlazor services can only be disposed asynchronously; xUnit v2 calls this before the synchronous Dispose.</summary>
    async Task IAsyncLifetime.DisposeAsync() => await DisposeAsync();

    internal sealed class FixedAntiforgery : Microsoft.AspNetCore.Components.Forms.AntiforgeryStateProvider
    {
        public override Microsoft.AspNetCore.Components.Forms.AntiforgeryRequestToken GetAntiforgeryToken() =>
            new(AntiforgeryToken, "__RequestVerificationToken");
    }
}

public sealed class FakeBankAccountQueries : IBankAccountQueries
{
    public List<VendorOverview> Vendors { get; } = [];

    public Dictionary<Guid, VendorBankDetails> Details { get; } = [];

    public Task<IReadOnlyList<VendorOverview>> VendorsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<VendorOverview>>(Vendors);

    public Task<VendorBankDetails?> VendorAsync(Guid vendorId, CancellationToken cancellationToken) => Task.FromResult(Details.GetValueOrDefault(vendorId));
}

public sealed class FakeBankAccountAdministration : IBankAccountAdministration
{
    public List<(Guid VendorId, string Iban, string Admin)> Proposed { get; } = [];

    public List<(Guid AccountId, string Admin)> Confirmed { get; } = [];

    public List<(Guid AccountId, string Admin, string? Reason)> Removed { get; } = [];

    public BankAccountResult Result { get; set; } = new(BankAccountOutcome.Proposed, Guid.NewGuid());

    public Task<BankAccountResult> ProposeAsync(Guid vendorId, string iban, string admin, CancellationToken cancellationToken)
    {
        Proposed.Add((vendorId, iban, admin));
        return Task.FromResult(Result);
    }

    public Task<BankAccountResult> ConfirmAsync(Guid accountId, string admin, CancellationToken cancellationToken)
    {
        Confirmed.Add((accountId, admin));
        return Task.FromResult(new BankAccountResult(BankAccountOutcome.Confirmed, accountId));
    }

    public Task<BankAccountResult> RemoveAsync(Guid accountId, string admin, string? reason, CancellationToken cancellationToken)
    {
        Removed.Add((accountId, admin, reason));
        return Task.FromResult(new BankAccountResult(BankAccountOutcome.Removed, accountId));
    }
}

public sealed class FakeKpiQueries : IKpiQueries
{
    public Dashboard Data { get; set; } = new(
        [new StatusKpi(InvoiceStatus.Exception, 3, 1234.50m), new StatusKpi(InvoiceStatus.Matched, 12, 98765.43m)],
        [new MonthKpi(new DateOnly(2026, 10, 1), 10, 7, 3, 0), new MonthKpi(new DateOnly(2026, 11, 1), 0, 0, 0, 0)],
        [new ReasonKpi("Finding", "IBAN_NOT_APPROVED", 2), new ReasonKpi("Match", "NoGoodsReceipt", 1)],
        new PostingKpi(1, 0, 0, 5, new DateTimeOffset(2026, 11, 16, 8, 0, 0, TimeSpan.Zero)));

    public DateOnly? AskedFor { get; private set; }

    public Task<Dashboard> DashboardAsync(DateOnly today, CancellationToken cancellationToken)
    {
        AskedFor = today;
        return Task.FromResult(Data);
    }
}

public sealed class FakeAuditLog : IAuditLog
{
    /// <summary>Newest first, as the page receives them.</summary>
    public List<AuditEventView> Events { get; } = [];

    public List<(int Page, int PageSize)> ListCalls { get; } = [];

    public AuditVerification Verification { get; set; } = new(true, 0, null, null, null, null);

    public Task AppendAsync(AuditDraft draft, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<AuditEventPage> ListAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        ListCalls.Add((page, pageSize));
        return Task.FromResult(new AuditEventPage(Events.Skip((page - 1) * pageSize).Take(pageSize).ToList(), page, pageSize, Events.Count));
    }

    public Task<AuditVerification> VerifyAsync(CancellationToken cancellationToken) => Task.FromResult(Verification);

    public async IAsyncEnumerable<AuditEntry> ReadInOrderAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield break;
    }
}

public sealed class FakeApproval : IInvoiceApproval
{
    public List<(DecisionRequest Request, Approver Approver)> Calls { get; } = [];

    public DecisionResult Result { get; set; } = new(DecisionOutcome.Approved, InvoiceStatus.Approved);

    /// <summary>Called after a decision, so the page shows the invoice as it is now.</summary>
    public Action<Guid>? AfterDecision { get; set; }

    public Task<DecisionResult> DecideAsync(DecisionRequest request, Approver approver, CancellationToken cancellationToken)
    {
        Calls.Add((request, approver));
        AfterDecision?.Invoke(request.InvoiceId);
        return Task.FromResult(Result);
    }
}

public sealed class FakeUserDirectory : IUserDirectory
{
    public List<UserSummary> Accounts { get; } = [];

    public Task<IReadOnlyList<UserSummary>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<UserSummary>>(Accounts);
}

public sealed class FakeQueries : IInvoiceQueries
{
    public List<InvoiceListQuery> ListCalls { get; } = [];

    public InvoicePage Page { get; set; } = new([], 1, InvoiceListQuery.DefaultPageSize, 0);

    public Dictionary<Guid, InvoiceDetail> Details { get; } = [];

    public List<ApprovalQueueItem> Queue { get; } = [];

    public List<(int Page, int PageSize)> QueueCalls { get; } = [];

    public Task<ApprovalQueuePage> ApprovalQueueAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        QueueCalls.Add((page, pageSize));
        return Task.FromResult(new ApprovalQueuePage(Queue.Skip((page - 1) * pageSize).Take(pageSize).ToList(), page, pageSize, Queue.Count));
    }

    public Task<InvoicePage> ListAsync(InvoiceListQuery query, CancellationToken cancellationToken)
    {
        ListCalls.Add(query);
        return Task.FromResult(Page with { Page = query.Page, PageSize = query.PageSize });
    }

    public Task<InvoiceDetail?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(Details.GetValueOrDefault(id));

    public Task<StoredDocument?> GetDocumentAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<StoredDocument?>(null);
}

public sealed class FakeIntake : IInvoiceIntake
{
    public List<(UploadedFile File, string SubmittedBy)> Calls { get; } = [];

    public IntakeResult Result { get; set; } = new(IntakeOutcome.Created, Guid.NewGuid(), InvoiceStatus.Matched);

    public Task<IntakeResult> SubmitAsync(UploadedFile file, string submittedBy, CancellationToken cancellationToken)
    {
        Calls.Add((file, submittedBy));
        return Task.FromResult(Result);
    }
}

public sealed class FakeRematch : IInvoiceRematch
{
    public List<(Guid Id, string Actor)> Calls { get; } = [];

    public RematchResult Result { get; set; } = new(RematchOutcome.Matched, InvoiceStatus.Matched, []);

    /// <summary>Called after a re-match, so the page shows the invoice as it is now.</summary>
    public Action<Guid>? AfterRematch { get; set; }

    public Task<RematchResult> RematchAsync(Guid invoiceId, string actor, CancellationToken cancellationToken)
    {
        Calls.Add((invoiceId, actor));
        AfterRematch?.Invoke(invoiceId);
        return Task.FromResult(Result);
    }
}

public sealed class FakeExplainer : IExceptionExplainer
{
    public bool UsesAi { get; set; } = true;

    public int Calls { get; private set; }

    public Explanation Result { get; set; } = new("Line 1 was billed 2.5 % above the order price.", "Ask the supplier for a credit note.", ExplanationSource.Ai);

    public Task<Explanation> ExplainAsync(InvoiceDetail invoice, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(Result);
    }
}

/// <summary>Builds invoice views for the tests: a clean, matched two-line invoice unless a test changes it.</summary>
public static class Views
{
    public static readonly Guid Vendor = Guid.Parse("00000000-0000-0000-0001-000000000001");
    public static readonly DateTimeOffset Received = new(2026, 11, 14, 8, 15, 0, TimeSpan.Zero); // 09:15 in Berlin

    public static InvoiceSummary Summary(Guid id, InvoiceStatus status = InvoiceStatus.Matched, string? number = "RE-2026-0001", string? vendor = "Beispiel Bürobedarf GmbH", SourceFormat format = SourceFormat.XRechnung) =>
        new(id, status, format, number, vendor, new DateOnly(2026, 11, 2), 1234.56m, "EUR", Received);

    public static InvoiceDetail Detail(
        Guid id,
        InvoiceStatus status = InvoiceStatus.Matched,
        IReadOnlyList<FindingView>? findings = null,
        IReadOnlyList<MatchLineView>? matches = null,
        IReadOnlyList<StatusChangeView>? history = null,
        string? vendorName = "Beispiel Bürobedarf GmbH",
        string description = "HDMI cable 2 m",
        SourceFormat format = SourceFormat.XRechnung) =>
        new(
            id,
            status,
            format,
            "clerk@test",
            "RE-2026-0001",
            vendorName is null ? null : Vendor,
            vendorName,
            new DateOnly(2026, 11, 2),
            "PO-0042",
            "EUR",
            1037.40m,
            197.16m,
            1234.56m,
            [
                new InvoiceLineView(1, description, 10m, 7.4125m, 74.13m, 19m, 1),
                new InvoiceLineView(2, "Handbook German commercial code", 4m, 39.90m, 159.60m, 7m, 2),
            ],
            history ??
            [
                new StatusChangeView(1, null, InvoiceStatus.Received, "clerk@test", Received, null),
                new StatusChangeView(2, InvoiceStatus.Received, InvoiceStatus.Parsed, "system", Received.AddSeconds(1), null),
                new StatusChangeView(3, InvoiceStatus.Parsed, InvoiceStatus.Validated, "system", Received.AddSeconds(2), null),
                new StatusChangeView(4, InvoiceStatus.Validated, status, "system", Received.AddSeconds(3), status == InvoiceStatus.Exception ? "NoGoodsReceipt" : null),
            ],
            findings ?? [],
            matches ??
            [
                Match(1, MatchOutcome.FullMatch),
                Match(2, MatchOutcome.FullMatch),
            ],
            new DocumentInfo("Rechnung 1.xml", "application/xml", 5120, Received));

    public static MatchLineView Match(int line, MatchOutcome outcome, decimal invoicePrice = 7.4125m, decimal? orderPrice = 7.4125m, decimal? variancePercent = 0m, decimal quantity = 10m, decimal? available = 10m) =>
        new(line, line, outcome, invoicePrice, orderPrice, variancePercent, orderPrice is null ? null : (invoicePrice - orderPrice) * quantity,
            quantity, available, available is null ? null : quantity - available, 2.00m, Received.AddSeconds(3));

    public static FindingView Finding(string code, FindingSeverity severity, string message) => new(code, severity, message, null);

    public const string Version = "AAAAAAAAB9E=";

    public static ApprovalQueueItem QueueItem(
        string number, InvoiceStatus status = InvoiceStatus.Matched, decimal gross = 1234.56m, string currency = "EUR",
        string submittedBy = "clerk@test", bool blocking = false) =>
        new(Guid.NewGuid(), status, number, "Beispiel Bürobedarf GmbH", gross, currency, submittedBy, Received, blocking);
}

public sealed class FakePurchasingQueries : IPurchasingQueries
{
    public PurchaseOrderList Page { get; set; } = new([], 1, PurchaseOrderListQuery.DefaultPageSize, 0);

    public List<PurchaseOrderListQuery> ListCalls { get; } = [];

    public Dictionary<Guid, PurchaseOrderDetail> Details { get; } = [];

    public List<(Guid Id, bool WithInvoices)> DetailCalls { get; } = [];

    public List<VendorChoice> Vendors { get; } = [];

    public int VendorCalls { get; private set; }

    public Task<PurchaseOrderList> ListAsync(PurchaseOrderListQuery query, CancellationToken cancellationToken)
    {
        ListCalls.Add(query);
        return Task.FromResult(Page);
    }

    public Task<PurchaseOrderDetail?> GetAsync(Guid id, bool withInvoices, CancellationToken cancellationToken)
    {
        DetailCalls.Add((id, withInvoices));
        return Task.FromResult(Details.GetValueOrDefault(id));
    }

    public Task<IReadOnlyList<VendorChoice>> ActiveVendorsAsync(CancellationToken cancellationToken)
    {
        VendorCalls++;
        return Task.FromResult<IReadOnlyList<VendorChoice>>(Vendors);
    }
}

public sealed class FakePurchasing : IPurchasing
{
    public List<(OrderDraft Draft, string Buyer)> Orders { get; } = [];

    public List<(Guid OrderId, ReceiptDraft Draft, string Buyer)> Receipts { get; } = [];

    public Guid CreatedId { get; } = Guid.NewGuid();

    /// <summary>Set to answer with a refusal; otherwise every call succeeds.</summary>
    public PurchasingResult? Result { get; set; }

    public Task<PurchasingResult> CreateOrderAsync(OrderDraft draft, string buyer, CancellationToken cancellationToken)
    {
        Orders.Add((draft, buyer));
        return Task.FromResult(Result ?? new PurchasingResult(PurchasingOutcome.OrderCreated, CreatedId));
    }

    public Task<PurchasingResult> RecordReceiptAsync(Guid orderId, ReceiptDraft draft, string buyer, CancellationToken cancellationToken)
    {
        Receipts.Add((orderId, draft, buyer));
        return Task.FromResult(Result ?? new PurchasingResult(PurchasingOutcome.ReceiptRecorded, Guid.NewGuid()));
    }
}
