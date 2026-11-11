using EInvoice.Domain.Purchasing;
using EInvoice.Domain.Vendors;

namespace EInvoice.UnitTests.Domain;

public class PurchasingTests
{
    private static readonly Guid VendorId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private static PurchaseOrderLine Line(int number, decimal quantity = 2m, decimal unitPrice = 50m) =>
        new(number, "CHAIR-01", "Office chair", quantity, unitPrice, 19m);

    [Fact]
    public void Purchase_order_keeps_its_lines()
    {
        var po = new PurchaseOrder("PO-1001", VendorId, new DateOnly(2026, 10, 1), "EUR", [Line(1), Line(2)]);

        Assert.Equal(2, po.Lines.Count);
        Assert.Equal("PO-1001", po.Number);
    }

    [Fact]
    public void Purchase_order_needs_at_least_one_line()
    {
        Assert.Throws<ArgumentException>(() => new PurchaseOrder("PO-1001", VendorId, new DateOnly(2026, 10, 1), "EUR", []));
    }

    [Fact]
    public void Purchase_order_line_numbers_must_be_unique()
    {
        Assert.Throws<ArgumentException>(() => new PurchaseOrder("PO-1001", VendorId, new DateOnly(2026, 10, 1), "EUR", [Line(1), Line(1)]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Purchase_order_line_quantity_must_be_positive(int quantity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Line(1, quantity: quantity));
    }

    [Fact]
    public void Purchase_order_line_unit_price_cannot_be_negative()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Line(1, unitPrice: -0.01m));
    }

    [Fact]
    public void Unit_price_keeps_more_than_two_decimals()
    {
        Assert.Equal(0.0125m, Line(1, unitPrice: 0.0125m).UnitPrice);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Goods_receipt_line_quantity_must_be_positive(int quantity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GoodsReceiptLine(1, quantity));
    }

    [Fact]
    public void Goods_receipt_needs_at_least_one_line()
    {
        Assert.Throws<ArgumentException>(() => new GoodsReceipt("GR-5001", Guid.NewGuid(), new DateOnly(2026, 10, 5), []));
    }

    [Fact]
    public void Vendor_requires_number_and_name_and_can_be_deactivated()
    {
        Assert.Throws<ArgumentException>(() => new Vendor("", "Muster GmbH", "DE123456789"));
        Assert.Throws<ArgumentException>(() => new Vendor("V-1001", " ", "DE123456789"));

        var vendor = new Vendor("V-1001", "Muster GmbH", "DE123456789");
        Assert.True(vendor.IsActive);

        vendor.Deactivate();
        Assert.False(vendor.IsActive);
    }
}
