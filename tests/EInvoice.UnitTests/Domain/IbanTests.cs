using EInvoice.Domain.Shared;

namespace EInvoice.UnitTests.Domain;

/// <summary>
/// Uses the published example IBANs from the ISO 13616 / national bank documentation,
/// not real customer accounts.
/// </summary>
public class IbanTests
{
    [Theory]
    [InlineData("DE89370400440532013000")]
    [InlineData("GB82WEST12345698765432")]
    [InlineData("NL91ABNA0417164300")]
    [InlineData("FR1420041010050500013M02606")]
    [InlineData("AT611904300234573201")]
    public void Valid_ibans_parse(string input)
    {
        var iban = Iban.Parse(input);

        Assert.Equal(input, iban.Value);
        Assert.Equal(input[..2], iban.CountryCode);
    }

    [Fact]
    public void Spaces_and_lower_case_are_normalised()
    {
        var iban = Iban.Parse(" de89 3704 0044 0532 0130 00 ");

        Assert.Equal("DE89370400440532013000", iban.Value);
    }

    [Theory]
    [InlineData("DE89370400440532013001")]   // checksum wrong (last digit changed)
    [InlineData("DE98370400440532013000")]   // check digits swapped
    [InlineData("DE8937040044053201300")]    // 21 characters, Germany needs 22
    [InlineData("DE893704004405320130000")]  // 23 characters
    [InlineData("XX89370400440532013000")]   // unknown country
    [InlineData("DE89-3704-0044-0532-0130-00")] // illegal characters
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Invalid_ibans_are_rejected(string? input)
    {
        Assert.False(Iban.TryParse(input, out var iban));
        Assert.Null(iban);
    }

    [Fact]
    public void Parse_throws_FormatException_for_invalid_input()
    {
        Assert.Throws<FormatException>(() => Iban.Parse("DE89370400440532013001"));
    }

    [Fact]
    public void ToString_is_masked_so_logs_never_contain_the_full_iban()
    {
        var iban = Iban.Parse("DE89370400440532013000");

        Assert.Equal("DE89 **** **** **** **30 00", iban.ToString());
        Assert.DoesNotContain("0532", iban.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Two_parses_of_the_same_iban_are_equal()
    {
        Assert.Equal(Iban.Parse("DE89 3704 0044 0532 0130 00"), Iban.Parse("DE89370400440532013000"));
    }
}
