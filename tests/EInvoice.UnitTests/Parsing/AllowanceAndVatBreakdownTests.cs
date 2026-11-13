using System.Text;
using System.Xml.Linq;
using EInvoice.Application.Parsing;
using EInvoice.Infrastructure.Parsing;

namespace EInvoice.UnitTests.Parsing;

/// <summary>
/// Line-level allowances and charges (BG-27, BG-28) are direct children of the invoice line. A discount inside the
/// price (BT-147) is something else: the stated net price already includes it. Mixing them up would make the
/// line arithmetic rule reject valid invoices.
/// </summary>
public class AllowanceAndVatBreakdownTests
{
    private static readonly XNamespace Cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    private static readonly XNamespace Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
    private static readonly XNamespace Ram = "urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:100";
    private static readonly XNamespace Udt = "urn:un:unece:uncefact:data:standard:UnqualifiedDataType:100";

    private static ParsedDocument Parse(XDocument doc)
    {
        var result = new XRechnungParser().Parse(new MemoryStream(Encoding.UTF8.GetBytes(doc.ToString())));
        Assert.True(result.Succeeded, result.Error?.Message);
        return result.Document!;
    }

    [Fact]
    public void Price_discount_is_not_a_line_allowance()
    {
        // 01.20: each line has a 10.00 discount inside cac:Price; the net price 100 already includes it.
        var doc = KositCorpusTests.Parse("01.20a-INVOICE_ubl.xml");

        Assert.All(doc.Lines, l => Assert.Equal(0m, l.LineAllowances));
    }

    [Fact]
    public void Ubl_line_allowances_and_charges_are_summed_separately()
    {
        var doc = XDocument.Load(Path.Combine(TestPaths.KositCorpus, "01.01a-INVOICE_ubl.xml"));
        var line = doc.Root!.Elements(Cac + "InvoiceLine").First();
        // BG-27/28 come after cac:OrderLineReference and before cac:Item in UBL element order.
        line.Element(Cac + "Item")!.AddBeforeSelf(
            UblAllowanceCharge(charge: false, "5.00"),
            UblAllowanceCharge(charge: false, "2.50"),
            UblAllowanceCharge(charge: true, "1.25"));

        var first = Parse(doc).Lines[0];

        Assert.Equal(7.50m, first.LineAllowances);
        Assert.Equal(1.25m, first.LineCharges);
    }

    [Fact]
    public void Cii_line_allowances_and_charges_are_summed_separately()
    {
        var doc = XDocument.Load(Path.Combine(TestPaths.KositCorpus, "01.01a-INVOICE_uncefact.xml"));
        var settlement = doc.Descendants(Ram + "IncludedSupplyChainTradeLineItem").First().Element(Ram + "SpecifiedLineTradeSettlement")!;
        settlement.Element(Ram + "SpecifiedTradeSettlementLineMonetarySummation")!.AddBeforeSelf(
            CiiAllowanceCharge(charge: false, "5.00"),
            CiiAllowanceCharge(charge: true, "1.25"));

        var first = Parse(doc).Lines[0];

        Assert.Equal(5.00m, first.LineAllowances);
        Assert.Equal(1.25m, first.LineCharges);
    }

    [Fact]
    public void Vat_breakdown_with_two_categories_is_read_in_both_syntaxes()
    {
        // 02.01 has two VAT categories.
        var ubl = KositCorpusTests.Parse("02.01a-INVOICE_ubl.xml");
        var cii = KositCorpusTests.Parse("02.01a-INVOICE_uncefact.xml");

        Assert.Equal(2, ubl.VatBreakdown.Count);
        Assert.Equal(ubl.VatBreakdown, cii.VatBreakdown);
        Assert.Equal(ubl.TaxTotal, ubl.VatBreakdown.Sum(v => v.TaxAmount));
    }

    private static XElement UblAllowanceCharge(bool charge, string amount) =>
        new(Cac + "AllowanceCharge",
            new XElement(Cbc + "ChargeIndicator", charge ? "true" : "false"),
            new XElement(Cbc + "AllowanceChargeReasonCode", charge ? "ABL" : "95"),
            new XElement(Cbc + "Amount", new XAttribute("currencyID", "EUR"), amount));

    private static XElement CiiAllowanceCharge(bool charge, string amount) =>
        new(Ram + "SpecifiedTradeAllowanceCharge",
            new XElement(Ram + "ChargeIndicator", new XElement(Udt + "Indicator", charge ? "true" : "false")),
            new XElement(Ram + "ActualAmount", amount),
            new XElement(Ram + "ReasonCode", charge ? "ABL" : "95"));
}
