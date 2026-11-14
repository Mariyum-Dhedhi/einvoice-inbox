using System.Globalization;

namespace EInvoice.Web.Components.Shared;

/// <summary>
/// Numbers and dates as German accounts-payable clerks read them (1.234,56 €, 02.11.2026, German time).
/// Amount and unit are joined by a non-breaking space so they never wrap apart.
/// </summary>
public static class Formats
{
    private const string Nbsp = " ";
    private const string Missing = "–";
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    public static string Money(decimal? amount, string? currency) =>
        amount is { } value ? value.ToString("#,##0.00", German) + Nbsp + Symbol(currency) : Missing;

    /// <summary>Unit prices keep up to six decimals (as stored); they are never rounded to cents for display.</summary>
    public static string UnitPrice(decimal? price, string? currency) =>
        price is { } value ? value.ToString("#,##0.00####", German) + Nbsp + Symbol(currency) : Missing;

    public static string Quantity(decimal? quantity) => quantity is { } value ? value.ToString("#,##0.####", German) : Missing;

    public static string Percent(decimal? percent) =>
        percent is { } value ? value.ToString("+#,##0.00;-#,##0.00;0,00", German) + Nbsp + "%" : Missing;

    /// <summary>A setting, such as the tolerance: no sign.</summary>
    public static string Rate(decimal value) => value.ToString("#,##0.00", German) + Nbsp + "%";

    public static string Date(DateOnly? date) => date is { } value ? value.ToString("dd.MM.yyyy", German) : Missing;

    public static string Timestamp(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, Berlin).ToString("dd.MM.yyyy HH:mm", German);

    public static string FileSize(long bytes) =>
        bytes < 1024 ? $"{bytes}{Nbsp}B" : (bytes / 1024m).ToString("#,##0.#", German) + Nbsp + "KB";

    private static string Symbol(string? currency) => currency switch
    {
        null or "" => string.Empty,
        "EUR" => "€",
        _ => currency,
    };
}
