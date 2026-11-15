using EInvoice.Application.Matching;
using EInvoice.Application.Validation;
using EInvoice.Domain.Invoices;
using EInvoice.Infrastructure.Approval;
using EInvoice.Infrastructure.Persistence;
using EInvoice.IntegrationTests.Infrastructure;
using EInvoice.IntegrationTests.Matching;

namespace EInvoice.IntegrationTests.Approval;

/// <summary>
/// The store's own promises, independent of the use case's checks: it saves only against the version it is given,
/// and only a Blocking finding counts as blocking. Each test runs in a transaction that is rolled back.
/// </summary>
[Trait("Category", "Database")]
[Collection(SqlServerCollection.Name)]
public sealed class EfApprovalStoreTests(SqlServerFixture fixture) : IAsyncLifetime
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

    private async Task<Invoice> InExceptionAsync()
    {
        var price = 20m + (Random.Shared.Next(1, 100_000) / 100m);
        var scenario = await new MatchScenario(_db).OrderLine(1, 1, price).Received(1, 1).SaveOrderAsync();
        var invoice = await scenario.AddInvoiceAsync((1, 1, price, 1));
        invoice.TransitionTo(InvoiceStatus.Exception, "system", Now);
        await _db.SaveChangesAsync();
        return invoice;
    }

    private async Task AddFindingAsync(Guid invoiceId, int sequence, FindingSeverity severity)
    {
        _db.InvoiceFindings.Add(new InvoiceFindingRecord
        {
            InvoiceId = invoiceId, Sequence = sequence, RuleCode = "TEST_RULE", Severity = severity, Message = "A test finding.",
        });
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task Saving_against_another_version_than_the_loaded_one_saves_nothing()
    {
        var invoice = await InExceptionAsync();
        var store = new EfApprovalStore(_db);
        var stored = (await store.FindAsync(invoice.Id, CancellationToken.None))!;
        stored.Invoice.TransitionTo(InvoiceStatus.Rejected, "approver@test", Now, "test");

        // The caller says it saw an older version: the database must refuse, whatever was loaded here.
        var older = Convert.ToBase64String(new byte[8]);
        await Assert.ThrowsAsync<ConcurrentStatusChangeException>(() => store.SaveChangesAsync(older, CancellationToken.None));

        var reloaded = (await new EfApprovalStore(_db).FindAsync(invoice.Id, CancellationToken.None))!;
        Assert.Equal(InvoiceStatus.Exception, reloaded.Invoice.Status);
        Assert.Equal(stored.Version, reloaded.Version);
    }

    [Fact]
    public async Task A_version_that_is_not_one_is_a_conflict_not_a_crash()
    {
        var invoice = await InExceptionAsync();
        var store = new EfApprovalStore(_db);
        var stored = (await store.FindAsync(invoice.Id, CancellationToken.None))!;
        stored.Invoice.TransitionTo(InvoiceStatus.Rejected, "approver@test", Now, "test");

        await Assert.ThrowsAsync<ConcurrentStatusChangeException>(() => store.SaveChangesAsync("not base64!", CancellationToken.None));
    }

    [Fact]
    public async Task Only_a_blocking_finding_blocks()
    {
        var reviewOnly = await InExceptionAsync();
        await AddFindingAsync(reviewOnly.Id, 1, FindingSeverity.Review);
        var blocking = await InExceptionAsync();
        await AddFindingAsync(blocking.Id, 1, FindingSeverity.Review);
        await AddFindingAsync(blocking.Id, 2, FindingSeverity.Blocking);
        var store = new EfApprovalStore(_db);

        Assert.False(await store.HasBlockingFindingsAsync(reviewOnly.Id, CancellationToken.None));
        Assert.True(await store.HasBlockingFindingsAsync(blocking.Id, CancellationToken.None));
        Assert.False(await store.HasBlockingFindingsAsync((await InExceptionAsync()).Id, CancellationToken.None));
    }
}
