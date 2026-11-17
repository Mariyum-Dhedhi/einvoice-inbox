using EInvoice.Application.Approval;
using EInvoice.Application.Intake;
using EInvoice.Application.Queries;
using EInvoice.Application.Validation;
using EInvoice.Domain.Invoices;
using EInvoice.Domain.Matching;
using EInvoice.Web.Components.Pages;
using EInvoice.Web.Components.Shared;
using Microsoft.AspNetCore.Components.Forms;

namespace EInvoice.ComponentTests.Localization;

/// <summary>
/// The pages in German: their own words come from the German texts, numbers stay in German formats (as in English),
/// and what was recorded in English (a finding's message, a failure reason) is shown as recorded, under German words.
/// </summary>
public sealed class GermanPagesTests : UiTest
{
    private static readonly IReadOnlyDictionary<string, string> De = TextResources.German;
    private static readonly IReadOnlyDictionary<string, string> En = TextResources.English;

    public GermanPagesTests() => UseLanguage("de");

    [Fact]
    public void Inbox_speaks_german_with_german_status_words()
    {
        var id = Guid.NewGuid();
        Queries.Page = new InvoicePage([Views.Summary(id, InvoiceStatus.Exception)], 1, InvoiceListQuery.DefaultPageSize, 1);

        var cut = Render<Inbox>();

        Assert.Equal(De["Inbox.Title"], cut.Find("h1").TextContent.Trim());
        Assert.Equal(De["Status.Exception"], cut.Find("[data-testid=invoice-row] [data-testid=status]").TextContent.Trim());
        Assert.Equal(De["Status.Exception"], cut.Find("[data-testid=filter-Exception]").TextContent.Trim());
        Assert.Contains(Formats.Money(1234.56m, "EUR"), cut.Find("[data-testid=invoice-row]").TextContent, StringComparison.Ordinal); // German format in both languages
    }

    [Fact]
    public void A_refused_upload_is_explained_in_german()
    {
        Intake.Result = new IntakeResult(IntakeOutcome.DuplicateFile, Guid.NewGuid(), Detail: "This file was uploaded before.");
        var cut = Render<Inbox>();

        cut.FindComponent<InputFile>().UploadFiles(InputFileContent.CreateFromText("<Invoice/>", "a.xml"));

        var error = cut.WaitForElement("[data-testid=upload-error]").TextContent;
        Assert.Contains(De["Intake.DuplicateFile"], error, StringComparison.Ordinal);
        Assert.DoesNotContain("uploaded before", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_finding_gets_its_german_rule_title_and_keeps_its_recorded_message()
    {
        var id = Guid.NewGuid();
        const string recorded = "Payment account DE89 **** 3000 is not an approved account of this vendor.";
        Queries.Details[id] = Views.Detail(id, InvoiceStatus.Exception,
            findings: [Views.Finding(RuleCodes.IbanNotApproved, FindingSeverity.Review, recorded)], matches: []);

        var finding = Render<InvoiceDetailPage>(p => p.Add(x => x.Id, id)).Find("[data-testid=finding]").TextContent;

        Assert.Contains(De[$"Rule.{RuleCodes.IbanNotApproved}.Title"], finding, StringComparison.Ordinal);
        Assert.Contains(De["Severity.Review"], finding, StringComparison.Ordinal);
        Assert.Contains(recorded, finding, StringComparison.Ordinal);
    }

    [Fact]
    public void The_explanation_of_a_price_variance_is_german_with_german_numbers()
    {
        var id = Guid.NewGuid();
        Queries.Details[id] = Views.Detail(id, InvoiceStatus.Exception,
            matches: [Views.Match(1, MatchOutcome.PriceVariance, invoicePrice: 7.90m, orderPrice: 7.4125m, variancePercent: 6.58m)]);

        var text = Render<InvoiceDetailPage>(p => p.Add(x => x.Id, id)).Find("[data-testid=explanation]").TextContent;

        Assert.Contains(Formats.UnitPrice(7.90m, "EUR"), text, StringComparison.Ordinal);     // 7,90 €
        Assert.Contains(Formats.UnitPrice(7.4125m, "EUR"), text, StringComparison.Ordinal);   // 7,4125 €
        Assert.Contains(Formats.Percent(6.58m), text, StringComparison.Ordinal);              // +6,58 %
        Assert.Contains(De["Explain.PriceVariance.Step"], text, StringComparison.Ordinal);
        Assert.DoesNotContain(En["Explain.PriceVariance.Step"], text, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_finding_code_is_explained_with_its_german_step()
    {
        var id = Guid.NewGuid();
        var codes = TextResources.Constants(typeof(RuleCodes)).ToList();
        Queries.Details[id] = Views.Detail(id, InvoiceStatus.Exception,
            findings: codes.Select(c => Views.Finding(c, FindingSeverity.Review, "recorded")).ToList(), matches: []);

        var text = Render<InvoiceDetailPage>(p => p.Add(x => x.Id, id)).Find("[data-testid=explanation]").TextContent;

        Assert.All(codes, c => Assert.Contains(De[$"Rule.{c}.Step"], text, StringComparison.Ordinal));
    }

    [Fact]
    public void A_blocking_finding_says_in_german_that_it_blocks_payment()
    {
        var id = Guid.NewGuid();
        Queries.Details[id] = Views.Detail(id, InvoiceStatus.Exception,
            findings: [Views.Finding(RuleCodes.Totals, FindingSeverity.Blocking, "recorded")], matches: []);

        var text = Render<InvoiceDetailPage>(p => p.Add(x => x.Id, id)).Find("[data-testid=explanation]").TextContent;

        Assert.Contains(De["Explain.Blocking"], text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_approval_queue_states_the_limit_in_german_words_and_format()
    {
        Auth.SetRoles("Approver");
        Auth.SetPolicies(ApproverPolicies);
        Queries.Queue.Add(Views.QueueItem("RE-2026-0100", gross: 9_999.00m, submittedBy: "someone-else@test"));

        var state = Render<ApprovalsPage>().Find("[data-testid=decision-state]").TextContent.Trim();

        Assert.Equal(string.Format(System.Globalization.CultureInfo.InvariantCulture, De["Approvals.OverLimit"], Formats.Money(DefaultLimit, "EUR")), state);
    }

    [Fact]
    public void A_refused_decision_is_explained_in_german()
    {
        Auth.SetRoles("Approver");
        Auth.SetPolicies(ApproverPolicies);
        var id = Guid.NewGuid();
        Queries.Details[id] = Views.Detail(id) with { SubmittedBy = "someone-else@test", Version = Views.Version };
        Approval.Result = new DecisionResult(DecisionOutcome.Conflict, null, "The invoice changed since you opened it.");
        var cut = Render<InvoiceDetailPage>(p => p.Add(x => x.Id, id));

        cut.Find("[data-testid=approve]").Click();

        var message = cut.WaitForElement("[data-testid=decision-result]").TextContent;
        Assert.Contains(De["Decision.Conflict"], message, StringComparison.Ordinal);
        Assert.DoesNotContain("changed since you opened", message, StringComparison.Ordinal);
    }
}
