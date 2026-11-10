using EInvoice.Domain.Invoices;
using EInvoice.Domain.Purchasing;
using EInvoice.Domain.Shared;

namespace EInvoice.Domain.Matching;

/// <summary>
/// Three-way match: each invoice line against its purchase order line (price) and goods receipts (quantity).
/// This is the specification of the rules; the set-based SQL procedure in package 6 is tested against the same cases.
/// </summary>
public static class ThreeWayMatcher
{
    /// <param name="invoiceLines">Lines of the invoice, matched in order.</param>
    /// <param name="order">The purchase order the invoice refers to, or null if there is none.</param>
    /// <param name="receipts">All goods receipts booked against <paramref name="order"/>.</param>
    /// <param name="alreadyInvoiced">Quantity per order line already billed on earlier invoices.</param>
    /// <param name="policy">Tolerances, from configuration.</param>
    public static InvoiceMatchResult Match(
        IReadOnlyList<InvoiceLine> invoiceLines,
        PurchaseOrder? order,
        IReadOnlyList<GoodsReceipt> receipts,
        IReadOnlyDictionary<int, decimal> alreadyInvoiced,
        MatchingPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(invoiceLines);
        ArgumentNullException.ThrowIfNull(receipts);
        ArgumentNullException.ThrowIfNull(alreadyInvoiced);
        ArgumentNullException.ThrowIfNull(policy);

        if (receipts.Any(r => r.PurchaseOrderId != order?.Id))
        {
            throw new ArgumentException("Every goods receipt must belong to the purchase order being matched.", nameof(receipts));
        }

        var receivedByLine = receipts
            .SelectMany(r => r.Lines)
            .GroupBy(l => l.PurchaseOrderLineNumber)
            .ToDictionary(g => g.Key, g => g.Sum(l => l.QuantityReceived));

        // Quantity already claimed by earlier lines of this same invoice, so two lines cannot bill one delivery twice.
        var claimedInThisInvoice = new Dictionary<int, decimal>();

        var results = new List<LineMatchResult>(invoiceLines.Count);
        foreach (var line in invoiceLines)
        {
            var orderLineNumber = line.PurchaseOrderLineNumber ?? line.LineNumber;
            var orderLine = order?.Lines.FirstOrDefault(l => l.LineNumber == orderLineNumber);

            if (orderLine is null)
            {
                results.Add(new LineMatchResult(line.LineNumber, null, MatchOutcome.NoPurchaseOrder,
                    line.UnitPrice, null, null, null, line.Quantity, null, null));
                continue;
            }

            var received = receivedByLine.GetValueOrDefault(orderLine.LineNumber);
            var available = Math.Max(0m,
                received
                - alreadyInvoiced.GetValueOrDefault(orderLine.LineNumber)
                - claimedInThisInvoice.GetValueOrDefault(orderLine.LineNumber));
            claimedInThisInvoice[orderLine.LineNumber] = claimedInThisInvoice.GetValueOrDefault(orderLine.LineNumber) + line.Quantity;

            var quantityVariance = Math.Max(0m, line.Quantity - available);
            var (pricePercent, priceAmount, priceOk) = ComparePrice(line, orderLine, policy);

            // Precedence: no receipt > quantity > price. Quantity wins because paying for goods
            // never received is the bigger risk; the price numbers are still kept on the result.
            var outcome = received == 0m ? MatchOutcome.NoGoodsReceipt
                : quantityVariance > 0m ? MatchOutcome.QuantityVariance
                : !priceOk ? MatchOutcome.PriceVariance
                : MatchOutcome.FullMatch;

            results.Add(new LineMatchResult(line.LineNumber, orderLine.LineNumber, outcome,
                line.UnitPrice, orderLine.UnitPrice, pricePercent, priceAmount,
                line.Quantity, available, quantityVariance));
        }

        return new InvoiceMatchResult(results.AsReadOnly());
    }

    /// <summary>
    /// Percentage is computed at full decimal precision and never rounded before the comparison,
    /// so 2.001% is outside a 2.00% tolerance. Only prices above the order count against the tolerance.
    /// </summary>
    private static (decimal? Percent, Money Amount, bool WithinTolerance) ComparePrice(
        InvoiceLine line, PurchaseOrderLine orderLine, MatchingPolicy policy)
    {
        var difference = line.UnitPrice - orderLine.UnitPrice;
        var amount = new Money(difference * line.Quantity, line.NetAmount.Currency);

        if (orderLine.UnitPrice == 0m)
        {
            // A percentage of zero is undefined: for a free item any charge is a variance.
            return (null, amount, difference <= 0m);
        }

        var percent = difference / orderLine.UnitPrice * 100m;
        return (percent, amount, percent <= policy.PriceTolerancePercent);
    }
}
