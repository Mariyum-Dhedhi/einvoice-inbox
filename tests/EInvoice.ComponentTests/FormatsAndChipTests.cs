using EInvoice.Domain.Invoices;
using EInvoice.Domain.Matching;
using EInvoice.Web.Components.Shared;

namespace EInvoice.ComponentTests;

/// <summary>German number and date formats (the users are German AP clerks); English labels.</summary>
public sealed class FormatsTests
{
    private const char Nbsp = ' '; // amount and currency never wrap apart

    [Theory]
    [InlineData(1234.56, "EUR", "1.234,56 €")]
    [InlineData(-5, "EUR", "-5,00 €")]
    [InlineData(0.1, "USD", "0,10 USD")]
    public void Money_is_German_with_two_decimals(decimal amount, string currency, string expected) =>
        Assert.Equal(expected, Formats.Money(amount, currency));

    [Fact]
    public void Missing_money_is_a_dash() => Assert.Equal("–", Formats.Money(null, null));

    [Theory]
    [InlineData(7.4125, "7,4125 €")] // unit prices keep their decimals (up to six), never rounded to cents
    [InlineData(39.9, "39,90 €")]
    [InlineData(0.123456, "0,123456 €")]
    public void Unit_price_keeps_its_decimals(decimal price, string expected) =>
        Assert.Equal(expected, Formats.UnitPrice(price, "EUR"));

    [Fact]
    public void Quantity_drops_trailing_zeros() => Assert.Equal("10", Formats.Quantity(10.0000m));

    [Fact]
    public void Date_is_day_month_year() => Assert.Equal("02.11.2026", Formats.Date(new DateOnly(2026, 11, 2)));

    [Fact]
    public void Timestamp_is_shown_in_German_time() =>
        Assert.Equal("14.11.2026 09:15", Formats.Timestamp(new DateTimeOffset(2026, 11, 14, 8, 15, 0, TimeSpan.Zero)));

    [Fact]
    public void Timestamp_follows_summer_time() =>
        Assert.Equal("01.07.2026 10:00", Formats.Timestamp(new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.Zero)));

    [Theory]
    [InlineData(3.25, "+3,25 %")]
    [InlineData(-0.5, "-0,50 %")]
    [InlineData(0, "0,00 %")] // found in a screenshot: "0,00" in a .NET format string means grouping and gave "000"
    public void Percent_has_a_sign(decimal percent, string expected) => Assert.Equal(expected, Formats.Percent(percent));

    [Fact]
    public void Amount_and_currency_are_joined_by_a_non_breaking_space()
    {
        var text = Formats.Money(1234.56m, "EUR");
        Assert.DoesNotContain(' ', text);
        Assert.Contains(Nbsp, text);
    }
}

/// <summary>Numbers typed into forms: the German format the pages show, nothing guessed.</summary>
public sealed class TypedNumberTests
{
    [Theory]
    [InlineData("40", 40)]
    [InlineData("7,4125", 7.4125)]
    [InlineData(" 0,5 ", 0.5)]
    [InlineData("0", 0)]
    [InlineData("1000000", 1000000)]
    public void Digits_with_one_decimal_comma_are_read(string typed, double expected)
    {
        Assert.True(Formats.TryParseNumber(typed, out var value));
        Assert.Equal((decimal)expected, value);
    }

    [Theory]
    [InlineData("7.5")]       // 75 or 7,5? refused, never guessed
    [InlineData("1.000")]
    [InlineData("1.000,50")]
    [InlineData("1,2,3")]
    [InlineData(",5")]
    [InlineData("5,")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData("1e3")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("١٢")]       // digits of another script
    public void Anything_else_is_refused(string? typed)
    {
        Assert.False(Formats.TryParseNumber(typed, out _));
    }

    [Fact]
    public void A_number_is_offered_for_editing_without_thousands_separators()
    {
        Assert.Equal("1234,5", Formats.Editable(1234.5m));
        Assert.True(Formats.TryParseNumber(Formats.Editable(99_999_999_999_999.9999m), out var back));
        Assert.Equal(99_999_999_999_999.9999m, back);
    }
}

/// <summary>Status and match outcome are never shown by colour alone: every chip has a text label and an icon.</summary>
public sealed class StatusChipTests : UiTest
{
    public static TheoryData<InvoiceStatus> Statuses() => new(Enum.GetValues<InvoiceStatus>());

    public static TheoryData<MatchOutcome> Outcomes() => new(Enum.GetValues<MatchOutcome>());

    [Theory]
    [MemberData(nameof(Statuses))]
    public void Every_status_has_a_label_and_an_icon(InvoiceStatus status)
    {
        var cut = Render<StatusChip>(p => p.Add(c => c.Status, status));

        Assert.Equal(Labels.Status(T, status), cut.Find("[data-testid=status]").TextContent.Trim());
        Assert.NotEmpty(cut.FindAll("[data-testid=status] svg"));
        Assert.Equal(status.ToString(), cut.Find("[data-testid=status]").GetAttribute("data-status"));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public void Status_labels_are_distinct(string language)
    {
        UseLanguage(language);
        var labels = Enum.GetValues<InvoiceStatus>().Select(s => Labels.Status(T, s)).ToList();
        Assert.Equal(labels.Count, labels.Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(Outcomes))]
    public void Every_match_outcome_has_a_label_and_an_icon(MatchOutcome outcome)
    {
        var cut = Render<OutcomeChip>(p => p.Add(c => c.Outcome, outcome));

        Assert.Equal(Labels.Outcome(T, outcome), cut.Find("[data-testid=outcome]").TextContent.Trim());
        Assert.NotEmpty(cut.FindAll("[data-testid=outcome] svg"));
    }
}
