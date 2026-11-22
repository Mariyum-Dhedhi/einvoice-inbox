using EInvoice.Domain.Purchasing;
using EInvoice.Domain.Shared;
using EInvoice.Domain.Vendors;

namespace EInvoice.UnitTests.Domain;

/// <summary>
/// Master data from the ERP (package 16c). Records the app creates itself stay as they are (add only). Records that come
/// from the ERP carry its id, are marked as such, and only the sync changes them, to follow the ERP: a changed name or
/// line, a vendor blocked, an order closed. Applying the same values again changes nothing and says so, so an unchanged
/// record writes no audit event. What other records point to stays fixed: an order's vendor, a receipt's order, numbers.
/// </summary>
public sealed class ErpRecordTests
{
    private static readonly Guid VendorId = Guid.NewGuid();

    private static PurchaseOrderLine Line(int number, decimal quantity = 10m, decimal price = 4.50m) =>
        new(number, $"ITEM-{number}", $"Item {number}", quantity, price, 19m);

    private static PurchaseOrder ErpOrder() =>
        PurchaseOrder.FromErp("ERP-PO-1", "PO-26-000001", VendorId, new DateOnly(2026, 11, 2), "EUR", [Line(1), Line(2)], PurchaseOrderStatus.Open);

    // ---- Records the app creates ------------------------------------------------------------------------------------

    [Fact]
    public void Records_made_in_the_app_are_local_with_no_erp_id()
    {
        var vendor = new Vendor("V-9001", "Local GmbH", "DE000000901");
        var order = new PurchaseOrder("PO-L-1", VendorId, new DateOnly(2026, 11, 2), "EUR", [Line(1)]);
        var receipt = new GoodsReceipt("GR-L-1", order.Id, new DateOnly(2026, 11, 3), [new GoodsReceiptLine(1, 2m)]);

        Assert.Equal((RecordSource.Local, (string?)null), (vendor.Source, vendor.ErpId));
        Assert.Equal((RecordSource.Local, (string?)null, PurchaseOrderStatus.Open), (order.Source, order.ErpId, order.Status));
        Assert.Equal((RecordSource.Local, (string?)null), (receipt.Source, receipt.ErpId));
    }

    [Fact]
    public void The_sync_cannot_change_a_record_made_in_the_app()
    {
        var vendor = new Vendor("V-9001", "Local GmbH", "DE000000901");
        var order = new PurchaseOrder("PO-L-1", VendorId, new DateOnly(2026, 11, 2), "EUR", [Line(1)]);
        var receipt = new GoodsReceipt("GR-L-1", order.Id, new DateOnly(2026, 11, 3), [new GoodsReceiptLine(1, 2m)]);

        Assert.Throws<InvalidOperationException>(() => vendor.ApplyFromErp("Other", null, blocked: false));
        Assert.Throws<InvalidOperationException>(() => order.ApplyFromErp(new DateOnly(2026, 11, 2), [Line(1)], PurchaseOrderStatus.Closed));
        Assert.Throws<InvalidOperationException>(() => receipt.ApplyFromErp(new DateOnly(2026, 11, 3), [new GoodsReceiptLine(1, 3m)]));
    }

    // ---- Vendors ----------------------------------------------------------------------------------------------------

    [Fact]
    public void A_vendor_from_the_erp_carries_its_id_and_a_blocked_one_is_inactive()
    {
        var vendor = Vendor.FromErp("ERP-V-1", "K-00001", "Nordlicht Papier GmbH", "DE000000123", blocked: true);

        Assert.Equal((RecordSource.Erp, "ERP-V-1", "K-00001", false), (vendor.Source, vendor.ErpId, vendor.Number, vendor.IsActive));
    }

    [Fact]
    public void The_same_values_again_change_nothing()
    {
        var vendor = Vendor.FromErp("ERP-V-1", "K-00001", "Nordlicht Papier GmbH", "DE000000123", blocked: false);

        Assert.False(vendor.ApplyFromErp("Nordlicht Papier GmbH", "DE000000123", blocked: false));
    }

    [Theory]
    [InlineData("Nordlicht Papier AG", "DE000000123", false)]
    [InlineData("Nordlicht Papier GmbH", "DE000000124", false)]
    [InlineData("Nordlicht Papier GmbH", null, false)]
    [InlineData("Nordlicht Papier GmbH", "DE000000123", true)]
    public void A_changed_name_vat_id_or_block_is_taken_over(string name, string? vatId, bool blocked)
    {
        var vendor = Vendor.FromErp("ERP-V-1", "K-00001", "Nordlicht Papier GmbH", "DE000000123", blocked: false);

        Assert.True(vendor.ApplyFromErp(name, vatId, blocked));
        Assert.Equal((name, vatId, !blocked), (vendor.Name, vendor.VatId, vendor.IsActive));
    }

    [Fact]
    public void An_unblocked_vendor_is_active_again()
    {
        var vendor = Vendor.FromErp("ERP-V-1", "K-00001", "Nordlicht Papier GmbH", null, blocked: true);

        Assert.True(vendor.ApplyFromErp("Nordlicht Papier GmbH", null, blocked: false));
        Assert.True(vendor.IsActive);
    }

    // ---- Purchase orders --------------------------------------------------------------------------------------------

    [Fact]
    public void An_order_from_the_erp_carries_its_id_and_status()
    {
        var order = PurchaseOrder.FromErp("ERP-PO-1", "PO-26-000001", VendorId, new DateOnly(2026, 11, 2), "EUR", [Line(1)], PurchaseOrderStatus.Closed);

        Assert.Equal((RecordSource.Erp, "ERP-PO-1", PurchaseOrderStatus.Closed), (order.Source, order.ErpId, order.Status));
    }

    [Fact]
    public void An_order_with_the_same_date_lines_and_status_is_unchanged()
    {
        var order = ErpOrder();

        Assert.False(order.ApplyFromErp(new DateOnly(2026, 11, 2), [Line(2), Line(1)], PurchaseOrderStatus.Open)); // order of lines does not matter
    }

    [Fact]
    public void Changed_lines_replace_the_old_ones()
    {
        var order = ErpOrder();

        Assert.True(order.ApplyFromErp(new DateOnly(2026, 11, 2), [Line(1, quantity: 12m), Line(3)], PurchaseOrderStatus.Open));

        Assert.Equal([(1, 12m), (3, 10m)], order.Lines.Select(l => (l.LineNumber, l.Quantity)));
    }

    [Fact]
    public void A_closed_order_and_a_new_date_are_taken_over()
    {
        var order = ErpOrder();

        Assert.True(order.ApplyFromErp(new DateOnly(2026, 11, 3), [Line(1), Line(2)], PurchaseOrderStatus.Closed));

        Assert.Equal((new DateOnly(2026, 11, 3), PurchaseOrderStatus.Closed), (order.OrderDate, order.Status));
    }

    [Fact]
    public void An_order_from_the_erp_still_needs_unique_lines()
    {
        Assert.Throws<ArgumentException>(() => ErpOrder().ApplyFromErp(new DateOnly(2026, 11, 2), [Line(1), Line(1)], PurchaseOrderStatus.Open));
        Assert.Throws<ArgumentException>(() => ErpOrder().ApplyFromErp(new DateOnly(2026, 11, 2), [], PurchaseOrderStatus.Open));
    }

    // ---- Goods receipts ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_receipt_from_the_erp_is_unchanged_with_the_same_lines_and_follows_changed_ones()
    {
        var receipt = GoodsReceipt.FromErp("ERP-GR-1", "GR-26-000001", Guid.NewGuid(), new DateOnly(2026, 11, 3), [new GoodsReceiptLine(1, 5m)]);

        Assert.Equal((RecordSource.Erp, "ERP-GR-1"), (receipt.Source, receipt.ErpId));
        Assert.False(receipt.ApplyFromErp(new DateOnly(2026, 11, 3), [new GoodsReceiptLine(1, 5m)]));
        Assert.True(receipt.ApplyFromErp(new DateOnly(2026, 11, 3), [new GoodsReceiptLine(1, 4m), new GoodsReceiptLine(2, 1m)]));
        Assert.Equal([(1, 4m), (2, 1m)], receipt.Lines.Select(l => (l.PurchaseOrderLineNumber, l.QuantityReceived)));
    }
}
