using EInvoice.Application.Matching;
using EInvoice.Application.Queries;
using EInvoice.Application.Validation;
using EInvoice.Domain.Invoices;
using EInvoice.Domain.Matching;
using EInvoice.Web.Components.Pages;
using EInvoice.Web.Components.Shared;

namespace EInvoice.ComponentTests;

/// <summary>
/// One invoice: what it says, why it is where it is (findings, match panel, history), the original file, and re-match.
/// </summary>
public sealed class InvoiceDetailTests : UiTest
{
    private readonly Guid _id = Guid.NewGuid();

    private IRenderedComponent<InvoiceDetailPage> Show(InvoiceDetail detail)
    {
        Queries.Details[_id] = detail;
        return Render<InvoiceDetailPage>(p => p.Add(c => c.Id, _id));
    }

    [Fact]
    public void Unknown_invoice_says_so_and_links_back_to_the_inbox()
    {
        var cut = Render<InvoiceDetailPage>(p => p.Add(c => c.Id, Guid.NewGuid()));

        Assert.Contains("not found", cut.Find("[data-testid=not-found]").TextContent, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(cut.FindAll("[data-testid=not-found] a"), a => a.GetAttribute("href") == "/");
    }

    [Fact]
    public void Header_shows_number_vendor_order_status_and_totals()
    {
        var cut = Show(Views.Detail(_id));

        Assert.Equal("RE-2026-0001", cut.Find("h1").TextContent.Trim());
        Assert.Contains("Beispiel Bürobedarf GmbH", cut.Find("[data-testid=vendor]").TextContent, StringComparison.Ordinal);
        Assert.Contains("PO-0042", cut.Find("[data-testid=order]").TextContent, StringComparison.Ordinal);
        Assert.Equal("Matched", cut.Find("[data-testid=status]").GetAttribute("data-status"));
        Assert.Contains("1.234,56 €", cut.Find("[data-testid=gross]").TextContent, StringComparison.Ordinal);
        Assert.Contains("1.037,40 €", cut.Find("[data-testid=net]").TextContent, StringComparison.Ordinal);
        Assert.Contains("197,16 €", cut.Find("[data-testid=tax]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_vendor_is_named_as_such()
    {
        var cut = Show(Views.Detail(_id, InvoiceStatus.Exception, vendorName: null));

        Assert.Contains("Unknown vendor", cut.Find("[data-testid=vendor]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Lines_show_quantity_unit_price_with_all_decimals_and_order_line()
    {
        var cut = Show(Views.Detail(_id));

        var lines = cut.FindAll("[data-testid=line]");
        Assert.Equal(2, lines.Count);
        Assert.Contains("HDMI cable 2 m", lines[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("7,4125 €", lines[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("74,13 €", lines[0].TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Findings_show_code_severity_and_message()
    {
        var cut = Show(Views.Detail(_id, InvoiceStatus.Exception, findings:
        [
            Views.Finding(RuleCodes.VendorUnknown, FindingSeverity.Blocking, "The seller VAT id matches no vendor."),
            Views.Finding("POSSIBLE_DUPLICATE", FindingSeverity.Review, "1 other invoice(s) have the same amount and date."),
        ]));

        var findings = cut.FindAll("[data-testid=finding]");
        Assert.Equal(2, findings.Count);
        Assert.Contains("Blocking", findings[0].TextContent, StringComparison.Ordinal);
        Assert.Contains(RuleCodes.VendorUnknown, findings[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("The seller VAT id matches no vendor.", findings[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("Review", findings[1].TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Clean_invoice_has_no_findings_section()
    {
        var cut = Show(Views.Detail(_id));

        Assert.Empty(cut.FindAll("[data-testid=finding]"));
    }

    [Fact]
    public void Match_panel_compares_invoice_and_order_and_flags_problem_lines_in_words()
    {
        var cut = Show(Views.Detail(_id, InvoiceStatus.Exception, matches:
        [
            Views.Match(1, MatchOutcome.PriceVariance, invoicePrice: 7.70m, orderPrice: 7.4125m, variancePercent: 3.878583m),
            Views.Match(2, MatchOutcome.FullMatch),
        ]));

        var rows = cut.FindAll("[data-testid=match-row]");
        Assert.Equal("PriceVariance", rows[0].GetAttribute("data-outcome"));
        Assert.Contains(Labels.Outcome(MatchOutcome.PriceVariance), rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("7,70 €", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("7,4125 €", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("+3,88 %", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("2,00 %", cut.Find("[data-testid=tolerance]").TextContent, StringComparison.Ordinal);
        Assert.Equal("FullMatch", rows[1].GetAttribute("data-outcome"));
    }

    [Fact]
    public void Match_panel_shows_received_quantity_for_quantity_problems()
    {
        var cut = Show(Views.Detail(_id, InvoiceStatus.Exception, matches: [Views.Match(1, MatchOutcome.QuantityVariance, quantity: 10m, available: 6m)]));

        var row = cut.Find("[data-testid=match-row]");
        Assert.Contains("10", row.QuerySelector("[data-testid=qty-invoiced]")!.TextContent, StringComparison.Ordinal);
        Assert.Contains("6", row.QuerySelector("[data-testid=qty-available]")!.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Not_yet_matched_invoice_explains_the_empty_match_panel()
    {
        var cut = Show(Views.Detail(_id, InvoiceStatus.Exception, matches: [],
            findings: [Views.Finding(RuleCodes.VendorUnknown, FindingSeverity.Blocking, "The seller VAT id matches no vendor.")]));

        Assert.Contains("not matched", cut.Find("[data-testid=no-match]").TextContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void History_lists_every_change_with_actor_time_and_reason()
    {
        var cut = Show(Views.Detail(_id, InvoiceStatus.Exception));

        var entries = cut.FindAll("[data-testid=history-entry]");
        Assert.Equal(4, entries.Count);
        Assert.Contains("clerk@test", entries[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("14.11.2026 09:15", entries[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("NoGoodsReceipt", entries[3].TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Failed_invoice_shows_why_it_could_not_be_read()
    {
        var cut = Show(Views.Detail(_id, InvoiceStatus.Failed, matches: [], history:
        [
            new(1, null, InvoiceStatus.Received, "clerk@test", Views.Received, null),
            new(2, InvoiceStatus.Received, InvoiceStatus.Failed, "system", Views.Received, "Malformed: the XML is not well-formed."),
        ]));

        Assert.Contains("Malformed: the XML is not well-formed.", cut.Find("[data-testid=failure-reason]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Original_file_is_a_download_link_to_the_api()
    {
        var cut = Show(Views.Detail(_id));

        var link = cut.Find("[data-testid=download]");
        Assert.Equal($"/api/invoices/{_id}/document", link.GetAttribute("href"));
        Assert.True(link.HasAttribute("download")); // the browser downloads it; the Blazor router leaves it alone
        Assert.Contains("Rechnung 1.xml", cut.Find("[data-testid=file-name]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Document_text_is_encoded()
    {
        var cut = Show(Views.Detail(_id, description: "<script>alert(1)</script>"));

        Assert.Empty(cut.FindAll("[data-testid=line] script"));
        Assert.Contains("<script>alert(1)</script>", cut.Find("[data-testid=line]").TextContent, StringComparison.Ordinal);
    }

    // ---- Re-match -----------------------------------------------------------------------------------------------

    [Fact]
    public void Rematch_is_offered_only_for_a_match_exception()
    {
        Assert.NotEmpty(Show(Views.Detail(_id, InvoiceStatus.Exception, matches: [Views.Match(1, MatchOutcome.NoGoodsReceipt)])).FindAll("[data-testid=rematch]"));
    }

    [Theory]
    [InlineData(InvoiceStatus.Matched, false)]
    [InlineData(InvoiceStatus.Failed, false)]
    [InlineData(InvoiceStatus.Exception, true)] // with findings
    public void Rematch_is_not_offered_otherwise(InvoiceStatus status, bool withFindings)
    {
        var findings = withFindings ? new[] { Views.Finding("CONFORMANCE_UNAVAILABLE", FindingSeverity.Review, "The conformance check could not be made.") } : [];

        var cut = Show(Views.Detail(_id, status, findings: findings));

        Assert.Empty(cut.FindAll("[data-testid=rematch]"));
    }

    [Fact]
    public void Rematch_that_matches_reloads_the_invoice_and_says_so()
    {
        var cut = Show(Views.Detail(_id, InvoiceStatus.Exception, matches: [Views.Match(1, MatchOutcome.NoGoodsReceipt)]));
        Rematch.Result = new RematchResult(RematchOutcome.Matched, InvoiceStatus.Matched, []);
        Rematch.AfterRematch = id => Queries.Details[id] = Views.Detail(id, InvoiceStatus.Matched);

        cut.Find("[data-testid=rematch]").Click();

        cut.WaitForAssertion(() => Assert.Equal("Matched", cut.Find("[data-testid=status]").GetAttribute("data-status")));
        Assert.Contains("matched", cut.Find("[data-testid=rematch-result]").TextContent, StringComparison.OrdinalIgnoreCase);
        Assert.Equal((_id, "clerk@test"), Assert.Single(Rematch.Calls));
        Assert.Empty(cut.FindAll("[data-testid=rematch]"));
    }

    [Fact]
    public void Rematch_still_short_names_the_reason()
    {
        var cut = Show(Views.Detail(_id, InvoiceStatus.Exception, matches: [Views.Match(1, MatchOutcome.NoGoodsReceipt)]));
        Rematch.Result = new RematchResult(RematchOutcome.StillException, InvoiceStatus.Exception, [MatchOutcome.NoGoodsReceipt]);

        cut.Find("[data-testid=rematch]").Click();

        cut.WaitForAssertion(() => Assert.Contains(Labels.Outcome(MatchOutcome.NoGoodsReceipt), cut.Find("[data-testid=rematch-result]").TextContent, StringComparison.Ordinal));
    }

    [Fact]
    public void Rematch_refused_shows_the_reason()
    {
        var cut = Show(Views.Detail(_id, InvoiceStatus.Exception, matches: [Views.Match(1, MatchOutcome.NoGoodsReceipt)]));
        Rematch.Result = new RematchResult(RematchOutcome.NotAllowed, null, [], "The invoice was changed by someone else in the meantime; reload it.");

        cut.Find("[data-testid=rematch]").Click();

        cut.WaitForAssertion(() => Assert.Contains("someone else", cut.Find("[data-testid=rematch-result]").TextContent, StringComparison.Ordinal));
    }
}
