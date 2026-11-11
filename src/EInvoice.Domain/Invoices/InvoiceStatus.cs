namespace EInvoice.Domain.Invoices;

/// <summary>
/// Lifecycle of an invoice. The numbers are fixed because they are stored in the database:
/// never renumber or reuse a value.
/// </summary>
public enum InvoiceStatus
{
    Received = 1,
    Parsed = 2,
    Validated = 3,
    Matched = 4,
    Exception = 5,
    Approved = 6,
    Rejected = 7,
    Posted = 8,

    /// <summary>Technical failure: the file could not be read, or posting failed after all retries. Never a business problem.</summary>
    Failed = 9,
}

public enum SourceFormat
{
    XRechnung = 1,
    Zugferd = 2,
    Pdf = 3,
}
