using EInvoice.Application.Audit;
using EInvoice.Application.Purchasing;
using EInvoice.Domain.Purchasing;
using EInvoice.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace EInvoice.Infrastructure.Purchasing;

/// <summary>
/// New orders and goods receipts with their audit events in one save. The unique indexes on the order and receipt
/// numbers decide when two people save the same number at the same moment.
/// </summary>
public sealed class EfPurchasingStore(EInvoiceDbContext db) : IPurchasingStore
{
    public async Task<bool?> VendorIsActiveAsync(Guid vendorId, CancellationToken cancellationToken) =>
        await db.Vendors.Where(v => v.Id == vendorId).Select(v => (bool?)v.IsActive).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    // The columns use the database's case-insensitive collation, as the unique index does.
    public Task<bool> OrderNumberExistsAsync(string number, CancellationToken cancellationToken) =>
        db.PurchaseOrders.AnyAsync(o => o.Number == number, cancellationToken);

    public Task<bool> ReceiptNumberExistsAsync(string number, CancellationToken cancellationToken) =>
        db.GoodsReceipts.AnyAsync(r => r.Number == number, cancellationToken);

    public Task<PurchaseOrder?> FindOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        db.PurchaseOrders.AsNoTracking().SingleOrDefaultAsync(o => o.Id == orderId, cancellationToken);

    public async Task<IReadOnlyDictionary<int, decimal>> ReceivedAsync(Guid orderId, CancellationToken cancellationToken) =>
        await db.Set<GoodsReceiptLine>()
            .Where(l => db.GoodsReceipts.Any(r => r.PurchaseOrderId == orderId && EF.Property<Guid>(l, "GoodsReceiptId") == r.Id))
            .GroupBy(l => l.PurchaseOrderLineNumber)
            .Select(g => new { Line = g.Key, Quantity = g.Sum(l => l.QuantityReceived) })
            .ToDictionaryAsync(x => x.Line, x => x.Quantity, cancellationToken)
            .ConfigureAwait(false);

    public void Add(PurchaseOrder order) => db.PurchaseOrders.Add(order);

    public void Add(GoodsReceipt receipt) => db.GoodsReceipts.Add(receipt);

    public void Audit(AuditDraft draft) => db.QueueAudit(draft);

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            // Nothing was stored: drop the changes and their event so the context can still be used.
            db.ChangeTracker.Clear();
            db.ClearQueuedAudit();
            throw new DuplicateNumberException();
        }
    }
}
