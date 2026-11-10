using EInvoice.Domain.Matching;

namespace EInvoice.UnitTests.Domain.Matching;

/// <summary>
/// Decision table: four conditions, 16 combinations, one expected outcome each.
/// Precedence: no order > no receipt > quantity > price > full match.
///
/// A goods receipt is always booked against an order line, so "receipt present without an order"
/// cannot occur in reality; those four rows collapse into NoPurchaseOrder (the receipt is never passed in).
/// Without a receipt the quantity check has nothing to compare against, so those rows are NoGoodsReceipt.
/// </summary>
public class MatchDecisionTableTests
{
    public static TheoryData<bool, bool, bool, bool, MatchOutcome> Table() => new()
    {
        // order, receipt, priceOk, quantityOk -> outcome
        { false, false, false, false, MatchOutcome.NoPurchaseOrder },
        { false, false, false, true, MatchOutcome.NoPurchaseOrder },
        { false, false, true, false, MatchOutcome.NoPurchaseOrder },
        { false, false, true, true, MatchOutcome.NoPurchaseOrder },
        { false, true, false, false, MatchOutcome.NoPurchaseOrder }, // impossible in reality
        { false, true, false, true, MatchOutcome.NoPurchaseOrder },  // impossible in reality
        { false, true, true, false, MatchOutcome.NoPurchaseOrder },  // impossible in reality
        { false, true, true, true, MatchOutcome.NoPurchaseOrder },   // impossible in reality
        { true, false, false, false, MatchOutcome.NoGoodsReceipt },
        { true, false, false, true, MatchOutcome.NoGoodsReceipt },
        { true, false, true, false, MatchOutcome.NoGoodsReceipt },
        { true, false, true, true, MatchOutcome.NoGoodsReceipt },
        { true, true, false, false, MatchOutcome.QuantityVariance },
        { true, true, false, true, MatchOutcome.PriceVariance },
        { true, true, true, false, MatchOutcome.QuantityVariance },
        { true, true, true, true, MatchOutcome.FullMatch },
    };

    [Fact]
    public void Table_has_all_16_combinations()
    {
        var rows = Table().Select(r => ((bool)r[0], (bool)r[1], (bool)r[2], (bool)r[3])).Distinct();

        Assert.Equal(16, rows.Count());
    }

    [Theory]
    [MemberData(nameof(Table))]
    public void Each_combination_gives_its_expected_outcome(bool order, bool receipt, bool priceOk, bool quantityOk, MatchOutcome expected)
    {
        var builder = new MatchBuilder();
        if (order)
        {
            builder.OrderLine(1, quantity: 10, unitPrice: 100m);
            if (receipt)
            {
                builder.Received(1, 10);
            }
        }
        else
        {
            builder.NoOrder();
        }

        builder.InvoiceLine(1, quantity: quantityOk ? 10 : 12, unitPrice: priceOk ? 101m : 105m);

        Assert.Equal(expected, builder.MatchSingleLine().Outcome);
    }
}
