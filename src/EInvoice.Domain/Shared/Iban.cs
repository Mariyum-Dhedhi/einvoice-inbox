using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace EInvoice.Domain.Shared;

/// <summary>
/// A checked International Bank Account Number (ISO 13616).
/// Only countries in the table below are accepted (SEPA area plus CH and GB).
/// <see cref="ToString"/> is masked so a full IBAN never ends up in a log by accident;
/// use <see cref="Value"/> only where the full number is genuinely needed.
/// </summary>
public sealed record Iban
{
    private static readonly FrozenDictionary<string, int> LengthByCountry = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["AT"] = 20, ["BE"] = 16, ["BG"] = 22, ["CH"] = 21, ["CY"] = 28, ["CZ"] = 24, ["DE"] = 22, ["DK"] = 18,
        ["EE"] = 20, ["ES"] = 24, ["FI"] = 18, ["FR"] = 27, ["GB"] = 22, ["GR"] = 27, ["HR"] = 21, ["HU"] = 28,
        ["IE"] = 22, ["IS"] = 26, ["IT"] = 27, ["LI"] = 21, ["LT"] = 20, ["LU"] = 20, ["LV"] = 21, ["MT"] = 31,
        ["NL"] = 18, ["NO"] = 15, ["PL"] = 28, ["PT"] = 25, ["RO"] = 24, ["SE"] = 24, ["SI"] = 19, ["SK"] = 24,
    }.ToFrozenDictionary(StringComparer.Ordinal);

    private Iban(string value) => Value = value;

    /// <summary>The full IBAN, upper case, without spaces.</summary>
    public string Value { get; }

    public string CountryCode => Value[..2];

    /// <summary>Parses an IBAN. The exception message never repeats the input, so it is safe to log.</summary>
    public static Iban Parse(string? input) =>
        TryParse(input, out var iban) ? iban : throw new FormatException("The value is not a valid IBAN.");

    public static bool TryParse(string? input, [NotNullWhen(true)] out Iban? iban)
    {
        iban = null;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var value = input.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

        if (value.Length < 4 || !value.All(char.IsAsciiLetterOrDigit)
            || !char.IsAsciiLetter(value[0]) || !char.IsAsciiLetter(value[1])
            || !char.IsAsciiDigit(value[2]) || !char.IsAsciiDigit(value[3]))
        {
            return false;
        }

        if (!LengthByCountry.TryGetValue(value[..2], out var expectedLength) || value.Length != expectedLength)
        {
            return false;
        }

        if (!HasValidChecksum(value))
        {
            return false;
        }

        iban = new Iban(value);
        return true;
    }

    /// <summary>Masked form, for example "DE89 **** **** **** **30 00": country, check digits and last four characters only.</summary>
    public override string ToString()
    {
        var masked = string.Concat(Value.AsSpan(0, 4), new string('*', Value.Length - 8), Value.AsSpan(Value.Length - 4));
        var grouped = new StringBuilder(masked.Length + (masked.Length / 4));
        for (var i = 0; i < masked.Length; i++)
        {
            if (i > 0 && i % 4 == 0)
            {
                grouped.Append(' ');
            }

            grouped.Append(masked[i]);
        }

        return grouped.ToString();
    }

    /// <summary>
    /// ISO 7064 mod 97-10: move the first four characters to the end, turn letters into numbers (A = 10 ... Z = 35),
    /// and the remainder of the resulting number divided by 97 must be 1. Computed digit by digit, so no big integers.
    /// </summary>
    private static bool HasValidChecksum(string value)
    {
        var rearranged = string.Concat(value.AsSpan(4), value.AsSpan(0, 4));
        var remainder = 0;
        foreach (var c in rearranged)
        {
            remainder = char.IsAsciiDigit(c)
                ? ((remainder * 10) + (c - '0')) % 97
                : ((remainder * 100) + (c - 'A' + 10)) % 97;
        }

        return remainder == 1;
    }
}
