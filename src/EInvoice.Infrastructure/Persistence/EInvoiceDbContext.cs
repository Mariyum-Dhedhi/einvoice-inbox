using EInvoice.Domain.Invoices;
using EInvoice.Domain.Purchasing;
using EInvoice.Domain.Vendors;
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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("dbo");
        base.OnModelCreating(modelBuilder); // Identity's model first; our configurations then rename and constrain it
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(EInvoiceDbContext).Assembly);
    }
}
