using EInvoice.Application.Intake;
using EInvoice.Application.Matching;
using EInvoice.Application.Validation;
using EInvoice.Domain.Invoices;
using EInvoice.Domain.Matching;
using EInvoice.Domain.Purchasing;
using EInvoice.Infrastructure.Intake;
using EInvoice.Infrastructure.Matching;
using EInvoice.Infrastructure.Parsing;
using EInvoice.Infrastructure.Persistence;
using EInvoice.Infrastructure.Validation;
using EInvoice.IntegrationTests.Infrastructure;
using EInvoice.IntegrationTests.TestData;
using Microsoft.EntityFrameworkCore;

namespace EInvoice.IntegrationTests.Matching;

/// <summary>
/// Re-match: an invoice in Exception only because of the match (for example the goods had not arrived yet)
/// is matched again on request. Invoices with validation findings, or in any other status, are refused.
/// Each test runs in a transaction that is rolled back.
/// </summary>
[Trait("Category", "Database")]
[Collection(SqlServerCollection.Name)]
public sealed class InvoiceRematchTests(SqlServerFixture fixture) : IAsyncLifetime
{
    private static readonly DateTimeOffset Later = new(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);

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

    private sealed class StubConformance(ConformanceResult result) : IConformanceValidator
    {
        public Task<ConformanceResult> CheckAsync(ReadOnlyMemory<byte> xml, CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private InvoiceRematch Rematch() =>
        new(new EfRematchStore(_db), new SqlInvoiceMatcher(_db), MatchingPolicy.Default, new FixedTime(Later));

    /// <summary>Order for seeded vendor 1 (10 x 7.4125), received as given, and an invoice for all 10 uploaded through the pipeline.</summary>
    private async Task<(MatchScenario Scenario, IntakeResult Upload)> UploadAsync(decimal received, ConformanceResult? conformance = null)
    {
        var scenario = new MatchScenario(_db).OrderLine(1, 10, 7.4125m);
        if (received > 0)
        {
            scenario.Received(1, received);
        }

        await scenario.SaveOrderAsync();
        var xml = new XRechnungBuilder { PurchaseOrderNumber = scenario.PurchaseOrderNumber }
            .Line("HDMI cable 2 m", 10m, 7.4125m, orderLine: "1")
            .Build();

        var data = new EfValidationDataSource(_db);
        var intake = new InvoiceIntake(
            new XRechnungParser(),
            new InvoiceValidator(data, new StubConformance(conformance ?? ConformanceResult.Accepted)),
            data,
            new SqlInvoiceMatcher(_db),
            new EfIntakeStore(_db),
            MatchingPolicy.Default,
            IntakeOptions.Default,
            TimeProvider.System);
        var upload = await intake.SubmitAsync(new UploadedFile("invoice.xml", xml), "clerk@test", CancellationToken.None);
        return (scenario, upload);
    }

    private async Task ReceiveLaterAsync(MatchScenario scenario, decimal quantity)
    {
        _db.GoodsReceipts.Add(new GoodsReceipt($"G{Guid.NewGuid():N}"[..16], scenario.Order!.Id, new DateOnly(2026, 11, 19), [new GoodsReceiptLine(1, quantity)]));
        await _db.SaveChangesAsync();
    }

    private async Task<Invoice> ReloadAsync(Guid id)
    {
        _db.ChangeTracker.Clear();
        return await _db.Invoices.SingleAsync(i => i.Id == id);
    }

    [Fact]
    public async Task Goods_received_later_turn_an_Exception_into_Matched()
    {
        var (scenario, upload) = await UploadAsync(received: 0);
        Assert.Equal(InvoiceStatus.Exception, upload.Status);
        await ReceiveLaterAsync(scenario, 10);

        var result = await Rematch().RematchAsync(upload.InvoiceId!.Value, "clerk2@test", CancellationToken.None);

        Assert.Equal(RematchOutcome.Matched, result.Outcome);
        Assert.Equal(InvoiceStatus.Matched, result.Status);
        Assert.Empty(result.Reasons);
        var invoice = await ReloadAsync(upload.InvoiceId.Value);
        Assert.Equal(InvoiceStatus.Matched, invoice.Status);
        var last = invoice.StatusHistory[^1];
        Assert.Equal((InvoiceStatus.Exception, InvoiceStatus.Matched, "clerk2@test", Later), (last.From!.Value, last.To, last.Actor, last.At));
        var rows = await _db.MatchResults.Where(r => r.InvoiceId == invoice.Id).ToListAsync();
        Assert.Equal(MatchOutcome.FullMatch, Assert.Single(rows).Outcome);
    }

    [Fact]
    public async Task Still_short_stays_in_Exception_with_the_current_reason_and_no_new_history()
    {
        var (scenario, upload) = await UploadAsync(received: 0);
        await ReceiveLaterAsync(scenario, 6);
        var historyBefore = (await ReloadAsync(upload.InvoiceId!.Value)).StatusHistory.Count;

        var result = await Rematch().RematchAsync(upload.InvoiceId.Value, "clerk2@test", CancellationToken.None);

        Assert.Equal(RematchOutcome.StillException, result.Outcome);
        Assert.Equal(InvoiceStatus.Exception, result.Status);
        Assert.Equal([MatchOutcome.QuantityVariance], result.Reasons);
        var invoice = await ReloadAsync(upload.InvoiceId.Value);
        Assert.Equal(InvoiceStatus.Exception, invoice.Status);
        Assert.Equal(historyBefore, invoice.StatusHistory.Count);
        var row = Assert.Single(await _db.MatchResults.Where(r => r.InvoiceId == invoice.Id).ToListAsync());
        Assert.Equal(6m, row.QuantityAvailable); // the stored result is refreshed
    }

    [Fact]
    public async Task Exception_with_validation_findings_is_not_rematched()
    {
        // Fully received, but the conformance check was unavailable: a human must look at it, a match cannot clear that.
        var (_, upload) = await UploadAsync(received: 10, ConformanceResult.Unavailable);
        Assert.Equal(InvoiceStatus.Exception, upload.Status);

        var result = await Rematch().RematchAsync(upload.InvoiceId!.Value, "clerk2@test", CancellationToken.None);

        Assert.Equal(RematchOutcome.NotAllowed, result.Outcome);
        Assert.Equal(InvoiceStatus.Exception, (await ReloadAsync(upload.InvoiceId.Value)).Status);
        Assert.Empty(await _db.MatchResults.Where(r => r.InvoiceId == upload.InvoiceId).ToListAsync());
    }

    [Fact]
    public async Task Matched_invoice_is_not_rematched()
    {
        var (_, upload) = await UploadAsync(received: 10);
        Assert.Equal(InvoiceStatus.Matched, upload.Status);

        var result = await Rematch().RematchAsync(upload.InvoiceId!.Value, "clerk2@test", CancellationToken.None);

        Assert.Equal(RematchOutcome.NotAllowed, result.Outcome);
        Assert.Equal(InvoiceStatus.Matched, result.Status);
    }

    [Fact]
    public async Task Unknown_invoice_is_not_found()
    {
        var result = await Rematch().RematchAsync(Guid.NewGuid(), "clerk2@test", CancellationToken.None);

        Assert.Equal(RematchOutcome.NotFound, result.Outcome);
    }
}

/// <summary>
/// Two clerks re-match the same invoice at the same moment. Both read it in Exception; the second save collides
/// on the status history key and becomes a clean refusal, not a server error. Commits, so it uses its own order.
/// </summary>
[Trait("Category", "Database")]
[Collection(SqlServerCollection.Name)]
public sealed class InvoiceRematchRaceTests(SqlServerFixture fixture)
{
    /// <summary>Lets the other clerk finish completely after this one has read the invoice, then matches.</summary>
    private sealed class OtherClerkFirst(IInvoiceMatcher inner, Func<Task> otherClerk) : IInvoiceMatcher
    {
        public async Task<InvoiceMatchResult> MatchAsync(Guid invoiceId, MatchingPolicy policy, CancellationToken cancellationToken)
        {
            await otherClerk();
            return await inner.MatchAsync(invoiceId, policy, cancellationToken);
        }
    }

    [Fact]
    public async Task Second_of_two_simultaneous_rematches_is_refused()
    {
        Guid invoiceId;
        await using (var setup = fixture.CreateContext())
        {
            // Its own amount: this invoice is committed and must not look like a duplicate of the invoices above.
            var scenario = await new MatchScenario(setup).OrderLine(1, 10, 2.5m).SaveOrderAsync();
            var invoice = await scenario.AddInvoiceAsync((1, 10m, 2.5m, 1));
            invoice.TransitionTo(InvoiceStatus.Exception, "system", DateTimeOffset.UtcNow, "QuantityVariance");
            setup.GoodsReceipts.Add(new GoodsReceipt($"G{Guid.NewGuid():N}"[..16], scenario.Order!.Id, new DateOnly(2026, 11, 19), [new GoodsReceiptLine(1, 10)]));
            await setup.SaveChangesAsync();
            invoiceId = invoice.Id;
        }

        RematchResult? first = null;
        await using var a = fixture.CreateContext();
        await using var b = fixture.CreateContext();
        var clerkA = new InvoiceRematch(new EfRematchStore(a), new SqlInvoiceMatcher(a), MatchingPolicy.Default, TimeProvider.System);
        var clerkB = new InvoiceRematch(
            new EfRematchStore(b),
            new OtherClerkFirst(new SqlInvoiceMatcher(b), async () => first = await clerkA.RematchAsync(invoiceId, "clerkA@test", CancellationToken.None)),
            MatchingPolicy.Default,
            TimeProvider.System);

        var second = await clerkB.RematchAsync(invoiceId, "clerkB@test", CancellationToken.None);

        Assert.Equal(RematchOutcome.Matched, first?.Outcome);
        Assert.Equal(RematchOutcome.NotAllowed, second.Outcome);
        await using var check = fixture.CreateContext();
        var stored = await check.Invoices.SingleAsync(i => i.Id == invoiceId);
        Assert.Equal(InvoiceStatus.Matched, stored.Status);
        Assert.Equal("clerkA@test", stored.StatusHistory[^1].Actor);
        Assert.Single(stored.StatusHistory, h => h.To == InvoiceStatus.Matched);
    }
}
