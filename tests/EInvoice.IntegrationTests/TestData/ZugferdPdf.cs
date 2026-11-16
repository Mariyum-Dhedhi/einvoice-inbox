using System.Globalization;
using System.Text;

namespace EInvoice.IntegrationTests.TestData;

/// <summary>
/// Writes a small PDF with embedded files, like a ZUGFeRD / Factur-X invoice: one page of visible text, and each
/// attachment registered in the document's EmbeddedFiles name tree and its /AF list. Built byte by byte (with a
/// correct cross-reference table) because the PDF writer used elsewhere cannot attach files. Synthetic data only.
/// </summary>
public static class ZugferdPdf
{
    public static byte[] Build(string visibleText, params (string Name, byte[] Content)[] attachments)
    {
        ArgumentNullException.ThrowIfNull(attachments);
        var objects = new List<byte[]>();
        int Add(byte[] body)
        {
            objects.Add(body);
            return objects.Count;
        }

        // Fixed numbers for the page tree; attachments follow.
        const int catalog = 1, pages = 2, page = 3, contents = 4, font = 5;
        var fileSpecs = new List<(string Name, int Spec)>();
        objects.AddRange(Enumerable.Repeat(Array.Empty<byte>(), font)); // objects 1-5, filled in below

        foreach (var (name, content) in attachments.OrderBy(a => a.Name, StringComparer.Ordinal))
        {
            var stream = Add(Stream($"<< /Type /EmbeddedFile /Subtype /text#2Fxml /Length {content.Length} >>", content));
            var spec = Add(Ascii($"<< /Type /Filespec /F {Text(name)} /UF {Text(name)} /EF << /F {stream} 0 R >> /AFRelationship /Alternative /Desc (Invoice data) >>"));
            fileSpecs.Add((name, spec));
        }

        var names = string.Join(' ', fileSpecs.Select(f => $"{Text(f.Name)} {f.Spec} 0 R"));
        var af = string.Join(' ', fileSpecs.Select(f => $"{f.Spec} 0 R"));
        objects[catalog - 1] = Ascii(fileSpecs.Count == 0
            ? $"<< /Type /Catalog /Pages {pages} 0 R >>"
            : $"<< /Type /Catalog /Pages {pages} 0 R /Names << /EmbeddedFiles << /Names [{names}] >> >> /AF [{af}] >>");
        objects[pages - 1] = Ascii($"<< /Type /Pages /Kids [{page} 0 R] /Count 1 >>");
        objects[page - 1] = Ascii($"<< /Type /Page /Parent {pages} 0 R /MediaBox [0 0 595 842] /Contents {contents} 0 R /Resources << /Font << /F1 {font} 0 R >> >> >>");
        var text = Ascii($"BT /F1 12 Tf 72 770 Td {Text(visibleText)} Tj ET");
        objects[contents - 1] = Stream($"<< /Length {text.Length} >>", text);
        objects[font - 1] = Ascii("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");

        using var pdf = new MemoryStream();
        Write(pdf, "%PDF-1.7\n%âãÏÓ\n");
        var offsets = new List<long>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(pdf.Position);
            Write(pdf, $"{i + 1} 0 obj\n");
            pdf.Write(objects[i]);
            Write(pdf, "\nendobj\n");
        }

        var xref = pdf.Position;
        Write(pdf, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            Write(pdf, $"{offset.ToString("D10", CultureInfo.InvariantCulture)} 00000 n \n");
        }

        Write(pdf, $"trailer\n<< /Size {objects.Count + 1} /Root {catalog} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return pdf.ToArray();
    }

    /// <summary>A ZUGFeRD invoice: the CII XML as factur-x.xml.</summary>
    public static byte[] Invoice(byte[] ciiXml, string attachmentName = "factur-x.xml") =>
        Build("Rechnung - die Rechnungsdaten sind als XML eingebettet.", (attachmentName, ciiXml));

    private static byte[] Stream(string dictionary, byte[] content) =>
        [.. Ascii(dictionary + "\nstream\n"), .. content, .. Ascii("\nendstream")];

    /// <summary>A PDF literal string, with the characters that need it escaped.</summary>
    private static string Text(string value) =>
        "(" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("(", "\\(", StringComparison.Ordinal).Replace(")", "\\)", StringComparison.Ordinal) + ")";

    private static byte[] Ascii(string value) => Encoding.Latin1.GetBytes(value);

    private static void Write(Stream stream, string value) => stream.Write(Encoding.Latin1.GetBytes(value));
}
