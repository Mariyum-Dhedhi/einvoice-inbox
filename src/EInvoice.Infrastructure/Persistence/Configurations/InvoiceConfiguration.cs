using EInvoice.Domain.Invoices;
using EInvoice.Domain.Vendors;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using static EInvoice.Infrastructure.Persistence.Configurations.MappingConventions;

namespace EInvoice.Infrastructure.Persistence.Configurations;

internal sealed class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    public void Configure(EntityTypeBuilder<Invoice> builder)
    {
        builder.ToTable("Invoice", table =>
        {
            table.HasCheckConstraint("CK_Invoice_Status", EnumCheck<InvoiceStatus>("Status"));
            table.HasCheckConstraint("CK_Invoice_SourceFormat", EnumCheck<SourceFormat>("SourceFormat"));
        });
        builder.HasKey(i => i.Id).IsClustered(false);
        builder.UseClusteredRowId("Invoice");

        builder.Property(i => i.SourceFormat);
        builder.Property(i => i.SubmittedBy).HasMaxLength(256);
        builder.Property(i => i.Status);
        builder.Property(i => i.InvoiceNumber).HasMaxLength(50);
        builder.Property(i => i.IssueDate);
        builder.Property(i => i.PurchaseOrderNumber).HasMaxLength(20);
        builder.ComplexProperty(i => i.NetTotal, m => m.MapMoney());
        builder.ComplexProperty(i => i.TaxTotal, m => m.MapMoney());
        builder.ComplexProperty(i => i.GrossTotal, m => m.MapMoney());

        builder.HasOne<Vendor>().WithMany().HasForeignKey(i => i.VendorId).OnDelete(DeleteBehavior.Restrict);

        // Duplicate protection in the database, so two uploads racing each other cannot both succeed.
        // Filtered only because a just-received invoice has no vendor or number yet. Status is deliberately
        // not in the filter: a rejected invoice still blocks its number (corrections need a new number).
        builder.HasIndex(i => new { i.VendorId, i.InvoiceNumber })
            .IsUnique()
            .HasFilter("[VendorId] IS NOT NULL AND [InvoiceNumber] IS NOT NULL")
            .HasDatabaseName("UX_Invoice_Vendor_InvoiceNumber");
        builder.HasIndex(i => i.Status).HasDatabaseName("IX_Invoice_Status");

        builder.HasMany(i => i.Lines).WithOne().HasForeignKey("InvoiceId").IsRequired().OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(i => i.Lines).UsePropertyAccessMode(PropertyAccessMode.Field).AutoInclude();

        builder.HasMany(i => i.StatusHistory).WithOne().HasForeignKey("InvoiceId").IsRequired().OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(i => i.StatusHistory).UsePropertyAccessMode(PropertyAccessMode.Field).AutoInclude();
    }
}

internal sealed class InvoiceLineConfiguration : IEntityTypeConfiguration<InvoiceLine>
{
    public void Configure(EntityTypeBuilder<InvoiceLine> builder)
    {
        builder.ToTable("InvoiceLine");
        builder.HasKey("InvoiceId", nameof(InvoiceLine.LineNumber)).IsClustered(false);
        builder.UseClusteredRowId("InvoiceLine");

        builder.Property(l => l.LineNumber).ValueGeneratedNever();
        builder.Property(l => l.Description).HasMaxLength(500);
        builder.Property(l => l.Quantity).HasColumnType(QuantityType);
        builder.Property(l => l.UnitPrice).HasColumnType(UnitPriceType);
        builder.ComplexProperty(l => l.NetAmount, m => m.MapMoney());
        builder.Property(l => l.TaxRatePercent).HasColumnType(TaxRateType);
        builder.Property(l => l.PurchaseOrderLineNumber);
    }
}

internal sealed class StatusChangeConfiguration : IEntityTypeConfiguration<StatusChange>
{
    public void Configure(EntityTypeBuilder<StatusChange> builder)
    {
        builder.ToTable("InvoiceStatusChange", table =>
        {
            table.HasCheckConstraint("CK_InvoiceStatusChange_FromStatus", EnumCheck<InvoiceStatus>("FromStatus"));
            table.HasCheckConstraint("CK_InvoiceStatusChange_ToStatus", EnumCheck<InvoiceStatus>("ToStatus"));
        });
        // (InvoiceId, Sequence) is the natural key: it orders the history, and two writers adding
        // entry 3 to the same invoice at the same moment collide instead of both succeeding.
        builder.HasKey("InvoiceId", nameof(StatusChange.Sequence)).IsClustered(false);
        builder.UseClusteredRowId("InvoiceStatusChange");

        builder.Property(c => c.Sequence).ValueGeneratedNever();
        builder.Property(c => c.From).HasColumnName("FromStatus");
        builder.Property(c => c.To).HasColumnName("ToStatus");
        builder.Property(c => c.Actor).HasMaxLength(256);
        builder.Property(c => c.At);
        builder.Property(c => c.Reason).HasMaxLength(200);
    }
}
