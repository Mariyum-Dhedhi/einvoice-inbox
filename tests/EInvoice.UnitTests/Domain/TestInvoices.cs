using EInvoice.Domain.Invoices;
using EInvoice.Domain.Shared;

namespace EInvoice.UnitTests.Domain;

/// <summary>Builders for synthetic test invoices.</summary>
internal static class TestInvoices
{
    public static readonly DateTimeOffset Now = new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);

    public static Invoice Received(string submittedBy = "clerk@test") =>
        Invoice.Receive(SourceFormat.XRechnung, submittedBy, Now);

    public static ParsedInvoice SampleContent() => new(
        InvoiceNumber: "RE-2026-0001",
        VendorId: Guid.Parse("11111111-1111-1111-1111-111111111111"),
        IssueDate: new DateOnly(2026, 11, 2),
        PurchaseOrderNumber: "PO-1001",
        NetTotal: Money.Euro(100.00m),
        TaxTotal: Money.Euro(19.00m),
        GrossTotal: Money.Euro(119.00m),
        Lines:
        [
            new InvoiceLine(1, "Office chair", 2m, 50.00m, Money.Euro(100.00m), 19m, purchaseOrderLineNumber: 1),
        ]);

    /// <summary>Walks a legal path from Received; parsing happens through MarkParsed, the rest through TransitionTo.</summary>
    public static Invoice InStatus(InvoiceStatus target, IEnumerable<InvoiceStatus> path)
    {
        var invoice = Received();
        var at = Now;
        foreach (var step in path)
        {
            at = at.AddMinutes(1);
            if (step == InvoiceStatus.Parsed)
            {
                invoice.MarkParsed(SampleContent(), "system", at);
            }
            else
            {
                invoice.TransitionTo(step, "system", at);
            }
        }

        if (invoice.Status != target)
        {
            throw new InvalidOperationException($"Test path ended in {invoice.Status}, expected {target}.");
        }

        return invoice;
    }
}
