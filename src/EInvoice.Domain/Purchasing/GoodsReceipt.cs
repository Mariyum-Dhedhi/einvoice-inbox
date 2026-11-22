using EInvoice.Domain.Shared;

namespace EInvoice.Domain.Purchasing;

/// <summary>What actually arrived for a purchase order. One order can have several receipts (partial deliveries).</summary>
public sealed class GoodsReceipt
{
    private readonly List<GoodsReceiptLine> _lines = [];

    public GoodsReceipt(string number, Guid purchaseOrderId, DateOnly receivedDate, IReadOnlyList<GoodsReceiptLine> lines)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(number);
        _lines.AddRange(LineRules.EnsureUnique(lines, l => l.PurchaseOrderLineNumber, "Goods receipt"));

        Number = number;
        PurchaseOrderId = purchaseOrderId;
        ReceivedDate = receivedDate;
    }

    /// <summary>For EF Core when loading from the database; validation ran when the receipt was first created.</summary>
    private GoodsReceipt() => Number = null!;

    public Guid Id { get; } = Guid.NewGuid();

    public string Number { get; }

    public Guid PurchaseOrderId { get; }

    public DateOnly ReceivedDate { get; private set; }

    public RecordSource Source { get; private set; } = RecordSource.Local;

    /// <summary>The ERP's id for this receipt; null for one recorded in the app.</summary>
    public string? ErpId { get; private set; }

    public IReadOnlyList<GoodsReceiptLine> Lines => _lines.OrderBy(l => l.PurchaseOrderLineNumber).ToList().AsReadOnly();

    /// <summary>A receipt read from the ERP.</summary>
    public static GoodsReceipt FromErp(string erpId, string number, Guid purchaseOrderId, DateOnly receivedDate, IReadOnlyList<GoodsReceiptLine> lines)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(erpId);
        return new GoodsReceipt(number, purchaseOrderId, receivedDate, lines) { Source = RecordSource.Erp, ErpId = erpId };
    }

    /// <summary>Follows the ERP: date and lines. Number and order stay.</summary>
    /// <returns>False when nothing changed.</returns>
    public bool ApplyFromErp(DateOnly receivedDate, IReadOnlyList<GoodsReceiptLine> lines)
    {
        Vendors.Vendor.ErpOnly(Source);
        LineRules.EnsureUnique(lines, l => l.PurchaseOrderLineNumber, "Goods receipt");
        if (ReceivedDate == receivedDate && LineRules.SameSet(_lines, lines))
        {
            return false;
        }

        ReceivedDate = receivedDate;
        LineRules.Replace(_lines, lines, l => l.PurchaseOrderLineNumber);
        return true;
    }
}

public sealed record GoodsReceiptLine
{
    public GoodsReceiptLine(int purchaseOrderLineNumber, decimal quantityReceived)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(purchaseOrderLineNumber);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantityReceived);

        PurchaseOrderLineNumber = purchaseOrderLineNumber;
        QuantityReceived = quantityReceived;
    }

    public int PurchaseOrderLineNumber { get; }

    public decimal QuantityReceived { get; }
}
