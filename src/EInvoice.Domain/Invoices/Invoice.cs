using EInvoice.Domain.Shared;

namespace EInvoice.Domain.Invoices;

/// <summary>
/// A supplier invoice, normalised from any source format. Status can only change through
/// <see cref="MarkParsed"/> and <see cref="TransitionTo"/>, both of which check <see cref="InvoiceStateMachine"/>
/// and record a <see cref="StatusChange"/>.
/// </summary>
public sealed class Invoice
{
    private readonly List<InvoiceLine> _lines = [];
    private readonly List<StatusChange> _statusHistory = [];

    private Invoice(SourceFormat sourceFormat, string submittedBy)
    {
        SourceFormat = sourceFormat;
        SubmittedBy = submittedBy;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public SourceFormat SourceFormat { get; }

    /// <summary>Who uploaded the invoice. Used later to stop people approving their own invoices.</summary>
    public string SubmittedBy { get; }

    public InvoiceStatus Status { get; private set; }

    public string? InvoiceNumber { get; private set; }

    /// <summary>Null when the seller on the invoice matches no known vendor; validation turns that into an exception.</summary>
    public Guid? VendorId { get; private set; }

    public DateOnly? IssueDate { get; private set; }

    public string? PurchaseOrderNumber { get; private set; }

    /// <summary>Totals as stated by the supplier. Validation recomputes them; they are never trusted as is.</summary>
    public Money? NetTotal { get; private set; }

    public Money? TaxTotal { get; private set; }

    public Money? GrossTotal { get; private set; }

    public IReadOnlyList<InvoiceLine> Lines => _lines.AsReadOnly();

    public IReadOnlyList<StatusChange> StatusHistory => _statusHistory.AsReadOnly();

    /// <summary>Creates an invoice for a newly uploaded file. Its content is not known until it is parsed.</summary>
    public static Invoice Receive(SourceFormat sourceFormat, string submittedBy, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(submittedBy);

        var invoice = new Invoice(sourceFormat, submittedBy) { Status = InvoiceStatus.Received };
        invoice._statusHistory.Add(new StatusChange(null, InvoiceStatus.Received, submittedBy, at, null));
        return invoice;
    }

    /// <summary>The only way into <see cref="InvoiceStatus.Parsed"/>: stores the parsed content and changes status in one step.</summary>
    public void MarkParsed(ParsedInvoice content, string actor, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        InvoiceStateMachine.EnsureAllowed(Status, InvoiceStatus.Parsed);
        content.EnsureComplete();

        InvoiceNumber = content.InvoiceNumber;
        VendorId = content.VendorId;
        IssueDate = content.IssueDate;
        PurchaseOrderNumber = content.PurchaseOrderNumber;
        NetTotal = content.NetTotal;
        TaxTotal = content.TaxTotal;
        GrossTotal = content.GrossTotal;
        _lines.Clear();
        _lines.AddRange(content.Lines);

        Record(InvoiceStatus.Parsed, actor, at, reason: null);
    }

    /// <summary>Moves the invoice to a new status, or throws <see cref="InvalidStatusTransitionException"/> if that change is not allowed.</summary>
    /// <param name="reason">Why the change happened, for example the exception reason code.</param>
    public void TransitionTo(InvoiceStatus to, string actor, DateTimeOffset at, string? reason = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        if (to == InvoiceStatus.Parsed)
        {
            throw new InvalidOperationException("Use MarkParsed to move an invoice to Parsed, so its content is stored at the same time.");
        }

        InvoiceStateMachine.EnsureAllowed(Status, to);
        Record(to, actor, at, reason);
    }

    private void Record(InvoiceStatus to, string actor, DateTimeOffset at, string? reason)
    {
        _statusHistory.Add(new StatusChange(Status, to, actor, at, reason));
        Status = to;
    }
}
