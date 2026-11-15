using EInvoice.Application.Audit;
using EInvoice.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EInvoice.Infrastructure.Audit;

/// <summary>
/// Appends events to the chain: takes an exclusive application lock for the transaction, reads the last event, and
/// links each new event to it. One writer at a time, so the chain never forks; the unique sequence backs that up.
/// Runs inside the caller's transaction (so a rolled-back change leaves no event) or its own.
/// </summary>
internal static class AuditChain
{
    // Fixed text, no values: nothing to parameterise. A refused lock stops the save.
    private const string TakeLock = """
        DECLARE @granted int;
        EXEC @granted = sp_getapplock @Resource = N'EInvoice.AuditChain', @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 30000;
        IF @granted < 0 THROW 50001, N'The audit chain lock was not granted.', 1;
        """;

    public static async Task AppendAsync(EInvoiceDbContext db, IReadOnlyList<AuditDraft> drafts, CancellationToken cancellationToken)
    {
        if (drafts.Count == 0)
        {
            return;
        }

        var own = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false) : null;
        try
        {
            await db.Database.ExecuteSqlRawAsync(TakeLock, cancellationToken).ConfigureAwait(false);
            var last = await db.AuditEvents.AsNoTracking()
                .OrderByDescending(e => e.Sequence)
                .Select(e => new { e.Sequence, e.Hash })
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            var sequence = last?.Sequence ?? 0;
            var previous = last?.Hash ?? AuditHash.Genesis;
            foreach (var draft in drafts)
            {
                sequence++;
                var at = draft.At.ToUniversalTime();
                var hash = AuditHash.Compute(sequence, at, draft.Actor, draft.Action, draft.Subject, draft.Details, previous);
                db.AuditEvents.Add(new AuditEventRecord
                {
                    Sequence = sequence, At = at, Actor = draft.Actor, Action = draft.Action, Subject = draft.Subject,
                    Details = draft.Details, PreviousHash = previous, Hash = hash,
                });
                previous = hash;
            }

            await db.SaveWithoutAuditAsync(cancellationToken).ConfigureAwait(false);
            if (own is not null)
            {
                await own.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (own is not null)
            {
                await own.DisposeAsync().ConfigureAwait(false); // not committed means rolled back
            }
        }
    }
}
