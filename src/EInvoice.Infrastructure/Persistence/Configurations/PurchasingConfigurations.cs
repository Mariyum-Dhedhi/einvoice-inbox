using EInvoice.Domain.Purchasing;
using EInvoice.Domain.Shared;
using EInvoice.Domain.Vendors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using static EInvoice.Infrastructure.Persistence.Configurations.MappingConventions;

namespace EInvoice.Infrastructure.Persistence.Configurations;

internal sealed class VendorConfiguration : IEntityTypeConfiguration<Vendor>
{
    public void Configure(EntityTypeBuilder<Vendor> builder)
    {
        builder.ToTable("Vendor");
        builder.HasKey(v => v.Id).IsClustered(false);
        builder.UseClusteredRowId("Vendor");

        builder.Property(v => v.Number).HasMaxLength(20);
        builder.Property(v => v.Name).HasMaxLength(200);
        builder.Property(v => v.VatId).HasMaxLength(20).IsUnicode(false);
        builder.Property(v => v.IsActive);

        builder.HasIndex(v => v.Number).IsUnique().HasDatabaseName("UX_Vendor_Number");

        // Validation finds the vendor by VAT id, so one VAT id must mean one vendor.
        builder.HasIndex(v => v.VatId).IsUnique().HasFilter("[VatId] IS NOT NULL").HasDatabaseName("UX_Vendor_VatId");
    }
}

internal sealed class VendorBankAccountConfiguration : IEntityTypeConfiguration<VendorBankAccount>
{
    public void Configure(EntityTypeBuilder<VendorBankAccount> builder)
    {
        builder.ToTable("VendorBankAccount");
        builder.HasKey(a => a.Id).IsClustered(false);
        builder.UseClusteredRowId("VendorBankAccount");

        builder.Property(a => a.VendorId);
        builder.Property(a => a.Iban)
            .HasConversion(iban => iban.Value, value => Iban.Parse(value))
            .HasColumnType("varchar(34)")
            .HasMaxLength(34)
            .IsUnicode(false);

        builder.HasOne<Vendor>().WithMany().HasForeignKey(a => a.VendorId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(a => new { a.VendorId, a.Iban }).IsUnique().HasDatabaseName("UX_VendorBankAccount_Vendor_Iban");
    }
}

internal sealed class PurchaseOrderConfiguration : IEntityTypeConfiguration<PurchaseOrder>
{
    public void Configure(EntityTypeBuilder<PurchaseOrder> builder)
    {
        builder.ToTable("PurchaseOrder");
        builder.HasKey(p => p.Id).IsClustered(false);
        builder.UseClusteredRowId("PurchaseOrder");

        builder.Property(p => p.Number).HasMaxLength(20);
        builder.Property(p => p.OrderDate);
        builder.Property(p => p.Currency).HasColumnType(CurrencyType).HasMaxLength(3).IsFixedLength().IsUnicode(false);

        builder.HasOne<Vendor>().WithMany().HasForeignKey(p => p.VendorId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(p => p.Number).IsUnique().HasDatabaseName("UX_PurchaseOrder_Number");

        builder.HasMany(p => p.Lines).WithOne().HasForeignKey("PurchaseOrderId").IsRequired().OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(p => p.Lines).UsePropertyAccessMode(PropertyAccessMode.Field).AutoInclude();
    }
}

internal sealed class PurchaseOrderLineConfiguration : IEntityTypeConfiguration<PurchaseOrderLine>
{
    public void Configure(EntityTypeBuilder<PurchaseOrderLine> builder)
    {
        builder.ToTable("PurchaseOrderLine");
        builder.HasKey("PurchaseOrderId", nameof(PurchaseOrderLine.LineNumber)).IsClustered(false);
        builder.UseClusteredRowId("PurchaseOrderLine");

        builder.Property(l => l.LineNumber).ValueGeneratedNever();
        builder.Property(l => l.ItemNumber).HasMaxLength(50);
        builder.Property(l => l.Description).HasMaxLength(500);
        builder.Property(l => l.Quantity).HasColumnType(QuantityType);
        builder.Property(l => l.UnitPrice).HasColumnType(UnitPriceType);
        builder.Property(l => l.TaxRatePercent).HasColumnType(TaxRateType);
    }
}

internal sealed class GoodsReceiptConfiguration : IEntityTypeConfiguration<GoodsReceipt>
{
    public void Configure(EntityTypeBuilder<GoodsReceipt> builder)
    {
        builder.ToTable("GoodsReceipt");
        builder.HasKey(g => g.Id).IsClustered(false);
        builder.UseClusteredRowId("GoodsReceipt");

        builder.Property(g => g.Number).HasMaxLength(20);
        builder.Property(g => g.ReceivedDate);

        builder.HasOne<PurchaseOrder>().WithMany().HasForeignKey(g => g.PurchaseOrderId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(g => g.Number).IsUnique().HasDatabaseName("UX_GoodsReceipt_Number");

        builder.HasMany(g => g.Lines).WithOne().HasForeignKey("GoodsReceiptId").IsRequired().OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(g => g.Lines).UsePropertyAccessMode(PropertyAccessMode.Field).AutoInclude();
    }
}

internal sealed class GoodsReceiptLineConfiguration : IEntityTypeConfiguration<GoodsReceiptLine>
{
    public void Configure(EntityTypeBuilder<GoodsReceiptLine> builder)
    {
        builder.ToTable("GoodsReceiptLine");
        builder.HasKey("GoodsReceiptId", nameof(GoodsReceiptLine.PurchaseOrderLineNumber)).IsClustered(false);
        builder.UseClusteredRowId("GoodsReceiptLine");

        builder.Property(l => l.PurchaseOrderLineNumber).ValueGeneratedNever();
        builder.Property(l => l.QuantityReceived).HasColumnType(QuantityType);
    }
}
