using System.Security.Cryptography;
using DotNet.Testcontainers.Builders;
using EInvoice.IntegrationTests.Infrastructure;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace EInvoice.IntegrationTests.Demo;

/// <summary>
/// The public demo's nightly reset (db/demo): the database is dropped and built again by the same db-init scripts, so
/// whatever visitors did is gone and the seed is back, without a list of tables to forget. Runs the real shell scripts
/// in a SQL Server container of its own: the shared test database is never touched.
/// </summary>
[Trait("Category", "Database")]
public sealed class DemoResetTests : IAsyncLifetime
{
    private const string Image = "mcr.microsoft.com/mssql/server:2022-CU27-ubuntu-22.04"; // as compose.yaml

    private readonly string _saPassword = $"Sa{Convert.ToHexString(RandomNumberGenerator.GetBytes(12))}#1x";
    private readonly string _appPassword = $"Ap{Convert.ToHexString(RandomNumberGenerator.GetBytes(12))}#1x";
    private MsSqlContainer _sql = null!;

    public async Task InitializeAsync()
    {
        _sql = new MsSqlBuilder(Image)
            .WithPassword(_saPassword)
            .WithResourceMapping(new DirectoryInfo(Path.Combine(SqlServerFixture.RepositoryRoot(), "db")), "/db/")
            .Build();
        await _sql.StartAsync();

        var init = await ShellAsync("/db/init.sh");
        Assert.True(init.ExitCode == 0, init.Stdout + init.Stderr);
    }

    public async Task DisposeAsync() => await _sql.DisposeAsync();

    private Task<DotNet.Testcontainers.Containers.ExecResult> ShellAsync(string script, string arguments = "", string database = "EInvoice") =>
        _sql.ExecAsync(["/bin/sh", "-c",
            $"MSSQL_SA_PASSWORD='{_saPassword}' EINVOICE_APP_PASSWORD='{_appPassword}' DB_HOST=localhost EINVOICE_DB='{database}' /bin/sh {script} {arguments}"]);

    private string Admin(string database = "EInvoice") =>
        new SqlConnectionStringBuilder(_sql.GetConnectionString()) { InitialCatalog = database }.ConnectionString;

    private string App() =>
        new SqlConnectionStringBuilder(Admin()) { UserID = "einvoice_app", Password = _appPassword }.ConnectionString;

    private static async Task ExecuteAsync(string connection, string sql)
    {
        await using var db = new SqlConnection(connection);
        await db.OpenAsync();
        await using var command = new SqlCommand(sql, db);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Rows per table, every table in the database (history tables of temporal tables included).</summary>
    private async Task<SortedDictionary<string, long>> RowCountsAsync()
    {
        var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        await using var db = new SqlConnection(Admin());
        await db.OpenAsync();
        await using var command = new SqlCommand(
            """
            SELECT s.name + '.' + t.name, SUM(p.rows)
            FROM sys.tables AS t
            JOIN sys.schemas AS s ON s.schema_id = t.schema_id
            JOIN sys.partitions AS p ON p.object_id = t.object_id AND p.index_id IN (0, 1)
            GROUP BY s.name, t.name
            """, db);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            counts[reader.GetString(0)] = reader.GetInt64(1);
        }

        return counts;
    }

    /// <summary>What a day of visitors leaves behind: accounts, an audit trail, a new vendor, a changed bank account (history).</summary>
    private async Task VisitorsWereHereAsync()
    {
        await ExecuteAsync(Admin(), """
            INSERT INTO dbo.AppUser (Id, UserName, NormalizedUserName, EmailConfirmed, PhoneNumberConfirmed, TwoFactorEnabled, LockoutEnabled, AccessFailedCount)
            VALUES (NEWID(), N'demo-clerk-abcdefgh@example.com', N'DEMO-CLERK-ABCDEFGH@EXAMPLE.COM', 0, 0, 0, 1, 0);
            INSERT INTO dbo.Vendor (Id, Number, Name, VatId, IsActive) VALUES (NEWID(), N'R-0001', N'Visitor GmbH', N'DE000000999', 1);
            UPDATE dbo.VendorBankAccount SET ConfirmedBy = N'visitor' WHERE VendorId = '00000000-0000-0000-0001-000000000001';
            """);
    }

    [Fact]
    public async Task After_the_reset_every_table_holds_exactly_what_a_fresh_build_holds()
    {
        var fresh = await RowCountsAsync();
        await VisitorsWereHereAsync();
        Assert.NotEqual(fresh, await RowCountsAsync());

        var reset = await ShellAsync("/db/demo/reset.sh");

        Assert.True(reset.ExitCode == 0, reset.Stdout + reset.Stderr);
        Assert.Equal(fresh, await RowCountsAsync());
        Assert.Contains("Database ready.", reset.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_app_signs_in_as_before_and_its_rights_are_the_same()
    {
        Assert.Equal(0, (await ShellAsync("/db/demo/reset.sh")).ExitCode);

        await ExecuteAsync(App(), "SELECT COUNT(*) FROM dbo.Vendor;");
        var denied = await Assert.ThrowsAsync<SqlException>(() => ExecuteAsync(App(), "DELETE FROM dbo.AuditEvent;"));
        Assert.Contains("DELETE", denied.Message, StringComparison.OrdinalIgnoreCase); // append-only, as after db-init
    }

    [Fact]
    public async Task An_open_connection_of_the_app_does_not_block_the_reset()
    {
        // At 03:00 the app may hold a connection, even in a transaction: the reset ends it rather than waiting.
        await using var held = new SqlConnection(App());
        await held.OpenAsync();
        await using var transaction = (SqlTransaction)await held.BeginTransactionAsync();
        await using (var command = new SqlCommand("UPDATE dbo.Vendor SET Name = Name WHERE Number = N'V-1001';", held, transaction))
        {
            await command.ExecuteNonQueryAsync();
        }

        var reset = await ShellAsync("/db/demo/reset.sh");

        Assert.True(reset.ExitCode == 0, reset.Stdout + reset.Stderr);
        Assert.Equal(20, await ScalarAsync("SELECT COUNT(*) FROM dbo.Vendor;"));
    }

    [Fact]
    public async Task Running_it_twice_is_the_same_as_once()
    {
        Assert.Equal(0, (await ShellAsync("/db/demo/reset.sh")).ExitCode);
        var once = await RowCountsAsync();

        Assert.Equal(0, (await ShellAsync("/db/demo/reset.sh")).ExitCode);

        Assert.Equal(once, await RowCountsAsync());
    }

    [Theory]
    [InlineData("EInvoice];DROP DATABASE [master")]
    [InlineData("")]
    [InlineData("E Invoice")]
    public async Task A_database_name_that_is_not_letters_and_digits_is_refused_before_anything_is_dropped(string name)
    {
        var reset = await ShellAsync("/db/demo/reset.sh", database: name);

        Assert.NotEqual(0, reset.ExitCode);
        Assert.Equal(20, await ScalarAsync("SELECT COUNT(*) FROM dbo.Vendor;"));
    }

    private async Task<int> ScalarAsync(string sql)
    {
        await using var db = new SqlConnection(Admin());
        await db.OpenAsync();
        await using var command = new SqlCommand(sql, db);
        return (int)(await command.ExecuteScalarAsync())!;
    }

    // ---- When: the next 03:00 in Berlin, whatever the season --------------------------------------------------------

    [Theory]
    [InlineData("2026-11-18T12:30:00+01:00", 52_200)]  // this afternoon: tomorrow 03:00
    [InlineData("2026-11-18T02:59:00+01:00", 60)]      // one minute before
    [InlineData("2026-11-18T03:00:00+01:00", 86_400)]  // just done: the next one is tomorrow
    [InlineData("2026-03-28T12:00:00+01:00", 50_400)]  // the night the clocks go forward: 14 hours, not 15
    [InlineData("2026-10-24T12:00:00+02:00", 57_600)]  // the night the clocks go back: 16 hours, not 15
    public async Task The_schedule_waits_until_the_next_three_oclock_in_Berlin(string now, int seconds)
    {
        var result = await _sql.ExecAsync(["/bin/sh", "/db/demo/nightly.sh", "--seconds-until-next", now]);

        Assert.True(result.ExitCode == 0, result.Stderr);
        Assert.Equal(seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), result.Stdout.Trim());
    }
}
