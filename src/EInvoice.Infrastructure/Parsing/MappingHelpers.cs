using System.Globalization;
using System.Xml.Linq;
using EInvoice.Application.Parsing;

namespace EInvoice.Infrastructure.Parsing;

/// <summary>
/// Value extraction shared by both syntaxes. Messages name the business term (BT-n) and never the value,
/// because the value comes from an untrusted document.
/// </summary>
internal static class MappingHelpers
{
    public static string Required(XElement? element, string term) =>
        Optional(element) ?? throw new DocumentRejectedException(ParseErrorCode.MissingField, $"{term} is missing.");

    public static string? Optional(XElement? element)
    {
        var value = element?.Value.Trim();
        return string.IsNullOrEmpty(value) ? null : value;
    }

    public static decimal RequiredDecimal(XElement? element, string term) => ParseDecimal(Required(element, term), term);

    public static decimal? OptionalDecimal(XElement? element, string term) =>
        Optional(element) is { } text ? ParseDecimal(text, term) : null;

    /// <summary>XML decimals use a dot and no thousands separator, whatever the server's culture.</summary>
    private static decimal ParseDecimal(string text, string term) =>
        decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new DocumentRejectedException(ParseErrorCode.InvalidValue, $"{term} is not a valid number.");

    public static DateOnly RequiredDate(XElement? element, string format, string term) =>
        DateOnly.TryParseExact(Required(element, term), format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new DocumentRejectedException(ParseErrorCode.InvalidValue, $"{term} is not a valid date.");

    /// <summary>VAT ids and IBANs are often written with spaces ("DE 123456789"); spaces carry no meaning.</summary>
    public static string? WithoutSpaces(string? value) =>
        value is null ? null : string.Concat(value.Where(c => !char.IsWhiteSpace(c))).ToUpperInvariant();

    /// <summary>
    /// The VAT total can appear twice: in the invoice currency (BT-110) and in a separate tax currency (BT-111).
    /// BT-110 is the one whose currencyID matches the invoice currency; without currencyID a single amount is BT-110.
    /// </summary>
    public static decimal TaxTotalInInvoiceCurrency(IEnumerable<XElement> amounts, string currency)
    {
        var list = amounts.ToList();
        var match = list.FirstOrDefault(a => (string?)a.Attribute("currencyID") == currency)
                    ?? (list.Count == 1 && list[0].Attribute("currencyID") is null ? list[0] : null);
        return RequiredDecimal(match, "BT-110 invoice total VAT amount");
    }

    /// <summary>Charge indicator of an allowance/charge: "true" is a charge, "false" an allowance (xs:boolean, lower case).</summary>
    public static bool IsCharge(XElement? indicator, int position) => Required(indicator, $"charge indicator on line {position}") switch
    {
        "true" => true,
        "false" => false,
        _ => throw new DocumentRejectedException(ParseErrorCode.InvalidValue, $"Charge indicator on line {position} must be true or false."),
    };

    public static decimal UnitPrice(decimal netPrice, decimal? baseQuantity, int position)
    {
        var divisor = baseQuantity ?? 1m;
        return divisor > 0m
            ? netPrice / divisor
            : throw new DocumentRejectedException(ParseErrorCode.InvalidValue, $"BT-149 base quantity on line {position} must be greater than zero.");
    }
}
