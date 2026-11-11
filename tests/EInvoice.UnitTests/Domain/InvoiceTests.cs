using EInvoice.Domain.Invoices;

namespace EInvoice.UnitTests.Domain;

public class InvoiceTests
{
    [Fact]
    public void Receive_starts_in_Received_and_records_the_first_history_entry()
    {
        var invoice = TestInvoices.Received("clerk@test");

        Assert.Equal(InvoiceStatus.Received, invoice.Status);
        Assert.Equal("clerk@test", invoice.SubmittedBy);
        var first = Assert.Single(invoice.StatusHistory);
        Assert.Null(first.From);
        Assert.Equal(InvoiceStatus.Received, first.To);
        Assert.Equal("clerk@test", first.Actor);
        Assert.Equal(TestInvoices.Now, first.At);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Receive_requires_a_submitter(string submittedBy)
    {
        Assert.Throws<ArgumentException>(() => Invoice.Receive(SourceFormat.XRechnung, submittedBy, TestInvoices.Now));
    }

    [Fact]
    public void MarkParsed_sets_content_and_moves_to_Parsed()
    {
        var invoice = TestInvoices.Received();
        var content = TestInvoices.SampleContent();

        invoice.MarkParsed(content, "system", TestInvoices.Now.AddMinutes(1));

        Assert.Equal(InvoiceStatus.Parsed, invoice.Status);
        Assert.Equal("RE-2026-0001", invoice.InvoiceNumber);
        Assert.Equal(content.VendorId, invoice.VendorId);
        Assert.Equal(119.00m, invoice.GrossTotal!.Amount);
        Assert.Single(invoice.Lines);
    }

    [Fact]
    public void MarkParsed_twice_is_an_illegal_transition()
    {
        var invoice = TestInvoices.Received();
        invoice.MarkParsed(TestInvoices.SampleContent(), "system", TestInvoices.Now);

        Assert.Throws<InvalidStatusTransitionException>(
            () => invoice.MarkParsed(TestInvoices.SampleContent(), "system", TestInvoices.Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Transition_requires_an_actor(string actor)
    {
        var invoice = TestInvoices.Received();

        Assert.Throws<ArgumentException>(() => invoice.TransitionTo(InvoiceStatus.Failed, actor, TestInvoices.Now));
        Assert.Equal(InvoiceStatus.Received, invoice.Status);
    }

    [Fact]
    public void Transition_keeps_an_optional_reason()
    {
        var invoice = TestInvoices.InStatus(InvoiceStatus.Parsed, [InvoiceStatus.Parsed]);

        invoice.TransitionTo(InvoiceStatus.Exception, "system", TestInvoices.Now, reason: "NoPurchaseOrder");

        Assert.Equal("NoPurchaseOrder", invoice.StatusHistory[^1].Reason);
    }

    [Fact]
    public void Status_history_cannot_be_modified_from_outside()
    {
        var invoice = TestInvoices.Received();

        Assert.False(invoice.StatusHistory is List<StatusChange>);
    }

    [Fact]
    public void Parsed_invoice_must_have_at_least_one_line()
    {
        var invoice = TestInvoices.Received();
        var empty = TestInvoices.SampleContent() with { Lines = [] };

        Assert.Throws<ArgumentException>(() => invoice.MarkParsed(empty, "system", TestInvoices.Now));
        Assert.Equal(InvoiceStatus.Received, invoice.Status);
    }
}
