using System.Globalization;
using System.Text;
using System.Xml.Linq;
using EInvoice.IntegrationTests.Infrastructure;

namespace EInvoice.IntegrationTests.TestData;

/// <summary>
/// Builds a synthetic XRechnung 3.0.2 UBL invoice for given seller, order and lines.
/// Starts from the official KoSIT sample 01.01 (Apache-2.0) so every XRechnung-required element at document level
/// (buyer, Leitweg-ID, seller contact, payment terms) is valid; seller identity, order reference, lines, VAT breakdown
/// and totals are replaced and computed consistently. The Kosit test category checks the output with the real validator.
/// </summary>
internal sealed class XRechnungBuilder
{
    private static readonly XNamespace Cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    private static readonly XNamespace Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";

    private readonly List<(string Name, decimal Quantity, decimal UnitPrice, decimal TaxRate, string? OrderLine)> _lines = [];

    public string InvoiceNumber { get; set; } = $"RE-T-{Guid.NewGuid():N}"[..20];

    public DateOnly IssueDate { get; set; } = new(2026, 11, 2);

    public string SellerName { get; set; } = "Beispiel Bürobedarf GmbH";

    public string SellerVatId { get; set; } = "DE000000001";

    public string Iban { get; set; } = "DE72000000000000001001";

    public string? PurchaseOrderNumber { get; set; }

    public XRechnungBuilder Line(string name, decimal quantity, decimal unitPrice, decimal taxRate = 19m, string? orderLine = null)
    {
        _lines.Add((name, quantity, unitPrice, taxRate, orderLine));
        return this;
    }

    public byte[] Build() => Encoding.UTF8.GetBytes(BuildXml());

    public string BuildXml()
    {
        var template = Path.Combine(SqlServerFixture.RepositoryRoot(), "testdata", "xrechnung", "kosit", "01.01a-INVOICE_ubl.xml");
        var doc = XDocument.Load(template);
        var root = doc.Root!;

        root.Element(Cbc + "ID")!.Value = InvoiceNumber;
        root.Element(Cbc + "IssueDate")!.Value = IssueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        if (PurchaseOrderNumber is not null)
        {
            // UBL order: ... BuyerReference, OrderReference, ..., AccountingSupplierParty.
            root.Element(Cac + "AccountingSupplierParty")!.AddBeforeSelf(new XElement(Cac + "OrderReference", new XElement(Cbc + "ID", PurchaseOrderNumber)));
        }

        var seller = root.Element(Cac + "AccountingSupplierParty")!.Element(Cac + "Party")!;
        seller.Element(Cac + "PartyLegalEntity")!.Element(Cbc + "RegistrationName")!.Value = SellerName;
        seller.Element(Cac + "PartyName")?.Element(Cbc + "Name")?.SetValue(SellerName);
        seller.Elements(Cac + "PartyTaxScheme")
            .Single(s => s.Element(Cac + "TaxScheme")!.Element(Cbc + "ID")!.Value == "VAT")
            .Element(Cbc + "CompanyID")!.Value = SellerVatId;

        foreach (var account in root.Elements(Cac + "PaymentMeans").Select(m => m.Element(Cac + "PayeeFinancialAccount")).OfType<XElement>())
        {
            account.Element(Cbc + "ID")!.Value = Iban;
        }

        var lines = _lines.Select((l, i) => (Line: l, Number: i + 1, Net: Round(l.Quantity * l.UnitPrice))).ToList();
        var breakdown = lines
            .GroupBy(l => l.Line.TaxRate)
            .OrderBy(g => g.Key)
            .Select(g => (Rate: g.Key, Taxable: g.Sum(l => l.Net), Tax: Round(g.Sum(l => l.Net) * g.Key / 100m)))
            .ToList();
        var net = lines.Sum(l => l.Net);
        var tax = breakdown.Sum(b => b.Tax);

        var taxTotal = root.Element(Cac + "TaxTotal")!;
        taxTotal.ReplaceNodes(
            Amount("TaxAmount", tax),
            breakdown.Select(b => new XElement(Cac + "TaxSubtotal",
                Amount("TaxableAmount", b.Taxable),
                Amount("TaxAmount", b.Tax),
                TaxCategory(b.Rate))));

        root.Element(Cac + "LegalMonetaryTotal")!.ReplaceNodes(
            Amount("LineExtensionAmount", net),
            Amount("TaxExclusiveAmount", net),
            Amount("TaxInclusiveAmount", net + tax),
            Amount("PayableAmount", net + tax));

        root.Elements(Cac + "InvoiceLine").Remove();
        root.Add(lines.Select(l => new XElement(Cac + "InvoiceLine",
            new XElement(Cbc + "ID", l.Number.ToString(CultureInfo.InvariantCulture)),
            new XElement(Cbc + "InvoicedQuantity", new XAttribute("unitCode", "C62"), Format(l.Line.Quantity)),
            Amount("LineExtensionAmount", l.Net),
            l.Line.OrderLine is null ? null : new XElement(Cac + "OrderLineReference", new XElement(Cbc + "LineID", l.Line.OrderLine)),
            new XElement(Cac + "Item",
                new XElement(Cbc + "Name", l.Line.Name),
                new XElement(Cac + "ClassifiedTaxCategory",
                    new XElement(Cbc + "ID", "S"),
                    new XElement(Cbc + "Percent", Format(l.Line.TaxRate)),
                    new XElement(Cac + "TaxScheme", new XElement(Cbc + "ID", "VAT")))),
            new XElement(Cac + "Price", Amount("PriceAmount", l.Line.UnitPrice, priceDecimals: true)))));

        return doc.Declaration + Environment.NewLine + doc.Root;
    }

    private static decimal Round(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    private static string Format(decimal value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static XElement Amount(string name, decimal value, bool priceDecimals = false) =>
        new(Cbc + name, new XAttribute("currencyID", "EUR"),
            priceDecimals ? Format(value) : value.ToString("0.00", CultureInfo.InvariantCulture));

    private static XElement TaxCategory(decimal rate) =>
        new(Cac + "TaxCategory",
            new XElement(Cbc + "ID", "S"),
            new XElement(Cbc + "Percent", Format(rate)),
            new XElement(Cac + "TaxScheme", new XElement(Cbc + "ID", "VAT")));
}
