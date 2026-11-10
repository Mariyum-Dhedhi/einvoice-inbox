using EInvoice.Domain.Shared;

namespace EInvoice.Domain.Matching;

/// <summary>
/// Outcome of one invoice line plus the numbers behind it, so the match panel can show
/// "+5.00 EUR (+2.5%)" and an approver can see both price and quantity problems at once.
/// </summary>
public sealed record LineMatchResult(
    int InvoiceLineNumber,
    int? PurchaseOrderLineNumber,
    MatchOutcome Outcome,
    decimal InvoiceUnitPrice,
    decimal? OrderUnitPrice,
    decimal? PriceVariancePercent,
    Money? PriceVarianceAmount,
    decimal QuantityInvoiced,
    decimal? QuantityAvailable,
    decimal? QuantityVariance);

public sealed record InvoiceMatchResult(IReadOnlyList<LineMatchResult> Lines)
{
    // Most severe first: this is the order reasons are shown to an approver.
    private static readonly MatchOutcome[] Severity =
    [
        MatchOutcome.NoPurchaseOrder,
        MatchOutcome.NoGoodsReceipt,
        MatchOutcome.QuantityVariance,
        MatchOutcome.PriceVariance,
    ];

    /// <summary>True only when there is at least one line and every line is a full match.</summary>
    public bool IsFullMatch => Lines.Count > 0 && Lines.All(l => l.Outcome == MatchOutcome.FullMatch);

    /// <summary>The distinct problems on this invoice, most severe first. Empty for a full match.</summary>
    public IReadOnlyList<MatchOutcome> ExceptionReasons =>
        Severity.Where(s => Lines.Any(l => l.Outcome == s)).ToArray();
}
