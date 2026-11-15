using EInvoice.Application.Approval;
using EInvoice.Application.Queries;
using EInvoice.Application.Security;
using EInvoice.Application.Validation;
using EInvoice.Domain.Invoices;
using EInvoice.Domain.Matching;
using EInvoice.Web.Components.Pages;
using EInvoice.Web.Security;

namespace EInvoice.ComponentTests;

/// <summary>Plays an approver: signed in as approver@test with the approver's policies and the default limit (5,000 EUR).</summary>
public abstract class ApproverTest : UiTest
{
    public const string Me = "approver@test";

    protected ApproverTest()
    {
        Auth.SetAuthorized(Me);
        Auth.SetRoles(Roles.Approver);
        Auth.SetPolicies(ApproverPolicies);
    }
}

/// <summary>The approvals queue: oldest first, and for each invoice in words what this approver can do with it.</summary>
public sealed class ApprovalsPageTests : ApproverTest
{
    private IRenderedComponent<ApprovalsPage> RenderAt(string uri)
    {
        NavigateTo(uri);
        return Render<ApprovalsPage>();
    }

    private static string StateOf(AngleSharp.Dom.IElement row) => row.QuerySelector("[data-testid=decision-state]")!.GetAttribute("data-state")!;

    [Fact]
    public void Lists_the_queue_in_the_order_given_with_links_to_each_invoice()
    {
        Queries.Queue.AddRange([Views.QueueItem("RE-1"), Views.QueueItem("RE-2", InvoiceStatus.Exception)]);

        var rows = RenderAt("/approvals").FindAll("[data-testid=queue-row]");

        Assert.Equal(2, rows.Count);
        Assert.Contains("RE-1", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Equal($"/invoices/{Queries.Queue[0].Id}", rows[0].QuerySelector("a")!.GetAttribute("href"));
        Assert.Contains("1.234,56", rows[0].TextContent, StringComparison.Ordinal); // German amount format
        Assert.Equal("Exception", rows[1].QuerySelector("[data-testid=status]")!.GetAttribute("data-status"));
    }

    [Fact]
    public void Says_for_each_invoice_what_this_approver_can_do_in_words()
    {
        Queries.Queue.AddRange(
        [
            Views.QueueItem("READY"),
            Views.QueueItem("OWN", submittedBy: "APPROVER@test"),
            Views.QueueItem("BIG", gross: 5_000.01m),
            Views.QueueItem("USD", currency: "USD"),
            Views.QueueItem("BLOCKED", InvoiceStatus.Exception, blocking: true),
        ]);

        var rows = RenderAt("/approvals").FindAll("[data-testid=queue-row]");

        Assert.Equal(new[] { "ready", "own", "over-limit", "currency", "blocked" }, rows.Select(StateOf));
        Assert.Contains("Ready to decide", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("Submitted by you", rows[1].TextContent, StringComparison.Ordinal);
        Assert.Contains("Above your limit of 5,000.00 EUR", rows[2].TextContent, StringComparison.Ordinal);
        Assert.Contains("Not in EUR", rows[3].TextContent, StringComparison.Ordinal);
        Assert.Contains("Only rejection", rows[4].TextContent, StringComparison.Ordinal);
        Assert.All(rows, r => Assert.NotNull(r.QuerySelector("[data-testid=decision-state] svg"))); // never colour alone
    }

    [Fact]
    public void A_limit_on_the_account_replaces_the_default_on_screen_too()
    {
        Auth.SetClaims(new System.Security.Claims.Claim(ApprovalLimits.ClaimType, "20000"));
        Queries.Queue.Add(Views.QueueItem("BIG", gross: 5_000.01m));

        var row = RenderAt("/approvals").Find("[data-testid=queue-row]");

        Assert.Equal("ready", StateOf(row));
    }

    [Fact]
    public void An_empty_queue_says_so()
    {
        Assert.Contains("Nothing is waiting for a decision", RenderAt("/approvals").Find("[data-testid=queue-empty]").TextContent, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/approvals?page=2", 2)]
    [InlineData("/approvals?page=0", 1)]
    [InlineData("/approvals?page=-3", 1)]
    [InlineData("/approvals?page=abc", 1)]
    [InlineData("/approvals?page=99999999999", 1)]
    public void The_page_comes_from_the_address_and_is_checked(string uri, int expected)
    {
        RenderAt(uri);

        Assert.Equal(expected, Queries.QueueCalls[^1].Page);
    }

    [Fact]
    public void Text_from_invoices_is_shown_as_text_not_markup()
    {
        Queries.Queue.Add(Views.QueueItem("<img src=x onerror=alert(1)>"));

        var row = RenderAt("/approvals").Find("[data-testid=queue-row]");

        Assert.Empty(row.QuerySelectorAll("img"));
        Assert.Contains("<img src=x", row.TextContent, StringComparison.Ordinal);
    }
}

/// <summary>The decision panel on the invoice page: what may be decided, why not, and what happened.</summary>
public sealed class DecisionPanelTests : ApproverTest
{
    private readonly Guid _id = Guid.NewGuid();

    private IRenderedComponent<InvoiceDetailPage> Show(
        InvoiceStatus status = InvoiceStatus.Matched, IReadOnlyList<FindingView>? findings = null)
    {
        Queries.Details[_id] = Views.Detail(_id, status, findings: findings,
            matches: status == InvoiceStatus.Exception ? [Views.Match(1, MatchOutcome.NoGoodsReceipt)] : null) with { Version = Views.Version };
        return Render<InvoiceDetailPage>(p => p.Add(c => c.Id, _id));
    }

    private static void TypeReason(IRenderedComponent<InvoiceDetailPage> cut, string reason) =>
        cut.Find("[data-testid=decision-reason] textarea").Input(reason);

    private static AngleSharp.Dom.IElement Button(IRenderedComponent<InvoiceDetailPage> cut, string testId) => cut.Find($"[data-testid={testId}]");

    [Theory]
    [InlineData(InvoiceStatus.Approved)]
    [InlineData(InvoiceStatus.Rejected)]
    [InlineData(InvoiceStatus.Failed)]
    public void Shown_only_while_the_invoice_waits_for_a_decision(InvoiceStatus status)
    {
        Assert.Empty(Show(status).FindAll("[data-testid=decision]"));
        Assert.NotEmpty(Show().FindAll("[data-testid=decision]"));
    }

    [Fact]
    public void Not_shown_to_roles_that_do_not_decide()
    {
        Auth.SetPolicies(ClerkPolicies);

        Assert.Empty(Show().FindAll("[data-testid=decision]"));
    }

    [Fact]
    public void A_clean_match_can_be_approved_at_once_and_rejected_with_a_reason()
    {
        var cut = Show();

        Assert.False(Button(cut, "approve").HasAttribute("disabled"));
        Assert.True(Button(cut, "reject").HasAttribute("disabled"));

        TypeReason(cut, "Not ordered.");

        Assert.False(Button(cut, "reject").HasAttribute("disabled"));
    }

    [Fact]
    public void An_exception_needs_a_reason_for_both_buttons()
    {
        var cut = Show(InvoiceStatus.Exception);

        Assert.True(Button(cut, "approve").HasAttribute("disabled"));
        Assert.True(Button(cut, "reject").HasAttribute("disabled"));
        Assert.Contains("required", cut.Find("[data-testid=decision-reason]").TextContent, StringComparison.OrdinalIgnoreCase);

        TypeReason(cut, "Price increase agreed.");

        Assert.False(Button(cut, "approve").HasAttribute("disabled"));
    }

    [Fact]
    public void Approve_sends_the_version_seen_the_approver_and_their_limit()
    {
        var cut = Show();

        Button(cut, "approve").Click();

        var (request, approver) = Assert.Single(Approval.Calls);
        Assert.Equal(new DecisionRequest(_id, Decision.Approve, Views.Version, null), request);
        Assert.Equal(new Approver(Me, DefaultLimit), approver);
    }

    [Fact]
    public void Reject_sends_the_reason()
    {
        var cut = Show();
        TypeReason(cut, "  Goods never ordered.  ");

        Button(cut, "reject").Click();

        var (request, _) = Assert.Single(Approval.Calls);
        Assert.Equal((Decision.Reject, "  Goods never ordered.  "), (request.Decision, request.Reason)); // trimmed by the use case
    }

    [Fact]
    public void A_decision_reloads_the_invoice_and_says_what_happened()
    {
        var cut = Show();
        Approval.AfterDecision = id => Queries.Details[id] = Views.Detail(id, InvoiceStatus.Approved) with { Version = "AAAAAAAAB9I=" };

        Button(cut, "approve").Click();

        cut.WaitForAssertion(() => Assert.Equal("Approved", cut.Find("[data-testid=status]").GetAttribute("data-status")));
        Assert.Contains("Approved", cut.Find("[data-testid=decision-result]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=decision]")); // decided: no more buttons
    }

    [Fact]
    public void A_conflict_says_the_invoice_changed_and_offers_a_reload()
    {
        var cut = Show();
        Approval.Result = new DecisionResult(DecisionOutcome.Conflict, InvoiceStatus.Matched, "The invoice changed since you opened it. Reload it and decide again.");

        Button(cut, "approve").Click();

        var result = cut.Find("[data-testid=decision-result]");
        Assert.Contains("changed since you opened it", result.TextContent, StringComparison.Ordinal);
        Assert.NotNull(cut.Find("[data-testid=decision-reload]"));
    }

    [Fact]
    public void Other_refusals_show_their_explanation()
    {
        var cut = Show();
        Approval.Result = new DecisionResult(DecisionOutcome.OverLimit, InvoiceStatus.Matched, "The gross amount is above your approval limit of 5,000.00 EUR.");

        Button(cut, "approve").Click();

        Assert.Contains("approval limit of 5,000.00 EUR", cut.Find("[data-testid=decision-result]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Own_invoice_says_who_decides_and_offers_no_buttons()
    {
        Auth.SetAuthorized("clerk@test"); // the detail's submitter
        Auth.SetPolicies(ApproverPolicies);

        var cut = Show();

        Assert.Contains("someone else", cut.Find("[data-testid=decision]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=approve]"));
        Assert.Empty(cut.FindAll("[data-testid=reject]"));
    }

    [Fact]
    public void Blocking_finding_disables_approve_with_the_reason_but_allows_reject()
    {
        var cut = Show(InvoiceStatus.Exception, [Views.Finding(RuleCodes.IbanNotApproved, FindingSeverity.Blocking, "The bank account is not approved for this vendor.")]);
        TypeReason(cut, "Bank account unknown.");

        Assert.True(Button(cut, "approve").HasAttribute("disabled"));
        Assert.Contains("can only be rejected", cut.Find("[data-testid=approve-blocked]").TextContent, StringComparison.Ordinal);
        Assert.False(Button(cut, "reject").HasAttribute("disabled"));
    }

    [Fact]
    public void Over_the_limit_disables_approve_and_names_the_limit()
    {
        Auth.SetClaims(new System.Security.Claims.Claim(ApprovalLimits.ClaimType, "1000"));

        var cut = Show(); // gross 1,234.56 EUR

        Assert.True(Button(cut, "approve").HasAttribute("disabled"));
        Assert.Contains("1,000.00 EUR", cut.Find("[data-testid=approve-blocked]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void The_reason_field_allows_at_most_200_characters()
    {
        var cut = Show();

        Assert.Equal("200", cut.Find("[data-testid=decision-reason] textarea").GetAttribute("maxlength"));
    }

    [Fact]
    public void A_decision_after_the_role_was_withdrawn_does_nothing()
    {
        var cut = Show();
        var approve = Button(cut, "approve");

        Auth.SetPolicies(AuditorPolicies);
        approve.Click();

        Assert.Empty(Approval.Calls);
    }
}
