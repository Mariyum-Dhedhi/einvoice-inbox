using System.Globalization;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace EInvoice.AiEvaluation;

/// <summary>
/// Turns a case into a plain PDF invoice in one of three layouts, so a model cannot do well by learning one format:
/// A English labels and a separated table (German numbers); B German labels, one sentence per line, seller details
/// in the footer (German numbers); C an international layout with a positioned table and English numbers.
/// Deterministic: the same case always gives the same bytes.
/// </summary>
public static class EvalPdfRenderer
{
    private const double FontSize = 10;
    private const double LineHeight = 15;
    private const double Left = 50;

    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    private sealed record Row(IReadOnlyList<(double X, string Text)> Cells);

    public static byte[] Render(EvalCase c)
    {
        ArgumentNullException.ThrowIfNull(c);
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PageSize.A4);

        var y = 800.0;
        foreach (var row in Rows(c))
        {
            foreach (var (x, text) in row.Cells)
            {
                page.AddText(text, FontSize, new PdfPoint(x, y), font);
            }

            y -= LineHeight;
        }

        if (c.HiddenText is { } hidden)
        {
            if (hidden.How == "TinyFont")
            {
                page.AddText(hidden.Text, 0.5, new PdfPoint(Left, y), font);
            }
            else
            {
                page.SetTextAndFillColor(255, 255, 255);
                page.AddText(hidden.Text, FontSize, new PdfPoint(Left, y), font);
                page.SetTextAndFillColor(0, 0, 0);
            }
        }

        return WithFixedId(builder.Build(), c.Id);
    }

    /// <summary>
    /// PdfPig writes a random document /ID (two hex strings) on every build, so the same case would give different
    /// bytes. The ID is replaced in place by one derived from the case id: same length, so every offset in the file stays valid.
    /// </summary>
    private static byte[] WithFixedId(byte[] pdf, string caseId)
    {
        var marker = "/ID ["u8;
        var at = pdf.AsSpan().IndexOf(marker);
        if (at < 0)
        {
            throw new InvalidOperationException("The PDF has no document ID to fix.");
        }

        var id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"einvoice-ai-eval/{caseId}")));
        var end = pdf.AsSpan(at).IndexOf((byte)']') + at;
        var written = 0;
        for (var i = at + marker.Length; i < end; i++)
        {
            if (pdf[i] == (byte)'<')
            {
                written = 0;
            }
            else if (Uri.IsHexDigit((char)pdf[i]))
            {
                pdf[i] = (byte)id[written++ % id.Length];
            }
        }

        return pdf;
    }

    /// <summary>The visible text, one line per row, as a reader of the page would see it.</summary>
    public static string RenderText(EvalCase c) =>
        string.Join('\n', Rows(c).Select(r => string.Join(' ', r.Cells.Select(cell => cell.Text))));

    private static List<Row> Rows(EvalCase c) => c.Layout switch
    {
        "A" => LayoutA(c),
        "B" => LayoutB(c),
        "C" => LayoutC(c),
        _ => throw new InvalidDataException($"{c.Id}: unknown layout {c.Layout}."),
    };

    private static Row Text(string text) => new([(Left, text)]);

    private static string Iban(string iban) => string.Join(' ', iban.Chunk(4).Select(chunk => new string(chunk)));

    private static string De(decimal value, string format = "#,##0.00") => value.ToString(format, German);

    private static string En(decimal value, string format = "#,##0.00") => value.ToString(format, English);

    private static List<Row> LayoutA(EvalCase c)
    {
        var t = c.Truth;
        var rows = new List<Row>
        {
            Text(t.SellerName),
            Text("Musterstrasse 1, 10115 Berlin"),
            Text($"VAT ID: {t.SellerVatId}"),
            Text("Contact: Erika Mustermann"),
            Text("Phone: +49 30 1234567"),
            Text("Invoice"),
            Text($"Invoice number: {c.InvoiceNumber}"),
            Text($"Invoice date: {c.IssueDate.ToString("dd.MM.yyyy", German)}"),
        };
        if (c.PurchaseOrder is not null)
        {
            rows.Add(Text($"Purchase order: {c.PurchaseOrder}"));
        }

        rows.Add(Text("Pos | Description | Qty | Unit price | Net | VAT | PO line"));
        rows.AddRange(t.Lines.Select((l, i) => Text(
            $"{i + 1} | {l.Description} | {De(l.Quantity, "0.####")} | {De(l.UnitPrice, "#,##0.00####")} | {De(l.NetAmount)} | {De(l.TaxRate, "0.##")} % | {l.OrderLine ?? "-"}")));
        rows.Add(Text($"Net total: {De(t.NetTotal)} EUR"));
        rows.Add(Text($"VAT: {De(t.TaxTotal)} EUR"));
        rows.Add(Text($"Gross total: {De(t.GrossTotal)} EUR"));
        rows.Add(Text($"IBAN: {Iban(t.Iban)}"));
        if (c.VisibleNote is not null)
        {
            rows.Add(Text(EvalTruth.Printable(c.VisibleNote)));
        }

        return rows;
    }

    private static List<Row> LayoutB(EvalCase c)
    {
        var t = c.Truth;
        var rows = new List<Row>
        {
            Text($"{t.SellerName} - Hauptstrasse 5 - 20095 Hamburg"),
            Text("RECHNUNG"),
            Text($"Rechnungsnummer: {c.InvoiceNumber}"),
            Text($"Rechnungsdatum: {c.IssueDate.ToString("dd.MM.yyyy", German)}"),
        };
        if (c.PurchaseOrder is not null)
        {
            rows.Add(Text($"Ihre Bestellnummer: {c.PurchaseOrder}"));
        }

        rows.Add(Text("Wir berechnen Ihnen:"));
        rows.AddRange(t.Lines.Select((l, i) => Text(
            $"Pos. {i + 1}: {l.Description}, {De(l.Quantity, "0.####")} x {De(l.UnitPrice, "#,##0.00####")} EUR = {De(l.NetAmount)} EUR, USt. {De(l.TaxRate, "0.##")} %"
            + (l.OrderLine is null ? string.Empty : $", Bestellposition {l.OrderLine}"))));
        rows.Add(Text($"Nettobetrag: {De(t.NetTotal)} EUR"));
        rows.Add(Text($"zzgl. Umsatzsteuer: {De(t.TaxTotal)} EUR"));
        rows.Add(Text($"Rechnungsbetrag: {De(t.GrossTotal)} EUR"));
        if (c.VisibleNote is not null)
        {
            rows.Add(Text(EvalTruth.Printable(c.VisibleNote)));
        }

        rows.Add(Text("Zahlbar innerhalb von 30 Tagen ohne Abzug."));
        rows.Add(Text($"{t.SellerName} | USt-IdNr.: {t.SellerVatId} | Ansprechpartner: Max Mustermann | Tel.: 040 9876543"));
        rows.Add(Text($"Bankverbindung: Musterbank, IBAN {Iban(t.Iban)}"));
        return rows;
    }

    private static List<Row> LayoutC(EvalCase c)
    {
        var t = c.Truth;
        var rows = new List<Row>
        {
            Text("INVOICE"),
            Text($"From: {t.SellerName}, 1 Example Road, Munich"),
            Text($"VAT reg. no. {t.SellerVatId}"),
            Text($"No. {c.InvoiceNumber}"),
            Text($"Date: {c.IssueDate.ToString("MMMM d, yyyy", English)}"),
        };
        if (c.PurchaseOrder is not null)
        {
            rows.Add(Text($"Your order: {c.PurchaseOrder}"));
        }

        // A positioned table: columns are placed on the page, no separators between them.
        rows.Add(new Row([(Left, "Item"), (300, "Qty"), (335, "Unit price"), (405, "Amount"), (470, "Tax"), (510, "PO line")]));
        rows.AddRange(t.Lines.Select(l => new Row([
            (Left, l.Description), (300, En(l.Quantity, "0.####")), (335, En(l.UnitPrice, "#,##0.00####")), (405, En(l.NetAmount)),
            (470, $"{En(l.TaxRate, "0.##")}%"), (510, l.OrderLine ?? "-"),
        ])));
        rows.Add(Text($"Subtotal EUR {En(t.NetTotal)}"));
        rows.Add(Text($"VAT EUR {En(t.TaxTotal)}"));
        rows.Add(Text($"Total due EUR {En(t.GrossTotal)}"));
        rows.Add(Text($"Pay to IBAN {Iban(t.Iban)}"));
        rows.Add(Text("Questions? billing@example.invalid"));
        if (c.VisibleNote is not null)
        {
            rows.Add(Text(EvalTruth.Printable(c.VisibleNote)));
        }

        return rows;
    }
}
