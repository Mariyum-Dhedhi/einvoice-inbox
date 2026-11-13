using System.Text;
using System.Xml.Linq;
using EInvoice.Infrastructure.Parsing;

namespace EInvoice.UnitTests.Parsing;

/// <summary>
/// UBL requires cbc:ID whenever cac:OrderReference is present. An invoice that only has a sales order number (BT-14)
/// therefore fills BT-13 with a placeholder: "NA" per the EN 16931 UBL binding, "Dummywert" in the KoSIT samples.
/// That placeholder is not a purchase order number and must not be looked up as one.
/// </summary>
public class UblOrderReferenceTests
{
    private static readonly XNamespace Cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    private static readonly XNamespace Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";

    private static string? PurchaseOrderReference(string? id, string? salesOrderId)
    {
        var doc = XDocument.Load(Path.Combine(TestPaths.KositCorpus, "01.01a-INVOICE_ubl.xml"));
        var orderRef = new XElement(Cac + "OrderReference");
        if (id is not null)
        {
            orderRef.Add(new XElement(Cbc + "ID", id));
        }

        if (salesOrderId is not null)
        {
            orderRef.Add(new XElement(Cbc + "SalesOrderID", salesOrderId));
        }

        doc.Root!.Element(Cbc + "BuyerReference")!.AddAfterSelf(orderRef);

        var result = new XRechnungParser().Parse(new MemoryStream(Encoding.UTF8.GetBytes(doc.ToString())));
        Assert.True(result.Succeeded, result.Error?.Message);
        return result.Document!.PurchaseOrderReference;
    }

    [Theory]
    [InlineData("NA")]
    [InlineData("Dummywert")]
    public void Placeholder_with_a_sales_order_number_means_no_purchase_order(string placeholder)
    {
        Assert.Null(PurchaseOrderReference(placeholder, salesOrderId: "SO-77"));
    }

    [Fact]
    public void Real_purchase_order_number_is_kept_even_with_a_sales_order_number()
    {
        Assert.Equal("PO-0001", PurchaseOrderReference("PO-0001", salesOrderId: "SO-77"));
    }

    [Fact]
    public void Without_a_sales_order_number_the_value_is_taken_as_stated()
    {
        // Only the documented situation is special-cased; "NA" alone is passed on and will simply not match an order.
        Assert.Equal("NA", PurchaseOrderReference("NA", salesOrderId: null));
    }
}
