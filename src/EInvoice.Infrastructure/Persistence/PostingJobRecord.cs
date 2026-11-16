using EInvoice.Application.Posting;

namespace EInvoice.Infrastructure.Persistence;

/// <summary>
/// The transactional outbox for ERP postings: written in the same save as the approval (so no approval is lost
/// between database and ERP), sent by the posting worker, retried after waits, kept as a record afterwards.
/// </summary>
public sealed class PostingJobRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid InvoiceId { get; init; }

    public PostingJobStatus Status { get; set; } = PostingJobStatus.Pending;

    public int Attempts { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>Not before this time; the approval time for a new job, later after a failure.</summary>
    public DateTimeOffset NextAttemptAt { get; set; }

    /// <summary>A worker owns the job until then; another worker skips it.</summary>
    public DateTimeOffset? LeaseUntil { get; set; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public string? LastError { get; set; }

    public string? ErpDocumentNumber { get; set; }
}
