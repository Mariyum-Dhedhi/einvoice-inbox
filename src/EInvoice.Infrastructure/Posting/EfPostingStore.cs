using EInvoice.Application.Posting;
using EInvoice.Domain.Invoices;
using EInvoice.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EInvoice.Infrastructure.Posting;

/// <summary>
/// Posting jobs in SQL Server. Claiming is one statement: due jobs are given a lease with UPDLOCK and READPAST, so two
/// workers (or two application instances) never take the same job; a crashed worker's lease simply runs out.
/// </summary>
public sealed class EfPostingStore(EInvoiceDbContext db) : IPostingStore
{
    public async Task<IReadOnlyList<ClaimedJob>> ClaimDueAsync(DateTimeOffset now, TimeSpan lease, int max, CancellationToken cancellationToken)
    {
        var leaseUntil = now + lease;
        var pending = (int)PostingJobStatus.Pending;

        // Parameterised by EF (interpolated values become parameters). Oldest due first.
        return await db.Database.SqlQuery<ClaimedJob>($"""
            WITH due AS (
                SELECT TOP ({max}) Id, InvoiceId, Attempts, LeaseUntil
                FROM dbo.PostingJob WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE Status = {pending} AND NextAttemptAt <= {now} AND (LeaseUntil IS NULL OR LeaseUntil < {now})
                ORDER BY NextAttemptAt, RowId)
            UPDATE due SET LeaseUntil = {leaseUntil}
            OUTPUT inserted.Id AS JobId, inserted.InvoiceId, inserted.Attempts;
            """).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<(Invoice Invoice, string? VendorAccount)?> FindInvoiceAsync(Guid invoiceId, CancellationToken cancellationToken)
    {
        var invoice = await db.Invoices.SingleOrDefaultAsync(i => i.Id == invoiceId, cancellationToken).ConfigureAwait(false);
        if (invoice is null)
        {
            return null;
        }

        var account = invoice.VendorId is { } vendorId
            ? await db.Vendors.Where(v => v.Id == vendorId).Select(v => v.Number).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            : null;
        return (invoice, account);
    }

    public Task CompleteAsync(Guid jobId, int attempts, string documentNumber, DateTimeOffset at, CancellationToken cancellationToken) =>
        SaveAsync(jobId, job =>
        {
            job.Status = PostingJobStatus.Done;
            job.Attempts = attempts;
            job.ErpDocumentNumber = documentNumber;
            job.ProcessedAt = at;
            job.LastError = null;
        }, cancellationToken);

    public Task RetryLaterAsync(Guid jobId, int attempts, DateTimeOffset next, string reason, CancellationToken cancellationToken) =>
        SaveAsync(jobId, job =>
        {
            job.Attempts = attempts;
            job.NextAttemptAt = next;
            job.LastError = reason;
        }, cancellationToken);

    public Task GiveUpAsync(Guid jobId, int attempts, string reason, DateTimeOffset at, CancellationToken cancellationToken) =>
        SaveAsync(jobId, job =>
        {
            job.Status = PostingJobStatus.Dead;
            job.Attempts = attempts;
            job.LastError = reason;
            job.ProcessedAt = at;
        }, cancellationToken);

    public Task ReleaseAsync(Guid jobId, CancellationToken cancellationToken) => SaveAsync(jobId, _ => { }, cancellationToken);

    /// <summary>The job and its invoice's status change (tracked in this context) in one save; the lease ends.</summary>
    private async Task SaveAsync(Guid jobId, Action<PostingJobRecord> change, CancellationToken cancellationToken)
    {
        var job = await db.PostingJobs.SingleAsync(j => j.Id == jobId, cancellationToken).ConfigureAwait(false);
        change(job);
        job.LeaseUntil = null;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
