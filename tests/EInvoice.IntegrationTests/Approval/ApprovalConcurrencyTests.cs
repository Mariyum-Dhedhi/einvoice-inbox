using EInvoice.Application.Approval;
using EInvoice.Domain.Invoices;
using EInvoice.Infrastructure.Approval;
using EInvoice.Infrastructure.Persistence;
using EInvoice.IntegrationTests.Infrastructure;
using EInvoice.IntegrationTests.Matching;
using Microsoft.EntityFrameworkCore;

namespace EInvoice.IntegrationTests.Approval;

/// <summary>
/// Package 10's "done when": two simultaneous approvals, one wins, one gets a conflict. The invoice's rowversion is
/// the referee: a decision is saved only if the invoice is still the version the approver looked at.
/// </summary>
[Trait("Category", "Database")]
[Collection(SqlServerCollection.Name)]
public sealed class ApprovalConcurrencyTests(SqlServerFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);

    /// <summary>A committed invoice in Matched, submitted by clerk@test.</summary>
    private async Task<Guid> MatchedInvoiceAsync()
    {
        await using var db = fixture.CreateContext();
        var price = 10m + (Random.Shared.Next(1, 100_000) / 100m);
        var scenario = await new MatchScenario(db).OrderLine(1, 1, price).Received(1, 1).SaveOrderAsync();
        var invoice = await scenario.AddInvoiceAsync((1, 1, price, 1));
        invoice.TransitionTo(InvoiceStatus.Validated, "system", Now);
        invoice.TransitionTo(InvoiceStatus.Matched, "system", Now);
        await db.SaveChangesAsync();
        return invoice.Id;
    }

    private async Task<string> VersionAsync(Guid invoiceId)
    {
        await using var db = fixture.CreateContext();
        return (await new EfApprovalStore(db).FindAsync(invoiceId, CancellationToken.None))!.Version;
    }

    [Fact]
    public async Task Two_simultaneous_approvals_one_wins_one_gets_a_conflict()
    {
        for (var round = 0; round < 5; round++)
        {
            var invoiceId = await MatchedInvoiceAsync();
            var version = await VersionAsync(invoiceId);
            var bothHaveRead = new BothRead();
            await using var first = fixture.CreateContext();
            await using var second = fixture.CreateContext();

            var results = await Task.WhenAll(
                Task.Run(() => Decide(first, bothHaveRead, invoiceId, version, "approver-a@test")),
                Task.Run(() => Decide(second, bothHaveRead, invoiceId, version, "approver-b@test")));

            Assert.Equal(
                new[] { DecisionOutcome.Approved, DecisionOutcome.Conflict },
                results.Select(r => r.Outcome).Order());

            await using var check = fixture.CreateContext();
            var stored = await check.Invoices.SingleAsync(i => i.Id == invoiceId);
            Assert.Equal(InvoiceStatus.Approved, stored.Status);
            var approval = Assert.Single(stored.StatusHistory, s => s.To == InvoiceStatus.Approved); // approved once, not twice
            Assert.Contains(approval.Actor, new[] { "approver-a@test", "approver-b@test" });
        }
    }

    [Fact]
    public async Task The_same_approver_clicking_twice_approves_once()
    {
        var invoiceId = await MatchedInvoiceAsync();
        var version = await VersionAsync(invoiceId);

        var firstClick = await Decide(fixture.CreateContext(), null, invoiceId, version, "approver-a@test");
        var secondClick = await Decide(fixture.CreateContext(), null, invoiceId, version, "approver-a@test");

        Assert.Equal((DecisionOutcome.Approved, DecisionOutcome.Conflict), (firstClick.Outcome, secondClick.Outcome));
    }

    [Fact]
    public async Task A_decision_on_the_version_from_before_another_change_is_a_conflict()
    {
        var invoiceId = await MatchedInvoiceAsync();
        var seenByApprover = await VersionAsync(invoiceId);
        await using (var db = fixture.CreateContext())
        {
            // Meanwhile something else changes the invoice (here: an exception is raised).
            var invoice = await db.Invoices.SingleAsync(i => i.Id == invoiceId);
            invoice.TransitionTo(InvoiceStatus.Exception, "system", Now, "late finding");
            await db.SaveChangesAsync();
        }

        var result = await Decide(fixture.CreateContext(), null, invoiceId, seenByApprover, "approver-a@test", "Approved as discussed");

        Assert.Equal(DecisionOutcome.Conflict, result.Outcome);
        await using var check = fixture.CreateContext();
        Assert.Equal(InvoiceStatus.Exception, (await check.Invoices.SingleAsync(i => i.Id == invoiceId)).Status);
    }

    [Fact]
    public async Task Every_change_to_an_invoice_gives_it_a_new_version()
    {
        var invoiceId = await MatchedInvoiceAsync();
        var before = await VersionAsync(invoiceId);

        var result = await Decide(fixture.CreateContext(), null, invoiceId, before, "approver-a@test");

        Assert.Equal(DecisionOutcome.Approved, result.Outcome);
        Assert.NotEqual(before, await VersionAsync(invoiceId));
        Assert.Matches("^[A-Za-z0-9+/]{11}=$", before); // eight bytes, base64: opaque to clients
    }

    private static async Task<DecisionResult> Decide(
        EInvoiceDbContext db, BothRead? bothHaveRead, Guid invoiceId, string version, string approver, string? reason = null)
    {
        await using (db)
        {
            IApprovalStore store = new EfApprovalStore(db);
            if (bothHaveRead is not null)
            {
                store = new WaitAfterReading(store, bothHaveRead);
            }

            var approval = new InvoiceApproval(store, TimeProvider.System);
            return await approval.DecideAsync(
                new DecisionRequest(invoiceId, Decision.Approve, version, reason), new Approver(approver, 100_000m), CancellationToken.None);
        }
    }

    /// <summary>Lets the two approvals continue only when both have read the invoice: they really overlap.</summary>
    private sealed class BothRead
    {
        private readonly TaskCompletionSource _both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;

        public Task ArriveAsync()
        {
            if (Interlocked.Increment(ref _count) == 2)
            {
                _both.SetResult();
            }

            return _both.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    private sealed class WaitAfterReading(IApprovalStore inner, BothRead bothHaveRead) : IApprovalStore
    {
        public async Task<StoredInvoice?> FindAsync(Guid invoiceId, CancellationToken cancellationToken)
        {
            var found = await inner.FindAsync(invoiceId, cancellationToken);
            await bothHaveRead.ArriveAsync();
            return found;
        }

        public Task<bool> HasBlockingFindingsAsync(Guid invoiceId, CancellationToken cancellationToken) =>
            inner.HasBlockingFindingsAsync(invoiceId, cancellationToken);

        public Task SaveChangesAsync(string expectedVersion, CancellationToken cancellationToken) =>
            inner.SaveChangesAsync(expectedVersion, cancellationToken);
    }
}
