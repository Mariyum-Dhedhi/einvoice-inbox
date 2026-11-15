using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EInvoice.AiEvaluation;

/// <summary>testdata/ai-eval/cases.json: the evaluation's answer key.</summary>
public sealed record EvalCaseFile(string Description, IReadOnlyList<EvalCase> Cases)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static string Folder(string repositoryRoot) => Path.Combine(repositoryRoot, "testdata", "ai-eval");

    public static string DefaultPath(string repositoryRoot) => Path.Combine(Folder(repositoryRoot), "cases.json");

    public static string PdfPath(string repositoryRoot, string id) => Path.Combine(Folder(repositoryRoot), $"{id}.pdf");

    /// <summary>Unknown properties are an error: a typo in the answer key must not be silently ignored.</summary>
    public static EvalCaseFile Load(string path) =>
        JsonSerializer.Deserialize<EvalCaseFile>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("The case file is empty.");

    public static string FindRepositoryRoot(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "EInvoice.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root (EInvoice.slnx) not found.");
    }
}

/// <param name="Vendor">Seeded vendor number (1 to 20), or null for a seller that is not a vendor.</param>
/// <param name="Iban">Overrides the vendor's approved account.</param>
public sealed record EvalCase(
    string Id,
    string Type,
    string Layout,
    int? Vendor,
    string? Note,
    string? SellerName,
    string? SellerVatId,
    string? Iban,
    string InvoiceNumber,
    DateOnly IssueDate,
    string? PurchaseOrder,
    IReadOnlyList<EvalLine> Lines,
    decimal? StatedTaxTotal,
    decimal? StatedGrossTotal,
    EvalHiddenText? HiddenText,
    string? VisibleNote,
    EvalOrder? Order,
    EvalExpected Expected)
{
    /// <summary>What the PDF states, including the supplier's deliberate mistakes. Extraction is scored against this.</summary>
    [JsonIgnore]
    public EvalTruth Truth => EvalTruth.Of(this);
}

/// <param name="StatedNetAmount">A printed line amount that differs from quantity x price (a supplier mistake).</param>
public sealed record EvalLine(string Description, decimal Quantity, decimal UnitPrice, decimal TaxRate, string? OrderLine, decimal? StatedNetAmount = null)
{
    [JsonIgnore]
    public decimal NetAmount => StatedNetAmount ?? decimal.Round(Quantity * UnitPrice, 2, MidpointRounding.AwayFromZero);
}

/// <param name="How">WhiteOnWhite or TinyFont.</param>
public sealed record EvalHiddenText(string Text, string How);

public sealed record EvalOrder(IReadOnlyList<EvalOrderLine> Lines, IReadOnlyList<EvalQuantity> Received, IReadOnlyList<EvalQuantity>? AlreadyInvoiced = null);

public sealed record EvalOrderLine(int Line, decimal Quantity, decimal UnitPrice);

public sealed record EvalQuantity(int Line, decimal Quantity);

/// <param name="Result">Matched, Exception or Refused.</param>
/// <param name="Reasons">For Exception: rule codes or match outcomes that must all be among the actual ones.</param>
/// <param name="Refusal">For Refused: the intake outcome, for example DuplicateInvoiceNumber.</param>
public sealed record EvalExpected(string Result, IReadOnlyList<string>? Reasons = null, string? Refusal = null);

public sealed record EvalTruth(string SellerName, string SellerVatId, string Iban, decimal NetTotal, decimal TaxTotal, decimal GrossTotal, IReadOnlyList<EvalLine> Lines)
{
    public static EvalTruth Of(EvalCase c)
    {
        ArgumentNullException.ThrowIfNull(c);
        var vendor = c.Vendor is { } n ? SeedVendors.Get(n) : null;
        var net = c.Lines.Sum(l => l.NetAmount);
        var tax = c.StatedTaxTotal ?? c.Lines.GroupBy(l => l.TaxRate)
            .Sum(g => decimal.Round(g.Sum(l => l.NetAmount) * g.Key / 100m, 2, MidpointRounding.AwayFromZero));
        return new EvalTruth(
            Printable(c.SellerName ?? vendor?.Name ?? throw new InvalidDataException($"{c.Id}: no seller name.")),
            c.SellerVatId ?? vendor?.VatId ?? throw new InvalidDataException($"{c.Id}: no seller VAT id."),
            c.Iban ?? vendor?.Iban ?? throw new InvalidDataException($"{c.Id}: no IBAN."),
            net,
            tax,
            c.StatedGrossTotal ?? net + tax,
            c.Lines.Select(l => l with { Description = Printable(l.Description) }).ToList());
    }

    /// <summary>
    /// The PDF fonts used here (the 14 standard fonts) have no umlauts, so German letters are written the way German
    /// systems without them do: ae, oe, ue, ss. The truth is what is printed. (Known limitation: no umlaut reading test.)
    /// </summary>
    public static string Printable(string text) =>
        (text ?? throw new ArgumentNullException(nameof(text)))
            .Replace("ä", "ae", StringComparison.Ordinal).Replace("ö", "oe", StringComparison.Ordinal).Replace("ü", "ue", StringComparison.Ordinal)
            .Replace("Ä", "Ae", StringComparison.Ordinal).Replace("Ö", "Oe", StringComparison.Ordinal).Replace("Ü", "Ue", StringComparison.Ordinal)
            .Replace("ß", "ss", StringComparison.Ordinal);
}

/// <summary>The fictional vendors from db/seed/seed.sql (keep in step with it).</summary>
public sealed record SeedVendor(int Number, Guid Id, string Name, string VatId, string Iban)
{
    public string VendorNumber => string.Create(CultureInfo.InvariantCulture, $"V-{1000 + Number}");
}

public static class SeedVendors
{
    private static readonly string[] Names =
    [
        "Beispiel Bürobedarf GmbH", "Muster Möbelwerk AG", "Fiktiv Elektro KG", "Demo Druck & Papier GmbH", "Test Technik Handel GmbH",
        "Platzhalter Logistik GmbH", "Probe Reinigungsdienst e.K.", "Exempel IT-Service GmbH", "Sample Office Supplies Ltd", "Musterfrau Catering GmbH",
        "Beispielhaft Werkzeuge OHG", "Demo Kabel & Co. KG", "Fiktiv Fachbuch Verlag GmbH", "Testlauf Sicherheitstechnik GmbH", "Probe Getränke GmbH",
        "Vorlage Büromaschinen AG", "Entwurf Gebäudeservice GmbH", "Beispiel Energie GmbH", "Muster Verpackung GmbH", "Ehemals Lieferant GmbH",
    ];

    private static readonly string[] CheckDigits =
        ["72", "45", "18", "88", "61", "34", "07", "77", "50", "23", "93", "66", "39", "12", "82", "55", "28", "98", "71", "44"];

    public static IReadOnlyList<SeedVendor> All { get; } = Enumerable.Range(1, 20).Select(n => new SeedVendor(
        n,
        Guid.Parse(string.Create(CultureInfo.InvariantCulture, $"00000000-0000-0000-0001-{n:000000000000}")),
        Names[n - 1],
        string.Create(CultureInfo.InvariantCulture, $"DE000000{n:000}"),
        string.Create(CultureInfo.InvariantCulture, $"DE{CheckDigits[n - 1]}00000000000000{1000 + n}"))).ToList();

    public static SeedVendor Get(int number) => All[number - 1];
}
