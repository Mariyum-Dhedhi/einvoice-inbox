using System.Collections.Frozen;
using EInvoice.Domain.Shared;

namespace EInvoice.Domain.Invoices;

/// <summary>
/// The single source of truth for which status changes are legal. Anything not listed is illegal,
/// so a new status is blocked everywhere until it is added here on purpose.
/// </summary>
public static class InvoiceStateMachine
{
    private static readonly FrozenDictionary<InvoiceStatus, FrozenSet<InvoiceStatus>> Allowed =
        new Dictionary<InvoiceStatus, InvoiceStatus[]>
        {
            [InvoiceStatus.Received] = [InvoiceStatus.Parsed, InvoiceStatus.Failed],
            [InvoiceStatus.Parsed] = [InvoiceStatus.Validated, InvoiceStatus.Exception],
            [InvoiceStatus.Validated] = [InvoiceStatus.Matched, InvoiceStatus.Exception],
            [InvoiceStatus.Matched] = [InvoiceStatus.Approved, InvoiceStatus.Exception],
            [InvoiceStatus.Exception] = [InvoiceStatus.Approved, InvoiceStatus.Rejected, InvoiceStatus.Matched], // re-match after a late goods receipt
            [InvoiceStatus.Approved] = [InvoiceStatus.Posted, InvoiceStatus.Failed],
            [InvoiceStatus.Failed] = [InvoiceStatus.Approved], // admin retries a failed posting
            [InvoiceStatus.Rejected] = [],
            [InvoiceStatus.Posted] = [],
        }.ToFrozenDictionary(pair => pair.Key, pair => pair.Value.ToFrozenSet());

    public static bool IsAllowed(InvoiceStatus from, InvoiceStatus to) =>
        Allowed.TryGetValue(from, out var targets) && targets.Contains(to);

    public static IReadOnlySet<InvoiceStatus> AllowedFrom(InvoiceStatus from) =>
        Allowed.TryGetValue(from, out var targets) ? targets : FrozenSet<InvoiceStatus>.Empty;

    public static void EnsureAllowed(InvoiceStatus from, InvoiceStatus to)
    {
        if (!IsAllowed(from, to))
        {
            throw new InvalidStatusTransitionException(from, to);
        }
    }
}

public sealed class InvalidStatusTransitionException : DomainException
{
    public InvalidStatusTransitionException(InvoiceStatus from, InvoiceStatus to)
        : base($"An invoice cannot move from {from} to {to}.")
    {
        From = from;
        To = to;
    }

    public InvoiceStatus From { get; }

    public InvoiceStatus To { get; }
}

/// <summary>One entry in an invoice's status history: who changed it, when, from what, to what, and why.</summary>
/// <param name="Sequence">1 for the first entry, then 2, 3, ...; gives the order and, with the invoice id, the key.</param>
/// <param name="From">Null only for the first entry, when the invoice is received.</param>
public sealed record StatusChange(int Sequence, InvoiceStatus? From, InvoiceStatus To, string Actor, DateTimeOffset At, string? Reason);
