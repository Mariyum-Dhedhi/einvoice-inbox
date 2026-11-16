using System.Globalization;
using System.Text;
using System.Xml.Linq;
using EInvoice.IntegrationTests.TestData;

namespace EInvoice.IntegrationTests.Performance;

/// <summary>
/// The invoice the k6 load test uploads (tests/load/invoice-template.xml): XRechnung for order PO-0101 (Platzhalter
/// Logistik GmbH), one line of copy paper at 4.89 EUR plus 19 % VAT, with placeholders for what changes per upload.
/// k6 fills them exactly like <see cref="Fill"/>; LoadTemplateTests proves that a filled template is the invoice
/// <see cref="XRechnungBuilder"/> builds for the same values, so every upload is a valid invoice.
/// </summary>
internal static class LoadInvoiceTemplate
{
    public const decimal UnitPrice = 4.89m;

    private static readonly XNamespace Cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    private static readonly XNamespace Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";

    public static XRechnungBuilder Invoice(string number, DateOnly issueDate, int quantity) =>
        new XRechnungBuilder
        {
            InvoiceNumber = number, IssueDate = issueDate, PurchaseOrderNumber = "PO-0101",
            SellerName = "Platzhalter Logistik GmbH", SellerVatId = "DE000000006", Iban = "DE34000000000000001006",
        }
        .Line("Copy paper A4, 500 sheets", quantity, UnitPrice, orderLine: "1");

    /// <summary>The template: the invoice for quantity 1 with every changing value replaced by its placeholder.</summary>
    public static string Build()
    {
        // Our own builder's output, not untrusted input: XDocument.Parse is fine here (SafeXml is for uploads).
        var doc = XDocument.Parse(Invoice("NUMBER", new DateOnly(2026, 1, 1), 1).BuildXml());
        var root = doc.Root!;
        root.Element(Cbc + "ID")!.Value = "{{NUMBER}}";
        root.Element(Cbc + "IssueDate")!.Value = "{{DATE}}";

        var tax = root.Element(Cac + "TaxTotal")!;
        tax.Element(Cbc + "TaxAmount")!.Value = "{{TAX}}";
        var subtotal = tax.Element(Cac + "TaxSubtotal")!;
        subtotal.Element(Cbc + "TaxableAmount")!.Value = "{{NET}}";
        subtotal.Element(Cbc + "TaxAmount")!.Value = "{{TAX}}";

        var totals = root.Element(Cac + "LegalMonetaryTotal")!;
        totals.Element(Cbc + "LineExtensionAmount")!.Value = "{{NET}}";
        totals.Element(Cbc + "TaxExclusiveAmount")!.Value = "{{NET}}";
        totals.Element(Cbc + "TaxInclusiveAmount")!.Value = "{{GROSS}}";
        totals.Element(Cbc + "PayableAmount")!.Value = "{{GROSS}}";

        var line = root.Element(Cac + "InvoiceLine")!;
        line.Element(Cbc + "InvoicedQuantity")!.Value = "{{QUANTITY}}";
        line.Element(Cbc + "LineExtensionAmount")!.Value = "{{NET}}";

        return Lf(doc.Declaration + "\n" + doc.Root);
    }

    /// <summary>What the k6 script does (fill in tests/load/upload-and-match.js): amounts in whole cents, VAT rounded half up.</summary>
    public static string Fill(string template, string number, DateOnly issueDate, int quantity)
    {
        var net = quantity * (long)(UnitPrice * 100);
        var tax = (net * 19 + 50) / 100;
        return template
            .Replace("{{NUMBER}}", number, StringComparison.Ordinal)
            .Replace("{{DATE}}", issueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{QUANTITY}}", quantity.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{NET}}", Cents(net), StringComparison.Ordinal)
            .Replace("{{TAX}}", Cents(tax), StringComparison.Ordinal)
            .Replace("{{GROSS}}", Cents(net + tax), StringComparison.Ordinal);
    }

    public static string Lf(string xml) => xml.Replace("\r\n", "\n", StringComparison.Ordinal);

    public static byte[] Bytes(string xml) => Encoding.UTF8.GetBytes(xml);

    private static string Cents(long cents) => string.Create(CultureInfo.InvariantCulture, $"{cents / 100}.{cents % 100:00}");
}
