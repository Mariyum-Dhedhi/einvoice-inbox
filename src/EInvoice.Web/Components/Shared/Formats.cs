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
        percent is { } value ? value.ToString("+#,##0.00;-#,##0.00;0.00", German) + Nbsp + "%" : Missing;

    /// <summary>A setting, such as the tolerance: no sign.</summary>
    public static string Rate(decimal value) => value.ToString("#,##0.00", German) + Nbsp + "%";

    public static string Date(DateOnly? date) => date is { } value ? value.ToString("dd.MM.yyyy", German) : Missing;

    /// <summary>A full IBAN in groups of four, for an admin comparing it with a letter; never for logs or findings.</summary>
    public static string IbanGrouped(string iban) =>
        string.Join(' ', Enumerable.Range(0, (iban.Length + 3) / 4).Select(i => iban.Substring(i * 4, Math.Min(4, iban.Length - (i * 4)))));

    /// <summary>"November 2026".</summary>
    public static string MonthName(DateOnly month) => month.ToString("MMMM yyyy", German);

    /// <summary>A share with one decimal: "70,0 %".</summary>
    public static string Percent1(decimal percent) => percent.ToString("0.0", German) + Nbsp + "%";

    /// <summary>Today's date as people in Germany see it now.</summary>
    public static DateOnly TodayInBerlin() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Berlin).DateTime);

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
