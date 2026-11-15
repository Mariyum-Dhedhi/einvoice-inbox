using EInvoice.Application.Approval;
using EInvoice.Application.Matching;
using EInvoice.Application.Validation;
using EInvoice.Domain.Invoices;
using EInvoice.Infrastructure.Persistence;
using EInvoice.Infrastructure.Persistence.Configurations;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace EInvoice.Infrastructure.Approval;

/// <summary>
/// Loads an invoice with its rowversion and saves a decision only against the version the approver saw:
/// the version is put into the UPDATE's WHERE clause, so the database itself refuses a save on a newer invoice.
/// </summary>
public sealed class EfApprovalStore(EInvoiceDbContext db) : IApprovalStore
{
    public async Task<StoredInvoice?> FindAsync(Guid invoiceId, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.SingleOrDefaultAsync(i => i.Id == invoiceId, cancellationToken).ConfigureAwait(false);
        return invoice is null
            ? null
            : new StoredInvoice(invoice, MappingConventions.VersionText(VersionOf(invoice).CurrentValue));
    }

    public Task<bool> HasBlockingFindingsAsync(Guid invoiceId, CancellationToken cancellationToken) =>
        db.InvoiceFindings.AnyAsync(f => f.InvoiceId == invoiceId && f.Severity == FindingSeverity.Blocking, cancellationToken);

    public async Task SaveChangesAsync(string expectedVersion, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var entry in db.ChangeTracker.Entries<Invoice>())
            {
                VersionOf(entry.Entity).OriginalValue = Convert.FromBase64String(expectedVersion);
            }

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DbUpdateConcurrencyException or FormatException
                                   || ex is DbUpdateException { InnerException: SqlException { Number: 2601 or 2627 } })
        {
            // Nothing was stored; forget the stale invoice so this context can still be used.
            db.ChangeTracker.Clear();
            throw new ConcurrentStatusChangeException();
        }
    }

    private Microsoft.EntityFrameworkCore.ChangeTracking.PropertyEntry<Invoice, byte[]> VersionOf(Invoice invoice) =>
        db.Entry(invoice).Property<byte[]>(MappingConventions.Version);
}
