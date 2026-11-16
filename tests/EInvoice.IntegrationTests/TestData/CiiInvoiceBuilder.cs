using System.Globalization;
using System.Text;
using System.Xml.Linq;
using EInvoice.IntegrationTests.Infrastructure;

namespace EInvoice.IntegrationTests.TestData;

/// <summary>
/// Synthetic CII invoices (the syntax inside ZUGFeRD / Factur-X PDFs), built from the official KoSIT CII sample so
/// the structure stays valid: number, date, seller, VAT id, IBAN, order reference, lines and totals are replaced,
/// and the profile (BT-24) can be chosen. Defaults match seeded vendor 1, like <see cref="XRechnungBuilder"/>.
/// </summary>
public sealed class CiiInvoiceBuilder
{
    private static readonly XNamespace Rsm = "urn:un:unece:uncefact:data:standard:CrossIndustryInvoice:100";
    private static readonly XNamespace Ram = "urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:100";
    private static readonly XNamespace Udt = "urn:un:unece:uncefact:data:standard:UnqualifiedDataType:100";

    private readonly List<(string Name, decimal Quantity, decimal UnitPrice, decimal TaxRate, string? OrderLine)> _lines = [];

    /// <summary>The official profile identifiers (BT-24) of ZUGFeRD 2.x / Factur-X 1.0.</summary>
    public static class Profiles
    {
        public const string Minimum = "urn:factur-x.eu:1p0:minimum";
        public const string BasicWl = "urn:factur-x.eu:1p0:basicwl";
        public const string Basic = "urn:cen.eu:en16931:2017#compliant#urn:factur-x.eu:1p0:basic";
        public const string En16931 = "urn:cen.eu:en16931:2017";
        public const string Extended = "urn:cen.eu:en16931:2017#conformant#urn:factur-x.eu:1p0:extended";
        public const string XRechnung = "urn:cen.eu:en16931:2017#compliant#urn:xeinkauf.de:kosit:xrechnung_3.0";
    }

    public string Profile { get; set; } = Profiles.En16931;

    public string InvoiceNumber { get; set; } = $"ZF-T-{Guid.NewGuid():N}"[..20];

    public DateOnly IssueDate { get; set; } = new(2026, 11, 2);

    public string SellerName { get; set; } = "Beispiel Bürobedarf GmbH";

    public string SellerVatId { get; set; } = "DE000000001";

    public string Iban { get; set; } = "DE72000000000000001001";

    public string? PurchaseOrderNumber { get; set; }

    public CiiInvoiceBuilder Line(string name, decimal quantity, decimal unitPrice, decimal taxRate = 19m, string? orderLine = null)
    {
        _lines.Add((name, quantity, unitPrice, taxRate, orderLine));
        return this;
    }

    public byte[] Build() => Encoding.UTF8.GetBytes(BuildXml());

    public string BuildXml()
    {
        var template = Path.Combine(SqlServerFixture.RepositoryRoot(), "testdata", "xrechnung", "kosit", "01.01a-INVOICE_uncefact.xml");
        var doc = XDocument.Load(template);
        var root = doc.Root!;

        root.Element(Rsm + "ExchangedDocumentContext")!.Element(Ram + "GuidelineSpecifiedDocumentContextParameter")!.Element(Ram + "ID")!.Value = Profile;
        var header = root.Element(Rsm + "ExchangedDocument")!;
        header.Element(Ram + "ID")!.Value = InvoiceNumber;
        header.Element(Ram + "IssueDateTime")!.Element(Udt + "DateTimeString")!.Value = IssueDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        var transaction = root.Element(Rsm + "SupplyChainTradeTransaction")!;
        var agreement = transaction.Element(Ram + "ApplicableHeaderTradeAgreement")!;
        var seller = agreement.Element(Ram + "SellerTradeParty")!;
        seller.Element(Ram + "Name")!.Value = SellerName;
        seller.Element(Ram + "SpecifiedTaxRegistration")!.Element(Ram + "ID")!.Value = SellerVatId;
        if (PurchaseOrderNumber is not null)
        {
            agreement.Element(Ram + "BuyerTradeParty")!.AddAfterSelf(
                new XElement(Ram + "BuyerOrderReferencedDocument", new XElement(Ram + "IssuerAssignedID", PurchaseOrderNumber)));
        }

        var settlement = transaction.Element(Ram + "ApplicableHeaderTradeSettlement")!;
        settlement.Element(Ram + "SpecifiedTradeSettlementPaymentMeans")!.Element(Ram + "PayeePartyCreditorFinancialAccount")!.Element(Ram + "IBANID")!.Value = Iban;

        var lines = _lines.Select((l, i) => (Line: l, Number: i + 1, Net: Round(l.Quantity * l.UnitPrice))).ToList();
        var breakdown = lines
            .GroupBy(l => l.Line.TaxRate)
            .OrderBy(g => g.Key)
            .Select(g => (Rate: g.Key, Taxable: g.Sum(l => l.Net), Tax: Round(g.Sum(l => l.Net) * g.Key / 100m)))
            .ToList();
        var net = lines.Sum(l => l.Net);
        var tax = breakdown.Sum(b => b.Tax);

        transaction.Elements(Ram + "IncludedSupplyChainTradeLineItem").Remove();
        agreement.AddBeforeSelf(lines.Select(l => new XElement(Ram + "IncludedSupplyChainTradeLineItem",
            new XElement(Ram + "AssociatedDocumentLineDocument", new XElement(Ram + "LineID", l.Number.ToString(CultureInfo.InvariantCulture))),
            new XElement(Ram + "SpecifiedTradeProduct", new XElement(Ram + "Name", l.Line.Name)),
            new XElement(Ram + "SpecifiedLineTradeAgreement",
                l.Line.OrderLine is null ? null : new XElement(Ram + "BuyerOrderReferencedDocument", new XElement(Ram + "LineID", l.Line.OrderLine)),
                new XElement(Ram + "NetPriceProductTradePrice", new XElement(Ram + "ChargeAmount", Format(l.Line.UnitPrice)))),
            new XElement(Ram + "SpecifiedLineTradeDelivery", new XElement(Ram + "BilledQuantity", new XAttribute("unitCode", "C62"), Format(l.Line.Quantity))),
            new XElement(Ram + "SpecifiedLineTradeSettlement",
                Tax(l.Line.TaxRate),
                new XElement(Ram + "SpecifiedTradeSettlementLineMonetarySummation", new XElement(Ram + "LineTotalAmount", Money(l.Net)))))));

        settlement.Elements(Ram + "ApplicableTradeTax").Remove();
        settlement.Element(Ram + "SpecifiedTradeSettlementPaymentMeans")!.AddAfterSelf(breakdown.Select(b => new XElement(Ram + "ApplicableTradeTax",
            new XElement(Ram + "CalculatedAmount", Money(b.Tax)),
            new XElement(Ram + "TypeCode", "VAT"),
            new XElement(Ram + "BasisAmount", Money(b.Taxable)),
            new XElement(Ram + "CategoryCode", "S"),
            new XElement(Ram + "RateApplicablePercent", Format(b.Rate)))));

        settlement.Element(Ram + "SpecifiedTradeSettlementHeaderMonetarySummation")!.ReplaceNodes(
            new XElement(Ram + "LineTotalAmount", Money(net)),
            new XElement(Ram + "TaxBasisTotalAmount", Money(net)),
            new XElement(Ram + "TaxTotalAmount", new XAttribute("currencyID", "EUR"), Money(tax)),
            new XElement(Ram + "GrandTotalAmount", Money(net + tax)),
            new XElement(Ram + "DuePayableAmount", Money(net + tax)));

        return doc.Declaration + Environment.NewLine + doc.Root;
    }

    private static XElement Tax(decimal rate) => new(Ram + "ApplicableTradeTax",
        new XElement(Ram + "TypeCode", "VAT"),
        new XElement(Ram + "CategoryCode", "S"),
        new XElement(Ram + "RateApplicablePercent", Format(rate)));

    private static decimal Round(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Format(decimal value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
