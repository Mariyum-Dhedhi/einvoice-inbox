using System.Globalization;
using System.Text.RegularExpressions;

namespace EInvoice.Application.Extraction;

/// <summary>
/// Decides, without asking the model, whether each extracted value is backed by the document: the value must be
/// found in the visible text as a whole word or number, and amounts must agree with each other
/// (lines add up to the net total, net + VAT is the gross total, quantity x price is the line amount).
/// A value that fails is not wrong for certain; it is one a person has to confirm.
/// </summary>
public static partial class EvidenceChecker
{
    private const decimal Tolerance = 0.01m;
    private const string NotFound = "The value was not found in the visible text of the document.";
    private const string Missing = "No value was extracted.";
    private const string LeftOut = "The document appears to state a value, but none was extracted.";
    private const string NotAddingUp = "The value is in the document, but the amounts do not add up.";
    private const string LineNotAddingUp = "The line amount does not equal quantity x price, so this value may be wrong.";
    private const string SwallowedNumbers = "The item name contains the line's own quantity, price or amount; it was probably read together with them.";

    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    /// <param name="invoice">The extraction, with IBAN placeholders already turned back into accounts.</param>
    /// <param name="visibleText">The unmasked visible text of the PDF.</param>
    /// <param name="ibansInDocument">The accounts found in the document (without spaces).</param>
    public static IReadOnlyList<FieldConfidence> Check(ExtractedInvoice invoice, string visibleText, IReadOnlyCollection<string> ibansInDocument)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentNullException.ThrowIfNull(visibleText);
        ArgumentNullException.ThrowIfNull(ibansInDocument);

        var text = Normalize(visibleText);
        var rows = visibleText.Split('\n').Select(Normalize).ToList();
        var compact = Whitespace().Replace(visibleText, string.Empty).ToUpperInvariant();
        var lines = invoice.Lines ?? [];
        var fields = new List<FieldConfidence>
        {
            Header("BT-1", invoice.InvoiceNumber is null ? Missing : HasToken(text, invoice.InvoiceNumber) ? null : NotFound),
            Header("BT-2", invoice.IssueDate is null ? Missing : DateIsPrinted(text, invoice.IssueDate) ? null : NotFound),
            Header("BT-5", invoice.Currency is null ? Missing : CurrencyIsPrinted(text, invoice.Currency) ? null : NotFound),
            Header("BT-13", invoice.PurchaseOrderReference is null
                ? (OrderLabel().IsMatch(text) ? LeftOut : null)
                : HasToken(text, invoice.PurchaseOrderReference) ? null : NotFound),
            Header("BT-27", invoice.SellerName is null ? Missing : HasToken(text, invoice.SellerName) ? null : NotFound),
            Header("BT-31", invoice.SellerVatId is null
                ? (VatIdLabel().IsMatch(text) ? LeftOut : null)
                : compact.Contains(Whitespace().Replace(invoice.SellerVatId, string.Empty).ToUpperInvariant(), StringComparison.Ordinal) ? null : NotFound),
            Header("BT-84", IbanReason(invoice.Ibans ?? [], ibansInDocument)),
            Header("BT-109", invoice.NetTotal is not { } net ? Missing
                : !AmountIsPrinted(text, net) ? NotFound
                : lines.Any(l => l.NetAmount is null) || Math.Abs(lines.Sum(l => l.NetAmount ?? 0m) - net) > Tolerance ? NotAddingUp : null),
            Header("BT-110", invoice.TaxTotal is not { } tax ? Missing : AmountIsPrinted(text, tax) ? null : NotFound),
            Header("BT-112", invoice.GrossTotal is not { } gross ? Missing
                : !AmountIsPrinted(text, gross) ? NotFound
                : invoice.NetTotal is null || invoice.TaxTotal is null || Math.Abs(invoice.NetTotal.Value + invoice.TaxTotal.Value - gross) > Tolerance ? NotAddingUp : null),
        };

        foreach (var (line, position) in lines.Select((l, i) => (l, i + 1)))
        {
            FieldConfidence Line(string term, string? reason) => new(term, position, reason is null, reason);

            // Quantity x price must give the line amount. When it does not, the arithmetic cannot tell which of the
            // three values is wrong (a "12" taken from "crate 12 x 1 l" is in the document too), so all three are in doubt.
            var lineAddsUp = line.Quantity is { } q && line.UnitPrice is { } p && line.NetAmount is { } a
                && Math.Abs(decimal.Round(q * p, 2, MidpointRounding.AwayFromZero) - a) <= Tolerance;

            fields.Add(Line("BT-153", line.Description is null ? Missing
                : !HasToken(text, line.Description) ? NotFound
                : SwallowedOwnNumbers(Normalize(line.Description), line) ? SwallowedNumbers : null));
            fields.Add(Line("BT-129", line.Quantity is not { } quantity ? Missing
                : !AmountIsPrinted(text, quantity) ? NotFound
                : lineAddsUp || line.NetAmount is null || line.UnitPrice is null ? null : LineNotAddingUp));
            fields.Add(Line("BT-146", line.UnitPrice is not { } price ? Missing
                : !AmountIsPrinted(text, price) ? NotFound
                : lineAddsUp || line.NetAmount is null || line.Quantity is null ? null : LineNotAddingUp));
            fields.Add(Line("BT-131", line.NetAmount is not { } amount ? Missing
                : !AmountIsPrinted(text, amount) ? NotFound
                : lineAddsUp ? null : NotAddingUp));
            fields.Add(Line("BT-152", line.TaxRatePercent is not { } rate ? Missing : AmountIsPrinted(text, rate) ? null : NotFound));
            fields.Add(Line("BT-132", line.OrderLineReference is null
                ? (invoice.PurchaseOrderReference is not null && OrderLineLabel().IsMatch(text) && !NoReferencePrinted(rows, line) ? LeftOut : null)
                : HasToken(text, line.OrderLineReference) ? null : NotFound));
        }

        return fields;

        static FieldConfidence Header(string term, string? reason) => new(term, null, reason is null, reason);
    }

    private static string? IbanReason(IReadOnlyList<string> extracted, IReadOnlyCollection<string> inDocument)
    {
        if (extracted.Count == 0)
        {
            return inDocument.Count == 0 ? null : LeftOut;
        }

        // Only accounts that were masked out of this very document count; anything else did not come from it.
        return extracted.All(iban => inDocument.Contains(iban, StringComparer.Ordinal)) ? null : NotFound;
    }

    /// <summary>Upper case, runs of whitespace as one space: "found" must not depend on case or line breaks.</summary>
    private static string Normalize(string value) => Whitespace().Replace(value, " ").Trim().ToUpperInvariant();

    /// <summary>The value appears as a whole: not as the beginning, middle or end of a longer word or number.</summary>
    private static bool HasToken(string text, string value)
    {
        var token = Normalize(value);
        if (token.Length == 0)
        {
            return false;
        }

        for (var at = text.IndexOf(token, StringComparison.Ordinal); at >= 0; at = text.IndexOf(token, at + 1, StringComparison.Ordinal))
        {
            var before = at == 0 || !char.IsLetterOrDigit(text[at - 1]);
            var end = at + token.Length;
            var after = end == text.Length || !char.IsLetterOrDigit(text[end]);
            if (before && after)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The number appears in German or English notation and is not a piece of a longer number.</summary>
    private static bool AmountIsPrinted(string text, decimal value)
    {
        foreach (var candidate in Notations(value))
        {
            for (var at = text.IndexOf(candidate, StringComparison.Ordinal); at >= 0; at = text.IndexOf(candidate, at + 1, StringComparison.Ordinal))
            {
                if (!ContinuesNumber(text, at - 1, -1) && !ContinuesNumber(text, at + candidate.Length, 1))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>True when the character at <paramref name="index"/> shows the number goes on: a digit, or a separator followed by a digit.</summary>
    private static bool ContinuesNumber(string text, int index, int direction)
    {
        if (index < 0 || index >= text.Length)
        {
            return false;
        }

        if (char.IsDigit(text[index]))
        {
            return true;
        }

        var next = index + direction;
        return text[index] is '.' or ',' && next >= 0 && next < text.Length && char.IsDigit(text[next]);
    }

    private static IEnumerable<string> Notations(decimal value)
    {
        // As many decimals as the value has (7.4125), and with two decimals (39.90, 19,00) as amounts are usually printed.
        var formats = new HashSet<string>(StringComparer.Ordinal) { "0.######", "#,##0.######" };
        if (decimal.Round(value, 2) == value)
        {
            formats.Add("0.00");
            formats.Add("#,##0.00");
        }

        return formats.SelectMany(f => new[] { value.ToString(f, German), value.ToString(f, English) }).Distinct(StringComparer.Ordinal);
    }

    private static bool DateIsPrinted(string text, string isoDate)
    {
        if (!DateOnly.TryParseExact(isoDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return false;
        }

        string[] numeric = ["dd.MM.yyyy", "d.M.yyyy", "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy"];
        string[] written = ["d. MMMM yyyy", "d MMMM yyyy", "MMMM d, yyyy"];
        var candidates = numeric.Select(f => date.ToString(f, CultureInfo.InvariantCulture))
            .Concat(written.SelectMany(f => new[] { date.ToString(f, German), date.ToString(f, English) }));
        return candidates.Any(c => HasToken(text, c)); // whole token: "2.11.2026" is not found inside "12.11.2026"
    }

    private static bool CurrencyIsPrinted(string text, string currency)
    {
        var code = currency.Trim().ToUpperInvariant();
        var symbol = code switch { "EUR" => "€", "USD" => "$", "GBP" => "£", "CHF" => "FR.", _ => null };
        return HasToken(text, code) || (symbol is not null && text.Contains(symbol, StringComparison.Ordinal));
    }

    /// <summary>
    /// The item name holds the line's own unit price or amount, in decimal notation ("Laptop 14 Zoll, 2 x 1.265,00 EUR"):
    /// the model read past the end of the name. Numbers that belong to the product ("crate 12 x 1 l") are not affected.
    /// </summary>
    private static bool SwallowedOwnNumbers(string description, ExtractedLine line) =>
        new[] { line.UnitPrice, line.NetAmount }.OfType<decimal>()
            .SelectMany(Notations)
            .Where(n => n.Contains(',', StringComparison.Ordinal) || n.Contains('.', StringComparison.Ordinal))
            .Any(n => ContainsNumber(description, n));

    private static bool ContainsNumber(string text, string number)
    {
        for (var at = text.IndexOf(number, StringComparison.Ordinal); at >= 0; at = text.IndexOf(number, at + 1, StringComparison.Ordinal))
        {
            if (!ContinuesNumber(text, at - 1, -1) && !ContinuesNumber(text, at + number.Length, 1))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The item's own row shows that it has no order line: it ends with "-" (an empty column) or with its VAT rate
    /// (no reference after it). Rows are found by the item name; when the row cannot be found, nothing is assumed.
    /// </summary>
    private static bool NoReferencePrinted(IReadOnlyList<string> rows, ExtractedLine line)
    {
        if (line.Description is null)
        {
            return false;
        }

        var row = rows.FirstOrDefault(r => r.Contains(Normalize(line.Description), StringComparison.Ordinal));
        return row is not null && (row.EndsWith('-') || row.EndsWith('–') || row.EndsWith('%'));
    }

    [GeneratedRegex(@"PO LINE|ORDER LINE|BESTELLPOSITION|BEST\.-POS|BESTELLPOS")]
    private static partial Regex OrderLineLabel();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    // Labels that announce an order number. The text is already upper case.
    [GeneratedRegex(@"PURCHASE ORDER|ORDER NO|ORDER NUMBER|PO NUMBER|PO NO\b|BESTELLNUMMER|BESTELL-NR|BESTELLNR|BESTELLUNG|IHRE BESTELL")]
    private static partial Regex OrderLabel();

    [GeneratedRegex(@"UST-ID|USTID|UST\.-ID|UMSATZSTEUER-ID|VAT ID|VAT NO|VAT REG")]
    private static partial Regex VatIdLabel();
}
