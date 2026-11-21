using EInvoice.Domain.Invoices;

namespace EInvoice.Application.Posting;

/// <summary>An approved invoice as the ERP receives it (the contract: contracts/d365/vendor-invoice.example.json).</summary>
public sealed record ErpVendorInvoice(
    string InvoiceNumber,
    string VendorAccount,
    DateOnly InvoiceDate,
    string Currency,
    decimal NetAmount,
    decimal TaxAmount,
    decimal TotalAmount,
    string? PurchaseOrder,
    IReadOnlyList<ErpVendorInvoiceLine> Lines);

public sealed record ErpVendorInvoiceLine(
    int LineNumber, string Description, decimal Quantity, decimal UnitPrice, decimal Amount, decimal TaxRatePercent, int? PurchaseOrderLine);

public static class ErpPayload
{
    /// <summary>The ERP gets only approved invoices: a person decided, and nothing else may be paid.</summary>
    public static ErpVendorInvoice From(Invoice invoice, string vendorAccount)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentException.ThrowIfNullOrWhiteSpace(vendorAccount);
        if (invoice.Status != InvoiceStatus.Approved)
        {
            throw new InvalidOperationException($"Only approved invoices are posted; this one is {invoice.Status}.");
        }

        return new ErpVendorInvoice(
            invoice.InvoiceNumber!,
            vendorAccount,
            invoice.IssueDate!.Value,
            invoice.GrossTotal!.Currency,
            invoice.NetTotal!.Amount,
            invoice.TaxTotal!.Amount,
            invoice.GrossTotal.Amount,
            invoice.PurchaseOrderNumber,
            invoice.Lines.Select(l => new ErpVendorInvoiceLine(
                l.LineNumber, l.Description, l.Quantity, l.UnitPrice, l.NetAmount.Amount, l.TaxRatePercent, l.PurchaseOrderLineNumber)).ToList());
    }
}

public enum ErpOutcome
{
    Posted = 1,

    /// <summary>No answer, a timeout, 408 or 5xx, or an answer that cannot be read: worth another try.</summary>
    Transient = 2,

    /// <summary>The ERP refused the invoice (4xx): another try would get the same answer.</summary>
    Rejected = 3,

    /// <summary>429: the ERP is busy and asks us to come back later. Uses up no try.</summary>
    Throttled = 4,
}

/// <param name="Reason">Short and safe to store (at most 200 characters); never a whole ERP answer.</param>
/// <param name="RetryAfter">How long the ERP asked us to wait (Retry-After on 429 and 503), uncapped; null: not said.</param>
public sealed record ErpResult(ErpOutcome Outcome, string? DocumentNumber = null, string? Reason = null, TimeSpan? RetryAfter = null);

public interface IErpConnector
{
    /// <param name="idempotencyKey">The invoice id: the ERP books one key once, so a retry after a lost reply is harmless.</param>
    Task<ErpResult> PostAsync(Guid idempotencyKey, ErpVendorInvoice invoice, CancellationToken cancellationToken);
}

/// <summary>How long to wait after each failed try; one try more than there are waits, then give up.</summary>
public sealed class RetryPolicy
{
    public RetryPolicy(IReadOnlyList<TimeSpan> waits)
    {
        ArgumentNullException.ThrowIfNull(waits);
        Waits = waits;
    }

    /// <summary>30 s, 2 min, 10 min, 30 min, 1 h: six tries over about 1 h 43 min.</summary>
    public static RetryPolicy Default { get; } = new(
        [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1)]);

    public IReadOnlyList<TimeSpan> Waits { get; }

    public int MaxAttempts => Waits.Count + 1;

    /// <summary>The wait before the next try after <paramref name="failedAttempts"/> failures; null: no more tries.</summary>
    public TimeSpan? WaitAfter(int failedAttempts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failedAttempts, 1);
        return failedAttempts <= Waits.Count ? Waits[failedAttempts - 1] : null;
    }
}

/// <summary>Stored as numbers: never renumber or reuse a value.</summary>
public enum PostingJobStatus
{
    Pending = 1,
    Done = 2,

    /// <summary>Given up (ERP rejected it, or every try failed): the invoice is Failed; an admin can re-queue it.</summary>
    Dead = 3,
}

/// <summary>A posting job claimed by one worker (lease), with its invoice ready to change.</summary>
public sealed record ClaimedJob(Guid JobId, Guid InvoiceId, int Attempts);

public interface IPostingStore
{
    /// <summary>Takes up to <paramref name="max"/> due jobs for this worker only (no other worker gets them until the lease ends).</summary>
    Task<IReadOnlyList<ClaimedJob>> ClaimDueAsync(DateTimeOffset now, TimeSpan lease, int max, CancellationToken cancellationToken);

    /// <summary>The invoice with its vendor's account number in the ERP; null when either is gone.</summary>
    Task<(Invoice Invoice, string? VendorAccount)?> FindInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken);

    Task CompleteAsync(Guid jobId, int attempts, string documentNumber, DateTimeOffset at, CancellationToken cancellationToken);

    Task RetryLaterAsync(Guid jobId, int attempts, DateTimeOffset next, string reason, CancellationToken cancellationToken);

    Task GiveUpAsync(Guid jobId, int attempts, string reason, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Ends the lease and changes nothing else: the job was not sent (breaker open) and is due again at once.</summary>
    Task ReleaseAsync(Guid jobId, CancellationToken cancellationToken);
}

/// <param name="Retried">Temporary failures and throttling, to be tried again.</param>
/// <param name="Held">Claimed but not sent because the breaker opened; due again, tries unchanged.</param>
public sealed record PostingRun(int Posted, int Retried, int Failed, int Held = 0);

public interface IInvoicePosting
{
    Task<PostingRun> ProcessDueAsync(int maxJobs, CancellationToken cancellationToken);
}

/// <summary>At most this many calls to the ERP at once, per worker.</summary>
public sealed record PostingLimits
{
    public PostingLimits(int maxConcurrentCalls = 4)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrentCalls, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxConcurrentCalls, 64);
        MaxConcurrentCalls = maxConcurrentCalls;
    }

    public int MaxConcurrentCalls { get; }
}

/// <summary>
/// What operators need to see about a busy ERP. The job id is the correlation id; nothing of the invoice or the
/// request body is passed, so nothing of it can be logged.
/// </summary>
public interface IPostingLog
{
    void Throttled(Guid jobId, TimeSpan wait);

    void BreakerChanged(Guid jobId, BreakerState state);
}

internal sealed class NoPostingLog : IPostingLog
{
    public static NoPostingLog Instance { get; } = new();

    public void Throttled(Guid jobId, TimeSpan wait)
    {
    }

    public void BreakerChanged(Guid jobId, BreakerState state)
    {
    }
}

/// <summary>
/// Sends due posting jobs to the ERP. Posted: the invoice becomes Posted with the ERP's document number. Temporary
/// failure: try again after the policy's wait, or as long as the ERP asked (Retry-After), at least 1 second and at most
/// 1 hour. Throttled (429): the same, but no try is used up. Rejected, or the last try failed: the invoice becomes
/// Failed with the reason (a technical failure; an admin can re-queue it).
/// Up to <see cref="PostingLimits.MaxConcurrentCalls"/> calls run at once; the store is used by one job at a time, so
/// each job's invoice change is saved with that job only. The circuit breaker decides whether a call may go at all: a
/// job it holds back is released unchanged, because the ERP being down is not the invoice's failure.
/// </summary>
public sealed class InvoicePosting(
    IPostingStore store,
    IErpConnector erp,
    RetryPolicy policy,
    TimeProvider time,
    ErpCircuitBreaker? breaker = null,
    PostingLimits? limits = null,
    IPostingLog? log = null) : IInvoicePosting
{
    public const string Actor = "erp-connector";

    /// <summary>The longest wait an ERP can ask for; a longer Retry-After is cut to this.</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromHours(1);

    private static readonly TimeSpan MinWait = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    private const int MaxReason = 200;

    private readonly ErpCircuitBreaker _breaker = breaker ?? new ErpCircuitBreaker(BreakerSettings.Default, time);
    private readonly PostingLimits _limits = limits ?? new PostingLimits();
    private readonly IPostingLog _log = log ?? NoPostingLog.Instance;
    private readonly SemaphoreSlim _storeGate = new(1, 1);

    private enum JobResult
    {
        Posted,
        Retried,
        Failed,
        Held,
        Dropped,
    }

    public async Task<PostingRun> ProcessDueAsync(int maxJobs, CancellationToken cancellationToken)
    {
        // Open: claim nothing, every due job stays as it is. Due for a trial: claim one job for the one call allowed.
        var max = _breaker.State == BreakerState.Closed ? maxJobs : _breaker.TrialDue ? 1 : 0;
        if (max == 0)
        {
            return new PostingRun(0, 0, 0);
        }

        var jobs = await store.ClaimDueAsync(time.GetUtcNow(), Lease, max, cancellationToken).ConfigureAwait(false);
        int posted = 0, retried = 0, failed = 0, held = 0;
        await Parallel.ForEachAsync(
            jobs,
            new ParallelOptions { MaxDegreeOfParallelism = _limits.MaxConcurrentCalls, CancellationToken = cancellationToken },
            async (job, token) =>
            {
                switch (await PostAsync(job, token).ConfigureAwait(false))
                {
                    case JobResult.Posted:
                        Interlocked.Increment(ref posted);
                        break;
                    case JobResult.Retried:
                        Interlocked.Increment(ref retried);
                        break;
                    case JobResult.Failed:
                        Interlocked.Increment(ref failed);
                        break;
                    case JobResult.Held:
                        Interlocked.Increment(ref held);
                        break;
                }
            }).ConfigureAwait(false);

        return new PostingRun(posted, retried, failed, held);
    }

    private async Task<JobResult> PostAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        var before = _breaker.State;
        var permit = _breaker.TryEnter();
        LogChange(job, before);
        if (permit == BreakerPermit.Denied)
        {
            await InStoreAsync(() => store.ReleaseAsync(job.JobId, cancellationToken), cancellationToken).ConfigureAwait(false);
            return JobResult.Held;
        }

        var found = await InStoreAsync(() => store.FindInvoiceAsync(job.InvoiceId, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (found is not { VendorAccount: { } account } || found.Value.Invoice.Status != InvoiceStatus.Approved)
        {
            // Changed since it was queued (for example re-matched or already posted): nothing to send. The ERP was not
            // asked, so the breaker learns nothing (an unused trial runs out after the open period).
            await InStoreAsync(
                () => store.GiveUpAsync(job.JobId, job.Attempts + 1, "The invoice is no longer approved, or its vendor is gone.", time.GetUtcNow(), cancellationToken),
                cancellationToken).ConfigureAwait(false);
            return JobResult.Dropped;
        }

        var invoice = found.Value.Invoice;
        var result = await erp.PostAsync(invoice.Id, ErpPayload.From(invoice, account), cancellationToken).ConfigureAwait(false);

        before = _breaker.State;
        _breaker.Record(permit, erpAnswered: result.Outcome is ErpOutcome.Posted or ErpOutcome.Rejected);
        LogChange(job, before);

        return await InStoreAsync(() => SaveAsync(job, invoice, result, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private async Task<JobResult> SaveAsync(ClaimedJob job, Invoice invoice, ErpResult result, CancellationToken cancellationToken)
    {
        var attempts = job.Attempts + 1;
        var now = time.GetUtcNow();
        switch (result.Outcome)
        {
            case ErpOutcome.Posted:
                invoice.TransitionTo(InvoiceStatus.Posted, Actor, now, Cut($"Posted to the ERP as {result.DocumentNumber}."));
                await store.CompleteAsync(job.JobId, attempts, result.DocumentNumber!, now, cancellationToken).ConfigureAwait(false);
                return JobResult.Posted;

            case ErpOutcome.Throttled:
                // Busy, not failing: the job keeps its tries. Without a usable Retry-After, wait as a failure here would.
                var throttledWait = Wait(result.RetryAfter, policy.WaitAfter(attempts) ?? LongestWait());
                _log.Throttled(job.JobId, throttledWait);
                await store.RetryLaterAsync(job.JobId, job.Attempts, now + throttledWait, Cut(result.Reason ?? "Throttled by the ERP."), cancellationToken).ConfigureAwait(false);
                return JobResult.Retried;

            case ErpOutcome.Transient when policy.WaitAfter(attempts) is { } wait:
                await store.RetryLaterAsync(job.JobId, attempts, now + Wait(result.RetryAfter, wait), Cut(result.Reason ?? "Temporary failure."), cancellationToken).ConfigureAwait(false);
                return JobResult.Retried;

            case ErpOutcome.Transient:
                invoice.TransitionTo(InvoiceStatus.Failed, Actor, now, Cut($"ERP unavailable after {attempts} attempts: {result.Reason}"));
                await store.GiveUpAsync(job.JobId, attempts, Cut(result.Reason ?? "Temporary failure."), now, cancellationToken).ConfigureAwait(false);
                return JobResult.Failed;

            default:
                invoice.TransitionTo(InvoiceStatus.Failed, Actor, now, Cut($"Rejected by the ERP: {result.Reason}"));
                await store.GiveUpAsync(job.JobId, attempts, Cut(result.Reason ?? "Rejected."), now, cancellationToken).ConfigureAwait(false);
                return JobResult.Failed;
        }
    }

    /// <summary>What the ERP asked for, between 1 second and 1 hour; the policy's wait when it asked for nothing.</summary>
    private static TimeSpan Wait(TimeSpan? retryAfter, TimeSpan policyWait) => retryAfter is { } asked
        ? TimeSpan.FromTicks(Math.Clamp(asked.Ticks, MinWait.Ticks, MaxWait.Ticks))
        : policyWait;

    private TimeSpan LongestWait() => policy.Waits.Count > 0 ? policy.Waits[^1] : MaxWait;

    private void LogChange(ClaimedJob job, BreakerState before)
    {
        var after = _breaker.State;
        if (after != before)
        {
            _log.BreakerChanged(job.JobId, after);
        }
    }

    /// <summary>One job at a time in the store: its unit of work (a DbContext) is shared and not thread-safe.</summary>
    private async Task<T> InStoreAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        await _storeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            _storeGate.Release();
        }
    }

    private Task InStoreAsync(Func<Task> action, CancellationToken cancellationToken) =>
        InStoreAsync(
            async () =>
            {
                await action().ConfigureAwait(false);
                return true;
            },
            cancellationToken);

    private static string Cut(string text) => text.Length <= MaxReason ? text : text[..MaxReason];
}
