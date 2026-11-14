using EInvoice.Application.Validation;
using EInvoice.Domain.Invoices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using static EInvoice.Infrastructure.Persistence.Configurations.MappingConventions;

namespace EInvoice.Infrastructure.Persistence.Configurations;

internal sealed class InvoiceDocumentConfiguration : IEntityTypeConfiguration<InvoiceDocument>
{
    public void Configure(EntityTypeBuilder<InvoiceDocument> builder)
    {
        builder.ToTable("InvoiceDocument");
        builder.HasKey(d => d.InvoiceId).IsClustered(false);
        builder.UseClusteredRowId("InvoiceDocument");

        builder.Property(d => d.FileName).HasMaxLength(255);
        builder.Property(d => d.ContentType).HasMaxLength(100).IsUnicode(false);
        builder.Property(d => d.Content).HasColumnType("varbinary(max)");
        builder.Property(d => d.Sha256).HasColumnType("binary(32)").HasMaxLength(32).IsFixedLength();
        builder.Property(d => d.SizeBytes);
        builder.Property(d => d.ReceivedAt);

        // Duplicate-file protection in the database: two simultaneous uploads of one file cannot both be stored.
        builder.HasIndex(d => d.Sha256).IsUnique().HasDatabaseName("UX_InvoiceDocument_Sha256");

        builder.HasOne<Invoice>().WithOne().HasForeignKey<InvoiceDocument>(d => d.InvoiceId).IsRequired().OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class InvoiceFindingConfiguration : IEntityTypeConfiguration<InvoiceFindingRecord>
{
    public void Configure(EntityTypeBuilder<InvoiceFindingRecord> builder)
    {
        builder.ToTable("InvoiceFinding", table =>
            table.HasCheckConstraint("CK_InvoiceFinding_Severity", EnumCheck<FindingSeverity>("Severity")));
        builder.HasKey(f => new { f.InvoiceId, f.Sequence }).IsClustered(false);
        builder.UseClusteredRowId("InvoiceFinding");

        builder.Property(f => f.Sequence).ValueGeneratedNever();
        builder.Property(f => f.RuleCode).HasMaxLength(40).IsUnicode(false);
        builder.Property(f => f.Message).HasMaxLength(500);
        builder.Property(f => f.LinePosition);

        builder.HasOne<Invoice>().WithMany().HasForeignKey(f => f.InvoiceId).IsRequired().OnDelete(DeleteBehavior.Cascade);
    }
}
