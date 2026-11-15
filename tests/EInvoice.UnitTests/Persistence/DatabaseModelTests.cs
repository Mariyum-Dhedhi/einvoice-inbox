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
            new[]
            {
                "AppRole", "AppRoleClaim", "AppUser", "AppUserClaim", "AppUserLogin", "AppUserRole", "AppUserToken", "AuditEvent",
                "GoodsReceipt", "GoodsReceiptLine", "Invoice", "InvoiceDocument", "InvoiceFinding", "InvoiceLine", "InvoiceStatusChange", "MatchResult",
                "PurchaseOrder", "PurchaseOrderLine", "Vendor", "VendorBankAccount",
            },
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
    public void Vat_id_identifies_at_most_one_vendor()
    {
        // Vendor lookup during validation is by VAT id; two vendors with the same id would make it ambiguous.
        var index = Assert.Single(Entity<EInvoice.Domain.Vendors.Vendor>().GetIndexes(), i => i.Properties.Single().Name == "VatId");

        Assert.True(index.IsUnique);
        Assert.Equal("[VatId] IS NOT NULL", index.GetFilter());
    }

    [Fact]
    public void A_bank_account_is_stored_once_per_vendor()
    {
        var index = Assert.Single(Entity<EInvoice.Domain.Vendors.VendorBankAccount>().GetIndexes(),
            i => i.Properties.Select(p => p.Name).SequenceEqual(["VendorId", "Iban"]));

        Assert.True(index.IsUnique);
        Assert.Equal("varchar(34)", Entity<EInvoice.Domain.Vendors.VendorBankAccount>().FindProperty("Iban")!.GetColumnType());
    }

    [Fact]
    public void Status_check_constraint_lists_exactly_the_enum_values()
    {
        var expected = $"[Status] IN ({string.Join(", ", Enum.GetValues<InvoiceStatus>().Cast<int>())})";

        Assert.Contains(Entity<Invoice>().GetCheckConstraints(), c => c.Sql == expected);
    }

    [Fact]
    public void Match_outcome_check_constraint_lists_exactly_the_enum_values()
    {
        var expected = $"[Outcome] IN ({string.Join(", ", Enum.GetValues<EInvoice.Domain.Matching.MatchOutcome>().Cast<int>())})";
        var matchResult = Model.GetEntityTypes().Single(e => e.GetTableName() == "MatchResult");

        Assert.Contains(matchResult.GetCheckConstraints(), c => c.Sql == expected);
    }

    [Fact]
    public void The_same_file_can_be_stored_only_once()
    {
        // Duplicate-file protection in the database, so two simultaneous uploads of one file cannot both succeed.
        var document = Model.GetEntityTypes().Single(e => e.GetTableName() == "InvoiceDocument");
        var index = Assert.Single(document.GetIndexes(), i => i.Properties.Single().Name == "Sha256");

        Assert.True(index.IsUnique);
        Assert.Equal("binary(32)", index.Properties.Single().GetColumnType());
        Assert.Equal("varbinary(max)", document.FindProperty("Content")!.GetColumnType());
    }

    [Fact]
    public void Finding_severity_check_constraint_lists_exactly_the_enum_values()
    {
        var expected = $"[Severity] IN ({string.Join(", ", Enum.GetValues<EInvoice.Application.Validation.FindingSeverity>().Cast<int>())})";
        var finding = Model.GetEntityTypes().Single(e => e.GetTableName() == "InvoiceFinding");

        Assert.Contains(finding.GetCheckConstraints(), c => c.Sql == expected);
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
            Assert.Equal(PropertySaveBehavior.Ignore, rowId.GetAfterSaveBehavior()); // an Update() must never send it
            Assert.Contains(e.GetIndexes(), i => i.IsClustered() == true && i.Properties.Single() == rowId);
        });
    }

    // A document's own lines are deleted with it. This is the complete list: nothing else may cascade.
    private static readonly (string Principal, string Dependent)[] DocumentLines =
    [
        ("Invoice", "InvoiceLine"),
        ("Invoice", "InvoiceStatusChange"),
        ("Invoice", "MatchResult"),
        ("Invoice", "InvoiceDocument"),
        ("Invoice", "InvoiceFinding"),
        ("PurchaseOrder", "PurchaseOrderLine"),
        ("GoodsReceipt", "GoodsReceiptLine"),

        // An account's own sign-in data (role memberships, claims, external logins, tokens) goes with it.
        ("AppUser", "AppUserRole"),
        ("AppUser", "AppUserClaim"),
        ("AppUser", "AppUserLogin"),
        ("AppUser", "AppUserToken"),
        ("AppRole", "AppUserRole"),
        ("AppRole", "AppRoleClaim"),
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

        Assert.NotEmpty(fromMasterData); // Vendor -> PurchaseOrder, Vendor -> Invoice, Vendor -> VendorBankAccount, PurchaseOrder -> GoodsReceipt
        Assert.All(fromMasterData, fk => Assert.Equal(DeleteBehavior.Restrict, fk.DeleteBehavior));
    }

    [Fact]
    public void Strings_have_explicit_lengths()
    {
        var strings = AllColumns().Where(p => p.ClrType == typeof(string));

        Assert.All(strings, p => Assert.True(p.GetMaxLength() is not null || p.GetColumnType().StartsWith("char", StringComparison.Ordinal),
            $"{p.DeclaringType.DisplayName()}.{p.Name} has no max length (would be nvarchar(max))"));
    }

    [Fact]
    public void Users_and_roles_have_guid_keys_like_every_other_table()
    {
        Assert.Equal(typeof(Guid), Entity<EInvoice.Infrastructure.Identity.AppUser>().FindPrimaryKey()!.Properties.Single().ClrType);
        Assert.Equal(typeof(Guid), Entity<EInvoice.Infrastructure.Identity.AppRole>().FindPrimaryKey()!.Properties.Single().ClrType);
    }

    [Fact]
    public void A_user_name_belongs_to_one_account()
    {
        // Sign-in looks accounts up by the normalised name; the actor recorded on invoices is this name.
        var index = Assert.Single(Entity<EInvoice.Infrastructure.Identity.AppUser>().GetIndexes(),
            i => i.Properties.Single().Name == "NormalizedUserName");

        Assert.True(index.IsUnique);
        Assert.Equal(256, index.Properties.Single().GetMaxLength());
    }

    [Fact]
    public void The_four_roles_are_part_of_the_schema_with_fixed_ids()
    {
        var seeded = Entity<EInvoice.Infrastructure.Identity.AppRole>().GetSeedData().ToList();

        Assert.Equal(
            EInvoice.Application.Security.Roles.All.Order(StringComparer.Ordinal),
            seeded.Select(r => (string)r["Name"]!).Order(StringComparer.Ordinal));
        Assert.All(seeded, r =>
        {
            Assert.NotEqual(Guid.Empty, (Guid)r["Id"]!);
            Assert.Equal(((string)r["Name"]!).ToUpperInvariant(), r["NormalizedName"]);
            Assert.NotNull(r["ConcurrencyStamp"]); // fixed, or every migration would rewrite the roles
        });
        Assert.Equal(seeded.Count, seeded.Select(r => r["Id"]).Distinct().Count());
    }

    [Fact]
    public void An_invoice_carries_a_rowversion_that_guards_every_save()
    {
        // Two decisions on the same version: the second save finds a newer version and fails (package 10).
        var version = Assert.Single(Entity<Invoice>().GetProperties(), p => p.IsConcurrencyToken);

        Assert.Equal("Version", version.Name);
        Assert.Equal("rowversion", version.GetColumnType());
        Assert.Equal(ValueGenerated.OnAddOrUpdate, version.ValueGenerated);
    }

    [Fact]
    public void Audit_events_have_one_place_each_in_the_chain_and_fixed_size_hashes()
    {
        var audit = Model.GetEntityTypes().Single(e => e.GetTableName() == "AuditEvent");

        var sequence = Assert.Single(audit.GetIndexes(), i => i.Properties.Single().Name == "Sequence");
        Assert.True(sequence.IsUnique); // two writers can never both append event n
        Assert.Equal("binary(32)", audit.FindProperty("Hash")!.GetColumnType());
        Assert.Equal("binary(32)", audit.FindProperty("PreviousHash")!.GetColumnType());
        Assert.Empty(audit.GetForeignKeys()); // the trail outlives what it describes
    }
}
