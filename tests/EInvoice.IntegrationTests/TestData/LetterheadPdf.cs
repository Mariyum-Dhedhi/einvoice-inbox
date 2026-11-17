using System.Buffers.Binary;
using System.Globalization;
using EInvoice.AiEvaluation;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace EInvoice.IntegrationTests.TestData;

/// <summary>
/// A plain PDF invoice laid out like a real supplier's, for the demo: a logo (an image), the company name as a
/// letterhead, the sender line above the address window, an information block on the right, a ruled table with
/// right-aligned numbers, totals per VAT rate and a three-column footer with registry, tax and bank details.
/// Everything is fictional (seeded vendor, "Musterbank", HRB 000000); the logo is drawn in code. Deterministic bytes.
/// The model only gets the page's visible text: the logo, an image, never reaches it.
/// </summary>
internal static class LetterheadPdf
{
    private const double Left = 56;
    private const double Right = 539;

    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    /// <summary>The letterhead details of the fictional supplier (not master data: the invoice states them).</summary>
    public sealed record Company(string Tagline, string Street, string City, string Phone, string Web, string Register, string Bic, (byte R, byte G, byte B) Colour);

    public static byte[] Render(EvalCase c, Company company)
    {
        ArgumentNullException.ThrowIfNull(c);
        var t = c.Truth;
        var builder = new PdfDocumentBuilder();
        var regular = builder.AddStandard14Font(Standard14Font.Helvetica);
        var bold = builder.AddStandard14Font(Standard14Font.HelveticaBold);
        var page = builder.AddPage(PageSize.A4);
        var (r, g, b) = company.Colour;

        void Write(string text, double x, double y, double size = 9.5, bool strong = false) =>
            page.AddText(text, size, new PdfPoint(x, y), strong ? bold : regular);

        void WriteRight(string text, double right, double y, double size = 9.5, bool strong = false)
        {
            var font = strong ? bold : regular;
            var letters = page.MeasureText(text, size, new PdfPoint(0, y), font);
            var width = letters.Count == 0 ? 0 : letters[^1].EndBaseLine.X - letters[0].StartBaseLine.X;
            page.AddText(text, size, new PdfPoint(right - width, y), font);
        }

        void Grey() => page.SetTextAndFillColor(90, 90, 90);
        void Black() => page.SetTextAndFillColor(0, 0, 0);

        // ---- Letterhead: logo, name, tagline; contact block on the right --------------------------------------------
        page.AddPng(Logo(company.Colour), new PdfRectangle(Left, 762, Left + 54, 816));
        page.SetTextAndFillColor(r, g, b);
        Write(t.SellerName, Left + 66, 795, 17, strong: true);
        Grey();
        Write(company.Tagline, Left + 66, 780, 8.5);
        WriteRight(company.Street, Right, 806, 8);
        WriteRight(company.City, Right, 795, 8);
        WriteRight($"Tel. {company.Phone}", Right, 784, 8);
        WriteRight(company.Web, Right, 773, 8);
        page.SetStrokeColor(r, g, b);
        page.DrawLine(new PdfPoint(Left, 750), new PdfPoint(Right, 750), 1.2);

        // ---- Sender line and address window (DIN 5008) ------------------------------------------------------------
        Grey();
        Write($"{t.SellerName} - {company.Street} - {company.City}", Left, 712, 7);
        page.SetStrokeColor(150, 150, 150);
        page.DrawLine(new PdfPoint(Left, 709), new PdfPoint(Left + 250, 709), 0.4);
        Black();
        string[] recipient = ["Beispielkunde AG", "Kreditorenbuchhaltung", "Postfach 10 20 30", "60311 Frankfurt am Main"];
        for (var i = 0; i < recipient.Length; i++)
        {
            Write(recipient[i], Left, 690 - (i * 13), 10);
        }

        // ---- Information block ------------------------------------------------------------------------------------------
        (string Label, string Value)[] info =
        [
            ("Rechnungsnummer", c.InvoiceNumber),
            ("Rechnungsdatum", c.IssueDate.ToString("dd.MM.yyyy", German)),
            ("Lieferdatum", c.IssueDate.AddDays(-3).ToString("dd.MM.yyyy", German)),
            ("Ihre Bestellung", c.PurchaseOrder ?? "-"),
            ("Kundennummer", "K-48213"),
        ];
        for (var i = 0; i < info.Length; i++)
        {
            Grey();
            Write(info[i].Label, 370, 690 - (i * 13), 8.5);
            Black();
            WriteRight(info[i].Value, Right, 690 - (i * 13), 9);
        }

        // ---- Title and greeting ---------------------------------------------------------------------------------------
        Write("Rechnung", Left, 590, 16, strong: true);
        Write("Sehr geehrte Damen und Herren,", Left, 566, 10);
        Write("fuer unsere Lieferung berechnen wir Ihnen wie folgt:", Left, 552, 10);

        // ---- Line table ---------------------------------------------------------------------------------------------------
        var y = 524.0;
        page.SetTextAndFillColor(236, 240, 244);
        page.DrawRectangle(new PdfPoint(Left, y - 5), Right - Left, 18, 0, fill: true);
        Black();
        Write("Pos.", Left + 4, y, 8.5, strong: true);
        Write("Beschreibung", Left + 34, y, 8.5, strong: true);
        WriteRight("Menge", 360, y, 8.5, strong: true);
        WriteRight("Einzelpreis", 430, y, 8.5, strong: true);
        WriteRight("USt.", 470, y, 8.5, strong: true);
        WriteRight("Betrag EUR", Right - 4, y, 8.5, strong: true);
        y -= 22;
        for (var i = 0; i < t.Lines.Count; i++)
        {
            var line = t.Lines[i];
            Write((i + 1).ToString(CultureInfo.InvariantCulture), Left + 4, y, 9.5);
            Write(line.Description, Left + 34, y, 9.5);
            // As real invoices do: the order line under the description, the quantity with its unit.
            Grey();
            Write(line.OrderLine is null ? "ohne Bestellbezug" : $"Ihre Bestellposition {line.OrderLine}", Left + 34, y - 10, 7.5);
            Black();
            WriteRight($"{line.Quantity.ToString("0.####", German)} Stk.", 360, y, 9.5);
            WriteRight(line.UnitPrice.ToString("#,##0.00####", German), 430, y, 9.5);
            WriteRight($"{line.TaxRate.ToString("0.##", German)} %", 470, y, 9.5);
            WriteRight(line.NetAmount.ToString("#,##0.00", German), Right - 4, y, 9.5);
            page.SetStrokeColor(220, 220, 220);
            page.DrawLine(new PdfPoint(Left, y - 15), new PdfPoint(Right, y - 15), 0.4);
            y -= 29;
        }

        // ---- Totals, one VAT line per rate --------------------------------------------------------------------------------
        y -= 8;
        Write("Summe netto", 330, y, 9.5);
        WriteRight($"{t.NetTotal.ToString("#,##0.00", German)} EUR", Right - 4, y, 9.5);
        foreach (var rate in t.Lines.GroupBy(l => l.TaxRate).OrderByDescending(gr => gr.Key))
        {
            y -= 15;
            var basis = rate.Sum(l => l.NetAmount);
            var tax = decimal.Round(basis * rate.Key / 100m, 2, MidpointRounding.AwayFromZero);
            Write($"zzgl. {rate.Key.ToString("0.##", German)} % USt. auf {basis.ToString("#,##0.00", German)}", 330, y, 9.5);
            WriteRight($"{tax.ToString("#,##0.00", German)} EUR", Right - 4, y, 9.5);
        }

        // The VAT total as well: a value the page does not state cannot be confirmed from it (EvidenceChecker).
        y -= 15;
        Write("Umsatzsteuer gesamt", 330, y, 9.5);
        WriteRight($"{t.TaxTotal.ToString("#,##0.00", German)} EUR", Right - 4, y, 9.5);

        y -= 8;
        page.SetStrokeColor(0, 0, 0);
        page.DrawLine(new PdfPoint(330, y), new PdfPoint(Right, y), 0.8);
        y -= 15;
        Write("Rechnungsbetrag", 330, y, 10.5, strong: true);
        WriteRight($"{t.GrossTotal.ToString("#,##0.00", German)} EUR", Right - 4, y, 10.5, strong: true);

        // ---- Payment terms ---------------------------------------------------------------------------------------------------
        y -= 40;
        Write($"Zahlbar bis {c.IssueDate.AddDays(30).ToString("dd.MM.yyyy", German)} ohne Abzug auf das unten genannte Konto.", Left, y, 9.5);
        Write("Vielen Dank fuer Ihren Auftrag.", Left, y - 14, 9.5);

        // ---- Footer: registry, tax, bank ----------------------------------------------------------------------------------
        page.SetStrokeColor(r, g, b);
        page.DrawLine(new PdfPoint(Left, 88), new PdfPoint(Right, 88), 0.8);
        Grey();
        string[][] footer =
        [
            [t.SellerName, company.Street, company.City],
            [company.Register, $"USt-IdNr.: {t.SellerVatId}", "Geschaeftsfuehrung: Max Mustermann"],
            ["Musterbank", $"IBAN {string.Join(' ', t.Iban.Chunk(4).Select(ch => new string(ch)))}", $"BIC {company.Bic}"],
        ];
        for (var column = 0; column < footer.Length; column++)
        {
            for (var row = 0; row < footer[column].Length; row++)
            {
                Write(footer[column][row], Left + (column * 165), 74 - (row * 10), 7);
            }
        }

        return WithFixedId(builder.Build(), c.Id);
    }

    // ---- The logo: a rounded square in the company colour with a white bolt and ring, drawn pixel by pixel. ------------

    private const int LogoSize = 160;

    private static byte[] Logo((byte R, byte G, byte B) colour)
    {
        const int sub = 3; // 3 x 3 samples per pixel: smooth edges
        var pixels = new byte[LogoSize * LogoSize * 3];
        (double X, double Y)[] bolt = [(0.56, 0.16), (0.30, 0.55), (0.48, 0.55), (0.42, 0.84), (0.70, 0.43), (0.52, 0.43), (0.60, 0.16)];
        for (var py = 0; py < LogoSize; py++)
        {
            for (var px = 0; px < LogoSize; px++)
            {
                double inSquare = 0, white = 0;
                for (var sy = 0; sy < sub; sy++)
                {
                    for (var sx = 0; sx < sub; sx++)
                    {
                        var x = (px + ((sx + 0.5) / sub)) / LogoSize;
                        var yy = (py + ((sy + 0.5) / sub)) / LogoSize;
                        if (!InRoundedSquare(x, yy, 0.18))
                        {
                            continue;
                        }

                        inSquare++;
                        var d = Math.Sqrt(((x - 0.5) * (x - 0.5)) + ((yy - 0.5) * (yy - 0.5)));
                        if ((d is > 0.36 and < 0.41) || InPolygon(x, yy, bolt))
                        {
                            white++;
                        }
                    }
                }

                var samples = sub * sub;
                var coloured = (inSquare - white) / samples;
                var lit = white / samples;
                var outside = 1 - (inSquare / samples);
                var i = ((py * LogoSize) + px) * 3;
                pixels[i] = Blend(colour.R, coloured, lit, outside);
                pixels[i + 1] = Blend(colour.G, coloured, lit, outside);
                pixels[i + 2] = Blend(colour.B, coloured, lit, outside);
            }
        }

        return Png(pixels, LogoSize, LogoSize);
    }

    private static byte Blend(byte channel, double coloured, double white, double outside) =>
        (byte)Math.Round((channel * coloured) + (255 * (white + outside)));

    private static bool InRoundedSquare(double x, double y, double radius)
    {
        var dx = Math.Max(Math.Max(radius - x, x - (1 - radius)), 0);
        var dy = Math.Max(Math.Max(radius - y, y - (1 - radius)), 0);
        return (dx * dx) + (dy * dy) <= radius * radius;
    }

    private static bool InPolygon(double x, double y, (double X, double Y)[] polygon)
    {
        var inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
        {
            if ((polygon[i].Y > y) != (polygon[j].Y > y)
                && x < ((polygon[j].X - polygon[i].X) * (y - polygon[i].Y) / (polygon[j].Y - polygon[i].Y)) + polygon[i].X)
            {
                inside = !inside;
            }
        }

        return inside;
    }

    /// <summary>
    /// A minimal PNG (8-bit RGB) with stored, uncompressed deflate blocks: the same bytes on every machine, no image
    /// library needed. Only for this synthetic logo.
    /// </summary>
    private static byte[] Png(byte[] rgb, int width, int height)
    {
        var raw = new byte[height * ((width * 3) + 1)];
        for (var row = 0; row < height; row++)
        {
            raw[row * ((width * 3) + 1)] = 0; // filter: none
            Array.Copy(rgb, row * width * 3, raw, (row * ((width * 3) + 1)) + 1, width * 3);
        }

        using var zlib = new MemoryStream();
        zlib.Write([0x78, 0x01]);
        for (var offset = 0; offset < raw.Length; offset += 65535)
        {
            var length = Math.Min(65535, raw.Length - offset);
            zlib.WriteByte(offset + length == raw.Length ? (byte)1 : (byte)0);
            zlib.Write([(byte)length, (byte)(length >> 8), (byte)~length, (byte)(~length >> 8)]);
            zlib.Write(raw, offset, length);
        }

        Span<byte> adler = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(adler, Adler32(raw));
        zlib.Write(adler);

        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bit depth
        header[9] = 2; // RGB
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", zlib.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void Chunk(Stream png, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        png.Write(length);
        var typed = new byte[4 + data.Length];
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(typed, 0);
        data.CopyTo(typed, 4);
        png.Write(typed);
        Span<byte> crc = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crc, Crc32(typed));
        png.Write(crc);
    }

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
            {
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            }
        }

        return ~crc;
    }

    private static uint Adler32(byte[] data)
    {
        uint a = 1, b = 0;
        foreach (var d in data)
        {
            a = (a + d) % 65521;
            b = (b + a) % 65521;
        }

        return (b << 16) | a;
    }

    /// <summary>PdfPig writes a random document ID; replaced in place by one from the case id (same as EvalPdfRenderer).</summary>
    private static byte[] WithFixedId(byte[] pdf, string caseId)
    {
        var marker = "/ID ["u8;
        var at = pdf.AsSpan().IndexOf(marker);
        if (at < 0)
        {
            throw new InvalidOperationException("The PDF has no document ID to fix.");
        }

        var id = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"einvoice-demo/{caseId}")));
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
}
