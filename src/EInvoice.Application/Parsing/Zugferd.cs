namespace EInvoice.Application.Parsing;

/// <summary>ZUGFeRD 2.x / Factur-X 1.0 profiles, recognised by their official identifier (BT-24).</summary>
public enum ZugferdProfile
{
    Unknown = 0,
    Minimum = 1,
    BasicWl = 2,
    Basic = 3,
    En16931 = 4,
    Extended = 5,
    XRechnung = 6,
}

/// <summary>
/// Which ZUGFeRD invoices are accepted and how they are checked. BASIC and above carry invoice lines and can be
/// matched and checked; MINIMUM and BASIC WL cannot. KoSIT is configured for XRechnung only, so only the XRECHNUNG
/// profile goes to it; the others get every document rule and a review finding that the official check did not run.
/// </summary>
public static class ZugferdProfiles
{
    private const string En16931 = "urn:cen.eu:en16931:2017";
    private const string XRechnungPrefix = "urn:cen.eu:en16931:2017#compliant#urn:xeinkauf.de:kosit:xrechnung_";

    public static ZugferdProfile Classify(string? identifier) => identifier?.Trim() switch
    {
        "urn:factur-x.eu:1p0:minimum" => ZugferdProfile.Minimum,
        "urn:factur-x.eu:1p0:basicwl" => ZugferdProfile.BasicWl,
        "urn:cen.eu:en16931:2017#compliant#urn:factur-x.eu:1p0:basic" => ZugferdProfile.Basic,
        En16931 => ZugferdProfile.En16931,
        "urn:cen.eu:en16931:2017#conformant#urn:factur-x.eu:1p0:extended" => ZugferdProfile.Extended,
        { } id when id.StartsWith(XRechnungPrefix, StringComparison.Ordinal) => ZugferdProfile.XRechnung,
        _ => ZugferdProfile.Unknown,
    };

    public static bool IsAccepted(ZugferdProfile profile) =>
        profile is ZugferdProfile.Basic or ZugferdProfile.En16931 or ZugferdProfile.Extended or ZugferdProfile.XRechnung;

    public static bool CheckedByKosit(ZugferdProfile profile) => profile == ZugferdProfile.XRechnung;

    public static string RefusalReason(ZugferdProfile profile) => profile switch
    {
        ZugferdProfile.Minimum => "ZUGFeRD profile MINIMUM carries no invoice lines, so the invoice cannot be matched or checked. Ask for profile BASIC or higher.",
        ZugferdProfile.BasicWl => "ZUGFeRD profile BASIC WL carries no invoice lines, so the invoice cannot be matched or checked. Ask for profile BASIC or higher.",
        _ => "The ZUGFeRD profile of this invoice is not supported. Ask for profile BASIC, EN 16931, EXTENDED or XRECHNUNG.",
    };
}

public enum EmbeddedInvoiceStatus
{
    /// <summary>No attachment under an official invoice name: a plain PDF.</summary>
    None = 1,

    Found = 2,

    /// <summary>More than one attachment under an official invoice name: none is chosen.</summary>
    Ambiguous = 3,

    TooLarge = 4,

    /// <summary>The PDF itself could not be read.</summary>
    Unreadable = 5,
}

/// <param name="Xml">The attachment's bytes; only when <see cref="EmbeddedInvoiceStatus.Found"/>.</param>
public sealed record EmbeddedInvoice(EmbeddedInvoiceStatus Status, byte[]? Xml = null, string? FileName = null)
{
    public static EmbeddedInvoice None { get; } = new(EmbeddedInvoiceStatus.None);
}

/// <summary>Finds the invoice XML inside a ZUGFeRD / Factur-X PDF. Never guesses: official names only, exactly one.</summary>
public interface IEmbeddedInvoiceReader
{
    EmbeddedInvoice Find(ReadOnlyMemory<byte> pdf);
}

/// <summary>For uses without ZUGFeRD support (and older tests): every PDF is a plain PDF.</summary>
public sealed class NoEmbeddedInvoices : IEmbeddedInvoiceReader
{
    public static NoEmbeddedInvoices Instance { get; } = new();

    public EmbeddedInvoice Find(ReadOnlyMemory<byte> pdf) => EmbeddedInvoice.None;
}
