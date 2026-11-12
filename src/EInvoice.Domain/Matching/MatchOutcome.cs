namespace EInvoice.Domain.Matching;

/// <summary>
/// Result of matching one invoice line. The numbers are stored in the database (MatchResult.ReasonCode):
/// never renumber or reuse a value.
/// </summary>
public enum MatchOutcome
{
    FullMatch = 1,

    /// <summary>Invoice unit price is more than the tolerance above the order price.</summary>
    PriceVariance = 2,

    /// <summary>Invoiced quantity is more than received and not yet invoiced.</summary>
    QuantityVariance = 3,

    /// <summary>No purchase order, or no order line this invoice line can be matched to.</summary>
    NoPurchaseOrder = 4,

    /// <summary>The order line exists but nothing has been received for it.</summary>
    NoGoodsReceipt = 5,
}

/// <summary>Matching settings. Configuration, not code: the values come from appsettings.</summary>
public sealed record MatchingPolicy
{
    public MatchingPolicy(decimal PriceTolerancePercent)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(PriceTolerancePercent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(PriceTolerancePercent, 100m);
        this.PriceTolerancePercent = PriceTolerancePercent;
    }

    public static MatchingPolicy Default { get; } = new(PriceTolerancePercent: 2.00m);

    /// <summary>How far, in percent, an invoice unit price may be above the order price. Lower prices are always accepted.</summary>
    public decimal PriceTolerancePercent { get; }
}
