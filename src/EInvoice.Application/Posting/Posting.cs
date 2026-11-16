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

    /// <summary>No answer, a timeout, 408, 429 or 5xx: worth another try.</summary>
    Transient = 2,

    /// <summary>The ERP refused the invoice (4xx): another try would get the same answer.</summary>
    Rejected = 3,
}

/// <param name="Reason">Short and safe to store (at most 200 characters); never a whole ERP answer.</param>
public sealed record ErpResult(ErpOutcome Outcome, string? DocumentNumber = null, string? Reason = null);

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
}

public sealed record PostingRun(int Posted, int Retried, int Failed);

public interface IInvoicePosting
{
    Task<PostingRun> ProcessDueAsync(int maxJobs, CancellationToken cancellationToken);
}

/// <summary>
/// Sends due posting jobs to the ERP. Posted: the invoice becomes Posted with the ERP's document number. Temporary
/// failure: try again after the policy's wait. Rejected, or the last try failed: the invoice becomes Failed with the
/// reason (a technical failure; an admin can re-queue it). Each job's invoice changes are saved with the job.
/// </summary>
public sealed class InvoicePosting(IPostingStore store, IErpConnector erp, RetryPolicy policy, TimeProvider time) : IInvoicePosting
{
    public const string Actor = "erp-connector";
    private const int MaxReason = 200;
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    public async Task<PostingRun> ProcessDueAsync(int maxJobs, CancellationToken cancellationToken)
    {
        int posted = 0, retried = 0, failed = 0;
        foreach (var job in await store.ClaimDueAsync(time.GetUtcNow(), Lease, maxJobs, cancellationToken).ConfigureAwait(false))
        {
            switch (await PostAsync(job, cancellationToken).ConfigureAwait(false))
            {
                case ErpOutcome.Posted:
                    posted++;
                    break;
                case ErpOutcome.Transient:
                    retried++;
                    break;
                default:
                    failed++;
                    break;
            }
        }

        return new PostingRun(posted, retried, failed);
    }

    private async Task<ErpOutcome?> PostAsync(ClaimedJob job, CancellationToken cancellationToken)
    {
        var attempts = job.Attempts + 1;
        var found = await store.FindInvoiceAsync(job.InvoiceId, cancellationToken).ConfigureAwait(false);
        if (found is not { VendorAccount: { } account } || found.Value.Invoice.Status != InvoiceStatus.Approved)
        {
            // Changed since it was queued (for example re-matched or already posted): nothing to send.
            await store.GiveUpAsync(job.JobId, attempts, "The invoice is no longer approved, or its vendor is gone.", time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            return null;
        }

        var invoice = found.Value.Invoice;
        var result = await erp.PostAsync(invoice.Id, ErpPayload.From(invoice, account), cancellationToken).ConfigureAwait(false);
        var now = time.GetUtcNow();
        switch (result.Outcome)
        {
            case ErpOutcome.Posted:
                invoice.TransitionTo(InvoiceStatus.Posted, Actor, now, Cut($"Posted to the ERP as {result.DocumentNumber}."));
                await store.CompleteAsync(job.JobId, attempts, result.DocumentNumber!, now, cancellationToken).ConfigureAwait(false);
                break;
            case ErpOutcome.Transient when policy.WaitAfter(attempts) is { } wait:
                await store.RetryLaterAsync(job.JobId, attempts, now + wait, Cut(result.Reason ?? "Temporary failure."), cancellationToken).ConfigureAwait(false);
                break;
            case ErpOutcome.Transient:
                invoice.TransitionTo(InvoiceStatus.Failed, Actor, now, Cut($"ERP unavailable after {attempts} attempts: {result.Reason}"));
                await store.GiveUpAsync(job.JobId, attempts, Cut(result.Reason ?? "Temporary failure."), now, cancellationToken).ConfigureAwait(false);
                break;
            default:
                invoice.TransitionTo(InvoiceStatus.Failed, Actor, now, Cut($"Rejected by the ERP: {result.Reason}"));
                await store.GiveUpAsync(job.JobId, attempts, Cut(result.Reason ?? "Rejected."), now, cancellationToken).ConfigureAwait(false);
                break;
        }

        return result.Outcome;
    }

    private static string Cut(string text) => text.Length <= MaxReason ? text : text[..MaxReason];
}
