namespace EInvoice.Infrastructure.Persistence;

/// <summary>
/// One row of the hash-chained audit trail. Written only by <see cref="EInvoice.Infrastructure.Audit.AuditChain"/>;
/// the application's database account may insert and read, never update or delete (db/security/app-user.sql).
/// </summary>
public sealed class AuditEventRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>1, 2, 3 ... without gaps; unique, so two writers can never both append the same place.</summary>
    public long Sequence { get; init; }

    public DateTimeOffset At { get; init; }

    public string Actor { get; init; } = string.Empty;

    public string Action { get; init; } = string.Empty;

    public string Subject { get; init; } = string.Empty;

    public string Details { get; init; } = string.Empty;

    public byte[] PreviousHash { get; init; } = [];

    public byte[] Hash { get; init; } = [];
}
