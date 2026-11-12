using EInvoice.Domain.Invoices;
using EInvoice.Domain.Shared;
using EInvoice.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace EInvoice.UnitTests.Persistence;

/// <summary>
/// Checks the EF Core model that generates the schema, without a database.
/// Uses the design-time model, which includes relational details such as check constraints and index filters.
/// </summary>
public class DatabaseModelTests
{
    private static readonly IModel Model = CreateModel();

    private static IModel CreateModel()
    {
        var options = new DbContextOptionsBuilder<EInvoiceDbContext>()
            .UseSqlServer("Server=model-only;Database=model-only")
            .Options;
        using var context = new EInvoiceDbContext(options);
        return context.GetService<IDesignTimeModel>().Model;
    }

    /// <summary>Every column, including the Amount/Currency columns of Money complex properties.</summary>
    private static IEnumerable<IProperty> AllColumns() =>
        Model.GetEntityTypes().SelectMany(e =>
            e.GetProperties().Concat(e.GetComplexProperties().SelectMany(c => c.ComplexType.GetProperties())));

    private static IEntityType Entity<T>() =>
        Model.FindEntityType(typeof(T)) ?? throw new InvalidOperationException($"{typeof(T).Name} is not mapped.");

    [Fact]
    public void Expected_tables_exist()
    {
        var tables = Model.GetEntityTypes().Select(e => e.GetTableName() ?? "(none)").Distinct().Order().ToList();

        Assert.Equal(
            new[] { "GoodsReceipt", "GoodsReceiptLine", "Invoice", "InvoiceLine", "InvoiceStatusChange", "PurchaseOrder", "PurchaseOrderLine", "Vendor" },
            tables);
    }

    [Fact]
    public void No_column_uses_float_or_real()
    {
        Assert.All(AllColumns(), p =>
        {
            Assert.NotEqual(typeof(double), Nullable.GetUnderlyingType(p.ClrType) ?? p.ClrType);
            Assert.NotEqual(typeof(float), Nullable.GetUnderlyingType(p.ClrType) ?? p.ClrType);
            Assert.DoesNotContain("float", p.GetColumnType(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("real", p.GetColumnType(), StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Every_decimal_column_has_explicit_precision()
    {
        var decimals = AllColumns().Where(p => (Nullable.GetUnderlyingType(p.ClrType) ?? p.ClrType) == typeof(decimal));

        Assert.All(decimals, p => Assert.Matches(@"^decimal\(\d+,\d+\)$", p.GetColumnType()));
    }

    [Fact]
    public void Money_amounts_are_decimal_18_2_and_currencies_char_3()
    {
        var money = AllColumns().Where(p => p.DeclaringType.ClrType == typeof(Money)).ToList();

        Assert.NotEmpty(money);
        Assert.All(money.Where(p => p.Name == nameof(Money.Amount)), p => Assert.Equal("decimal(18,2)", p.GetColumnType()));
        Assert.All(money.Where(p => p.Name == nameof(Money.Currency)), p => Assert.Equal("char(3)", p.GetColumnType()));
    }

    [Theory]
    [InlineData("UnitPrice", "decimal(18,6)")]       // unit prices may have more than two decimals
    [InlineData("Quantity", "decimal(18,4)")]
    [InlineData("QuantityReceived", "decimal(18,4)")]
    [InlineData("TaxRatePercent", "decimal(5,2)")]
    public void Non_money_decimals_have_their_own_precision(string name, string expected)
    {
        var columns = AllColumns().Where(p => p.Name == name).ToList();

        Assert.NotEmpty(columns);
        Assert.All(columns, p => Assert.Equal(expected, p.GetColumnType()));
    }

    [Fact]
    public void Vendor_and_invoice_number_are_unique_even_for_rejected_invoices()
    {
        var index = Assert.Single(Entity<Invoice>().GetIndexes(),
            i => i.Properties.Select(p => p.Name).SequenceEqual(["VendorId", "InvoiceNumber"]));

        Assert.True(index.IsUnique);
        // Filtered only because a just-received invoice has no vendor or number yet. Status is not in the filter,
        // so a rejected invoice still blocks its number: corrections need a credit note and a new number.
        Assert.Equal("[VendorId] IS NOT NULL AND [InvoiceNumber] IS NOT NULL", index.GetFilter());
    }

    [Theory]
    [InlineData(typeof(EInvoice.Domain.Vendors.Vendor))]
    [InlineData(typeof(EInvoice.Domain.Purchasing.PurchaseOrder))]
    [InlineData(typeof(EInvoice.Domain.Purchasing.GoodsReceipt))]
    public void Business_numbers_are_unique(Type entity)
    {
        var type = Model.FindEntityType(entity)!;

        Assert.Contains(type.GetIndexes(), i => i.IsUnique && i.Properties.Single().Name == "Number" && i.GetFilter() is null);
    }

    [Fact]
    public void Status_check_constraint_lists_exactly_the_enum_values()
    {
        var expected = $"[Status] IN ({string.Join(", ", Enum.GetValues<InvoiceStatus>().Cast<int>())})";

        Assert.Contains(Entity<Invoice>().GetCheckConstraints(), c => c.Sql == expected);
    }

    [Fact]
    public void Source_format_check_constraint_lists_exactly_the_enum_values()
    {
        var expected = $"[SourceFormat] IN ({string.Join(", ", Enum.GetValues<SourceFormat>().Cast<int>())})";

        Assert.Contains(Entity<Invoice>().GetCheckConstraints(), c => c.Sql == expected);
    }

    [Fact]
    public void Every_table_is_clustered_on_an_identity_RowId_and_the_key_is_nonclustered()
    {
        Assert.All(Model.GetEntityTypes(), e =>
        {
            Assert.False(e.FindPrimaryKey()!.IsClustered() ?? true, $"{e.DisplayName()}: primary key must be nonclustered");

            var rowId = e.FindProperty("RowId");
            Assert.NotNull(rowId);
            Assert.Equal(typeof(long), rowId.ClrType);
            Assert.Equal(ValueGenerated.OnAdd, rowId.ValueGenerated);
            Assert.Contains(e.GetIndexes(), i => i.IsClustered() == true && i.Properties.Single() == rowId);
        });
    }

    // A document's own lines are deleted with it. This is the complete list: nothing else may cascade.
    private static readonly (string Principal, string Dependent)[] DocumentLines =
    [
        ("Invoice", "InvoiceLine"),
        ("Invoice", "InvoiceStatusChange"),
        ("PurchaseOrder", "PurchaseOrderLine"),
        ("GoodsReceipt", "GoodsReceiptLine"),
    ];

    [Fact]
    public void Only_a_documents_own_lines_cascade_on_delete()
    {
        var foreignKeys = Model.GetEntityTypes().SelectMany(e => e.GetForeignKeys()).ToList();

        Assert.All(foreignKeys, fk =>
        {
            var pair = (fk.PrincipalEntityType.GetTableName()!, fk.DeclaringEntityType.GetTableName()!);
            var expected = DocumentLines.Contains(pair) ? DeleteBehavior.Cascade : DeleteBehavior.Restrict;
            Assert.True(expected == fk.DeleteBehavior, $"{pair.Item1} -> {pair.Item2} should be {expected}, is {fk.DeleteBehavior}");
        });
    }

    [Fact]
    public void Lines_and_history_always_belong_to_a_document()
    {
        var lineKeys = Model.GetEntityTypes()
            .SelectMany(e => e.GetForeignKeys())
            .Where(fk => DocumentLines.Contains((fk.PrincipalEntityType.GetTableName()!, fk.DeclaringEntityType.GetTableName()!)))
            .ToList();

        Assert.Equal(DocumentLines.Length, lineKeys.Count);
        Assert.All(lineKeys, fk => Assert.True(fk.IsRequired, $"{fk.DeclaringEntityType.GetTableName()} rows must not exist without their document"));
    }

    [Fact]
    public void Deleting_master_data_never_cascades_to_documents()
    {
        var fromMasterData = Model.GetEntityTypes()
            .SelectMany(e => e.GetForeignKeys())
            .Where(fk => fk.PrincipalEntityType.GetTableName() is "Vendor" or "PurchaseOrder"
                         && !DocumentLines.Contains((fk.PrincipalEntityType.GetTableName()!, fk.DeclaringEntityType.GetTableName()!)))
            .ToList();

        Assert.NotEmpty(fromMasterData); // Vendor -> PurchaseOrder, Vendor -> Invoice, PurchaseOrder -> GoodsReceipt
        Assert.All(fromMasterData, fk => Assert.Equal(DeleteBehavior.Restrict, fk.DeleteBehavior));
    }

    [Fact]
    public void Strings_have_explicit_lengths()
    {
        var strings = AllColumns().Where(p => p.ClrType == typeof(string));

        Assert.All(strings, p => Assert.True(p.GetMaxLength() is not null || p.GetColumnType().StartsWith("char", StringComparison.Ordinal),
            $"{p.DeclaringType.DisplayName()}.{p.Name} has no max length (would be nvarchar(max))"));
    }
}
