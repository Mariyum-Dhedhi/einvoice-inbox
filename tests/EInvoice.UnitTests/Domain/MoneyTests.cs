using EInvoice.Domain.Shared;

namespace EInvoice.UnitTests.Domain;

public class MoneyTests
{
    [Theory]
    [InlineData("10.004", "10.00")]
    [InlineData("10.005", "10.01")] // German commercial rounding: halves round away from zero (banker's: 10.00)
    [InlineData("10.015", "10.02")] // both methods agree here
    [InlineData("10.025", "10.03")] // banker's rounding would give 10.02
    [InlineData("-10.005", "-10.01")]
    [InlineData("0", "0.00")]
    public void Amount_is_rounded_to_cents_commercially(string input, string expected)
    {
        var money = Money.Euro(decimal.Parse(input, System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), money.Amount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("eur")]
    [InlineData("EURO")]
    [InlineData("E1R")]
    public void Currency_must_be_a_three_letter_upper_case_code(string currency)
    {
        Assert.Throws<ArgumentException>(() => new Money(1m, currency));
    }

    [Fact]
    public void Adding_and_subtracting_same_currency()
    {
        Assert.Equal(Money.Euro(15.50m), Money.Euro(10.25m) + Money.Euro(5.25m));
        Assert.Equal(Money.Euro(5.00m), Money.Euro(10.25m) - Money.Euro(5.25m));
    }

    [Fact]
    public void Adding_different_currencies_throws()
    {
        Assert.Throws<CurrencyMismatchException>(() => Money.Euro(1m) + new Money(1m, "USD"));
    }

    [Fact]
    public void Comparing_different_currencies_throws()
    {
        Assert.Throws<CurrencyMismatchException>(() => Money.Euro(1m) > new Money(1m, "USD"));
    }

    [Fact]
    public void Multiply_rounds_the_result_to_cents()
    {
        // Unit price 0.10 x quantity 3.333 = 0.3333 -> 0.33
        Assert.Equal(Money.Euro(0.33m), Money.Euro(0.10m).Multiply(3.333m));
    }

    [Fact]
    public void Equal_amounts_are_equal_regardless_of_trailing_zeros()
    {
        Assert.Equal(Money.Euro(1m), Money.Euro(1.00m));
        Assert.True(Money.Euro(1m) == Money.Euro(1.00m));
    }

    [Fact]
    public void Comparison_operators()
    {
        Assert.True(Money.Euro(10m) > Money.Euro(9.99m));
        Assert.True(Money.Euro(10m) >= Money.Euro(10m));
        Assert.True(Money.Euro(9.99m) < Money.Euro(10m));
        Assert.True(Money.Euro(10m) <= Money.Euro(10m));
    }

    [Fact]
    public void Zero_is_zero_in_the_given_currency()
    {
        var zero = Money.Zero("EUR");

        Assert.Equal(0m, zero.Amount);
        Assert.Equal("EUR", zero.Currency);
    }

    [Fact]
    public void ToString_uses_invariant_format_with_currency()
    {
        Assert.Equal("1234.50 EUR", Money.Euro(1234.5m).ToString());
    }
}
