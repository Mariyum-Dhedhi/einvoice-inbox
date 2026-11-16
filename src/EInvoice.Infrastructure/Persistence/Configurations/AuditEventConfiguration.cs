using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EInvoice.Infrastructure.Persistence.Configurations;

internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEventRecord>
{
    public const int SubjectLength = 300; // "User/" + a typed user name of up to 256 characters

    public void Configure(EntityTypeBuilder<AuditEventRecord> builder)
    {
        builder.ToTable("AuditEvent");
        builder.HasKey(e => e.Id).IsClustered(false);
        builder.UseClusteredRowId("AuditEvent");

        builder.Property(e => e.Sequence);
        builder.Property(e => e.At);
        builder.Property(e => e.Actor).HasMaxLength(256);
        builder.Property(e => e.Action).HasMaxLength(50).IsUnicode(false);
        builder.Property(e => e.Subject).HasMaxLength(SubjectLength);
        builder.Property(e => e.Details).HasMaxLength(2000);
        builder.Property(e => e.PreviousHash).HasColumnType("binary(32)").HasMaxLength(32).IsFixedLength();
        builder.Property(e => e.Hash).HasColumnType("binary(32)").HasMaxLength(32).IsFixedLength();

        builder.HasIndex(e => e.Sequence).IsUnique().HasDatabaseName("UX_AuditEvent_Sequence");
        builder.HasIndex(e => e.Subject).HasDatabaseName("IX_AuditEvent_Subject");
    }
}
