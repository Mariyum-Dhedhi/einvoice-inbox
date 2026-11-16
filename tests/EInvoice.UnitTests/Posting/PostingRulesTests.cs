using EInvoice.Application.Posting;
using EInvoice.Domain.Invoices;
using EInvoice.UnitTests.Domain;

namespace EInvoice.UnitTests.Posting;

/// <summary>
/// The retry policy and the posting payload, without a database or a network: how long to wait after each failed try,
/// when to give up, and exactly what an approved invoice looks like to the ERP.
/// </summary>
public sealed class PostingRulesTests
{
    private static readonly RetryPolicy Default = RetryPolicy.Default;

    [Fact]
    public void Six_tries_with_growing_waits_by_default()
    {
        Assert.Equal(6, Default.MaxAttempts);
        Assert.Equal(
            new[] { TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1) },
            Enumerable.Range(1, 5).Select(failed => Default.WaitAfter(failed)!.Value));
    }

    [Fact]
    public void After_the_last_try_there_is_no_further_wait()
    {
        Assert.Null(Default.WaitAfter(6)); // give up: the invoice becomes Failed
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_wait_before_the_first_failure_makes_no_sense(int failedAttempts)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Default.WaitAfter(failedAttempts));
    }

    [Fact]
    public void A_configured_policy_tries_one_more_time_than_it_has_waits()
    {
        var policy = new RetryPolicy([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)]);

        Assert.Equal(3, policy.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(2), policy.WaitAfter(2));
        Assert.Null(policy.WaitAfter(3));
    }

    [Fact]
    public void The_payload_carries_every_line_and_the_totals_as_approved()
    {
        var invoice = TestInvoices.InStatus(InvoiceStatus.Approved,
            [InvoiceStatus.Parsed, InvoiceStatus.Validated, InvoiceStatus.Matched, InvoiceStatus.Approved]);

        var payload = ErpPayload.From(invoice, vendorAccount: "V-1001");

        Assert.Equal(("RE-2026-0001", "V-1001", new DateOnly(2026, 11, 2), "EUR"), (payload.InvoiceNumber, payload.VendorAccount, payload.InvoiceDate, payload.Currency));
        Assert.Equal((100.00m, 19.00m, 119.00m), (payload.NetAmount, payload.TaxAmount, payload.TotalAmount));
        Assert.Equal("PO-1001", payload.PurchaseOrder);
        var line = Assert.Single(payload.Lines);
        Assert.Equal((1, "Office chair", 2m, 50.00m, 100.00m, 19m, (int?)1),
            (line.LineNumber, line.Description, line.Quantity, line.UnitPrice, line.Amount, line.TaxRatePercent, line.PurchaseOrderLine));
    }

    [Theory]
    [InlineData(InvoiceStatus.Matched)]
    [InlineData(InvoiceStatus.Exception)]
    [InlineData(InvoiceStatus.Rejected)]
    public void Only_an_approved_invoice_is_posted(InvoiceStatus status)
    {
        var invoice = status switch
        {
            InvoiceStatus.Matched => TestInvoices.InStatus(status, [InvoiceStatus.Parsed, InvoiceStatus.Validated, InvoiceStatus.Matched]),
            InvoiceStatus.Exception => TestInvoices.InStatus(status, [InvoiceStatus.Parsed, InvoiceStatus.Exception]),
            _ => TestInvoices.InStatus(status, [InvoiceStatus.Parsed, InvoiceStatus.Exception, InvoiceStatus.Rejected]),
        };

        Assert.Throws<InvalidOperationException>(() => ErpPayload.From(invoice, "V-1001"));
    }
}
