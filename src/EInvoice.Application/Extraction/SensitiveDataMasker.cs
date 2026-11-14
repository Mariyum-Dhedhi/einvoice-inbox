using System.Globalization;
using System.Text.RegularExpressions;

namespace EInvoice.Application.Extraction;

/// <summary>Document text with bank accounts and contact details replaced by placeholders.</summary>
public sealed class MaskedText
{
    internal MaskedText(string text, IReadOnlyDictionary<string, string> ibans)
    {
        Text = text;
        Ibans = ibans;
    }

    public string Text { get; }

    /// <summary>Placeholder ("[IBAN_1]") to the account it stands for, without spaces.</summary>
    public IReadOnlyDictionary<string, string> Ibans { get; }

    /// <summary>The account behind a placeholder, or null for anything that is not a placeholder from this document.</summary>
    public string? Unmask(string? value) => value is not null && Ibans.TryGetValue(value.Trim(), out var iban) ? iban : null;
}

/// <summary>
/// Removes what a model does not need before any text leaves the application: IBANs (replaced by numbered
/// placeholders that can be turned back afterwards), e-mail addresses, phone numbers, and the names that follow a
/// contact label. Names without a label cannot be recognised reliably and are not claimed to be masked.
/// </summary>
public static partial class SensitiveDataMasker
{
    public static MaskedText Mask(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        // Something that already looks like one of our placeholders is the supplier's text, not ours: defuse it first.
        var result = ExistingPlaceholder().Replace(text, m => $"({m.Groups[1].Value})");

        var placeholders = new Dictionary<string, string>(StringComparer.Ordinal); // account -> placeholder
        result = IbanPattern().Replace(result, m =>
        {
            var account = m.Value.Replace(" ", string.Empty, StringComparison.Ordinal);
            if (!placeholders.TryGetValue(account, out var placeholder))
            {
                placeholder = string.Create(CultureInfo.InvariantCulture, $"[IBAN_{placeholders.Count + 1}]");
                placeholders[account] = placeholder;
            }

            return placeholder;
        });

        result = ContactLine().Replace(result, "$1[CONTACT]");
        result = Email().Replace(result, "[EMAIL]");
        result = Phone().Replace(result, "$1[PHONE]");

        return new MaskedText(result, placeholders.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal));
    }

    [GeneratedRegex(@"\[(IBAN_\d+|CONTACT|EMAIL|PHONE)\]")]
    private static partial Regex ExistingPlaceholder();

    // Country, check digits, then 11 to 30 more characters, printed together or in groups of four.
    [GeneratedRegex(@"\b[A-Z]{2}\d{2}(?: ?[A-Z0-9]{4}){2,7}(?: ?[A-Z0-9]{1,3})?\b")]
    private static partial Regex IbanPattern();

    [GeneratedRegex(@"^([ \t]*(?:Ihre?[ \t]+)?(?:Ansprechpartner(?:in)?|Kontakt(?:person)?|Contact(?:[ \t]person)?|Sachbearbeiter(?:in)?|Attn\.?|z\.[ \t]?Hd\.?)[ \t]*:?[ \t]*)\S.*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex ContactLine();

    [GeneratedRegex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    [GeneratedRegex(@"((?:Tel(?:efon)?|Phone|Fax|Mobil(?:e)?|Handy)\.?[ \t]*:?[ \t]*)\+?\(?\d[\d ()/\-]{5,}\d", RegexOptions.IgnoreCase)]
    private static partial Regex Phone();
}
