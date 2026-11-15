using EInvoice.Application.Approval;
using EInvoice.Application.Matching;
using EInvoice.Domain.Invoices;
using EInvoice.Domain.Shared;
using EInvoice.UnitTests.Domain;

namespace EInvoice.UnitTests.Approval;

/// <summary>
/// The approval rules, without a database: who may decide, which invoices can be approved, limits, reasons and
/// versions. Every refusal leaves the invoice unchanged and saves nothing.
/// </summary>
public sealed class InvoiceApprovalTests
{
    private const string Clerk = "clerk@test";
    private const string CurrentVersion = "AAAAAAAAB9E=";
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);
    private static readonly Approver Approver = new("approver@test", LimitEur: 1_000m);

    private readonly FakeApprovalStore _store = new();

    private InvoiceApproval Approval() => new(_store, new FixedTime(Now));

    private static Invoice Matched(string submittedBy = Clerk, Money? gross = null) =>
        Walk(submittedBy, gross, InvoiceStatus.Parsed, InvoiceStatus.Validated, InvoiceStatus.Matched);

    private static Invoice InException(string submittedBy = Clerk, Money? gross = null) =>
        Walk(submittedBy, gross, InvoiceStatus.Parsed, InvoiceStatus.Exception);

    private static Invoice Walk(string submittedBy, Money? gross, params InvoiceStatus[] path)
    {
        var invoice = TestInvoices.Received(submittedBy);
        var content = TestInvoices.SampleContent();
        if (gross is { } g)
        {
            // One line, 19 % VAT, everything in the gross amount's currency: a consistent invoice of that size.
            var net = new Money(Math.Round(g.Amount / 1.19m, 2), g.Currency);
            var line = new InvoiceLine(1, "Office chair", 1m, net.Amount, net, 19m, purchaseOrderLineNumber: 1);
            content = content with { NetTotal = net, TaxTotal = new Money(g.Amount - net.Amount, g.Currency), GrossTotal = g, Lines = [line] };
        }

        invoice.MarkParsed(content, "system", TestInvoices.Now.AddMinutes(1));
        foreach (var step in path.Skip(1))
        {
            invoice.TransitionTo(step, "system", TestInvoices.Now.AddMinutes(2));
        }

        return invoice;
    }

    private Task<DecisionResult> Decide(Invoice invoice, Decision decision, string? reason = null, string version = CurrentVersion, Approver? approver = null)
    {
        _store.Invoice = invoice;
        _store.Version = CurrentVersion;
        return Approval().DecideAsync(new DecisionRequest(invoice.Id, decision, version, reason), approver ?? Approver, CancellationToken.None);
    }

    // ---- Approving ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_clean_match_within_the_limit_is_approved_without_a_reason()
    {
        var invoice = Matched();

        var result = await Decide(invoice, Decision.Approve);

        Assert.Equal(new DecisionResult(DecisionOutcome.Approved, InvoiceStatus.Approved), result);
        var last = invoice.StatusHistory[^1];
        Assert.Equal((InvoiceStatus.Matched, InvoiceStatus.Approved, Approver.Name, Now, (string?)null), (last.From!.Value, last.To, last.Actor, last.At, last.Reason));
        Assert.Equal(CurrentVersion, _store.SavedWithVersion); // saved only if nobody changed it since the approver looked
    }

    [Fact]
    public async Task An_exception_needs_a_reason_to_be_approved()
    {
        var invoice = InException();

        var refused = await Decide(invoice, Decision.Approve, reason: "  ");

        Assert.Equal(DecisionOutcome.ReasonRequired, refused.Outcome);
        Assert.Equal(InvoiceStatus.Exception, invoice.Status);
        Assert.Null(_store.SavedWithVersion);
    }

    [Fact]
    public async Task An_exception_approved_with_a_reason_keeps_the_reason_in_the_history()
    {
        var invoice = InException();

        var result = await Decide(invoice, Decision.Approve, reason: "  Price increase agreed by phone on 12.11.  ");

        Assert.Equal(DecisionOutcome.Approved, result.Outcome);
        Assert.Equal("Price increase agreed by phone on 12.11.", invoice.StatusHistory[^1].Reason);
    }

    [Fact]
    public async Task The_limit_itself_may_be_approved_one_cent_more_may_not()
    {
        var atLimit = await Decide(Matched(gross: Money.Euro(1_000.00m)), Decision.Approve);
        var overLimit = await Decide(Matched(gross: Money.Euro(1_000.01m)), Decision.Approve);

        Assert.Equal(DecisionOutcome.Approved, atLimit.Outcome);
        Assert.Equal(DecisionOutcome.OverLimit, overLimit.Outcome);
        Assert.Contains("1,000.00 EUR", overLimit.Detail, StringComparison.Ordinal); // tells the approver their limit
    }

    [Fact]
    public async Task Other_currencies_cannot_be_approved_until_limits_cover_them()
    {
        var result = await Decide(Matched(gross: new Money(10m, "USD")), Decision.Approve);

        Assert.Equal(DecisionOutcome.CurrencyNotSupported, result.Outcome); // fails closed: no limit in USD exists
    }

    [Fact]
    public async Task Blocking_findings_allow_only_a_rejection()
    {
        _store.Blocking = true;
        var invoice = InException();

        var approve = await Decide(invoice, Decision.Approve, reason: "Looks fine to me");
        var reject = await Decide(invoice, Decision.Reject, reason: "Bank account not approved");

        Assert.Equal(DecisionOutcome.BlockingFindings, approve.Outcome);
        Assert.Equal(DecisionOutcome.Rejected, reject.Outcome);
        Assert.Equal(InvoiceStatus.Rejected, invoice.Status);
    }

    // ---- Rejecting ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Rejecting_always_needs_a_reason(string? reason)
    {
        var invoice = Matched();

        var result = await Decide(invoice, Decision.Reject, reason);

        Assert.Equal(DecisionOutcome.ReasonRequired, result.Outcome);
        Assert.Equal(InvoiceStatus.Matched, invoice.Status);
    }

    [Fact]
    public async Task A_rejection_records_who_and_why()
    {
        var invoice = Matched();

        var result = await Decide(invoice, Decision.Reject, "Ordered by mistake; supplier informed.");

        Assert.Equal(new DecisionResult(DecisionOutcome.Rejected, InvoiceStatus.Rejected), result);
        Assert.Equal((Approver.Name, "Ordered by mistake; supplier informed."), (invoice.StatusHistory[^1].Actor, invoice.StatusHistory[^1].Reason));
    }

    [Fact]
    public async Task Rejecting_above_the_limit_is_allowed()
    {
        // Stopping a payment needs no authority over the amount.
        var result = await Decide(Matched(gross: Money.Euro(50_000m)), Decision.Reject, "Duplicate of an invoice paid last month.");

        Assert.Equal(DecisionOutcome.Rejected, result.Outcome);
    }

    [Fact]
    public async Task A_reason_longer_than_200_characters_is_refused_not_cut()
    {
        var invoice = Matched();

        var result = await Decide(invoice, Decision.Reject, new string('x', 201));

        Assert.Equal(DecisionOutcome.ReasonTooLong, result.Outcome);
        Assert.Equal(InvoiceStatus.Matched, invoice.Status);
    }

    [Fact]
    public async Task A_reason_of_exactly_200_characters_is_kept_whole()
    {
        var invoice = Matched();
        var reason = new string('x', 200);

        await Decide(invoice, Decision.Reject, reason);

        Assert.Equal(reason, invoice.StatusHistory[^1].Reason);
    }

    [Fact]
    public async Task A_reason_with_control_characters_is_refused()
    {
        var result = await Decide(Matched(), Decision.Reject, "Wrong\u0000amount");

        Assert.Equal(DecisionOutcome.ReasonRequired, result.Outcome);
    }

    // ---- Who may decide -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(Decision.Approve)]
    [InlineData(Decision.Reject)]
    public async Task Nobody_decides_an_invoice_they_submitted(Decision decision)
    {
        var invoice = Matched(submittedBy: "Approver@Test"); // the same account, written differently

        var result = await Decide(invoice, decision, "reason");

        Assert.Equal(DecisionOutcome.OwnInvoice, result.Outcome);
        Assert.Equal(InvoiceStatus.Matched, invoice.Status);
        Assert.Null(_store.SavedWithVersion);
    }

    // ---- Status, version, concurrency -----------------------------------------------------------------------------

    [Theory]
    [InlineData(InvoiceStatus.Received)]
    [InlineData(InvoiceStatus.Approved)]
    [InlineData(InvoiceStatus.Rejected)]
    [InlineData(InvoiceStatus.Failed)]
    public async Task Only_matched_and_exception_invoices_wait_for_a_decision(InvoiceStatus status)
    {
        var invoice = status switch
        {
            InvoiceStatus.Received => TestInvoices.Received(),
            InvoiceStatus.Failed => TestInvoices.InStatus(InvoiceStatus.Failed, [InvoiceStatus.Failed]),
            InvoiceStatus.Approved => TestInvoices.InStatus(InvoiceStatus.Approved, [InvoiceStatus.Parsed, InvoiceStatus.Validated, InvoiceStatus.Matched, InvoiceStatus.Approved]),
            _ => TestInvoices.InStatus(InvoiceStatus.Rejected, [InvoiceStatus.Parsed, InvoiceStatus.Exception, InvoiceStatus.Rejected]),
        };

        var result = await Decide(invoice, Decision.Reject, "reason");

        Assert.Equal(DecisionOutcome.NotAllowed, result.Outcome);
        Assert.Equal(status, invoice.Status);
    }

    [Fact]
    public async Task A_decision_on_an_outdated_view_is_a_conflict()
    {
        var invoice = Matched();

        var result = await Decide(invoice, Decision.Approve, version: "AAAAAAAAB9A="); // the approver saw an older version

        Assert.Equal(DecisionOutcome.Conflict, result.Outcome);
        Assert.Equal(InvoiceStatus.Matched, invoice.Status);
        Assert.Null(_store.SavedWithVersion);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_decision_without_a_version_is_refused(string version)
    {
        var result = await Decide(Matched(), Decision.Approve, version: version);

        Assert.Equal(DecisionOutcome.VersionRequired, result.Outcome);
    }

    [Fact]
    public async Task Someone_else_deciding_first_while_saving_is_a_conflict()
    {
        _store.ThrowOnSave = true; // the database saw a newer version

        var result = await Decide(Matched(), Decision.Approve);

        Assert.Equal(DecisionOutcome.Conflict, result.Outcome);
    }

    [Fact]
    public async Task An_unknown_invoice_is_not_found()
    {
        var result = await Approval().DecideAsync(new DecisionRequest(Guid.NewGuid(), Decision.Approve, CurrentVersion, null), Approver, CancellationToken.None);

        Assert.Equal(DecisionOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task The_order_of_checks_never_reveals_more_than_needed()
    {
        // An outdated view is reported before anything about the invoice's content: reload first, then decide.
        var invoice = Matched(submittedBy: Approver.Name, gross: Money.Euro(99_999m));

        var result = await Decide(invoice, Decision.Approve, version: "AAAAAAAAB9A=");

        Assert.Equal(DecisionOutcome.Conflict, result.Outcome);
    }

    private sealed class FakeApprovalStore : IApprovalStore
    {
        public Invoice? Invoice { get; set; }

        public string Version { get; set; } = CurrentVersion;

        public bool Blocking { get; set; }

        public bool ThrowOnSave { get; set; }

        public string? SavedWithVersion { get; private set; }

        public Task<StoredInvoice?> FindAsync(Guid invoiceId, CancellationToken cancellationToken) =>
            Task.FromResult(Invoice?.Id == invoiceId ? new StoredInvoice(Invoice, Version) : null);

        public Task<bool> HasBlockingFindingsAsync(Guid invoiceId, CancellationToken cancellationToken) => Task.FromResult(Blocking);

        public Task SaveChangesAsync(string expectedVersion, CancellationToken cancellationToken)
        {
            if (ThrowOnSave)
            {
                throw new ConcurrentStatusChangeException();
            }

            SavedWithVersion = expectedVersion;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
