using EInvoice.Domain.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EInvoice.Infrastructure.Persistence.Configurations;

/// <summary>Column types and the clustered-RowId pattern shared by all tables.</summary>
internal static class MappingConventions
{
    public const string MoneyType = "decimal(18,2)";
    public const string UnitPriceType = "decimal(18,6)";
    public const string QuantityType = "decimal(18,4)";
    public const string TaxRateType = "decimal(5,2)";
    public const string CurrencyType = "char(3)";
    public const string RowId = "RowId";

    /// <summary>
    /// Keys are Guids (or Guid + line number) created by the domain, so entities are valid before they are saved.
    /// Random Guids make a poor clustered index (page splits), so the key is nonclustered and each table is
    /// clustered on an ever-increasing bigint IDENTITY column that the domain never sees.
    /// </summary>
    public static void UseClusteredRowId<T>(this EntityTypeBuilder<T> builder, string table)
        where T : class
    {
        builder.Property<long>(RowId).UseIdentityColumn();
        builder.HasIndex(RowId).IsClustered().HasDatabaseName($"CX_{table}_RowId");
    }

    public static ComplexPropertyBuilder<Money> MapMoney(this ComplexPropertyBuilder<Money> money)
    {
        money.Property(m => m.Amount).HasColumnType(MoneyType);
        money.Property(m => m.Currency).HasColumnType(CurrencyType).HasMaxLength(3).IsFixedLength().IsUnicode(false);
        return money;
    }

    /// <summary>CHECK constraint generated from the C# enum, so the database refuses values the code does not know.</summary>
    public static string EnumCheck<TEnum>(string column)
        where TEnum : struct, Enum =>
        $"[{column}] IN ({string.Join(", ", Enum.GetValues<TEnum>().Select(v => Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture)))})";
}
