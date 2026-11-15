using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Testcontainers.MsSql;

namespace EInvoice.AiEvaluation;

/// <summary>
/// A fresh, throwaway SQL Server for one evaluation run, built from the db/ scripts in the same order as db-init
/// (schema, procedures, app user, seed). Same image as compose.yaml. The run connects as the least-privilege app user.
/// </summary>
public sealed partial class EvalDatabase : IAsyncDisposable
{
    private const string Image = "mcr.microsoft.com/mssql/server:2022-CU27-ubuntu-22.04";

    private readonly MsSqlContainer _container;

    private EvalDatabase(MsSqlContainer container, string appConnectionString)
    {
        _container = container;
        AppConnectionString = appConnectionString;
    }

    public string AppConnectionString { get; }

    public static async Task<EvalDatabase> StartAsync(string repositoryRoot, CancellationToken cancellationToken)
    {
        var appPassword = $"Ev{Convert.ToHexString(RandomNumberGenerator.GetBytes(12))}#1x";
        var container = new MsSqlBuilder(Image).Build();
        await container.StartAsync(cancellationToken).ConfigureAwait(false);

        var admin = new SqlConnectionStringBuilder(container.GetConnectionString());
        await ExecuteAsync(admin.ConnectionString, "CREATE DATABASE EInvoice;", cancellationToken).ConfigureAwait(false);
        admin.InitialCatalog = "EInvoice";

        var db = Path.Combine(repositoryRoot, "db");
        await RunScriptAsync(admin.ConnectionString, Path.Combine(db, "migrations", "schema.sql"), null, cancellationToken).ConfigureAwait(false);
        foreach (var procedure in Directory.EnumerateFiles(Path.Combine(db, "procedures"), "*.sql").Order(StringComparer.Ordinal))
        {
            await RunScriptAsync(admin.ConnectionString, procedure, null, cancellationToken).ConfigureAwait(false);
        }

        await RunScriptAsync(admin.ConnectionString, Path.Combine(db, "security", "app-user.sql"),
            new Dictionary<string, string> { ["EINVOICE_APP_PASSWORD"] = appPassword }, cancellationToken).ConfigureAwait(false);
        await RunScriptAsync(admin.ConnectionString, Path.Combine(db, "seed", "seed.sql"), null, cancellationToken).ConfigureAwait(false);

        var app = new SqlConnectionStringBuilder(admin.ConnectionString) { UserID = "einvoice_app", Password = appPassword };
        return new EvalDatabase(container, app.ConnectionString);
    }

    public async ValueTask DisposeAsync() => await _container.DisposeAsync().ConfigureAwait(false);

    private static async Task RunScriptAsync(string connectionString, string path, IReadOnlyDictionary<string, string>? variables, CancellationToken cancellationToken)
    {
        var script = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        foreach (var (name, value) in variables ?? new Dictionary<string, string>())
        {
            script = script.Replace($"$({name})", value, StringComparison.Ordinal);
        }

        foreach (var batch in GoSeparator().Split(script).Where(b => !string.IsNullOrWhiteSpace(b)))
        {
            await ExecuteAsync(connectionString, batch, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task ExecuteAsync(string connectionString, string sql, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CA2100 // The db/ scripts from this repository, not user input.
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoSeparator();
}
