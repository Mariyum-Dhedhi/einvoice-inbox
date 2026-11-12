using EInvoice.Domain.Matching;

namespace EInvoice.UnitTests.Domain.Matching;

/// <summary>
/// Boundary value analysis for the price tolerance (default 2.00% per line, prices above the order only).
/// Order unit price is 100.00, so the invoice price equals 100 + variance percent.
/// </summary>
public class PriceToleranceTests
{
    [Theory]
    [InlineData("100.00", MatchOutcome.FullMatch)]      // exact
    [InlineData("101.99", MatchOutcome.FullMatch)]      // 1.99%: just inside
    [InlineData("102.00", MatchOutcome.FullMatch)]      // 2.00%: on the boundary, inside
    [InlineData("102.001", MatchOutcome.PriceVariance)] // 2.001%: must not be rounded down to 2.00%
    [InlineData("102.01", MatchOutcome.PriceVariance)]  // 2.01%: just outside
    [InlineData("95.00", MatchOutcome.FullMatch)]       // -5%: lower prices are accepted
    public void Price_boundaries(string invoicePrice, MatchOutcome expected)
    {
        var line = new MatchBuilder()
            .OrderLine(1, quantity: 10, unitPrice: 100.00m)
            .Received(1, 10)
            .InvoiceLine(1, quantity: 1, unitPrice: decimal.Parse(invoicePrice, System.Globalization.CultureInfo.InvariantCulture))
            .MatchSingleLine();

        Assert.Equal(expected, line.Outcome);
    }

    [Fact]
    public void Variance_amount_and_percent_are_recorded()
    {
        var line = new MatchBuilder()
            .OrderLine(1, quantity: 10, unitPrice: 100.00m)
            .Received(1, 10)
            .InvoiceLine(1, quantity: 2, unitPrice: 102.50m)
            .MatchSingleLine();

        Assert.Equal(MatchOutcome.PriceVariance, line.Outcome);
        Assert.Equal(2.5m, line.PriceVariancePercent);
        Assert.Equal(5.00m, line.PriceVarianceAmount!.Amount); // (102.50 - 100.00) x 2
        Assert.Equal(100.00m, line.OrderUnitPrice);
        Assert.Equal(102.50m, line.InvoiceUnitPrice);
    }

    [Fact]
    public void Lower_price_is_accepted_but_its_variance_is_still_recorded()
    {
        var line = new MatchBuilder()
            .OrderLine(1, quantity: 10, unitPrice: 100.00m)
            .Received(1, 10)
            .InvoiceLine(1, quantity: 1, unitPrice: 95.00m)
            .MatchSingleLine();

        Assert.Equal(MatchOutcome.FullMatch, line.Outcome);
        Assert.Equal(-5m, line.PriceVariancePercent);
        Assert.Equal(-5.00m, line.PriceVarianceAmount!.Amount);
    }

    [Fact]
    public void Tolerance_comes_from_the_policy_not_from_code()
    {
        var strict = new MatchBuilder()
            .OrderLine(1, quantity: 10, unitPrice: 100.00m)
            .Received(1, 10)
            .InvoiceLine(1, quantity: 1, unitPrice: 100.01m)
            .Policy(new MatchingPolicy(PriceTolerancePercent: 0m))
            .MatchSingleLine();

        Assert.Equal(MatchOutcome.PriceVariance, strict.Outcome);
    }

    [Fact]
    public void Free_order_line_accepts_zero_price_without_dividing_by_zero()
    {
        var line = new MatchBuilder()
            .OrderLine(1, quantity: 10, unitPrice: 0m)
            .Received(1, 10)
            .InvoiceLine(1, quantity: 1, unitPrice: 0m)
            .MatchSingleLine();

        Assert.Equal(MatchOutcome.FullMatch, line.Outcome);
        Assert.Null(line.PriceVariancePercent);
    }

    [Fact]
    public void Free_order_line_flags_any_charge()
    {
        var line = new MatchBuilder()
            .OrderLine(1, quantity: 10, unitPrice: 0m)
            .Received(1, 10)
            .InvoiceLine(1, quantity: 1, unitPrice: 0.01m)
            .MatchSingleLine();

        Assert.Equal(MatchOutcome.PriceVariance, line.Outcome);
        Assert.Null(line.PriceVariancePercent); // a percentage of zero is undefined
        Assert.Equal(0.01m, line.PriceVarianceAmount!.Amount);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(100.01)]
    public void Policy_rejects_tolerance_outside_0_to_100(double percent)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MatchingPolicy((decimal)percent));
    }

    [Fact]
    public void Default_policy_is_two_percent()
    {
        Assert.Equal(2.00m, MatchingPolicy.Default.PriceTolerancePercent);
    }
}
