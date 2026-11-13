using System.Text;
using System.Xml.Linq;
using EInvoice.Infrastructure.Parsing;

namespace EInvoice.UnitTests.Parsing;

/// <summary>
/// An invoice in EUR with VAT also reported in GBP states the VAT total twice: BT-110 (invoice currency)
/// and BT-111 (tax currency). Case 02.01 has the same number in both and lists EUR first, so a parser that simply
/// took the first amount would pass every unmodified sample. These tests give BT-111 a different value and put it
/// first: only selecting by currency gives the right answer.
/// </summary>
public class TaxCurrencyTests
{
    private const decimal Bt110Eur = 2048.44m;

    [Fact]
    public void Ubl_selects_the_VAT_total_in_the_invoice_currency_even_when_listed_second()
    {
        XNamespace cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
        XNamespace cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
        var doc = XDocument.Load(Path.Combine(TestPaths.KositCorpus, "02.01a-INVOICE_ubl.xml"));

        var gbp = doc.Root!.Elements(cac + "TaxTotal").Single(t => (string?)t.Element(cbc + "TaxAmount")!.Attribute("currencyID") == "GBP");
        gbp.Element(cbc + "TaxAmount")!.Value = "1750.00";
        gbp.Remove();
        doc.Root.Elements(cac + "TaxTotal").First().AddBeforeSelf(gbp);

        Assert.Equal(Bt110Eur, Parse(doc).TaxTotal);
    }

    [Fact]
    public void Cii_selects_the_VAT_total_in_the_invoice_currency_even_when_listed_second()
    {
        XNamespace ram = "urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:100";
        var doc = XDocument.Load(Path.Combine(TestPaths.KositCorpus, "02.01a-INVOICE_uncefact.xml"));

        var amounts = doc.Descendants(ram + "TaxTotalAmount").ToList();
        var gbp = amounts.Single(a => (string?)a.Attribute("currencyID") == "GBP");
        gbp.Value = "1750.00";
        gbp.Remove();
        amounts.Single(a => (string?)a.Attribute("currencyID") == "EUR").AddBeforeSelf(gbp);

        Assert.Equal(Bt110Eur, Parse(doc).TaxTotal);
    }

    private static EInvoice.Application.Parsing.ParsedDocument Parse(XDocument doc)
    {
        var result = new XRechnungParser().Parse(new MemoryStream(Encoding.UTF8.GetBytes(doc.ToString())));
        Assert.True(result.Succeeded, result.Error?.Message);
        return result.Document!;
    }
}
