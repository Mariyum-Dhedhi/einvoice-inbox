using EInvoice.Domain.Matching;

namespace EInvoice.Infrastructure.Persistence;

/// <summary>
/// One stored row of dbo.MatchResult: the outcome of one invoice line, written only by dbo.usp_MatchInvoice.
/// The read model for the match panel; the business object is <see cref="InvoiceMatchResult"/>.
/// </summary>
public sealed class MatchResultRecord
{
    public Guid InvoiceId { get; init; }

    public int InvoiceLineNumber { get; init; }

    public int? PurchaseOrderLineNumber { get; init; }

    public MatchOutcome Outcome { get; init; }

    public decimal InvoiceUnitPrice { get; init; }

    public decimal? OrderUnitPrice { get; init; }

    /// <summary>For display only (six decimals). The match decision is made without division.</summary>
    public decimal? PriceVariancePercent { get; init; }

    public decimal? PriceVarianceAmount { get; init; }

    public string Currency { get; init; } = string.Empty;

    public decimal QuantityInvoiced { get; init; }

    public decimal? QuantityAvailable { get; init; }

    public decimal? QuantityVariance { get; init; }

    /// <summary>The tolerance in force when this result was produced, so a result can always be explained later.</summary>
    public decimal PriceTolerancePercent { get; init; }

    public DateTimeOffset MatchedAt { get; init; }
}
