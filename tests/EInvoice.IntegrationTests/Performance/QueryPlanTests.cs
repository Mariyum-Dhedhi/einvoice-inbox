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
/// actual execution plan, so they do not depend on timing. On the few seeded rows any plan is cheap and SQL Server may
/// well scan, so each test first adds <see cref="Volume"/> invoices of other orders and compiles afresh (WITH RECOMPILE):
/// the plan is the one for a table that has grown.
/// Reading a plan needs SHOWPLAN, which the app user does not have: the tests connect as the administrator, inside a
/// rolled-back transaction (the volume goes with it).
/// </summary>
[Trait("Category", "Database")]
[Collection(SqlServerCollection.Name)]
public sealed class QueryPlanTests(SqlServerFixture fixture) : IAsyncLifetime
{
    private static readonly XNamespace Showplan = "http://schemas.microsoft.com/sqlserver/2004/07/showplan";
    private static readonly string[] Seeks = ["Index Seek", "Clustered Index Seek"];

    private const int Volume = 5000;

    private const string Match = "EXEC dbo.usp_MatchInvoice @InvoiceId = @InvoiceId, @PriceTolerancePercent = @Tolerance WITH RECOMPILE";

    private EInvoiceDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _db = fixture.CreateAdminContext();
        await _db.Database.BeginTransactionAsync();

        await AddInvoicesAsync(Volume);
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
            Match,
            new SqlParameter("@InvoiceId", invoiceId),
            new SqlParameter("@Tolerance", 2m));

        var invoiceReads = ReadsOf(plans, "[Invoice]");
        Assert.NotEmpty(invoiceReads);
        Assert.All(invoiceReads, read => Assert.True(Seeks.Contains(read.Operation), $"dbo.Invoice is read by {read.Operation} on {read.Index}"));
    }

    [Fact]
    public async Task Matching_reads_the_lines_of_other_invoices_from_one_index()
    {
        // The lines of every other invoice of the order are added up. Found by the primary key alone, each line needs a
        // second read in the table (clustered by RowId) for its quantity: on an order with 7,083 invoices that was
        // 43,040 pages per match in the load test, and so many locks that SQL Server locked the whole table.
        // Here the order of RE-2026-0001 gets 300 more invoices; reads in the table may run once per line of the
        // invoice itself (its price and currency), never once per other invoice, and the table is never scanned.
        await AddInvoicesAsync(300, onOrder: "PO-0001");
        var invoiceId = await _db.Invoices.Where(i => i.InvoiceNumber == "RE-2026-0001").Select(i => i.Id).SingleAsync();
        var ownLines = await _db.Database.SqlQuery<int>($"SELECT COUNT(*) AS Value FROM dbo.InvoiceLine WHERE InvoiceId = {invoiceId}").SingleAsync();

        var plans = await ActualPlansAsync(Match, new SqlParameter("@InvoiceId", invoiceId), new SqlParameter("@Tolerance", 2m));

        var lineReads = ReadsOf(plans, "[InvoiceLine]");
        Assert.NotEmpty(lineReads);
        Assert.All(lineReads, read => Assert.False(read.Operation.Contains("Scan", StringComparison.Ordinal), $"dbo.InvoiceLine is read by {read.Operation} on {read.Index}"));
        Assert.All(lineReads.Where(r => r.Index == "[CX_InvoiceLine_RowId]"), read => Assert.True(
            read.Executions <= ownLines,
            $"dbo.InvoiceLine is read in the table {read.Executions} times ({read.Operation}); the invoice has {ownLines} lines"));
    }

    // ---- Volume --------------------------------------------------------------------------------------------------

    /// <summary>Invoices with one line each, of other orders (or all of one order), inside the test's transaction.</summary>
    private async Task AddInvoicesAsync(int count, string? onOrder = null)
    {
        var run = Guid.NewGuid().ToString("N")[..8];
        await _db.Database.ExecuteSqlAsync($"""
            INSERT INTO dbo.Invoice (Id, SourceFormat, SubmittedBy, Status, InvoiceNumber, VendorId, IssueDate, PurchaseOrderNumber)
            SELECT NEWID(), 1, CONCAT(N'volume-', {run}), 8, CONCAT(N'PLAN-', {run}, N'-', n), '00000000-0000-0000-0001-000000000002', '2031-01-01',
                   COALESCE({onOrder}, CONCAT(N'PO-P', n))
            FROM (SELECT TOP ({count}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS n FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b) AS numbers;
            INSERT INTO dbo.InvoiceLine (InvoiceId, LineNumber, Description, Quantity, UnitPrice, NetAmount_Amount, NetAmount_Currency, TaxRatePercent, PurchaseOrderLineNumber)
            SELECT Id, 1, N'Volume', 1, 1, 1, 'EUR', 19, 1 FROM dbo.Invoice WHERE SubmittedBy = CONCAT(N'volume-', {run});
            """);
    }

    // ---- Plans ---------------------------------------------------------------------------------------------------

    private sealed record TableRead(string Operation, string Index, long Executions);

    /// <summary>Every operator in the plans that reads <paramref name="table"/>, with its own index (not a child's).</summary>
    private static List<TableRead> ReadsOf(IEnumerable<XDocument> plans, string table) =>
        plans.SelectMany(p => p.Descendants(Showplan + "RelOp"))
            .SelectMany(op => op.Elements().Elements(Showplan + "Object")
                .Where(o => (string?)o.Attribute("Table") == table)
                .Select(o => new TableRead(
                    (string)op.Attribute("PhysicalOp")!,
                    (string?)o.Attribute("Index") ?? "(heap)",
                    op.Elements(Showplan + "RunTimeInformation").Elements(Showplan + "RunTimeCountersPerThread")
                        .Sum(t => (long?)t.Attribute("ActualExecutions") ?? 0))))
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
