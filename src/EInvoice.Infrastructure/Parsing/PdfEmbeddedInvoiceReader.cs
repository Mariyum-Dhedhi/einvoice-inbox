using EInvoice.Application.Extraction;
using EInvoice.Application.Parsing;
using UglyToad.PdfPig;

namespace EInvoice.Infrastructure.Parsing;

/// <summary>
/// Finds the invoice XML attached to a ZUGFeRD / Factur-X PDF. Only the official file names count (any other
/// attachment could be anything); exactly one must be present; its size is limited. The PDF is parsed strictly, and
/// a PDF that cannot be read is reported, never guessed at. The XML is not read here: the parser does that, safely.
/// </summary>
public sealed class PdfEmbeddedInvoiceReader(PdfLimits limits, long maxXmlBytes) : IEmbeddedInvoiceReader
{
    /// <summary>ZUGFeRD 2.1+ / Factur-X, ZUGFeRD 2.0, and the XRECHNUNG profile.</summary>
    private static readonly HashSet<string> InvoiceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "factur-x.xml", "zugferd-invoice.xml", "xrechnung.xml",
    };

    public EmbeddedInvoice Find(ReadOnlyMemory<byte> pdf)
    {
        try
        {
            using var document = PdfDocument.Open(pdf.ToArray(), new ParsingOptions { UseLenientParsing = false });
            if (document.NumberOfPages > limits.MaxPages)
            {
                return new EmbeddedInvoice(EmbeddedInvoiceStatus.TooLarge);
            }

            if (!document.Advanced.TryGetEmbeddedFiles(out var files) || files.Count == 0)
            {
                return EmbeddedInvoice.None;
            }

            var invoices = files.Where(f => InvoiceNames.Contains(f.Name) || (f.FileSpecification is { } spec && InvoiceNames.Contains(spec))).ToList();
            return invoices.Count switch
            {
                0 => EmbeddedInvoice.None,
                > 1 => new EmbeddedInvoice(EmbeddedInvoiceStatus.Ambiguous),
                _ when invoices[0].Memory.Length > maxXmlBytes => new EmbeddedInvoice(EmbeddedInvoiceStatus.TooLarge),
                _ => new EmbeddedInvoice(EmbeddedInvoiceStatus.Found, invoices[0].Memory.ToArray(), invoices[0].Name),
            };
        }
        catch (Exception)
        {
            // A damaged or hostile PDF: whatever went wrong inside the PDF reader, the answer is the same.
            return new EmbeddedInvoice(EmbeddedInvoiceStatus.Unreadable);
        }
    }
}
