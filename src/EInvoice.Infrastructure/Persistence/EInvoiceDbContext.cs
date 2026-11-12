using EInvoice.Domain.Invoices;
using EInvoice.Domain.Purchasing;
using EInvoice.Domain.Vendors;
using Microsoft.EntityFrameworkCore;

namespace EInvoice.Infrastructure.Persistence;

public sealed class EInvoiceDbContext(DbContextOptions<EInvoiceDbContext> options) : DbContext(options)
{
    public DbSet<Vendor> Vendors => Set<Vendor>();

    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();

    public DbSet<GoodsReceipt> GoodsReceipts => Set<GoodsReceipt>();

    public DbSet<Invoice> Invoices => Set<Invoice>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("dbo");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(EInvoiceDbContext).Assembly);
    }
}
