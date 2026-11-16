using System.Data.Common;
using System.Xml.Linq;
using EInvoice.Infrastructure.Persistence;
using EInvoice.IntegrationTests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace EInvoice.IntegrationTests.Performance;

/// <summary>
/// The hot queries read by index, not by scanning a table that grows with every invoice. These tests look at the
/// actual execution plan, so they do not depend on timing or on how much data the shared test database holds.
/// Reading a plan needs SHOWPLAN, which the app user does not have: the tests connect as the administrator, inside a
/// rolled-back transaction.
/// </summary>
[Trait("Category", "Database")]
[Collection(SqlServerCollection.Name)]
public sealed class QueryPlanTests(SqlServerFixture fixture) : IAsyncLifetime
{
    private static readonly XNamespace Showplan = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
    private static readonly string[] Seeks = ["Index Seek", "Clustered Index Seek"];

    private EInvoiceDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _db = fixture.CreateAdminContext();
        await _db.Database.BeginTransactionAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.Database.RollbackTransactionAsync();
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task Matching_reads_only_the_invoices_of_its_order()
    {
        // What every other invoice already billed for the order: without an index on PurchaseOrderNumber this reads the
        // whole Invoice table, so every match gets slower as invoices pile up.
        var invoiceId = await _db.Invoices.Where(i => i.InvoiceNumber == "RE-2026-0001").Select(i => i.Id).SingleAsync();

        var plans = await ActualPlansAsync(
            "EXEC dbo.usp_MatchInvoice @InvoiceId = @InvoiceId, @PriceTolerancePercent = @Tolerance",
            new SqlParameter("@InvoiceId", invoiceId),
            new SqlParameter("@Tolerance", 2m));

        var invoiceReads = ReadsOf(plans, "[Invoice]");
        Assert.NotEmpty(invoiceReads);
        Assert.All(invoiceReads, read => Assert.True(Seeks.Contains(read.Operation), $"dbo.Invoice is read by {read.Operation} on {read.Index}"));
    }

    // ---- Plans ---------------------------------------------------------------------------------------------------

    private sealed record TableRead(string Operation, string Index);

    /// <summary>Every operator in the plans that reads <paramref name="table"/>, with its own index (not a child's).</summary>
    private static List<TableRead> ReadsOf(IEnumerable<XDocument> plans, string table) =>
        plans.SelectMany(p => p.Descendants(Showplan + "RelOp"))
            .SelectMany(op => op.Elements().Elements(Showplan + "Object")
                .Where(o => (string?)o.Attribute("Table") == table)
                .Select(o => new TableRead((string)op.Attribute("PhysicalOp")!, (string?)o.Attribute("Index") ?? "(heap)")))
            .ToList();

    /// <summary>Runs the statement with SET STATISTICS XML ON and returns the actual plan of every statement it ran.</summary>
    private async Task<List<XDocument>> ActualPlansAsync(string sql, params SqlParameter[] parameters)
    {
        var connection = _db.Database.GetDbConnection();
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = _db.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = $"SET STATISTICS XML ON; {sql}; SET STATISTICS XML OFF;";
        command.Parameters.AddRange(parameters);

        var plans = new List<XDocument>();
        await using var reader = await command.ExecuteReaderAsync();
        do
        {
            var isPlan = reader.FieldCount == 1 && reader.GetName(0).Contains("Showplan", StringComparison.Ordinal);
            while (await reader.ReadAsync())
            {
                if (isPlan)
                {
                    // Our own server's output, not a document from outside; XDocument.Parse prohibits DTDs by default.
                    plans.Add(XDocument.Parse(reader.GetString(0)));
                }
            }
        }
        while (await reader.NextResultAsync());

        return plans;
    }
}
