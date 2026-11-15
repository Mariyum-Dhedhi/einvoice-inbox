using System.Globalization;
using EInvoice.Domain.Invoices;

namespace EInvoice.Application.Approval;

/// <summary>Why an approver may not approve an invoice, in words the screen and the API both show.</summary>
public sealed record ApprovalBlock(DecisionOutcome Outcome, string Detail);

/// <summary>
/// The approval rules in one place. <see cref="InvoiceApproval"/> enforces them; the queue and the decision panel show
/// them in advance, so a screen never offers what the server then refuses.
/// </summary>
public static class ApprovalRules
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>Matched and Exception invoices wait for a person; nothing else can be decided.</summary>
    public static bool IsWaiting(InvoiceStatus status) => status is InvoiceStatus.Matched or InvoiceStatus.Exception;

    /// <summary>Nobody decides an invoice they submitted; the same account written in another case is the same person.</summary>
    public static bool IsOwn(string submittedBy, Approver approver)
    {
        ArgumentNullException.ThrowIfNull(approver);
        return string.Equals(submittedBy, approver.Name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Rejecting always needs a reason; approving needs one when the invoice is an exception.</summary>
    public static bool ReasonRequired(Decision decision, InvoiceStatus status) =>
        decision == Decision.Reject || status == InvoiceStatus.Exception;

    /// <summary>Null when the approver may approve; otherwise the first reason why not (a blocking finding first).</summary>
    public static ApprovalBlock? WhyNotApprove(Approver approver, decimal? gross, string? currency, bool hasBlockingFindings)
    {
        ArgumentNullException.ThrowIfNull(approver);
        if (hasBlockingFindings)
        {
            return new(DecisionOutcome.BlockingFindings, "The invoice has a finding that blocks payment; it can only be rejected.");
        }

        if (gross is not { } amount)
        {
            return new(DecisionOutcome.NotAllowed, "The invoice has no total to approve.");
        }

        if (currency != ApprovalLimits.Currency)
        {
            return new(DecisionOutcome.CurrencyNotSupported, "Approval limits are in EUR; invoices in other currencies cannot be approved yet.");
        }

        return amount > approver.LimitEur
            ? new(DecisionOutcome.OverLimit, string.Create(Invariant,
                $"The gross amount is above your approval limit of {approver.LimitEur:#,##0.00} EUR; someone with a higher limit has to approve it."))
            : null;
    }
}
