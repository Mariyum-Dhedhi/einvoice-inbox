using System.Globalization;

namespace EInvoice.IntegrationTests.TestData;

/// <summary>
/// Valid German IBANs that cannot be real: bank code 00000000 does not exist. Random account numbers keep tests that
/// share the database apart; the check digits are computed (ISO 7064 mod 97-10), so every one passes validation.
/// </summary>
public static class TestIbans
{
    public static string New()
    {
        var account = Random.Shared.NextInt64(1_000_000_000L, 9_999_999_999L).ToString(CultureInfo.InvariantCulture);
        var bban = "00000000" + account;
        var numeric = bban + "131400"; // "DE" = 13 14, check digits 00 for the calculation
        var remainder = numeric.Aggregate(0, (r, c) => ((r * 10) + (c - '0')) % 97);
        return $"DE{98 - remainder:00}{bban}";
    }
}
