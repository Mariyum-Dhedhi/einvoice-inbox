using EInvoice.Application.Audit;
using EInvoice.Application.Matching;
using EInvoice.Application.Vendors;
using EInvoice.Domain.Shared;
using EInvoice.Domain.Vendors;
using EInvoice.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace EInvoice.Infrastructure.Vendors;

/// <summary>
/// Bank account changes and their audit events in one save. The account's rowversion makes two admins changing the
/// same account at once a conflict; the unique (vendor, IBAN) index makes two identical proposals one.
/// </summary>
public sealed class EfBankAccountStore(EInvoiceDbContext db) : IBankAccountStore
{
    public Task<bool> VendorExistsAsync(Guid vendorId, CancellationToken cancellationToken) =>
        db.Vendors.AnyAsync(v => v.Id == vendorId, cancellationToken);

    public Task<bool> ExistsAsync(Guid vendorId, Iban iban, CancellationToken cancellationToken) =>
        db.VendorBankAccounts.AnyAsync(a => a.VendorId == vendorId && a.Iban == iban, cancellationToken);

    public Task<VendorBankAccount?> FindAsync(Guid accountId, CancellationToken cancellationToken) =>
        db.VendorBankAccounts.SingleOrDefaultAsync(a => a.Id == accountId, cancellationToken);

    public void Add(VendorBankAccount account) => db.VendorBankAccounts.Add(account);

    public void Remove(VendorBankAccount account) => db.VendorBankAccounts.Remove(account);

    public void Audit(AuditDraft draft) => db.QueueAudit(draft);

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            Forget();
            throw new ConcurrentStatusChangeException();
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            Forget();
            throw new BankAccountExistsException();
        }
    }

    // Nothing was stored: drop the stale changes and their events so the context can still be used.
    private void Forget()
    {
        db.ChangeTracker.Clear();
        db.ClearQueuedAudit();
    }
}
