using EInvoice.Application.Validation;

namespace EInvoice.Infrastructure.Persistence;

/// <summary>One stored validation finding of an invoice: why it is in the exception queue.</summary>
public sealed class InvoiceFindingRecord
{
    public Guid InvoiceId { get; init; }

    /// <summary>1, 2, 3 ... in the order the validator reported them (blocking first).</summary>
    public int Sequence { get; init; }

    public string RuleCode { get; init; } = string.Empty;

    public FindingSeverity Severity { get; init; }

    public string Message { get; init; } = string.Empty;

    public int? LinePosition { get; init; }
}
