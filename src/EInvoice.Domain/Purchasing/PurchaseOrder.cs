namespace EInvoice.Domain.Purchasing;

/// <summary>What was ordered from a vendor. Mirrors the purchase order header and lines of an ERP.</summary>
public sealed class PurchaseOrder
{
    private readonly List<PurchaseOrderLine> _lines = [];

    public PurchaseOrder(string number, Guid vendorId, DateOnly orderDate, string currency, IReadOnlyList<PurchaseOrderLine> lines)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(number);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);
        _lines.AddRange(LineRules.EnsureUnique(lines, l => l.LineNumber, "Purchase order"));

        Number = number;
        VendorId = vendorId;
        OrderDate = orderDate;
        Currency = currency;
    }

    /// <summary>For EF Core when loading from the database; validation ran when the order was first created.</summary>
    private PurchaseOrder()
    {
        Number = null!;
        Currency = null!;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public string Number { get; }

    public Guid VendorId { get; }

    public DateOnly OrderDate { get; }

    public string Currency { get; }

    public IReadOnlyList<PurchaseOrderLine> Lines => _lines.OrderBy(l => l.LineNumber).ToList().AsReadOnly();
}

/// <param name="UnitPrice">Plain decimal, not Money: unit prices may have more than two decimals.</param>
public sealed record PurchaseOrderLine
{
    public PurchaseOrderLine(int lineNumber, string itemNumber, string description, decimal quantity, decimal unitPrice, decimal taxRatePercent)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lineNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(itemNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(quantity);
        ArgumentOutOfRangeException.ThrowIfNegative(unitPrice);
        ArgumentOutOfRangeException.ThrowIfNegative(taxRatePercent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(taxRatePercent, 100m);

        LineNumber = lineNumber;
        ItemNumber = itemNumber;
        Description = description;
        Quantity = quantity;
        UnitPrice = unitPrice;
        TaxRatePercent = taxRatePercent;
    }

    public int LineNumber { get; }

    public string ItemNumber { get; }

    public string Description { get; }

    public decimal Quantity { get; }

    public decimal UnitPrice { get; }

    public decimal TaxRatePercent { get; }
}

internal static class LineRules
{
    public static IReadOnlyList<T> EnsureUnique<T>(IReadOnlyList<T> lines, Func<T, int> key, string documentName)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count == 0)
        {
            throw new ArgumentException($"{documentName} must have at least one line.", nameof(lines));
        }

        if (lines.Select(key).Distinct().Count() != lines.Count)
        {
            throw new ArgumentException($"{documentName} line numbers must be unique.", nameof(lines));
        }

        return lines;
    }
}
