using EInvoice.Domain.Shared;

namespace EInvoice.Domain.Invoices;

/// <summary>
/// The internal invoice schema every parser produces, whatever the source format (XRechnung, ZUGFeRD, PDF).
/// </summary>
public sealed record ParsedInvoice(
    string InvoiceNumber,
    Guid? VendorId,
    DateOnly IssueDate,
    string? PurchaseOrderNumber,
    Money NetTotal,
    Money TaxTotal,
    Money GrossTotal,
    IReadOnlyList<InvoiceLine> Lines)
{
    internal void EnsureComplete()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(InvoiceNumber, nameof(InvoiceNumber));
        ArgumentNullException.ThrowIfNull(NetTotal);
        ArgumentNullException.ThrowIfNull(TaxTotal);
        ArgumentNullException.ThrowIfNull(GrossTotal);

        if (Lines is null || Lines.Count == 0)
        {
            throw new ArgumentException("A parsed invoice must have at least one line.", nameof(Lines));
        }

        if (Lines.Select(l => l.LineNumber).Distinct().Count() != Lines.Count)
        {
            throw new ArgumentException("Invoice line numbers must be unique.", nameof(Lines));
        }

        // One currency per invoice: multi-currency is out of scope.
        var currency = GrossTotal.Currency;
        if (NetTotal.Currency != currency || TaxTotal.Currency != currency)
        {
            throw new CurrencyMismatchException(NetTotal.Currency, currency);
        }

        var foreign = Lines.FirstOrDefault(l => l.NetAmount.Currency != currency);
        if (foreign is not null)
        {
            throw new CurrencyMismatchException(foreign.NetAmount.Currency, currency);
        }
    }
}

/// <summary>One invoice line. Quantity and unit price are plain decimal; only the line amount is Money.</summary>
public sealed record InvoiceLine
{
    public InvoiceLine(
        int lineNumber,
        string description,
        decimal quantity,
        decimal unitPrice,
        Money netAmount,
        decimal taxRatePercent,
        int? purchaseOrderLineNumber = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(lineNumber);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ArgumentNullException.ThrowIfNull(netAmount);
        ArgumentOutOfRangeException.ThrowIfNegative(taxRatePercent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(taxRatePercent, 100m);

        LineNumber = lineNumber;
        Description = description;
        Quantity = quantity;
        UnitPrice = unitPrice;
        NetAmount = netAmount;
        TaxRatePercent = taxRatePercent;
        PurchaseOrderLineNumber = purchaseOrderLineNumber;
    }

    public int LineNumber { get; }

    public string Description { get; }

    public decimal Quantity { get; }

    public decimal UnitPrice { get; }

    public Money NetAmount { get; }

    public decimal TaxRatePercent { get; }

    /// <summary>The purchase order line this invoice line refers to, if the supplier stated it.</summary>
    public int? PurchaseOrderLineNumber { get; }
}
