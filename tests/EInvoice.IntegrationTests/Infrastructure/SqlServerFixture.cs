using System.Security.Cryptography;
using System.Text.RegularExpressions;
using EInvoice.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MsSql;

namespace EInvoice.IntegrationTests.Infrastructure;

/// <summary>
/// One SQL Server container for the whole test run, built with the same image and the same db/ scripts,
/// in the same order, as the db-init service: schema, stored procedures, app user, seed.
/// Tests connect as the least-privilege app user, exactly like the application.
/// </summary>
public sealed partial class SqlServerFixture : IAsyncLifetime
{
    // Same image as compose.yaml.
    private const string Image = "mcr.microsoft.com/mssql/server:2022-CU27-ubuntu-22.04";

    private readonly string _appPassword = $"Ap{Convert.ToHexString(RandomNumberGenerator.GetBytes(12))}#1x";
    private MsSqlContainer _container = null!;

    /// <summary>Connection string for the application user (read/write data, execute procedures, no schema changes).</summary>
    public string AppConnectionString { get; private set; } = string.Empty;

    public EInvoiceDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<EInvoiceDbContext>().UseSqlServer(AppConnectionString).Options);

    public async Task InitializeAsync()
    {
        _container = new MsSqlBuilder(Image).Build();
        await _container.StartAsync();

        var admin = new SqlConnectionStringBuilder(_container.GetConnectionString());
        await ExecuteAsync(admin.ConnectionString, "CREATE DATABASE EInvoice;");

        admin.InitialCatalog = "EInvoice";
        var db = Path.Combine(RepositoryRoot(), "db");
        await RunScriptAsync(admin.ConnectionString, Path.Combine(db, "migrations", "schema.sql"));
        foreach (var procedure in Directory.EnumerateFiles(Path.Combine(db, "procedures"), "*.sql").Order())
        {
            await RunScriptAsync(admin.ConnectionString, procedure);
        }

        await RunScriptAsync(admin.ConnectionString, Path.Combine(db, "security", "app-user.sql"),
            new Dictionary<string, string> { ["EINVOICE_APP_PASSWORD"] = _appPassword });
        await RunScriptAsync(admin.ConnectionString, Path.Combine(db, "seed", "seed.sql"));

        AppConnectionString = new SqlConnectionStringBuilder(admin.ConnectionString)
        {
            UserID = "einvoice_app",
            Password = _appPassword,
        }.ConnectionString;
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();

    /// <summary>Runs a sqlcmd-style script: batches separated by GO lines, $(NAME) variables substituted.</summary>
    private static async Task RunScriptAsync(string connectionString, string path, IReadOnlyDictionary<string, string>? variables = null)
    {
        var script = await File.ReadAllTextAsync(path);
        foreach (var (name, value) in variables ?? new Dictionary<string, string>())
        {
            script = script.Replace($"$({name})", value, StringComparison.Ordinal);
        }

        foreach (var batch in GoSeparator().Split(script).Where(b => !string.IsNullOrWhiteSpace(b)))
        {
            await ExecuteAsync(connectionString, batch);
        }
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
        await command.ExecuteNonQueryAsync();
    }

    internal static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EInvoice.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoSeparator();
}

[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SQL Server";
}
