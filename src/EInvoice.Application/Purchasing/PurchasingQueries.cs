using EInvoice.Domain.Invoices;

namespace EInvoice.Application.Purchasing;

/// <param name="Search">Part of an order number or vendor name, taken literally; null for all.</param>
/// <param name="Page">1-based.</param>
public sealed record PurchaseOrderListQuery(string? Search, int Page, int PageSize)
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;
    public const int MaxSearchLength = 50;
}

public enum ReceiptState
{
    NothingReceived = 1,
    Partly = 2,
    Complete = 3,
}

public sealed record PurchaseOrderSummary(Guid Id, string Number, string VendorName, DateOnly OrderDate, string Currency, decimal NetTotal, ReceiptState Received);

public sealed record PurchaseOrderList(IReadOnlyList<PurchaseOrderSummary> Items, int Page, int PageSize, int TotalCount);

/// <param name="Invoiced">Invoiced on this order line, over the order's invoices that are not rejected or failed.</param>
public sealed record OrderLineView(
    int LineNumber, string ItemNumber, string Description, decimal Quantity, decimal UnitPrice, decimal TaxRatePercent, decimal NetAmount, decimal Received, decimal Invoiced)
{
    /// <summary>Ordered, not received yet (never below 0).</summary>
    public decimal Open => Math.Max(0m, Quantity - Received);
}

public sealed record ReceiptLineView(int OrderLine, decimal Quantity);

public sealed record ReceiptView(string Number, DateOnly ReceivedDate, IReadOnlyList<ReceiptLineView> Lines);

public sealed record OrderInvoiceView(Guid Id, string? InvoiceNumber, InvoiceStatus Status, decimal? GrossTotal, string? Currency);

/// <param name="Invoices">The invoices quoting this order; null when the reader may not read invoices (the buyer).</param>
public sealed record PurchaseOrderDetail(
    Guid Id, string Number, Guid VendorId, string VendorNumber, string VendorName, DateOnly OrderDate, string Currency,
    IReadOnlyList<OrderLineView> Lines, IReadOnlyList<ReceiptView> Receipts, IReadOnlyList<OrderInvoiceView>? Invoices);

public sealed record VendorChoice(Guid Id, string Number, string Name);

/// <summary>Read side of purchasing: views, never tracked entities.</summary>
public interface IPurchasingQueries
{
    /// <summary>Newest order first (order date, then number).</summary>
    Task<PurchaseOrderList> ListAsync(PurchaseOrderListQuery query, CancellationToken cancellationToken);

    /// <param name="withInvoices">Only for readers of invoices; otherwise the invoices are not even loaded.</param>
    Task<PurchaseOrderDetail?> GetAsync(Guid id, bool withInvoices, CancellationToken cancellationToken);

    /// <summary>Vendors that may get new orders, by number.</summary>
    Task<IReadOnlyList<VendorChoice>> ActiveVendorsAsync(CancellationToken cancellationToken);
}
