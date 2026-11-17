using EInvoice.Application.Purchasing;
using EInvoice.Domain.Invoices;
using EInvoice.Domain.Purchasing;
using EInvoice.Infrastructure.Persistence;
using EInvoice.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace EInvoice.Infrastructure.Purchasing;

/// <summary>
/// Purchase orders as people read them. A page of orders is chosen in SQL (search, newest first); its totals and
/// receipt states come from the lines and receipts of just those orders. The search is a parameter, compared as text.
/// </summary>
public sealed class EfPurchasingQueries(EInvoiceDbContext db) : IPurchasingQueries
{
    private static readonly InvoiceStatus[] NotCounted = [InvoiceStatus.Rejected, InvoiceStatus.Failed];

    public async Task<PurchaseOrderList> ListAsync(PurchaseOrderListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        var pageSize = Math.Clamp(query.PageSize, 1, PurchaseOrderListQuery.MaxPageSize);
        var page = Math.Max(1, query.Page);

        var orders =
            from o in db.PurchaseOrders.AsNoTracking()
            join v in db.Vendors on o.VendorId equals v.Id
            select new { o.Id, o.Number, Vendor = v.Name, o.OrderDate, o.Currency };
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var search = query.Search.Trim();
            orders = orders.Where(o => o.Number.Contains(search) || o.Vendor.Contains(search));
        }

        var total = await orders.CountAsync(cancellationToken).ConfigureAwait(false);
        var items = await orders
            .OrderByDescending(o => o.OrderDate).ThenByDescending(o => o.Number)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var ids = items.Select(i => i.Id).ToList();
        var lines = await OrderLines(ids).ToListAsync(cancellationToken).ConfigureAwait(false);
        var received = await Received(ids).ToListAsync(cancellationToken).ConfigureAwait(false);

        return new PurchaseOrderList(
            items.Select(o =>
            {
                var own = lines.Where(l => l.OrderId == o.Id).ToList();
                var state = State(own.Select(l => (l.Quantity, received.Where(r => r.OrderId == o.Id && r.Line == l.LineNumber).Sum(r => r.Quantity))).ToList());
                return new PurchaseOrderSummary(o.Id, o.Number, o.Vendor, o.OrderDate, o.Currency, own.Sum(l => Net(l.Quantity, l.UnitPrice)), state);
            }).ToList(),
            page,
            pageSize,
            total);
    }

    public async Task<PurchaseOrderDetail?> GetAsync(Guid id, bool withInvoices, CancellationToken cancellationToken)
    {
        var order = await db.PurchaseOrders.AsNoTracking().SingleOrDefaultAsync(o => o.Id == id, cancellationToken).ConfigureAwait(false);
        if (order is null)
        {
            return null;
        }

        var vendor = await db.Vendors.AsNoTracking().Where(v => v.Id == order.VendorId).Select(v => new { v.Number, v.Name })
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        var receipts = await db.GoodsReceipts.AsNoTracking().Where(r => r.PurchaseOrderId == id)
            .OrderBy(r => r.ReceivedDate).ThenBy(r => r.Number)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // Invoiced per order line: lines of the invoices quoting this order, unless rejected or failed.
        var invoiced = await (
                from l in db.Set<InvoiceLine>()
                join i in db.Invoices on EF.Property<Guid>(l, "InvoiceId") equals i.Id
                where i.PurchaseOrderNumber == order.Number && !NotCounted.Contains(i.Status) && l.PurchaseOrderLineNumber != null
                group l by l.PurchaseOrderLineNumber into g
                select new { Line = g.Key!.Value, Quantity = g.Sum(x => x.Quantity) })
            .ToDictionaryAsync(x => x.Line, x => x.Quantity, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<OrderInvoiceView>? invoices = null;
        if (withInvoices)
        {
            invoices = await db.Invoices.AsNoTracking()
                .Where(i => i.PurchaseOrderNumber == order.Number)
                .OrderBy(i => EF.Property<long>(i, MappingConventions.RowId)) // in the order they arrived
                .Select(i => new OrderInvoiceView(
                    i.Id,
                    i.InvoiceNumber,
                    i.Status,
                    i.GrossTotal == null ? null : i.GrossTotal.Amount,
                    i.GrossTotal == null ? null : i.GrossTotal.Currency))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        var receivedByLine = receipts.SelectMany(r => r.Lines).GroupBy(l => l.PurchaseOrderLineNumber).ToDictionary(g => g.Key, g => g.Sum(l => l.QuantityReceived));
        return new PurchaseOrderDetail(
            order.Id,
            order.Number,
            order.VendorId,
            vendor.Number,
            vendor.Name,
            order.OrderDate,
            order.Currency,
            order.Lines.Select(l => new OrderLineView(
                l.LineNumber, l.ItemNumber, l.Description, l.Quantity, l.UnitPrice, l.TaxRatePercent, Net(l.Quantity, l.UnitPrice),
                receivedByLine.GetValueOrDefault(l.LineNumber), invoiced.GetValueOrDefault(l.LineNumber))).ToList(),
            receipts.Select(r => new ReceiptView(r.Number, r.ReceivedDate, r.Lines.Select(l => new ReceiptLineView(l.PurchaseOrderLineNumber, l.QuantityReceived)).ToList())).ToList(),
            invoices);
    }

    public async Task<IReadOnlyList<VendorChoice>> ActiveVendorsAsync(CancellationToken cancellationToken) =>
        await db.Vendors.AsNoTracking().Where(v => v.IsActive).OrderBy(v => v.Number)
            .Select(v => new VendorChoice(v.Id, v.Number, v.Name))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    private IQueryable<OrderLineRow> OrderLines(List<Guid> ids) =>
        db.Set<PurchaseOrderLine>().AsNoTracking()
            .Where(l => ids.Contains(EF.Property<Guid>(l, "PurchaseOrderId")))
            .Select(l => new OrderLineRow(EF.Property<Guid>(l, "PurchaseOrderId"), l.LineNumber, l.Quantity, l.UnitPrice));

    private IQueryable<ReceivedRow> Received(List<Guid> ids) =>
        from l in db.Set<GoodsReceiptLine>().AsNoTracking()
        join r in db.GoodsReceipts on EF.Property<Guid>(l, "GoodsReceiptId") equals r.Id
        where ids.Contains(r.PurchaseOrderId)
        select new ReceivedRow(r.PurchaseOrderId, l.PurchaseOrderLineNumber, l.QuantityReceived);

    /// <summary>Net amount of a line, rounded to cents as on an invoice.</summary>
    private static decimal Net(decimal quantity, decimal unitPrice) => decimal.Round(quantity * unitPrice, 2, MidpointRounding.AwayFromZero);

    private static ReceiptState State(IReadOnlyList<(decimal Ordered, decimal Received)> lines) =>
        lines.All(l => l.Received == 0) ? ReceiptState.NothingReceived
        : lines.All(l => l.Received >= l.Ordered) ? ReceiptState.Complete
        : ReceiptState.Partly;

    private sealed record OrderLineRow(Guid OrderId, int LineNumber, decimal Quantity, decimal UnitPrice);

    private sealed record ReceivedRow(Guid OrderId, int Line, decimal Quantity);
}
