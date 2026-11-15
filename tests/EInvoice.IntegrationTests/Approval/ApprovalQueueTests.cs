using EInvoice.Application.Queries;
using EInvoice.Application.Validation;
using EInvoice.Domain.Invoices;
using EInvoice.Infrastructure.Persistence;
using EInvoice.Infrastructure.Queries;
using EInvoice.IntegrationTests.Infrastructure;
using EInvoice.IntegrationTests.Matching;

namespace EInvoice.IntegrationTests.Approval;

/// <summary>
/// The approval queue as the database delivers it: only invoices waiting for a decision, oldest first, with what the
/// screen needs to say what the approver can do. Other tests add invoices too, so only this test's rows are compared.
/// Each test runs in a transaction that is rolled back.
/// </summary>
[Trait("Category", "Database")]
[Collection(SqlServerCollection.Name)]
public sealed class ApprovalQueueTests(SqlServerFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);

    private EInvoiceDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _db = fixture.CreateContext();
        await _db.Database.BeginTransactionAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.Database.RollbackTransactionAsync();
        await _db.DisposeAsync();
    }

    private async Task<Invoice> InvoiceAsync(params InvoiceStatus[] path)
    {
        var price = 30m + (Random.Shared.Next(1, 100_000) / 100m);
        var scenario = await new MatchScenario(_db).OrderLine(1, 1, price).Received(1, 1).SaveOrderAsync();
        var invoice = await scenario.AddInvoiceAsync((1, 1, price, 1));
        foreach (var step in path)
        {
            invoice.TransitionTo(step, "system", Now);
        }

        await _db.SaveChangesAsync();
        return invoice;
    }

    private async Task<IReadOnlyList<ApprovalQueueItem>> WholeQueueAsync()
    {
        var queries = new EfInvoiceQueries(_db);
        var first = await queries.ApprovalQueueAsync(1, InvoiceListQuery.MaxPageSize, CancellationToken.None);
        var all = first.Items.ToList();
        for (var page = 2; (page - 1) * InvoiceListQuery.MaxPageSize < first.TotalCount; page++)
        {
            all.AddRange((await queries.ApprovalQueueAsync(page, InvoiceListQuery.MaxPageSize, CancellationToken.None)).Items);
        }

        return all;
    }

    [Fact]
    public async Task Holds_matched_and_exception_invoices_oldest_first()
    {
        var older = await InvoiceAsync(InvoiceStatus.Validated, InvoiceStatus.Matched);
        var exception = await InvoiceAsync(InvoiceStatus.Exception);
        var approved = await InvoiceAsync(InvoiceStatus.Validated, InvoiceStatus.Matched, InvoiceStatus.Approved);
        var rejected = await InvoiceAsync(InvoiceStatus.Exception, InvoiceStatus.Rejected);
        var parsedOnly = await InvoiceAsync();

        var mine = (await WholeQueueAsync()).Where(i => new[] { older.Id, exception.Id, approved.Id, rejected.Id, parsedOnly.Id }.Contains(i.Id)).ToList();

        Assert.Equal(new[] { older.Id, exception.Id }, mine.Select(i => i.Id)); // oldest waits longest: first
        Assert.Equal(new[] { InvoiceStatus.Matched, InvoiceStatus.Exception }, mine.Select(i => i.Status));
    }

    [Fact]
    public async Task Each_entry_carries_what_the_approver_needs_to_know()
    {
        var invoice = await InvoiceAsync(InvoiceStatus.Exception);
        _db.InvoiceFindings.Add(new InvoiceFindingRecord { InvoiceId = invoice.Id, Sequence = 1, RuleCode = "TEST", Severity = FindingSeverity.Blocking, Message = "m" });
        var reviewOnly = await InvoiceAsync(InvoiceStatus.Exception);
        _db.InvoiceFindings.Add(new InvoiceFindingRecord { InvoiceId = reviewOnly.Id, Sequence = 1, RuleCode = "TEST", Severity = FindingSeverity.Review, Message = "m" });
        await _db.SaveChangesAsync();

        var queue = await WholeQueueAsync();
        var entry = Assert.Single(queue, i => i.Id == invoice.Id);

        Assert.Equal("clerk@test", entry.SubmittedBy);
        Assert.Equal(invoice.InvoiceNumber, entry.InvoiceNumber);
        Assert.Equal(invoice.GrossTotal!.Amount, entry.GrossTotal);
        Assert.Equal("EUR", entry.Currency);
        Assert.NotNull(entry.VendorName);
        Assert.True(entry.HasBlockingFindings);
        Assert.False(Assert.Single(queue, i => i.Id == reviewOnly.Id).HasBlockingFindings);
    }

    [Fact]
    public async Task Pages_split_the_queue_without_gaps_or_repeats()
    {
        for (var i = 0; i < 3; i++)
        {
            await InvoiceAsync(InvoiceStatus.Exception);
        }

        var queries = new EfInvoiceQueries(_db);
        var whole = await WholeQueueAsync();
        var paged = new List<Guid>();
        for (var page = 1; (page - 1) * 2 < whole.Count; page++)
        {
            var result = await queries.ApprovalQueueAsync(page, 2, CancellationToken.None);
            Assert.Equal(whole.Count, result.TotalCount);
            paged.AddRange(result.Items.Select(i => i.Id));
        }

        Assert.Equal(whole.Select(i => i.Id), paged);
    }
}
