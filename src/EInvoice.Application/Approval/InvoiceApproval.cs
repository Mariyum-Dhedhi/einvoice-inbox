using EInvoice.Application.Matching;
using EInvoice.Domain.Invoices;

namespace EInvoice.Application.Approval;

public enum Decision
{
    Approve = 1,
    Reject = 2,
}

public enum DecisionOutcome
{
    Approved = 1,
    Rejected = 2,
    NotFound = 3,

    /// <summary>The invoice is not waiting for a decision (only Matched and Exception are), or has no total to check.</summary>
    NotAllowed = 4,

    /// <summary>The invoice changed since the approver looked at it, or someone else decided first.</summary>
    Conflict = 5,

    /// <summary>Nobody decides an invoice they submitted.</summary>
    OwnInvoice = 6,

    OverLimit = 7,

    /// <summary>Limits are in EUR; other currencies cannot be approved until limits cover them.</summary>
    CurrencyNotSupported = 8,

    /// <summary>A finding that only allows rejection.</summary>
    BlockingFindings = 9,

    ReasonRequired = 10,
    ReasonTooLong = 11,
    VersionRequired = 12,
}

/// <summary>Who decides, and up to which gross amount in EUR they may approve.</summary>
public sealed record Approver(string Name, decimal LimitEur);

/// <param name="Version">The invoice version the approver looked at (from the detail); the decision applies only to it.</param>
public sealed record DecisionRequest(Guid InvoiceId, Decision Decision, string Version, string? Reason);

public sealed record DecisionResult(DecisionOutcome Outcome, InvoiceStatus? Status, string? Detail = null);

/// <summary>An invoice with the version it was loaded at (opaque text).</summary>
public sealed record StoredInvoice(Invoice Invoice, string Version);

public static class ApprovalLimits
{
    /// <summary>The account claim holding an approver's own limit (gross, EUR, invariant notation like "2500.00").</summary>
    public const string ClaimType = "approval_limit_eur";

    public const string Currency = "EUR";

    /// <summary>The status history column; a longer reason is refused, never cut.</summary>
    public const int MaxReasonLength = 200;
}

public interface IApprovalStore
{
    Task<StoredInvoice?> FindAsync(Guid invoiceId, CancellationToken cancellationToken);

    Task<bool> HasBlockingFindingsAsync(Guid invoiceId, CancellationToken cancellationToken);

    /// <summary>Saves only if the invoice in the database is still at <paramref name="expectedVersion"/>.</summary>
    /// <exception cref="ConcurrentStatusChangeException">The invoice changed in the meantime; nothing was saved.</exception>
    Task SaveChangesAsync(string expectedVersion, CancellationToken cancellationToken);
}

public interface IInvoiceApproval
{
    Task<DecisionResult> DecideAsync(DecisionRequest request, Approver approver, CancellationToken cancellationToken);
}

/// <summary>
/// Approve or reject an invoice waiting for a decision (Matched or Exception). Checks, in this order: the approver saw
/// the current version; the invoice waits for a decision; it is not their own; the reason; then, to approve, no blocking
/// finding and the amount within the approver's limit. Every refusal changes and saves nothing.
/// </summary>
public sealed class InvoiceApproval(IApprovalStore store, TimeProvider time) : IInvoiceApproval
{
    public async Task<DecisionResult> DecideAsync(DecisionRequest request, Approver approver, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(approver);
        ArgumentException.ThrowIfNullOrWhiteSpace(approver.Name);

        if (string.IsNullOrWhiteSpace(request.Version))
        {
            return Refused(DecisionOutcome.VersionRequired, null, "Send the version of the invoice you looked at.");
        }

        var stored = await store.FindAsync(request.InvoiceId, cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            return Refused(DecisionOutcome.NotFound, null, "No invoice with this id.");
        }

        var invoice = stored.Invoice;
        if (!string.Equals(stored.Version, request.Version, StringComparison.Ordinal))
        {
            return Refused(DecisionOutcome.Conflict, invoice.Status, "The invoice changed since you opened it. Reload it and decide again.");
        }

        if (!ApprovalRules.IsWaiting(invoice.Status))
        {
            return Refused(DecisionOutcome.NotAllowed, invoice.Status, $"Only Matched and Exception invoices wait for a decision; this one is {invoice.Status}.");
        }

        if (ApprovalRules.IsOwn(invoice.SubmittedBy, approver))
        {
            return Refused(DecisionOutcome.OwnInvoice, invoice.Status, "You submitted this invoice; someone else has to decide it.");
        }

        var (reason, problem) = Reason(request.Reason, ApprovalRules.ReasonRequired(request.Decision, invoice.Status));
        if (problem is { } refusal)
        {
            return refusal with { Status = invoice.Status };
        }

        if (request.Decision == Decision.Approve)
        {
            var hasBlockingFindings = await store.HasBlockingFindingsAsync(invoice.Id, cancellationToken).ConfigureAwait(false);
            if (ApprovalRules.WhyNotApprove(approver, invoice.GrossTotal?.Amount, invoice.GrossTotal?.Currency, hasBlockingFindings) is { } block)
            {
                return Refused(block.Outcome, invoice.Status, block.Detail);
            }
        }

        var to = request.Decision == Decision.Approve ? InvoiceStatus.Approved : InvoiceStatus.Rejected;
        invoice.TransitionTo(to, approver.Name, time.GetUtcNow(), reason);
        try
        {
            await store.SaveChangesAsync(stored.Version, cancellationToken).ConfigureAwait(false);
        }
        catch (ConcurrentStatusChangeException)
        {
            return Refused(DecisionOutcome.Conflict, null, "Someone else changed or decided the invoice at the same time. Reload it.");
        }

        return new DecisionResult(to == InvoiceStatus.Approved ? DecisionOutcome.Approved : DecisionOutcome.Rejected, to);
    }

    /// <summary>The reason as stored: trimmed, at most 200 characters, no control characters; never cut.</summary>
    private static (string? Reason, DecisionResult? Problem) Reason(string? raw, bool required)
    {
        var reason = raw?.Trim();
        if (string.IsNullOrEmpty(reason))
        {
            return required ? (null, Refused(DecisionOutcome.ReasonRequired, null, "Give a reason for this decision.")) : (null, null);
        }

        if (reason.Any(char.IsControl))
        {
            return (null, Refused(DecisionOutcome.ReasonRequired, null, "The reason contains characters that cannot be stored; write it as plain text."));
        }

        return reason.Length > ApprovalLimits.MaxReasonLength
            ? (null, Refused(DecisionOutcome.ReasonTooLong, null, $"The reason can have at most {ApprovalLimits.MaxReasonLength} characters."))
            : (reason, null);
    }

    private static DecisionResult Refused(DecisionOutcome outcome, InvoiceStatus? status, string detail) => new(outcome, status, detail);
}
