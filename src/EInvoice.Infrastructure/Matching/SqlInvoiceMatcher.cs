using EInvoice.Application.Matching;
using EInvoice.Domain.Matching;
using EInvoice.Domain.Shared;
using EInvoice.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EInvoice.Infrastructure.Matching;

/// <summary>Runs dbo.usp_MatchInvoice (parameterised) and maps its rows to the domain result.</summary>
public sealed class SqlInvoiceMatcher(EInvoiceDbContext db) : IInvoiceMatcher
{
    public async Task<InvoiceMatchResult> MatchAsync(Guid invoiceId, MatchingPolicy policy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(policy);

        // FormattableString: EF Core turns {invoiceId} and {tolerance} into SQL parameters, never into SQL text.
        var tolerance = policy.PriceTolerancePercent;
        var rows = await db.Database
            .SqlQuery<MatchRow>($"EXEC dbo.usp_MatchInvoice @InvoiceId = {invoiceId}, @PriceTolerancePercent = {tolerance}")
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new InvoiceMatchResult(rows
            .OrderBy(r => r.InvoiceLineNumber)
            .Select(r => new LineMatchResult(
                r.InvoiceLineNumber,
                r.PurchaseOrderLineNumber,
                (MatchOutcome)r.Outcome,
                r.InvoiceUnitPrice,
                r.OrderUnitPrice,
                r.PriceVariancePercent,
                r.PriceVarianceAmount is { } amount ? new Money(amount, r.Currency) : null,
                r.QuantityInvoiced,
                r.QuantityAvailable,
                r.QuantityVariance))
            .ToList());
    }

    /// <summary>Shape of the procedure's result set.</summary>
    private sealed class MatchRow
    {
        public int InvoiceLineNumber { get; init; }

        public int? PurchaseOrderLineNumber { get; init; }

        public int Outcome { get; init; }

        public decimal InvoiceUnitPrice { get; init; }

        public decimal? OrderUnitPrice { get; init; }

        public decimal? PriceVariancePercent { get; init; }

        public decimal? PriceVarianceAmount { get; init; }

        public string Currency { get; init; } = string.Empty;

        public decimal QuantityInvoiced { get; init; }

        public decimal? QuantityAvailable { get; init; }

        public decimal? QuantityVariance { get; init; }
    }
}
