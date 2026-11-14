using System.Globalization;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace EInvoice.UnitTests.Extraction;

/// <summary>
/// Builds plain PDF invoices for tests: a fictional seller, labelled header fields, a line table and totals in
/// German number format. Synthetic data only. Can also plant text a reader of the page cannot see.
/// </summary>
public sealed class SyntheticPdf
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    private readonly List<(string Description, decimal Quantity, decimal UnitPrice, decimal Rate, string? OrderLine)> _lines = [];
    private readonly List<(string Text, Hidden How)> _hidden = [];
    private readonly List<string> _extraVisible = [];

    public enum Hidden
    {
        WhiteOnWhite,
        InvisibleRenderingMode,
        TinyFont,
    }

    public string SellerName { get; set; } = "Beispiel Buerobedarf GmbH";

    public string SellerVatId { get; set; } = "DE000000001";

    public string InvoiceNumber { get; set; } = "RE-2026-1001";

    public DateOnly IssueDate { get; set; } = new(2026, 11, 2);

    public string? PurchaseOrder { get; set; } = "PO-0042";

    public string Iban { get; set; } = "DE72 0000 0000 0000 0010 01";

    public string ContactName { get; set; } = "Erika Mustermann";

    /// <summary>Overrides the printed totals (to build an invoice whose totals do not add up).</summary>
    public decimal? StatedTaxTotal { get; set; }

    public decimal? StatedGrossTotal { get; set; }

    public int Pages { get; set; } = 1;

    public decimal NetTotal => _lines.Sum(l => LineNet(l.Quantity, l.UnitPrice));

    public decimal TaxTotal => StatedTaxTotal ?? _lines.GroupBy(l => l.Rate).Sum(g => decimal.Round(g.Sum(l => LineNet(l.Quantity, l.UnitPrice)) * g.Key / 100m, 2, MidpointRounding.AwayFromZero));

    public decimal GrossTotal => StatedGrossTotal ?? NetTotal + TaxTotal;

    public SyntheticPdf Line(string description, decimal quantity, decimal unitPrice, decimal rate = 19m, string? orderLine = null)
    {
        _lines.Add((description, quantity, unitPrice, rate, orderLine));
        return this;
    }

    /// <summary>Text that is in the file but that nobody looking at the page can see.</summary>
    public SyntheticPdf HiddenText(string text, Hidden how = Hidden.WhiteOnWhite)
    {
        _hidden.Add((text, how));
        return this;
    }

    /// <summary>An extra visible line below the totals (for example a note, or injected instructions in plain sight).</summary>
    public SyntheticPdf VisibleNote(string text)
    {
        _extraVisible.Add(text);
        return this;
    }

    public static decimal LineNet(decimal quantity, decimal unitPrice) => decimal.Round(quantity * unitPrice, 2, MidpointRounding.AwayFromZero);

    public IReadOnlyList<string> VisibleLines()
    {
        var lines = new List<string>
        {
            SellerName,
            "Musterstrasse 1, 10115 Berlin",
            $"USt-IdNr.: {SellerVatId}",
            $"Ansprechpartner: {ContactName}",
            "Tel.: +49 30 1234567",
            "E-Mail: rechnung@beispiel.example",
            "Rechnung",
            $"Invoice number: {InvoiceNumber}",
            $"Invoice date: {IssueDate.ToString("dd.MM.yyyy", German)}",
        };
        if (PurchaseOrder is not null)
        {
            lines.Add($"Purchase order: {PurchaseOrder}");
        }

        lines.Add("Pos Description Qty Unit price Net VAT PO line");
        lines.AddRange(_lines.Select((l, i) =>
            $"{i + 1} | {l.Description} | {Number(l.Quantity, "0.####")} | {Number(l.UnitPrice, "0.00####")} | {Number(LineNet(l.Quantity, l.UnitPrice), "#,##0.00")} | {Number(l.Rate, "0.##")} % | {l.OrderLine ?? "-"}"));
        lines.Add($"Net total: {Number(NetTotal, "#,##0.00")} EUR");
        lines.Add($"VAT: {Number(TaxTotal, "#,##0.00")} EUR");
        lines.Add($"Gross total: {Number(GrossTotal, "#,##0.00")} EUR");
        lines.Add($"IBAN: {Iban}");
        lines.AddRange(_extraVisible);
        return lines;
    }

    public byte[] Build()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);

        var y = 800.0;
        foreach (var line in VisibleLines())
        {
            page.AddText(line, 10, new PdfPoint(50, y), font);
            y -= 16;
        }

        foreach (var (text, how) in _hidden)
        {
            switch (how)
            {
                case Hidden.WhiteOnWhite:
                    page.SetTextAndFillColor(255, 255, 255);
                    page.AddText(text, 10, new PdfPoint(50, y), font);
                    page.SetTextAndFillColor(0, 0, 0);
                    break;
                case Hidden.InvisibleRenderingMode:
                    page.SetTextRenderingMode(TextRenderingMode.Neither);
                    page.AddText(text, 10, new PdfPoint(50, y), font);
                    page.SetTextRenderingMode(TextRenderingMode.Fill);
                    break;
                default:
                    page.AddText(text, 0.5, new PdfPoint(50, y), font);
                    break;
            }

            y -= 16;
        }

        for (var extra = 1; extra < Pages; extra++)
        {
            builder.AddPage(PageSize.A4).AddText($"Page {extra + 1}", 10, new PdfPoint(50, 800), font);
        }

        return builder.Build();
    }

    private static string Number(decimal value, string format) => value.ToString(format, German);
}
