using EInvoice.Application.Intake;
using EInvoice.Application.Matching;
using EInvoice.Application.Queries;
using EInvoice.Application.Validation;
using EInvoice.Domain.Invoices;
using EInvoice.Domain.Matching;
using EInvoice.Web.Api;
using EInvoice.Web.Components.Shared;
using Microsoft.Extensions.DependencyInjection;
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
        Services.AddMudServices();
        Services.AddSingleton<IInvoiceQueries>(Queries);
        Services.AddSingleton<IInvoiceIntake>(Intake);
        Services.AddSingleton<IInvoiceRematch>(Rematch);
        Services.AddSingleton(new IntakeOptions(MaxBytes));
        Services.AddSingleton<ICurrentUser>(new FixedUser("clerk@test"));
        Services.AddSingleton<Operations>();
        SetRendererInfo(new Microsoft.AspNetCore.Components.RendererInfo("Server", isInteractive: true));
        Render<MudPopoverProvider>();
    }

    protected FakeQueries Queries { get; } = new();

    protected FakeIntake Intake { get; } = new();

    protected FakeRematch Rematch { get; } = new();

    protected string CurrentUri => Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri;

    protected void NavigateTo(string uri) => Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo(uri);

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>MudBlazor services can only be disposed asynchronously; xUnit v2 calls this before the synchronous Dispose.</summary>
    async Task IAsyncLifetime.DisposeAsync() => await DisposeAsync();

    private sealed class FixedUser(string name) : ICurrentUser
    {
        public string Name => name;
    }
}

public sealed class FakeQueries : IInvoiceQueries
{
    public List<InvoiceListQuery> ListCalls { get; } = [];

    public InvoicePage Page { get; set; } = new([], 1, InvoiceListQuery.DefaultPageSize, 0);

    public Dictionary<Guid, InvoiceDetail> Details { get; } = [];

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

/// <summary>Builds invoice views for the tests: a clean, matched two-line invoice unless a test changes it.</summary>
public static class Views
{
    public static readonly Guid Vendor = Guid.Parse("00000000-0000-0000-0001-000000000001");
    public static readonly DateTimeOffset Received = new(2026, 11, 14, 8, 15, 0, TimeSpan.Zero); // 09:15 in Berlin

    public static InvoiceSummary Summary(Guid id, InvoiceStatus status = InvoiceStatus.Matched, string? number = "RE-2026-0001", string? vendor = "Beispiel Bürobedarf GmbH") =>
        new(id, status, number, vendor, new DateOnly(2026, 11, 2), 1234.56m, "EUR", Received);

    public static InvoiceDetail Detail(
        Guid id,
        InvoiceStatus status = InvoiceStatus.Matched,
        IReadOnlyList<FindingView>? findings = null,
        IReadOnlyList<MatchLineView>? matches = null,
        IReadOnlyList<StatusChangeView>? history = null,
        string? vendorName = "Beispiel Bürobedarf GmbH",
        string description = "HDMI cable 2 m") =>
        new(
            id,
            status,
            SourceFormat.XRechnung,
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
}
