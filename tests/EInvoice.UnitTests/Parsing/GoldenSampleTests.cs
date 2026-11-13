using EInvoice.Application.Parsing;

namespace EInvoice.UnitTests.Parsing;

/// <summary>Case 01.01, every mapped field checked by hand against the XML.</summary>
public class GoldenSampleTests
{
    [Theory]
    [InlineData("01.01a-INVOICE_ubl.xml")]
    [InlineData("01.01a-INVOICE_uncefact.xml")]
    public void Case_01_01_maps_every_field(string fileName)
    {
        var doc = KositCorpusTests.Parse(fileName);

        Assert.Equal("380", doc.InvoiceTypeCode);
        Assert.Equal("123456XX", doc.InvoiceNumber);                 // BT-1
        Assert.Equal(new DateOnly(2016, 4, 4), doc.IssueDate);       // BT-2
        Assert.Equal("EUR", doc.Currency);                           // BT-5
        Assert.Equal("04011000-12345-03", doc.BuyerReference);       // BT-10 (Leitweg-ID)
        Assert.Null(doc.PurchaseOrderReference);                     // BT-13 not present in this case
        Assert.Equal("[Seller name]", doc.SellerName);               // BT-27
        Assert.Equal("DE123456789", doc.SellerVatId);                // BT-31, space removed
        Assert.Equal(["DE79000000001234567890"], doc.PaymentAccountIbans); // BT-84, stored as stated
        Assert.Equal(314.86m, doc.SumOfLineNetAmounts);              // BT-106
        Assert.Equal(314.86m, doc.NetTotal);                         // BT-109
        Assert.Equal(22.04m, doc.TaxTotal);                          // BT-110
        Assert.Equal(336.90m, doc.GrossTotal);                       // BT-112

        Assert.Equal(
            [
                new ParsedDocumentLine(1, "Zeitschrift [...]", "Zeitschrift [...]", 1m, 288.79m, 288.79m, 7m, "6171175.1"),
                new ParsedDocumentLine(2, "Porto + Versandkosten", "Porto + Versandkosten", 1m, 26.07m, 26.07m, 7m, null),
            ],
            doc.Lines);
    }
}
