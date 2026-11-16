using System.Text.Json;
using EInvoice.Application.Audit;
using EInvoice.Domain.Invoices;
using EInvoice.Domain.Purchasing;
using EInvoice.Domain.Vendors;
using EInvoice.Infrastructure.Audit;
using EInvoice.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace EInvoice.Infrastructure.Persistence;

/// <summary>Business data and the sign-in tables (users, roles) in one database and one migration history.</summary>
public sealed class EInvoiceDbContext(DbContextOptions<EInvoiceDbContext> options) : IdentityDbContext<AppUser, AppRole, Guid>(options)
{
    public DbSet<Vendor> Vendors => Set<Vendor>();

    public DbSet<VendorBankAccount> VendorBankAccounts => Set<VendorBankAccount>();

    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();

    public DbSet<GoodsReceipt> GoodsReceipts => Set<GoodsReceipt>();

    public DbSet<Invoice> Invoices => Set<Invoice>();

    public DbSet<InvoiceDocument> InvoiceDocuments => Set<InvoiceDocument>();

    public DbSet<InvoiceFindingRecord> InvoiceFindings => Set<InvoiceFindingRecord>();

    /// <summary>Written only by dbo.usp_MatchInvoice; read by the match panel.</summary>
    public DbSet<MatchResultRecord> MatchResults => Set<MatchResultRecord>();

    /// <summary>The hash-chained audit trail; rows are only ever added (see AuditChain).</summary>
    public DbSet<AuditEventRecord> AuditEvents => Set<AuditEventRecord>();

    /// <summary>
    /// Saves, and writes one audit event per new invoice status change in the same transaction: a change is never
    /// stored without its event, and a rolled-back change leaves none.
    /// </summary>
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var drafts = StatusChangeEvents();
        drafts.AddRange(_queuedAudit);
        if (drafts.Count == 0)
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken).ConfigureAwait(false);
        }

        var own = Database.CurrentTransaction is null ? await Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false) : null;
        try
        {
            var saved = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken).ConfigureAwait(false);
            await AuditChain.AppendAsync(this, drafts, cancellationToken).ConfigureAwait(false);
            if (own is not null)
            {
                await own.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            _queuedAudit.Clear();
            return saved;
        }
        finally
        {
            if (own is not null)
            {
                await own.DisposeAsync().ConfigureAwait(false); // not committed means rolled back
            }
        }
    }

    private readonly List<AuditDraft> _queuedAudit = [];

    /// <summary>An audit event for the next save (for example a bank account change), written in its transaction.</summary>
    public void QueueAudit(AuditDraft draft) => _queuedAudit.Add(draft);

    /// <summary>Forgets queued events after a failed save, together with the tracked changes they describe.</summary>
    public void ClearQueuedAudit() => _queuedAudit.Clear();

    /// <summary>Synchronous saves cannot take the audit lock; audited changes must be saved with SaveChangesAsync.</summary>
    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        StatusChangeEvents().Count == 0 && _queuedAudit.Count == 0
            ? base.SaveChanges(acceptAllChangesOnSuccess)
            : throw new InvalidOperationException("Invoice status changes are audited; save them with SaveChangesAsync.");

    /// <summary>For the audit chain itself: a save that does not look for status changes again.</summary>
    internal Task<int> SaveWithoutAuditAsync(CancellationToken cancellationToken) => base.SaveChangesAsync(true, cancellationToken);

    private List<AuditDraft> StatusChangeEvents() =>
        ChangeTracker.Entries<StatusChange>()
            .Where(e => e.State == EntityState.Added)
            .Select(e => (InvoiceId: (Guid)e.Property("InvoiceId").CurrentValue!, Change: e.Entity))
            .OrderBy(c => c.InvoiceId).ThenBy(c => c.Change.Sequence)
            .Select(c => new AuditDraft(
                c.Change.At,
                c.Change.Actor,
                AuditActions.InvoiceStatusChanged,
                $"Invoice/{c.InvoiceId}",
                JsonSerializer.Serialize(new { from = c.Change.From?.ToString(), to = c.Change.To.ToString(), reason = c.Change.Reason })))
            .ToList();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("dbo");
        base.OnModelCreating(modelBuilder); // Identity's model first; our configurations then rename and constrain it
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(EInvoiceDbContext).Assembly);
    }
}
