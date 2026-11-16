using EInvoice.IntegrationTests.Infrastructure;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace EInvoice.IntegrationTests.Performance;

/// <summary>
/// Readers do not wait for writers. The load test found the deadlock this prevents: a match adding up the other
/// invoices of an order waited for an upload's uncommitted invoice of the same order, while that upload waited for the
/// match's lock on the invoice lines; SQL Server killed one of them and the clerk got a 500. With snapshot reads
/// (READ_COMMITTED_SNAPSHOT, db/database.sql) a read sees the last committed data instead of waiting.
/// Both transactions are rolled back.
/// </summary>
[Trait("Category", "Database")]
[Collection(SqlServerCollection.Name)]
public sealed class LockingTests(SqlServerFixture fixture)
{
    private static readonly Guid VendorOfPo0001 = Guid.Parse("00000000-0000-0000-0001-000000000001");

    [Fact]
    public async Task A_match_does_not_wait_for_an_upload_that_is_not_committed_yet()
    {
        // An upload for order PO-0001, in the middle of its save: the invoice row is written, not yet committed.
        await using var upload = fixture.CreateContext();
        await using var uploading = await upload.Database.BeginTransactionAsync();
        var id = Guid.NewGuid();
        var number = $"LOCK-{id:N}"[..20];
        await upload.Database.ExecuteSqlAsync(
            $"INSERT INTO dbo.Invoice (Id, SourceFormat, SubmittedBy, Status, InvoiceNumber, VendorId, IssueDate, PurchaseOrderNumber) VALUES ({id}, 1, {"clerk@test"}, 2, {number}, {VendorOfPo0001}, {new DateOnly(2031, 2, 3)}, {"PO-0001"})");

        // Meanwhile another invoice of the same order is matched.
        await using var match = fixture.CreateContext();
        await using var matching = await match.Database.BeginTransactionAsync();
        await match.Database.ExecuteSqlRawAsync("SET LOCK_TIMEOUT 3000;"); // waiting longer than this counts as waiting
        var seeded = await match.Invoices.Where(i => i.InvoiceNumber == "RE-2026-0001").Select(i => i.Id).SingleAsync();

        var waited = await Record.ExceptionAsync(() =>
            match.Database.ExecuteSqlAsync($"EXEC dbo.usp_MatchInvoice @InvoiceId = {seeded}, @PriceTolerancePercent = {2.00m}"));

        Assert.False(waited is SqlException { Number: 1222 }, "The match waited for the uncommitted upload (lock request timeout).");
        Assert.Null(waited);
    }
}
