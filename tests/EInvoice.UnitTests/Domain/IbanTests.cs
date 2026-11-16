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

    /// <summary>
    /// Lengths from the SWIFT IBAN registry, written out here independently of Iban's own table, so a wrong or missing
    /// entry there fails here (found by mutation testing: most countries were never parsed by a test).
    /// </summary>
    public static TheoryData<string, int> Countries => new()
    {
        { "AT", 20 }, { "BE", 16 }, { "BG", 22 }, { "CH", 21 }, { "CY", 28 }, { "CZ", 24 }, { "DE", 22 }, { "DK", 18 },
        { "EE", 20 }, { "ES", 24 }, { "FI", 18 }, { "FR", 27 }, { "GB", 22 }, { "GR", 27 }, { "HR", 21 }, { "HU", 28 },
        { "IE", 22 }, { "IS", 26 }, { "IT", 27 }, { "LI", 21 }, { "LT", 20 }, { "LU", 20 }, { "LV", 21 }, { "MT", 31 },
        { "NL", 18 }, { "NO", 15 }, { "PL", 28 }, { "PT", 25 }, { "RO", 24 }, { "SE", 24 }, { "SI", 19 }, { "SK", 24 },
    };

    [Theory]
    [MemberData(nameof(Countries))]
    public void Every_supported_country_accepts_exactly_its_own_length(string country, int length)
    {
        // Synthetic accounts (all zeros) with correct check digits: only the length decides.
        Assert.True(Iban.TryParse(Synthetic(country, new string('0', length - 4)), out _));
        Assert.False(Iban.TryParse(Synthetic(country, new string('0', length - 5)), out _));
        Assert.False(Iban.TryParse(Synthetic(country, new string('0', length - 3)), out _));
    }

    [Fact]
    public void A_character_that_is_no_letter_or_digit_is_rejected_even_when_the_checksum_works_out()
    {
        // The checksum arithmetic would take '_' like a letter; only the character check stops it.
        Assert.False(Iban.TryParse(Synthetic("DE", "00000000000000_000"), out _));
    }

    /// <summary>An IBAN for the account part <paramref name="bban"/>, with check digits computed (ISO 7064 mod 97-10).</summary>
    private static string Synthetic(string country, string bban)
    {
        var remainder = 0;
        foreach (var c in bban + country + "00")
        {
            remainder = char.IsAsciiDigit(c) ? ((remainder * 10) + (c - '0')) % 97 : ((remainder * 100) + (c - 'A' + 10)) % 97;
        }

        return $"{country}{98 - remainder:00}{bban}";
    }
}
