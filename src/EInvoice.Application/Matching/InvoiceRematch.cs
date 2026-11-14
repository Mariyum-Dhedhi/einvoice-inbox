using EInvoice.Domain.Invoices;
using EInvoice.Domain.Matching;

namespace EInvoice.Application.Matching;

public enum RematchOutcome
{
    /// <summary>The match is clean now; the invoice moved from Exception to Matched.</summary>
    Matched = 1,

    /// <summary>Matched again, still not clean; the invoice stays in Exception and the stored results are current.</summary>
    StillException = 2,

    NotFound = 3,

    /// <summary>Only an invoice in Exception because of its match can be re-matched (not one with validation findings).</summary>
    NotAllowed = 4,
}

/// <param name="Reasons">Why the invoice is still an exception; empty when matched.</param>
public sealed record RematchResult(RematchOutcome Outcome, InvoiceStatus? Status, IReadOnlyList<MatchOutcome> Reasons, string? Detail = null);

public interface IRematchStore
{
    Task<Invoice?> FindAsync(Guid invoiceId, CancellationToken cancellationToken);

    Task<bool> HasFindingsAsync(Guid invoiceId, CancellationToken cancellationToken);

    /// <exception cref="ConcurrentStatusChangeException">Someone else changed the invoice's status in the meantime.</exception>
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public sealed class ConcurrentStatusChangeException : Exception
{
    public ConcurrentStatusChangeException()
        : base("The invoice's status was changed by someone else in the meantime.")
    {
    }
}

/// <summary>
/// Matches an invoice again, typically after a late goods receipt. Allowed only for an invoice in Exception that has
/// no validation findings: a match can clear a match problem, never a finding that a human has to look at.
/// </summary>
public sealed class InvoiceRematch(IRematchStore store, IInvoiceMatcher matcher, MatchingPolicy policy, TimeProvider time)
{
    public async Task<RematchResult> RematchAsync(Guid invoiceId, string actor, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);

        var invoice = await store.FindAsync(invoiceId, cancellationToken).ConfigureAwait(false);
        if (invoice is null)
        {
            return new RematchResult(RematchOutcome.NotFound, null, []);
        }

        if (invoice.Status != InvoiceStatus.Exception)
        {
            return new RematchResult(RematchOutcome.NotAllowed, invoice.Status, [], $"Only invoices in Exception can be re-matched; this one is {invoice.Status}.");
        }

        if (await store.HasFindingsAsync(invoiceId, cancellationToken).ConfigureAwait(false))
        {
            return new RematchResult(RematchOutcome.NotAllowed, invoice.Status, [], "The invoice has validation findings; a person has to review it.");
        }

        var match = await matcher.MatchAsync(invoiceId, policy, cancellationToken).ConfigureAwait(false);
        if (!match.IsFullMatch)
        {
            return new RematchResult(RematchOutcome.StillException, invoice.Status, match.ExceptionReasons);
        }

        invoice.TransitionTo(InvoiceStatus.Matched, actor, time.GetUtcNow());
        try
        {
            await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ConcurrentStatusChangeException)
        {
            return new RematchResult(RematchOutcome.NotAllowed, null, [], "The invoice was changed by someone else in the meantime; reload it.");
        }

        return new RematchResult(RematchOutcome.Matched, invoice.Status, []);
    }
}
