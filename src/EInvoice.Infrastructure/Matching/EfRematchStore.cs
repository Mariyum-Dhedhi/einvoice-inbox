using EInvoice.Application.Matching;
using EInvoice.Domain.Invoices;
using EInvoice.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace EInvoice.Infrastructure.Matching;

/// <summary>
/// Loads and saves invoices for re-matching. Two writers adding the same status history entry collide on its
/// key (InvoiceId, Sequence); that unique violation becomes <see cref="ConcurrentStatusChangeException"/>.
/// </summary>
public sealed class EfRematchStore(EInvoiceDbContext db) : IRematchStore
{
    public Task<Invoice?> FindAsync(Guid invoiceId, CancellationToken cancellationToken) =>
        db.Invoices.SingleOrDefaultAsync(i => i.Id == invoiceId, cancellationToken);

    public Task<bool> HasFindingsAsync(Guid invoiceId, CancellationToken cancellationToken) =>
        db.InvoiceFindings.AnyAsync(f => f.InvoiceId == invoiceId, cancellationToken);

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            // Nothing was stored; forget the stale invoice so this context can still be used.
            db.ChangeTracker.Clear();
            throw new ConcurrentStatusChangeException();
        }
    }
}
