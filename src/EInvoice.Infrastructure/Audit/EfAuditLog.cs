using System.Runtime.CompilerServices;
using EInvoice.Application.Audit;
using EInvoice.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EInvoice.Infrastructure.Audit;

public sealed class EfAuditLog(EInvoiceDbContext db) : IAuditLog
{
    public Task AppendAsync(AuditDraft draft, CancellationToken cancellationToken) =>
        AuditChain.AppendAsync(db, [draft], cancellationToken);

    public async Task<AuditEventPage> ListAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        var total = await db.AuditEvents.CountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.AuditEvents.AsNoTracking()
            .OrderByDescending(e => e.Sequence)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return new AuditEventPage(
            rows.Select(e => new AuditEventView(e.Sequence, e.At, e.Actor, e.Action, e.Subject, e.Details, Convert.ToHexStringLower(e.Hash))).ToList(),
            page, pageSize, total);
    }

    public async IAsyncEnumerable<AuditEntry> ReadInOrderAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var rows = db.AuditEvents.AsNoTracking().OrderBy(e => e.Sequence).AsAsyncEnumerable();
        await foreach (var e in rows.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            yield return new AuditEntry(e.Sequence, e.At, e.Actor, e.Action, e.Subject, e.Details, e.PreviousHash, e.Hash);
        }
    }

    public Task<AuditVerification> VerifyAsync(CancellationToken cancellationToken) =>
        AuditVerifier.VerifyAsync(ReadInOrderAsync(cancellationToken), cancellationToken);
}
