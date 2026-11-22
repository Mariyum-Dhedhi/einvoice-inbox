using EInvoice.Application.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using static EInvoice.Infrastructure.Persistence.Configurations.MappingConventions;

namespace EInvoice.Infrastructure.Persistence.Configurations;

internal sealed class SyncWatermarkConfiguration : IEntityTypeConfiguration<SyncWatermarkRecord>
{
    public void Configure(EntityTypeBuilder<SyncWatermarkRecord> builder)
    {
        builder.ToTable("SyncWatermark", table => table.HasCheckConstraint("CK_SyncWatermark_Entity", EnumCheck<SyncEntity>("Entity")));
        builder.HasKey(w => w.Id).IsClustered(false);
        builder.UseClusteredRowId("SyncWatermark");

        builder.Property(w => w.Entity);
        builder.HasIndex(w => w.Entity).IsUnique().HasDatabaseName("UX_SyncWatermark_Entity"); // one per entity
    }
}

internal sealed class SyncFailureConfiguration : IEntityTypeConfiguration<SyncFailureRecord>
{
    public void Configure(EntityTypeBuilder<SyncFailureRecord> builder)
    {
        builder.ToTable("SyncFailure", table => table.HasCheckConstraint("CK_SyncFailure_Entity", EnumCheck<SyncEntity>("Entity")));
        builder.HasKey(f => f.Id).IsClustered(false);
        builder.UseClusteredRowId("SyncFailure");

        builder.Property(f => f.Entity);
        builder.Property(f => f.ErpId).HasMaxLength(ErpSync.MaxErpIdLength).IsUnicode(false);
        builder.Property(f => f.Reason).HasMaxLength(ErpSync.MaxReasonLength);
        builder.HasIndex(f => new { f.Entity, f.ErpId }).IsUnique().HasDatabaseName("UX_SyncFailure_Entity_ErpId");
    }
}
