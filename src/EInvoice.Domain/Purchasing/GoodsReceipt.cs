namespace EInvoice.Domain.Purchasing;

/// <summary>What actually arrived for a purchase order. One order can have several receipts (partial deliveries).</summary>
public sealed class GoodsReceipt
{
    public GoodsReceipt(string number, Guid purchaseOrderId, DateOnly receivedDate, IReadOnlyList<GoodsReceiptLine> lines)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(number);
        Lines = LineRules.EnsureUnique(lines, l => l.PurchaseOrderLineNumber, "Goods receipt");

        Number = number;
        PurchaseOrderId = purchaseOrderId;
        ReceivedDate = receivedDate;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public string Number { get; }

    public Guid PurchaseOrderId { get; }

    public DateOnly ReceivedDate { get; }

    public IReadOnlyList<GoodsReceiptLine> Lines { get; }
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
