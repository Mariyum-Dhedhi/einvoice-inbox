using EInvoice.Domain.Matching;
using EInvoice.Domain.Purchasing;

namespace EInvoice.UnitTests.Domain.Matching;

public class QuantityAndLookupTests
{
    [Theory]
    [InlineData(10, MatchOutcome.FullMatch)]           // exactly what was received
    [InlineData(9, MatchOutcome.FullMatch)]            // partial invoice
    [InlineData(10.001, MatchOutcome.QuantityVariance)] // no tolerance on quantity
    [InlineData(11, MatchOutcome.QuantityVariance)]
    public void Invoiced_quantity_never_exceeds_received(double quantity, MatchOutcome expected)
    {
        var line = new MatchBuilder()
            .OrderLine(1, quantity: 20, unitPrice: 10m)
            .Received(1, 10)
            .InvoiceLine(1, quantity: (decimal)quantity, unitPrice: 10m)
            .MatchSingleLine();

        Assert.Equal(expected, line.Outcome);
    }

    [Theory]
    [InlineData(6, MatchOutcome.FullMatch)]
    [InlineData(7, MatchOutcome.QuantityVariance)]
    public void Already_invoiced_quantity_is_not_available_again(int quantity, MatchOutcome expected)
    {
        // Received 10, an earlier invoice already billed 4, so 6 remain.
        var line = new MatchBuilder()
            .OrderLine(1, quantity: 10, unitPrice: 10m)
            .Received(1, 10)
            .AlreadyInvoiced(1, 4)
            .InvoiceLine(1, quantity: quantity, unitPrice: 10m)
            .MatchSingleLine();

        Assert.Equal(expected, line.Outcome);
        Assert.Equal(6m, line.QuantityAvailable);
    }

    [Fact]
    public void Partial_deliveries_are_summed()
    {
        var line = new MatchBuilder()
            .OrderLine(1, quantity: 10, unitPrice: 10m)
            .Received(1, 4)
            .Received(1, 6)
            .InvoiceLine(1, quantity: 10, unitPrice: 10m)
            .MatchSingleLine();

        Assert.Equal(MatchOutcome.FullMatch, line.Outcome);
        Assert.Equal(10m, line.QuantityAvailable);
    }

    [Fact]
    public void Receipts_for_other_order_lines_do_not_count()
    {
        var line = new MatchBuilder()
            .OrderLine(1, quantity: 10, unitPrice: 10m)
            .OrderLine(2, quantity: 10, unitPrice: 10m)
            .Received(2, 10)
            .InvoiceLine(1, quantity: 5, unitPrice: 10m)
            .MatchSingleLine();

        Assert.Equal(MatchOutcome.NoGoodsReceipt, line.Outcome);
    }

    [Fact]
    public void Two_invoice_lines_on_the_same_order_line_share_the_received_quantity()
    {
        // 10 received; two lines of 6 each would be 12. The second line must fail.
        var result = new MatchBuilder()
            .OrderLine(1, quantity: 10, unitPrice: 10m)
            .Received(1, 10)
            .InvoiceLine(1, quantity: 6, unitPrice: 10m, poLine: 1)
            .InvoiceLine(2, quantity: 6, unitPrice: 10m, poLine: 1)
            .Match();

        Assert.Equal(MatchOutcome.FullMatch, result.Lines[0].Outcome);
        Assert.Equal(MatchOutcome.QuantityVariance, result.Lines[1].Outcome);
        Assert.Equal(4m, result.Lines[1].QuantityAvailable);
    }

    [Fact]
    public void Quantity_wins_over_price_when_both_are_wrong_but_both_numbers_are_kept()
    {
        var line = new MatchBuilder()
            .OrderLine(1, quantity: 10, unitPrice: 100m)
            .Received(1, 10)
            .InvoiceLine(1, quantity: 12, unitPrice: 110m)
            .MatchSingleLine();

        Assert.Equal(MatchOutcome.QuantityVariance, line.Outcome);
        Assert.Equal(10m, line.PriceVariancePercent);
        Assert.Equal(2m, line.QuantityVariance);
    }

    [Fact]
    public void Stated_order_line_number_is_used()
    {
        var line = new MatchBuilder()
            .OrderLine(1, quantity: 10, unitPrice: 10m)
            .OrderLine(2, quantity: 10, unitPrice: 50m)
            .Received(2, 10)
            .InvoiceLine(1, quantity: 1, unitPrice: 50m, poLine: 2)
            .MatchSingleLine();

        Assert.Equal(MatchOutcome.FullMatch, line.Outcome);
        Assert.Equal(2, line.PurchaseOrderLineNumber);
    }

    [Fact]
    public void Without_a_stated_order_line_the_invoice_line_number_is_used()
    {
        var line = new MatchBuilder()
            .OrderLine(1, quantity: 10, unitPrice: 10m)
            .OrderLine(2, quantity: 10, unitPrice: 50m)
            .Received(2, 10)
            .InvoiceLine(2, quantity: 1, unitPrice: 50m)
            .MatchSingleLine();

        Assert.Equal(MatchOutcome.FullMatch, line.Outcome);
        Assert.Equal(2, line.PurchaseOrderLineNumber);
    }

    [Fact]
    public void Unknown_order_line_is_NoPurchaseOrder()
    {
        var line = new MatchBuilder()
            .OrderLine(1, quantity: 10, unitPrice: 10m)
            .Received(1, 10)
            .InvoiceLine(1, quantity: 1, unitPrice: 10m, poLine: 9)
            .MatchSingleLine();

        Assert.Equal(MatchOutcome.NoPurchaseOrder, line.Outcome);
        Assert.Null(line.PurchaseOrderLineNumber);
    }

    [Fact]
    public void Receipt_belonging_to_another_order_is_a_caller_error()
    {
        var order = new PurchaseOrder("PO-1", Guid.NewGuid(), new DateOnly(2026, 10, 1), "EUR",
            [new PurchaseOrderLine(1, "ITEM-01", "Item", 10m, 10m, 19m)]);
        var foreignReceipt = new GoodsReceipt("GR-9", Guid.NewGuid(), new DateOnly(2026, 10, 2), [new GoodsReceiptLine(1, 10m)]);

        Assert.Throws<ArgumentException>(() =>
            ThreeWayMatcher.Match([], order, [foreignReceipt], new Dictionary<int, decimal>(), MatchingPolicy.Default));
    }

    [Fact]
    public void Invoice_is_a_full_match_only_if_every_line_is()
    {
        var result = new MatchBuilder()
            .OrderLine(1, quantity: 10, unitPrice: 10m)
            .OrderLine(2, quantity: 10, unitPrice: 10m)
            .Received(1, 10)
            .Received(2, 10)
            .InvoiceLine(1, quantity: 10, unitPrice: 10m)
            .InvoiceLine(2, quantity: 10, unitPrice: 11m)
            .Match();

        Assert.False(result.IsFullMatch);
        Assert.Equal([MatchOutcome.PriceVariance], result.ExceptionReasons);
    }

    [Fact]
    public void Clean_invoice_has_no_exception_reasons()
    {
        var result = new MatchBuilder()
            .OrderLine(1, quantity: 10, unitPrice: 10m)
            .Received(1, 10)
            .InvoiceLine(1, quantity: 10, unitPrice: 10m)
            .Match();

        Assert.True(result.IsFullMatch);
        Assert.Empty(result.ExceptionReasons);
    }
}
