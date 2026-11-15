using System.Security.Claims;
using EInvoice.Application.Security;
using EInvoice.Domain.Invoices;
using EInvoice.Domain.Matching;
using EInvoice.Web.Components.Account;
using EInvoice.Web.Components.Admin;
using EInvoice.Web.Components.Pages;
using EInvoice.Web.Security;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace EInvoice.ComponentTests;

/// <summary>
/// Pages offer only what the signed-in role may do. Hiding is for the user; the server checks again
/// (RoleMatrixTests for the API and pages, the policy check in each action).
/// </summary>
public sealed class RoleVisibilityTests : UiTest
{
    private readonly Guid _id = Guid.NewGuid();

    private IRenderedComponent<InvoiceDetailPage> ShowException()
    {
        Queries.Details[_id] = Views.Detail(_id, InvoiceStatus.Exception, matches: [Views.Match(1, MatchOutcome.NoGoodsReceipt)]);
        return Render<InvoiceDetailPage>(p => p.Add(c => c.Id, _id));
    }

    [Fact]
    public void Clerk_can_upload()
    {
        var cut = Render<Inbox>();

        Assert.NotEmpty(cut.FindAll("input[type=file]"));
        Assert.Contains("Upload invoice", cut.Markup, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Approver")]
    [InlineData("Auditor")]
    public void Other_readers_see_the_inbox_without_an_upload(string role)
    {
        Auth.SetPolicies(role == "Approver" ? ApproverPolicies : AuditorPolicies);

        var cut = Render<Inbox>();

        Assert.NotEmpty(cut.FindAll("h1"));
        Assert.Empty(cut.FindAll("input[type=file]"));
        Assert.DoesNotContain("Upload invoice", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void Clerk_can_match_again_and_ask_the_ai()
    {
        var cut = ShowException();

        Assert.NotEmpty(cut.FindAll("[data-testid=rematch]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=explain-ai]"));
    }

    [Fact]
    public void Approver_can_ask_the_ai_but_not_match_again()
    {
        Auth.SetPolicies(ApproverPolicies);

        var cut = ShowException();

        Assert.Empty(cut.FindAll("[data-testid=rematch]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=explain-ai]"));
    }

    [Fact]
    public void Auditor_reads_the_rule_based_explanation_only()
    {
        Auth.SetPolicies(AuditorPolicies);

        var cut = ShowException();

        Assert.Empty(cut.FindAll("[data-testid=rematch]"));
        Assert.Empty(cut.FindAll("[data-testid=explain-ai]"));
        Assert.NotEmpty(cut.FindAll("[data-testid=explanation]")); // facts from the rules are for every reader
    }
}

/// <summary>
/// A role withdrawn while the page is open: the button is still on screen, but the server's own check refuses.
/// (Hiding a button is a convenience; this check is the protection.)
/// </summary>
public sealed class ServerCheckTests : UiTest
{
    private readonly Guid _id = Guid.NewGuid();

    private IRenderedComponent<InvoiceDetailPage> ShowException()
    {
        Queries.Details[_id] = Views.Detail(_id, InvoiceStatus.Exception, matches: [Views.Match(1, MatchOutcome.NoGoodsReceipt)]);
        return Render<InvoiceDetailPage>(p => p.Add(c => c.Id, _id));
    }

    [Fact]
    public void Match_again_after_the_role_was_withdrawn_does_nothing()
    {
        var cut = ShowException();
        var button = cut.Find("[data-testid=rematch]");

        Auth.SetPolicies(AuditorPolicies); // the role changes; the open page still shows the button
        button.Click();

        Assert.Empty(Rematch.Calls);
    }

    [Fact]
    public void Ai_explanation_after_the_role_was_withdrawn_does_nothing()
    {
        var cut = ShowException();
        var button = cut.Find("[data-testid=explain-ai]");

        Auth.SetPolicies(AuditorPolicies);
        button.Click();

        Assert.Equal(0, Explainer.Calls);
    }

    [Fact]
    public void Upload_after_the_role_was_withdrawn_stores_nothing()
    {
        var cut = Render<Inbox>();
        var input = cut.FindComponent<Microsoft.AspNetCore.Components.Forms.InputFile>();

        Auth.SetPolicies(ApproverPolicies);
        input.UploadFiles(Bunit.InputFileContent.CreateFromText("<Invoice/>", "invoice.xml"));

        Assert.Empty(Intake.Calls);
    }

    [Fact]
    public void With_the_role_the_same_clicks_work()
    {
        var cut = ShowException();

        cut.Find("[data-testid=rematch]").Click();

        Assert.Single(Rematch.Calls); // the check above refuses because of the role, not because clicks never work
    }
}

/// <summary>The page a signed-in user sees when their role may not open a page.</summary>
public sealed class AccessDeniedTests : UiTest
{
    [Fact]
    public void Names_the_account_and_its_roles_and_offers_another_sign_in()
    {
        Auth.SetClaims(new Claim(ClaimTypes.Role, Roles.Auditor));

        var cut = Render<AccessDenied>();

        Assert.Contains("no access", cut.Find("h1").TextContent, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(SignedInUser, cut.Find("[data-testid=denied-user]").TextContent, StringComparison.Ordinal);
        Assert.Contains(Roles.Auditor, cut.Find("[data-testid=denied-roles]").TextContent, StringComparison.Ordinal);
        var form = cut.Find("form[data-testid=sign-out]");
        Assert.Equal("/account/logout", form.GetAttribute("action"));
        Assert.Equal(AntiforgeryToken, form.QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value"));
    }

    [Fact]
    public void A_user_without_any_role_is_told_so()
    {
        Auth.SetRoles();
        var cut = Render<AccessDenied>();

        Assert.Contains("no role", cut.Find("[data-testid=denied-roles]").TextContent, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>The admin's read-only list of accounts.</summary>
public sealed class UsersPageTests : UiTest
{
    private static readonly DateTimeOffset LockedUntil = new(2026, 11, 15, 16, 30, 0, TimeSpan.Zero);

    public UsersPageTests()
    {
        Auth.SetRoles(Roles.Admin);
        Auth.SetPolicies(Policies.ManageUsers);
        Users.Accounts.Add(new UserSummary("approver@example.com", [Roles.Approver], null));
        Users.Accounts.Add(new UserSummary("clerk@example.com", [Roles.Clerk], LockedUntil));
        Users.Accounts.Add(new UserSummary("<b>new</b>@example.com", [], null));
    }

    [Fact]
    public void Lists_every_account_in_the_given_order_with_its_roles()
    {
        var rows = Render<UsersPage>().FindAll("[data-testid=user-row]");

        Assert.Equal(3, rows.Count);
        Assert.Contains("approver@example.com", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains(Roles.Approver, rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("clerk@example.com", rows[1].TextContent, StringComparison.Ordinal);
        Assert.Contains("No role", rows[2].TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Lockout_is_shown_in_words_with_the_berlin_time_not_by_colour_alone()
    {
        var rows = Render<UsersPage>().FindAll("[data-testid=user-row]");

        var locked = rows[1].QuerySelector("[data-testid=user-status]")!;
        Assert.Equal("locked", locked.GetAttribute("data-status"));
        Assert.Contains("Locked until 15.11.2026 17:30", locked.TextContent, StringComparison.Ordinal); // 16:30 UTC = 17:30 in Berlin
        Assert.NotNull(locked.QuerySelector("svg"));
        Assert.Equal("active", rows[0].QuerySelector("[data-testid=user-status]")!.GetAttribute("data-status"));
        Assert.Contains("Active", rows[0].TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Offers_no_way_to_change_anything_yet()
    {
        var cut = Render<UsersPage>();

        Assert.Empty(cut.FindAll("form"));
        Assert.Empty(cut.FindAll("button"));
        Assert.Empty(cut.FindAll("input"));
    }

    [Fact]
    public void User_names_are_shown_as_text_not_markup()
    {
        var cut = Render<UsersPage>();

        Assert.Empty(cut.FindAll("[data-testid=user-row] b"));
        Assert.Contains("<b>new</b>@example.com", cut.FindAll("[data-testid=user-row]")[2].TextContent, StringComparison.Ordinal);
    }
}

/// <summary>A page the user may not see inside an open session: login when signed out, "no access" otherwise.</summary>
public sealed class NotAuthorizedTests : UiTest
{
    [Fact]
    public void Signed_in_user_without_the_role_goes_to_the_no_access_page()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/admin/users");

        Render<RedirectToLogin>();

        Assert.StartsWith("http://localhost/account/access-denied?ReturnUrl=%2Fadmin%2Fusers", CurrentUri, StringComparison.Ordinal);
    }

    [Fact]
    public void Signed_out_user_goes_to_the_login_page()
    {
        Auth.SetNotAuthorized();
        Services.GetRequiredService<NavigationManager>().NavigateTo("/invoices/1?x=1");

        Render<RedirectToLogin>();

        Assert.StartsWith("http://localhost/account/login?ReturnUrl=%2Finvoices%2F1%3Fx%3D1", CurrentUri, StringComparison.Ordinal);
    }
}
