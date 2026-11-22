namespace EInvoice.Domain.Shared;

/// <summary>
/// Where master data comes from. Stored as numbers: never renumber or reuse a value.
/// Records made in the app stay as they are (add only); records from the ERP follow the ERP, changed only by the sync.
/// </summary>
public enum RecordSource
{
    /// <summary>Recorded in this app (by the buyer, the admin or the seed).</summary>
    Local = 1,

    /// <summary>Read from the ERP by the sync, with the ERP's id.</summary>
    Erp = 2,
}
