using EInvoice.Domain.Invoices;

namespace EInvoice.UnitTests.Domain;

/// <summary>
/// State transition testing: every one of the 9 x 9 status pairs is checked.
/// The expected table is written out here on purpose, independent of the production table,
/// so a wrong rule in production cannot pass its own test.
/// </summary>
public class InvoiceStateMachineTests
{
    private static readonly Dictionary<InvoiceStatus, InvoiceStatus[]> ExpectedLegal = new()
    {
        [InvoiceStatus.Received] = [InvoiceStatus.Parsed, InvoiceStatus.Failed],
        [InvoiceStatus.Parsed] = [InvoiceStatus.Validated, InvoiceStatus.Exception],
        [InvoiceStatus.Validated] = [InvoiceStatus.Matched, InvoiceStatus.Exception],
        [InvoiceStatus.Matched] = [InvoiceStatus.Approved, InvoiceStatus.Exception],
        [InvoiceStatus.Exception] = [InvoiceStatus.Approved, InvoiceStatus.Rejected],
        [InvoiceStatus.Approved] = [InvoiceStatus.Posted, InvoiceStatus.Failed],
        [InvoiceStatus.Failed] = [InvoiceStatus.Approved],
        [InvoiceStatus.Rejected] = [],
        [InvoiceStatus.Posted] = [],
    };

    // A legal path from Received to each status, used to put an invoice into any starting state.
    private static readonly Dictionary<InvoiceStatus, InvoiceStatus[]> PathTo = new()
    {
        [InvoiceStatus.Received] = [],
        [InvoiceStatus.Parsed] = [InvoiceStatus.Parsed],
        [InvoiceStatus.Validated] = [InvoiceStatus.Parsed, InvoiceStatus.Validated],
        [InvoiceStatus.Matched] = [InvoiceStatus.Parsed, InvoiceStatus.Validated, InvoiceStatus.Matched],
        [InvoiceStatus.Exception] = [InvoiceStatus.Parsed, InvoiceStatus.Exception],
        [InvoiceStatus.Approved] = [InvoiceStatus.Parsed, InvoiceStatus.Validated, InvoiceStatus.Matched, InvoiceStatus.Approved],
        [InvoiceStatus.Rejected] = [InvoiceStatus.Parsed, InvoiceStatus.Exception, InvoiceStatus.Rejected],
        [InvoiceStatus.Posted] = [InvoiceStatus.Parsed, InvoiceStatus.Validated, InvoiceStatus.Matched, InvoiceStatus.Approved, InvoiceStatus.Posted],
        [InvoiceStatus.Failed] = [InvoiceStatus.Failed],
    };

    public static TheoryData<InvoiceStatus, InvoiceStatus, bool> AllPairs()
    {
        var data = new TheoryData<InvoiceStatus, InvoiceStatus, bool>();
        foreach (var from in Enum.GetValues<InvoiceStatus>())
        {
            foreach (var to in Enum.GetValues<InvoiceStatus>())
            {
                data.Add(from, to, ExpectedLegal[from].Contains(to));
            }
        }

        return data;
    }

    [Fact]
    public void Matrix_covers_all_81_pairs()
    {
        Assert.Equal(9, Enum.GetValues<InvoiceStatus>().Length);
        Assert.Equal(81, AllPairs().Count);
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void IsAllowed_matches_the_expected_table(InvoiceStatus from, InvoiceStatus to, bool legal)
    {
        Assert.Equal(legal, InvoiceStateMachine.IsAllowed(from, to));
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void Invoice_transition_succeeds_only_when_legal(InvoiceStatus from, InvoiceStatus to, bool legal)
    {
        var invoice = TestInvoices.InStatus(from, PathTo[from]);
        var at = TestInvoices.Now.AddMinutes(10);

        if (legal)
        {
            Apply(invoice, to, at);

            Assert.Equal(to, invoice.Status);
            var last = invoice.StatusHistory[^1];
            Assert.Equal(from, last.From);
            Assert.Equal(to, last.To);
            Assert.Equal("approver@test", last.Actor);
            Assert.Equal(at, last.At);
        }
        else
        {
            var historyBefore = invoice.StatusHistory.Count;

            var ex = Assert.Throws<InvalidStatusTransitionException>(() => Apply(invoice, to, at));

            Assert.Equal(from, ex.From);
            Assert.Equal(to, ex.To);
            Assert.Equal(from, invoice.Status);
            Assert.Equal(historyBefore, invoice.StatusHistory.Count);
        }
    }

    [Fact]
    public void Received_cannot_jump_straight_to_Posted()
    {
        var invoice = TestInvoices.Received();

        Assert.Throws<InvalidStatusTransitionException>(
            () => invoice.TransitionTo(InvoiceStatus.Posted, "clerk@test", TestInvoices.Now));
    }

    [Fact]
    public void Parsed_can_only_be_reached_through_MarkParsed_so_an_invoice_is_never_Parsed_without_content()
    {
        var invoice = TestInvoices.Received();

        Assert.Throws<InvalidOperationException>(
            () => invoice.TransitionTo(InvoiceStatus.Parsed, "system", TestInvoices.Now));
        Assert.Equal(InvoiceStatus.Received, invoice.Status);
    }

    // Parsed is entered only through MarkParsed (it carries the invoice content); every other status through TransitionTo.
    private static void Apply(Invoice invoice, InvoiceStatus to, DateTimeOffset at)
    {
        if (to == InvoiceStatus.Parsed)
        {
            invoice.MarkParsed(TestInvoices.SampleContent(), "approver@test", at);
        }
        else
        {
            invoice.TransitionTo(to, "approver@test", at);
        }
    }
}
