using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace EInvoice.Application.Audit;

/// <summary>What happened. Stable codes: they are part of every event's hash, so never rename one.</summary>
public static class AuditActions
{
    public const string InvoiceStatusChanged = nameof(InvoiceStatusChanged);
    public const string SignedIn = nameof(SignedIn);
    public const string SignedOut = nameof(SignedOut);
    public const string SignInFailed = nameof(SignInFailed);
    public const string SignInLockedOut = nameof(SignInLockedOut);
    public const string BankAccountProposed = nameof(BankAccountProposed);
    public const string BankAccountConfirmed = nameof(BankAccountConfirmed);
    public const string BankAccountRemoved = nameof(BankAccountRemoved);
    public const string PurchaseOrderCreated = nameof(PurchaseOrderCreated);
    public const string GoodsReceiptRecorded = nameof(GoodsReceiptRecorded);
}

/// <summary>An event to append: the chain gives it its sequence number and hashes.</summary>
/// <param name="Subject">What it is about, for example "Invoice/{id}" or "User/{name}".</param>
/// <param name="Details">A small JSON object; never document content, never a password.</param>
public sealed record AuditDraft(DateTimeOffset At, string Actor, string Action, string Subject, string Details);

/// <summary>An event as stored: its place in the chain, its content, the previous event's hash and its own.</summary>
public sealed record AuditEntry(long Sequence, DateTimeOffset At, string Actor, string Action, string Subject, string Details, byte[] PreviousHash, byte[] Hash);

/// <summary>An event as the auditor's page shows it; the hash as lowercase hex.</summary>
public sealed record AuditEventView(long Sequence, DateTimeOffset At, string Actor, string Action, string Subject, string Details, string Hash);

public sealed record AuditEventPage(IReadOnlyList<AuditEventView> Items, int Page, int PageSize, int TotalCount);

public enum AuditProblem
{
    /// <summary>An event is missing: the sequence numbers do not continue.</summary>
    SequenceGap = 1,

    /// <summary>The event does not point to the hash of the event before it (or, for the first, to the genesis hash).</summary>
    ChainBroken = 2,

    /// <summary>The event's content does not produce its stored hash: it was changed after it was written.</summary>
    HashMismatch = 3,
}

/// <param name="Count">Events confirmed: all of them when intact, otherwise those before the first problem.</param>
/// <param name="LastHash">The last confirmed event's hash (hex); worth noting down outside the system.</param>
public sealed record AuditVerification(bool IsIntact, long Count, long? LastSequence, string? LastHash, long? BrokenAt, AuditProblem? Problem);

/// <summary>The append-only audit trail. The application can add events and read them; it cannot change or delete them.</summary>
public interface IAuditLog
{
    Task AppendAsync(AuditDraft draft, CancellationToken cancellationToken);

    /// <summary>Newest first.</summary>
    Task<AuditEventPage> ListAsync(int page, int pageSize, CancellationToken cancellationToken);

    /// <summary>Every event in chain order, streamed.</summary>
    IAsyncEnumerable<AuditEntry> ReadInOrderAsync(CancellationToken cancellationToken);

    Task<AuditVerification> VerifyAsync(CancellationToken cancellationToken);
}

/// <summary>
/// SHA-256 over every field of an event and the previous event's hash. Each field is written with its length first,
/// so text cannot move from one field to the next without changing the hash. Times are hashed in UTC.
/// </summary>
public static class AuditHash
{
    /// <summary>What the first event points to.</summary>
    public static byte[] Genesis => new byte[32];

    public static byte[] Compute(long sequence, DateTimeOffset at, string actor, string action, string subject, string details, byte[] previousHash)
    {
        ArgumentNullException.ThrowIfNull(previousHash);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var field in new[]
                 {
                     sequence.ToString(CultureInfo.InvariantCulture),
                     at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture),
                     actor,
                     action,
                     subject,
                     details,
                     Convert.ToHexStringLower(previousHash),
                 })
        {
            var bytes = Encoding.UTF8.GetBytes(field ?? string.Empty);
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
        }

        return hash.GetHashAndReset();
    }
}

/// <summary>
/// Reads the trail in order and stops at the first event that is missing, does not point to its predecessor, or was
/// changed. A chain cannot show that its newest events were cut off: compare the count and last hash with a copy
/// noted down earlier.
/// </summary>
public static class AuditVerifier
{
    public static async Task<AuditVerification> VerifyAsync(IAsyncEnumerable<AuditEntry> entries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var expected = 1L;
        var previous = AuditHash.Genesis;
        long count = 0;
        long? last = null;
        await foreach (var entry in entries.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var problem =
                entry.Sequence != expected ? AuditProblem.SequenceGap
                : !entry.PreviousHash.AsSpan().SequenceEqual(previous) ? AuditProblem.ChainBroken
                : !AuditHash.Compute(entry.Sequence, entry.At, entry.Actor, entry.Action, entry.Subject, entry.Details, entry.PreviousHash).AsSpan().SequenceEqual(entry.Hash)
                    ? AuditProblem.HashMismatch
                    : (AuditProblem?)null;
            if (problem is { } found)
            {
                return new AuditVerification(false, count, last, last is null ? null : Convert.ToHexStringLower(previous), entry.Sequence, found);
            }

            count++;
            last = entry.Sequence;
            previous = entry.Hash;
            expected++;
        }

        return new AuditVerification(true, count, last, last is null ? null : Convert.ToHexStringLower(previous), null, null);
    }
}
