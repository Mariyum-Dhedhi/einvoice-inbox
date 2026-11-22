using EInvoice.Application.Audit;
using EInvoice.Application.Purchasing;
using EInvoice.Application.Sync;
using EInvoice.Domain.Purchasing;
using EInvoice.Domain.Shared;
using EInvoice.Domain.Vendors;
using EInvoice.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace EInvoice.Infrastructure.Sync;

/// <summary>
/// Master data from the ERP in SQL Server: records found by their ERP id (unique filtered indexes), saved with their
/// audit events in one transaction; watermarks and open failures per entity. Only records from the ERP are found here,
/// so the sync can never touch a record made in the app.
/// </summary>
public sealed class EfErpSyncStore(EInvoiceDbContext db) : IErpSyncStore
{
    public Task<Vendor?> FindVendorAsync(string erpId, CancellationToken cancellationToken) =>
        db.Vendors.SingleOrDefaultAsync(v => v.ErpId == erpId && v.Source == RecordSource.Erp, cancellationToken);

    public Task<PurchaseOrder?> FindOrderAsync(string erpId, CancellationToken cancellationToken) =>
        db.PurchaseOrders.SingleOrDefaultAsync(o => o.ErpId == erpId && o.Source == RecordSource.Erp, cancellationToken);

    public Task<GoodsReceipt?> FindReceiptAsync(string erpId, CancellationToken cancellationToken) =>
        db.GoodsReceipts.SingleOrDefaultAsync(r => r.ErpId == erpId && r.Source == RecordSource.Erp, cancellationToken);

    public Task<bool> VendorNumberExistsAsync(string number, CancellationToken cancellationToken) =>
        db.Vendors.AnyAsync(v => v.Number == number, cancellationToken);

    public Task<bool> OrderNumberExistsAsync(string number, CancellationToken cancellationToken) =>
        db.PurchaseOrders.AnyAsync(o => o.Number == number, cancellationToken);

    public Task<bool> ReceiptNumberExistsAsync(string number, CancellationToken cancellationToken) =>
        db.GoodsReceipts.AnyAsync(r => r.Number == number, cancellationToken);

    public async Task<IReadOnlyDictionary<int, decimal>> ReceivedAsync(Guid orderId, Guid? exceptReceipt, CancellationToken cancellationToken) =>
        await db.Set<GoodsReceiptLine>()
            .Where(l => db.GoodsReceipts.Any(r => r.PurchaseOrderId == orderId && r.Id != exceptReceipt && EF.Property<Guid>(l, "GoodsReceiptId") == r.Id))
            .GroupBy(l => l.PurchaseOrderLineNumber)
            .Select(g => new { Line = g.Key, Quantity = g.Sum(l => l.QuantityReceived) })
            .ToDictionaryAsync(x => x.Line, x => x.Quantity, cancellationToken)
            .ConfigureAwait(false);

    public void Add(Vendor vendor) => db.Vendors.Add(vendor);

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

    public async Task<DateTimeOffset?> WatermarkAsync(SyncEntity entity, CancellationToken cancellationToken) =>
        await db.SyncWatermarks.AsNoTracking().Where(w => w.Entity == entity).Select(w => (DateTimeOffset?)w.Value)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

    public async Task SetWatermarkAsync(SyncEntity entity, DateTimeOffset value, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var mark = await db.SyncWatermarks.SingleOrDefaultAsync(w => w.Entity == entity, cancellationToken).ConfigureAwait(false);
        if (mark is null)
        {
            db.SyncWatermarks.Add(new SyncWatermarkRecord { Entity = entity, Value = value, UpdatedAt = at });
        }
        else
        {
            mark.Value = value;
            mark.UpdatedAt = at;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SyncFailureView>> FailuresAsync(SyncEntity entity, CancellationToken cancellationToken) =>
        (await db.SyncFailures.AsNoTracking().Where(f => f.Entity == entity)
            .Select(f => new { f.ErpId, f.Reason, f.FirstSeen, f.LastSeen, f.Tries })
            .ToListAsync(cancellationToken).ConfigureAwait(false))
        .OrderBy(f => f.FirstSeen).ThenBy(f => f.ErpId, StringComparer.Ordinal)
        .Select(f => new SyncFailureView(f.ErpId, f.Reason, f.FirstSeen, f.LastSeen, f.Tries))
        .ToList();

    public async Task RecordFailureAsync(SyncEntity entity, string erpId, string reason, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var failure = await db.SyncFailures.SingleOrDefaultAsync(f => f.Entity == entity && f.ErpId == erpId, cancellationToken).ConfigureAwait(false);
        if (failure is null)
        {
            db.SyncFailures.Add(new SyncFailureRecord { Entity = entity, ErpId = erpId, Reason = reason, FirstSeen = at, LastSeen = at, Tries = 1 });
        }
        else
        {
            failure.Reason = reason;
            failure.LastSeen = at;
            failure.Tries++;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task ClearFailureAsync(SyncEntity entity, string erpId, CancellationToken cancellationToken) =>
        db.SyncFailures.Where(f => f.Entity == entity && f.ErpId == erpId).ExecuteDeleteAsync(cancellationToken);
}
