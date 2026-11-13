using System.Xml.Linq;
using EInvoice.Application.Parsing;

namespace EInvoice.Infrastructure.Parsing;

/// <summary>
/// Parses XRechnung in either syntax. The syntax is detected from the root element's namespace,
/// never from the file name. Only invoices are accepted (type codes 380, 384, 389); credit notes are out of scope.
/// </summary>
public sealed class XRechnungParser(XmlLimits limits) : IInvoiceDocumentParser
{
    internal static readonly XNamespace UblInvoice = "urn:oasis:names:specification:ubl:schema:xsd:Invoice-2";
    internal static readonly XNamespace CiiInvoice = "urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100";

    private static readonly HashSet<string> SupportedTypeCodes = ["380", "384", "389"];

    public XRechnungParser()
        : this(XmlLimits.Default)
    {
    }

    public ParseResult Parse(Stream content)
    {
        try
        {
            var document = SafeXml.Load(content, limits);
            var root = document.Root!;

            ParsedDocument parsed;
            if (root.Name == UblInvoice + "Invoice")
            {
                parsed = UblMapper.Map(root);
            }
            else if (root.Name == CiiInvoice + "CrossIndustryInvoice")
            {
                parsed = CiiMapper.Map(root);
            }
            else
            {
                return ParseResult.Failure(ParseErrorCode.UnsupportedDocument,
                    "The document is not an XRechnung invoice (expected a UBL Invoice or a CII CrossIndustryInvoice).");
            }

            if (!SupportedTypeCodes.Contains(parsed.InvoiceTypeCode))
            {
                return ParseResult.Failure(ParseErrorCode.UnsupportedDocument,
                    "BT-3 invoice type code is not supported (accepted: 380, 384, 389).");
            }

            return ParseResult.Success(parsed);
        }
        catch (DocumentRejectedException ex)
        {
            return ParseResult.Failure(ex.Code, ex.Message);
        }
    }
}
