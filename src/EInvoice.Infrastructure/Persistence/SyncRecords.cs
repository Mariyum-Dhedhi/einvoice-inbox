using EInvoice.Application.Sync;

namespace EInvoice.Infrastructure.Persistence;

/// <summary>How far the sync has read one entity from the ERP: the latest change time of its last complete run.</summary>
public sealed class SyncWatermarkRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public SyncEntity Entity { get; init; }

    public DateTimeOffset Value { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A record the sync could not take yet (its vendor or order was not here): tried again on every run until it works.</summary>
public sealed class SyncFailureRecord
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public SyncEntity Entity { get; init; }

    public string ErpId { get; init; } = string.Empty;

    public string Reason { get; set; } = string.Empty;

    public DateTimeOffset FirstSeen { get; init; }

    public DateTimeOffset LastSeen { get; set; }

    public int Tries { get; set; }
}
