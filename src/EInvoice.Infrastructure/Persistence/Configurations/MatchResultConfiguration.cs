using EInvoice.Domain.Invoices;
using EInvoice.Domain.Matching;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using static EInvoice.Infrastructure.Persistence.Configurations.MappingConventions;

namespace EInvoice.Infrastructure.Persistence.Configurations;

internal sealed class MatchResultConfiguration : IEntityTypeConfiguration<MatchResultRecord>
{
    public void Configure(EntityTypeBuilder<MatchResultRecord> builder)
    {
        builder.ToTable("MatchResult", table =>
            table.HasCheckConstraint("CK_MatchResult_Outcome", EnumCheck<MatchOutcome>("Outcome")));
        builder.HasKey(r => new { r.InvoiceId, r.InvoiceLineNumber }).IsClustered(false);
        builder.UseClusteredRowId("MatchResult");

        builder.Property(r => r.InvoiceLineNumber).ValueGeneratedNever();
        builder.Property(r => r.InvoiceUnitPrice).HasColumnType(UnitPriceType);
        builder.Property(r => r.OrderUnitPrice).HasColumnType(UnitPriceType);
        builder.Property(r => r.PriceVariancePercent).HasColumnType("decimal(19,6)");
        builder.Property(r => r.PriceVarianceAmount).HasColumnType(MoneyType);
        builder.Property(r => r.Currency).HasColumnType(CurrencyType).HasMaxLength(3).IsFixedLength().IsUnicode(false);
        builder.Property(r => r.QuantityInvoiced).HasColumnType(QuantityType);
        builder.Property(r => r.QuantityAvailable).HasColumnType(QuantityType);
        builder.Property(r => r.QuantityVariance).HasColumnType(QuantityType);
        builder.Property(r => r.PriceTolerancePercent).HasColumnType("decimal(5,2)");

        // Results belong to their invoice and are deleted with it.
        builder.HasOne<Invoice>().WithMany().HasForeignKey(r => r.InvoiceId).IsRequired().OnDelete(DeleteBehavior.Cascade);
    }
}
