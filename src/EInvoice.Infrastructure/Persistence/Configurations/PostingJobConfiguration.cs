using EInvoice.Application.Posting;
using EInvoice.Domain.Invoices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using static EInvoice.Infrastructure.Persistence.Configurations.MappingConventions;

namespace EInvoice.Infrastructure.Persistence.Configurations;

internal sealed class PostingJobConfiguration : IEntityTypeConfiguration<PostingJobRecord>
{
    public void Configure(EntityTypeBuilder<PostingJobRecord> builder)
    {
        builder.ToTable("PostingJob", table => table.HasCheckConstraint("CK_PostingJob_Status", EnumCheck<PostingJobStatus>("Status")));
        builder.HasKey(j => j.Id).IsClustered(false);
        builder.UseClusteredRowId("PostingJob");

        builder.Property(j => j.Status);
        builder.Property(j => j.LastError).HasMaxLength(200);
        builder.Property(j => j.ErpDocumentNumber).HasMaxLength(50);

        builder.HasOne<Invoice>().WithMany().HasForeignKey(j => j.InvoiceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(j => new { j.Status, j.NextAttemptAt }).HasDatabaseName("IX_PostingJob_Due"); // what the worker asks
    }
}
