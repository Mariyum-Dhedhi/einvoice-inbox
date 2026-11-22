using EInvoice.Domain.Shared;

namespace EInvoice.Domain.Purchasing;

/// <summary>Stored as numbers: never renumber or reuse a value.</summary>
public enum PurchaseOrderStatus
{
    Open = 1,

    /// <summary>Closed in the ERP: nothing more is expected. Invoices are still matched against it as before.</summary>
    Closed = 2,
}

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

    public DateOnly OrderDate { get; private set; }

    public string Currency { get; }

    public PurchaseOrderStatus Status { get; private set; } = PurchaseOrderStatus.Open;

    public RecordSource Source { get; private set; } = RecordSource.Local;

    /// <summary>The ERP's id for this order; null for one made in the app.</summary>
    public string? ErpId { get; private set; }

    public IReadOnlyList<PurchaseOrderLine> Lines => _lines.OrderBy(l => l.LineNumber).ToList().AsReadOnly();

    /// <summary>An order read from the ERP.</summary>
    public static PurchaseOrder FromErp(
        string erpId, string number, Guid vendorId, DateOnly orderDate, string currency, IReadOnlyList<PurchaseOrderLine> lines, PurchaseOrderStatus status)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(erpId);
        return new PurchaseOrder(number, vendorId, orderDate, currency, lines) { Source = RecordSource.Erp, ErpId = erpId, Status = status };
    }

    /// <summary>
    /// Follows the ERP: date, lines (replaced as a whole; their order does not matter) and status. Number, vendor and
    /// currency stay: invoices and receipts refer to them.
    /// </summary>
    /// <returns>False when nothing changed: an unchanged record writes no audit event.</returns>
    public bool ApplyFromErp(DateOnly orderDate, IReadOnlyList<PurchaseOrderLine> lines, PurchaseOrderStatus status)
    {
        Vendors.Vendor.ErpOnly(Source);
        LineRules.EnsureUnique(lines, l => l.LineNumber, "Purchase order");
        if (OrderDate == orderDate && Status == status && LineRules.SameSet(_lines, lines))
        {
            return false;
        }

        OrderDate = orderDate;
        Status = status;
        LineRules.Replace(_lines, lines, l => l.LineNumber);
        return true;
    }
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

    /// <summary>The same lines, in any order.</summary>
    public static bool SameSet<T>(IReadOnlyCollection<T> current, IReadOnlyCollection<T> next) =>
        current.Count == next.Count && current.ToHashSet().SetEquals(next);

    /// <summary>
    /// Makes <paramref name="current"/> hold <paramref name="next"/>: unchanged lines keep their instance, changed ones are
    /// replaced, missing ones removed, new ones added.
    /// </summary>
    public static void Replace<T>(List<T> current, IReadOnlyList<T> next, Func<T, int> key)
    {
        var wanted = next.ToDictionary(key);
        current.RemoveAll(line => !wanted.TryGetValue(key(line), out var same) || !EqualityComparer<T>.Default.Equals(same, line));
        var kept = current.Select(key).ToHashSet();
        current.AddRange(next.Where(line => !kept.Contains(key(line))));
    }
}
