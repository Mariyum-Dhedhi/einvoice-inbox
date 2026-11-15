using EInvoice.Application.Approval;
using EInvoice.Domain.Invoices;

namespace EInvoice.UnitTests.Approval;

/// <summary>
/// The rules the use case enforces and the screens show in advance, from one place: the queue and the decision panel
/// cannot promise something the server then refuses.
/// </summary>
public sealed class ApprovalRulesTests
{
    private static readonly Approver Approver = new("approver@test", 1_000m);

    [Theory]
    [InlineData(InvoiceStatus.Matched, true)]
    [InlineData(InvoiceStatus.Exception, true)]
    [InlineData(InvoiceStatus.Received, false)]
    [InlineData(InvoiceStatus.Approved, false)]
    [InlineData(InvoiceStatus.Rejected, false)]
    [InlineData(InvoiceStatus.Failed, false)]
    public void Only_matched_and_exception_wait_for_a_decision(InvoiceStatus status, bool waiting)
    {
        Assert.Equal(waiting, ApprovalRules.IsWaiting(status));
    }

    [Theory]
    [InlineData("approver@test", true)]
    [InlineData("APPROVER@test", true)]
    [InlineData("clerk@test", false)]
    public void Own_invoice_ignores_upper_and_lower_case(string submittedBy, bool own)
    {
        Assert.Equal(own, ApprovalRules.IsOwn(submittedBy, Approver));
    }

    [Theory]
    [InlineData(Decision.Reject, InvoiceStatus.Matched, true)]
    [InlineData(Decision.Reject, InvoiceStatus.Exception, true)]
    [InlineData(Decision.Approve, InvoiceStatus.Exception, true)]
    [InlineData(Decision.Approve, InvoiceStatus.Matched, false)]
    public void A_reason_is_required_to_reject_and_to_approve_an_exception(Decision decision, InvoiceStatus status, bool required)
    {
        Assert.Equal(required, ApprovalRules.ReasonRequired(decision, status));
    }

    [Fact]
    public void Within_the_limit_in_euro_without_blocking_findings_may_be_approved()
    {
        Assert.Null(ApprovalRules.WhyNotApprove(Approver, 1_000m, "EUR", hasBlockingFindings: false));
    }

    [Fact]
    public void A_blocking_finding_is_named_first()
    {
        var block = ApprovalRules.WhyNotApprove(Approver, 99_999m, "USD", hasBlockingFindings: true);

        Assert.Equal(DecisionOutcome.BlockingFindings, block?.Outcome);
    }

    [Theory]
    [InlineData(null, "EUR", DecisionOutcome.NotAllowed)]
    [InlineData(10.0, "USD", DecisionOutcome.CurrencyNotSupported)]
    [InlineData(10.0, null, DecisionOutcome.CurrencyNotSupported)]
    [InlineData(1000.01, "EUR", DecisionOutcome.OverLimit)]
    public void Each_reason_not_to_approve_has_its_outcome(double? gross, string? currency, DecisionOutcome expected)
    {
        var block = ApprovalRules.WhyNotApprove(Approver, gross is null ? null : (decimal)gross.Value, currency, hasBlockingFindings: false);

        Assert.Equal(expected, block?.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(block?.Detail));
    }

    [Fact]
    public void Over_the_limit_names_the_limit()
    {
        Assert.Contains("1,000.00 EUR", ApprovalRules.WhyNotApprove(Approver, 5_000m, "EUR", false)!.Detail, StringComparison.Ordinal);
    }
}
