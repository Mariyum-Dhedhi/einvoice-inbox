using EInvoice.Application.Posting;
using EInvoice.Domain.Invoices;
using EInvoice.UnitTests.Domain;

namespace EInvoice.UnitTests.Posting;

/// <summary>
/// A posting run against a busy ERP, without a database or a network:
/// - When the ERP says when to come back (Retry-After), the next try is then, at least 1 second and at most 1 hour later.
/// - Throttling (429) uses up no try: only the ERP failing does.
/// - The circuit breaker: while it is open, due jobs are not sent and stay due with their tries unchanged, because the
///   invoice did nothing wrong; counting those would fail invoices for the ERP's downtime.
/// - At most N calls are in flight at once.
/// - Throttling and breaker changes are logged with the job id as the correlation id.
/// </summary>
public sealed class PostingResilienceTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 21, 9, 0, 0, TimeSpan.Zero);

    private readonly MovableTime _time = new(Start);
    private readonly FakeStore _store = new();
    private readonly FakeLog _log = new();

    private sealed class MovableTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class Job(Guid id, Invoice invoice)
    {
        public Guid Id { get; } = id;

        public Invoice Invoice { get; } = invoice;

        public int Attempts { get; set; }

        public DateTimeOffset Next { get; set; } = Start;

        public DateTimeOffset? Lease { get; set; }

        public PostingJobStatus Status { get; set; } = PostingJobStatus.Pending;
    }

    private sealed class FakeStore : IPostingStore
    {
        public List<Job> Jobs { get; } = [];

        public int Releases { get; private set; }

        public Job Add(int attempts = 0)
        {
            var invoice = TestInvoices.InStatus(InvoiceStatus.Approved, [InvoiceStatus.Parsed, InvoiceStatus.Validated, InvoiceStatus.Matched, InvoiceStatus.Approved]);
            var job = new Job(Guid.NewGuid(), invoice) { Attempts = attempts };
            Jobs.Add(job);
            return job;
        }

        public Task<IReadOnlyList<ClaimedJob>> ClaimDueAsync(DateTimeOffset now, TimeSpan lease, int max, CancellationToken cancellationToken)
        {
            var due = Jobs.Where(j => j.Status == PostingJobStatus.Pending && j.Next <= now && (j.Lease is null || j.Lease < now)).Take(max).ToList();
            due.ForEach(j => j.Lease = now + lease);
            return Task.FromResult<IReadOnlyList<ClaimedJob>>([.. due.Select(j => new ClaimedJob(j.Id, j.Invoice.Id, j.Attempts))]);
        }

        public Task<(Invoice Invoice, string? VendorAccount)?> FindInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken) =>
            Task.FromResult<(Invoice, string?)?>((Jobs.Single(j => j.Invoice.Id == invoiceId).Invoice, "V-1001"));

        public Task CompleteAsync(Guid jobId, int attempts, string documentNumber, DateTimeOffset at, CancellationToken cancellationToken) =>
            Change(jobId, j => (j.Status, j.Attempts) = (PostingJobStatus.Done, attempts));

        public Task RetryLaterAsync(Guid jobId, int attempts, DateTimeOffset next, string reason, CancellationToken cancellationToken) =>
            Change(jobId, j => (j.Attempts, j.Next) = (attempts, next));

        public Task GiveUpAsync(Guid jobId, int attempts, string reason, DateTimeOffset at, CancellationToken cancellationToken) =>
            Change(jobId, j => (j.Status, j.Attempts) = (PostingJobStatus.Dead, attempts));

        public Task ReleaseAsync(Guid jobId, CancellationToken cancellationToken)
        {
            Releases++;
            return Change(jobId, _ => { });
        }

        private Task Change(Guid jobId, Action<Job> change)
        {
            var job = Jobs.Single(j => j.Id == jobId);
            change(job);
            job.Lease = null;
            return Task.CompletedTask;
        }
    }

    /// <summary>Answers each call with the next planned result (then the last one again), counting calls in flight.</summary>
    private sealed class FakeErp(params ErpResult[] answers) : IErpConnector
    {
        private int _calls;
        private int _inFlight;

        public int Calls => _calls;

        public int MaxInFlight { get; private set; }

        public TimeSpan Delay { get; init; }

        public async Task<ErpResult> PostAsync(Guid idempotencyKey, ErpVendorInvoice invoice, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls) - 1;
            var inFlight = Interlocked.Increment(ref _inFlight);
            lock (answers)
            {
                MaxInFlight = Math.Max(MaxInFlight, inFlight);
            }

            await Task.Delay(Delay, cancellationToken);
            Interlocked.Decrement(ref _inFlight);
            return answers[Math.Min(call, answers.Length - 1)];
        }
    }

    private sealed class FakeLog : IPostingLog
    {
        public List<string> Lines { get; } = [];

        public void Throttled(Guid jobId, TimeSpan wait)
        {
            lock (Lines)
            {
                Lines.Add($"throttled {jobId} {wait.TotalSeconds}");
            }
        }

        public void BreakerChanged(Guid jobId, BreakerState state)
        {
            lock (Lines)
            {
                Lines.Add($"breaker {state} {jobId}");
            }
        }
    }

    private static readonly ErpResult Posted = new(ErpOutcome.Posted, "VI-000001");
    private static readonly ErpResult Unavailable = new(ErpOutcome.Transient, Reason: "HTTP 503 from the ERP.");

    private static ErpResult Throttled(int? seconds) =>
        new(ErpOutcome.Throttled, Reason: "HTTP 429 from the ERP.", RetryAfter: seconds is { } s ? TimeSpan.FromSeconds(s) : null);

    private ErpCircuitBreaker? _breaker;

    private ErpCircuitBreaker Breaker => _breaker ??= new ErpCircuitBreaker(BreakerSettings.Default, _time);

    private InvoicePosting Posting(IErpConnector erp, int maxConcurrentCalls = 4) =>
        new(_store, erp, RetryPolicy.Default, _time, Breaker, new PostingLimits(maxConcurrentCalls), _log);

    // ---- Retry-After ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_429_with_retry_after_20_is_tried_again_in_20_seconds_not_30()
    {
        var job = _store.Add();

        await Posting(new FakeErp(Throttled(20))).ProcessDueAsync(10, CancellationToken.None);

        Assert.Equal(Start + TimeSpan.FromSeconds(20), job.Next);
    }

    [Fact]
    public async Task A_429_without_a_usable_retry_after_waits_as_the_policy_says()
    {
        // The connector gives no wait for a date in the past or an unreadable header.
        var job = _store.Add();

        await Posting(new FakeErp(Throttled(null))).ProcessDueAsync(10, CancellationToken.None);

        Assert.Equal(Start + TimeSpan.FromSeconds(30), job.Next);
    }

    [Fact]
    public async Task A_retry_after_of_99999_seconds_is_capped_at_one_hour()
    {
        var job = _store.Add();

        await Posting(new FakeErp(Throttled(99_999))).ProcessDueAsync(10, CancellationToken.None);

        Assert.Equal(Start + TimeSpan.FromHours(1), job.Next);
    }

    [Fact]
    public async Task A_retry_after_of_0_still_waits_one_second()
    {
        var job = _store.Add();

        await Posting(new FakeErp(Throttled(0))).ProcessDueAsync(10, CancellationToken.None);

        Assert.Equal(Start + TimeSpan.FromSeconds(1), job.Next);
    }

    [Fact]
    public async Task A_503_with_retry_after_waits_that_long_and_uses_up_a_try()
    {
        var job = _store.Add();

        await Posting(new FakeErp(Unavailable with { RetryAfter = TimeSpan.FromSeconds(20) })).ProcessDueAsync(10, CancellationToken.None);

        Assert.Equal((Start + TimeSpan.FromSeconds(20), 1), (job.Next, job.Attempts));
    }

    // ---- Throttling uses no try -------------------------------------------------------------------------------------

    [Fact]
    public async Task Throttling_uses_up_no_try()
    {
        var job = _store.Add(attempts: 2);

        var run = await Posting(new FakeErp(Throttled(20))).ProcessDueAsync(10, CancellationToken.None);

        Assert.Equal((2, PostingJobStatus.Pending), (job.Attempts, job.Status));
        Assert.Equal(1, run.Retried);
    }

    [Fact]
    public async Task Throttling_on_the_last_try_does_not_fail_the_invoice()
    {
        var job = _store.Add(attempts: RetryPolicy.Default.MaxAttempts - 1);

        await Posting(new FakeErp(Throttled(20))).ProcessDueAsync(10, CancellationToken.None);

        Assert.Equal((InvoiceStatus.Approved, PostingJobStatus.Pending), (job.Invoice.Status, job.Status));
    }

    [Fact]
    public async Task A_temporary_failure_on_the_last_try_still_fails_the_invoice()
    {
        var job = _store.Add(attempts: RetryPolicy.Default.MaxAttempts - 1);

        await Posting(new FakeErp(Unavailable with { RetryAfter = TimeSpan.FromSeconds(20) })).ProcessDueAsync(10, CancellationToken.None);

        Assert.Equal((InvoiceStatus.Failed, PostingJobStatus.Dead), (job.Invoice.Status, job.Status));
    }

    // ---- Circuit breaker --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Five_503s_in_a_row_open_the_breaker_and_the_next_due_job_is_not_attempted()
    {
        for (var i = 0; i < 5; i++)
        {
            _store.Add();
        }

        var erp = new FakeErp(Unavailable);
        await Posting(erp, maxConcurrentCalls: 1).ProcessDueAsync(10, CancellationToken.None);
        Assert.Equal(BreakerState.Open, Breaker.State);

        var next = _store.Add(attempts: 3);
        var run = await Posting(erp).ProcessDueAsync(10, CancellationToken.None);

        Assert.Equal(5, erp.Calls);
        Assert.Equal((3, PostingJobStatus.Pending, (DateTimeOffset?)null), (next.Attempts, next.Status, next.Lease));
        Assert.Equal(Start, next.Next); // still due: the next pass after the breaker closes takes it
        Assert.Equal(new PostingRun(0, 0, 0), run);
    }

    [Fact]
    public async Task Jobs_claimed_when_the_breaker_opens_during_a_run_are_released_unchanged()
    {
        for (var i = 0; i < 8; i++)
        {
            _store.Add();
        }

        var erp = new FakeErp(Unavailable);
        var run = await Posting(erp, maxConcurrentCalls: 1).ProcessDueAsync(10, CancellationToken.None);

        Assert.Equal(5, erp.Calls);
        Assert.Equal((5, 3), (run.Retried, run.Held));
        Assert.Equal(3, _store.Jobs.Count(j => j.Attempts == 0 && j.Lease is null && j.Next == Start));
    }

    [Fact]
    public async Task Half_open_sends_one_job_and_a_success_closes_the_breaker()
    {
        for (var i = 0; i < 5; i++)
        {
            _store.Add();
        }

        await Posting(new FakeErp(Unavailable), maxConcurrentCalls: 1).ProcessDueAsync(10, CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(2));

        var erp = new FakeErp(Posted);
        var trial = await Posting(erp).ProcessDueAsync(10, CancellationToken.None);

        Assert.Equal((1, 1), (erp.Calls, trial.Posted));
        Assert.Equal(BreakerState.Closed, Breaker.State);
    }

    [Fact]
    public async Task Half_open_and_a_failure_opens_the_breaker_again()
    {
        for (var i = 0; i < 5; i++)
        {
            _store.Add();
        }

        await Posting(new FakeErp(Unavailable), maxConcurrentCalls: 1).ProcessDueAsync(10, CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(2));

        var erp = new FakeErp(Unavailable);
        await Posting(erp).ProcessDueAsync(10, CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(1));
        await Posting(erp).ProcessDueAsync(10, CancellationToken.None);

        Assert.Equal(1, erp.Calls);
        Assert.Equal(BreakerState.Open, Breaker.State);
    }

    [Fact]
    public async Task A_rejection_is_an_answer_and_keeps_the_breaker_closed()
    {
        for (var i = 0; i < 8; i++)
        {
            _store.Add();
        }

        var erp = new FakeErp(Unavailable, Unavailable, Unavailable, Unavailable, new ErpResult(ErpOutcome.Rejected, Reason: "HTTP 422: no."), Unavailable);
        await Posting(erp, maxConcurrentCalls: 1).ProcessDueAsync(10, CancellationToken.None);

        Assert.Equal(8, erp.Calls);
        Assert.Equal(BreakerState.Closed, Breaker.State); // 4 failures, an answer, then 3
    }

    // ---- Concurrency ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task No_more_calls_are_in_flight_than_allowed(int limit)
    {
        for (var i = 0; i < 12; i++)
        {
            _store.Add();
        }

        var erp = new FakeErp(Posted) { Delay = TimeSpan.FromMilliseconds(50) };
        var run = await Posting(erp, maxConcurrentCalls: limit).ProcessDueAsync(20, CancellationToken.None);

        Assert.Equal(12, run.Posted);
        Assert.Equal(limit, erp.MaxInFlight); // never more, and the limit is used
        Assert.All(_store.Jobs, j => Assert.Equal(InvoiceStatus.Posted, j.Invoice.Status));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    public void The_concurrency_limit_is_between_1_and_64(int limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PostingLimits(limit));
    }

    // ---- Logging ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Throttling_and_every_breaker_change_are_logged_with_the_job_id()
    {
        var throttled = _store.Add();
        await Posting(new FakeErp(Throttled(20))).ProcessDueAsync(10, CancellationToken.None);
        _time.Advance(TimeSpan.FromSeconds(20));
        _store.Jobs.Remove(throttled);

        var failing = Enumerable.Range(0, 4).Select(_ => _store.Add()).ToList();
        await Posting(new FakeErp(Unavailable), maxConcurrentCalls: 1).ProcessDueAsync(10, CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(2));
        await Posting(new FakeErp(Posted)).ProcessDueAsync(10, CancellationToken.None);

        Assert.Equal($"throttled {throttled.Id} 20", _log.Lines[0]);
        Assert.Equal(
            [$"breaker {BreakerState.Open} {failing[3].Id}", $"breaker {BreakerState.HalfOpen} {failing[0].Id}", $"breaker {BreakerState.Closed} {failing[0].Id}"],
            _log.Lines.Skip(1));
    }
}
