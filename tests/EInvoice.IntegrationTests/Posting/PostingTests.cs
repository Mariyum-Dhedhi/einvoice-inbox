using System.Net.Http.Json;
using EInvoice.Application.Posting;
using EInvoice.Domain.Invoices;
using EInvoice.Domain.Shared;
using EInvoice.Infrastructure.Persistence;
using EInvoice.Infrastructure.Posting;
using EInvoice.IntegrationTests.Infrastructure;
using EInvoice.IntegrationTests.Matching;
using Microsoft.EntityFrameworkCore;

namespace EInvoice.IntegrationTests.Posting;

/// <summary>
/// Package 14's "done when": a posted invoice reaches the (mock) ERP, and a failure is retried. Approval writes a
/// posting job in the same transaction (transactional outbox); the worker sends due jobs with the invoice id as
/// idempotency key, retries temporary failures with growing waits, and gives up into Failed. Jobs of other tests may be
/// processed too (the database is shared), so every check looks at this test's own invoice, and injected ERP failures
/// target this test's invoice number only.
/// </summary>
[Trait("Category", "Database")]
[Collection(SqlServerCollection.Name)]
public sealed class PostingTests(SqlServerFixture fixture) : IDisposable
{
    private static readonly DateTimeOffset Approved = new(2026, 11, 1, 9, 0, 0, TimeSpan.Zero); // in the past: due at once
    private readonly MockErpFactory _erp = new();
    private readonly MovableTime _time = new(DateTimeOffset.UtcNow);

    public void Dispose() => _erp.Dispose();

    private sealed class MovableTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>A committed, approved invoice of its own (unique number and amount); returns id and number.</summary>
    private async Task<(Guid Id, string Number)> ApprovedInvoiceAsync(string currency = "EUR")
    {
        await using var db = fixture.CreateContext();
        var price = 6m + (Random.Shared.Next(1, 1_000_000) / 10_000m);
        var net = new Money(price, currency);
        var line = new InvoiceLine(1, "HDMI cable 2 m", 1m, price, net, 19m, purchaseOrderLineNumber: 1);
        var tax = new Money(decimal.Round(price * 0.19m, 2, MidpointRounding.AwayFromZero), currency);
        var invoice = Invoice.Receive(SourceFormat.XRechnung, "clerk@test", Approved);
        invoice.MarkParsed(new ParsedInvoice($"P{Guid.NewGuid():N}"[..20], SeededVendor, new DateOnly(2026, 11, 2), null, net, tax, net + tax, [line]), "system", Approved);
        invoice.TransitionTo(InvoiceStatus.Validated, "system", Approved);
        invoice.TransitionTo(InvoiceStatus.Matched, "system", Approved);
        invoice.TransitionTo(InvoiceStatus.Approved, "approver@test", Approved);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();
        return (invoice.Id, invoice.InvoiceNumber!);
    }

    private static readonly Guid SeededVendor = Guid.Parse("00000000-0000-0000-0001-000000000001");

    /// <summary>One worker pass, as the background service runs it: its own scope (context), the shared ERP.</summary>
    private async Task RunWorkerAsync()
    {
        await using var db = fixture.CreateContext();
        using var http = _erp.Client(TimeSpan.FromSeconds(5));
        var posting = new InvoicePosting(new EfPostingStore(db), new HttpErpConnector(http), RetryPolicy.Default, _time);
        await posting.ProcessDueAsync(maxJobs: 500, CancellationToken.None);
    }

    private async Task<(Invoice Invoice, PostingJobRecord Job)> StateAsync(Guid invoiceId)
    {
        await using var db = fixture.CreateContext();
        var invoice = await db.Invoices.AsNoTracking().SingleAsync(i => i.Id == invoiceId);
        var job = await db.PostingJobs.AsNoTracking().SingleAsync(j => j.InvoiceId == invoiceId);
        return (invoice, job);
    }

    private async Task<List<System.Text.Json.JsonElement>> BookingsAsync(string invoiceNumber) =>
        (await _erp.PostingsAsync()).Where(p => p.GetProperty("invoice").GetProperty("invoiceNumber").GetString() == invoiceNumber).ToList();

    private async Task FailAsync(string invoiceNumber, int status, int count, bool afterBooking = false)
    {
        using var client = _erp.Client();
        using var response = await client.PostAsJsonAsync("_mock/failures", new { invoiceNumber, status, count, afterBooking, delayMs = 0 });
        response.EnsureSuccessStatusCode();
    }

    // ---- The outbox -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Approving_an_invoice_queues_its_posting_in_the_same_save()
    {
        var (id, _) = await ApprovedInvoiceAsync();

        var (invoice, job) = await StateAsync(id);

        Assert.Equal(InvoiceStatus.Approved, invoice.Status);
        Assert.Equal((PostingJobStatus.Pending, 0, Approved), (job.Status, job.Attempts, job.NextAttemptAt));
    }

    [Fact]
    public async Task An_approval_that_is_rolled_back_queues_nothing()
    {
        Guid id;
        await using (var db = fixture.CreateContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            var scenario = await new MatchScenario(db).OrderLine(1, 1, 9.99m).Received(1, 1).SaveOrderAsync();
            var invoice = await scenario.AddInvoiceAsync((1, 1, 9.99m, 1));
            invoice.TransitionTo(InvoiceStatus.Validated, "system", Approved);
            invoice.TransitionTo(InvoiceStatus.Matched, "system", Approved);
            invoice.TransitionTo(InvoiceStatus.Approved, "approver@test", Approved);
            await db.SaveChangesAsync();
            id = invoice.Id;
            await tx.RollbackAsync();
        }

        await using var check = fixture.CreateContext();
        Assert.False(await check.PostingJobs.AnyAsync(j => j.InvoiceId == id));
    }

    // ---- Done when: a posted invoice reaches the mock ... ------------------------------------------------------------

    [Fact]
    public async Task An_approved_invoice_reaches_the_erp_and_becomes_posted()
    {
        var (id, number) = await ApprovedInvoiceAsync();

        await RunWorkerAsync();

        var (invoice, job) = await StateAsync(id);
        var booking = Assert.Single(await BookingsAsync(number));
        var document = booking.GetProperty("documentNumber").GetString();
        Assert.Equal(id.ToString(), booking.GetProperty("idempotencyKey").GetString()); // the invoice id: never booked twice
        Assert.Equal(InvoiceStatus.Posted, invoice.Status);
        var last = invoice.StatusHistory[^1];
        Assert.Equal((InvoiceStatus.Approved, "erp-connector"), (last.From!.Value, last.Actor));
        Assert.Contains(document!, last.Reason, StringComparison.Ordinal);
        Assert.Equal((PostingJobStatus.Done, 1, document), (job.Status, job.Attempts, job.ErpDocumentNumber));

        await using var db = fixture.CreateContext();
        Assert.True(await db.AuditEvents.AnyAsync(e => e.Subject == $"Invoice/{id}" && e.Details.Contains("\"to\":\"Posted\"")));
    }

    // ---- ... and a failure is retried --------------------------------------------------------------------------------

    [Fact]
    public async Task A_temporary_failure_is_retried_after_the_wait_until_it_is_posted()
    {
        var (id, number) = await ApprovedInvoiceAsync();
        await FailAsync(number, status: 503, count: 2);

        await RunWorkerAsync(); // try 1: 503
        var (afterFirst, job1) = await StateAsync(id);
        Assert.Equal(InvoiceStatus.Approved, afterFirst.Status);
        Assert.Equal((PostingJobStatus.Pending, 1, _time.GetUtcNow() + TimeSpan.FromSeconds(30)), (job1.Status, job1.Attempts, job1.NextAttemptAt));
        Assert.Contains("503", job1.LastError, StringComparison.Ordinal);

        _time.Advance(TimeSpan.FromSeconds(10));
        await RunWorkerAsync(); // not due yet
        Assert.Equal(1, (await StateAsync(id)).Job.Attempts);

        _time.Advance(TimeSpan.FromSeconds(21));
        await RunWorkerAsync(); // try 2: 503 again; next wait 2 minutes
        Assert.Equal(2, (await StateAsync(id)).Job.Attempts);

        _time.Advance(TimeSpan.FromMinutes(2));
        await RunWorkerAsync(); // try 3: booked

        var (invoice, job) = await StateAsync(id);
        Assert.Equal((InvoiceStatus.Posted, PostingJobStatus.Done, 3), (invoice.Status, job.Status, job.Attempts));
        Assert.Single(await BookingsAsync(number));
    }

    [Fact]
    public async Task A_lost_reply_is_retried_without_booking_twice()
    {
        var (id, number) = await ApprovedInvoiceAsync();
        await FailAsync(number, status: 500, count: 1, afterBooking: true);

        await RunWorkerAsync();
        _time.Advance(TimeSpan.FromSeconds(31));
        await RunWorkerAsync();

        var booking = Assert.Single(await BookingsAsync(number)); // booked once, although sent twice
        var (invoice, job) = await StateAsync(id);
        Assert.Equal((InvoiceStatus.Posted, booking.GetProperty("documentNumber").GetString()), (invoice.Status, job.ErpDocumentNumber));
    }

    [Fact]
    public async Task After_the_last_try_the_invoice_is_failed_with_the_reason()
    {
        var (id, number) = await ApprovedInvoiceAsync();
        await FailAsync(number, status: 503, count: 6);

        foreach (var wait in new[] { 0, 30, 120, 600, 1800, 3600 })
        {
            _time.Advance(TimeSpan.FromSeconds(wait));
            await RunWorkerAsync();
        }

        var (invoice, job) = await StateAsync(id);
        Assert.Equal((InvoiceStatus.Failed, PostingJobStatus.Dead, 6), (invoice.Status, job.Status, job.Attempts));
        Assert.Contains("6 attempts", invoice.StatusHistory[^1].Reason, StringComparison.Ordinal);
        Assert.Empty(await BookingsAsync(number));

        _time.Advance(TimeSpan.FromDays(1));
        await RunWorkerAsync(); // a dead job is never tried again
        Assert.Equal(6, (await StateAsync(id)).Job.Attempts);
    }

    [Fact]
    public async Task A_rejection_by_the_erp_fails_the_invoice_at_once()
    {
        var (id, number) = await ApprovedInvoiceAsync(currency: "USD");

        await RunWorkerAsync();

        var (invoice, job) = await StateAsync(id);
        Assert.Equal((InvoiceStatus.Failed, PostingJobStatus.Dead, 1), (invoice.Status, job.Status, job.Attempts));
        Assert.Contains("422", invoice.StatusHistory[^1].Reason, StringComparison.Ordinal);
        Assert.Empty(await BookingsAsync(number));
    }

    [Fact]
    public async Task Two_workers_at_the_same_time_post_each_invoice_once()
    {
        var invoices = new List<(Guid Id, string Number)>();
        for (var i = 0; i < 5; i++)
        {
            invoices.Add(await ApprovedInvoiceAsync());
        }

        await Task.WhenAll(Task.Run(RunWorkerAsync), Task.Run(RunWorkerAsync));

        foreach (var (id, number) in invoices)
        {
            Assert.Single(await BookingsAsync(number));
            var (invoice, job) = await StateAsync(id);
            Assert.Equal((InvoiceStatus.Posted, 1), (invoice.Status, job.Attempts)); // claimed by one worker only
        }
    }
}
