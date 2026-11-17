using EInvoice.Web.Components.Layout;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace EInvoice.ComponentTests;

/// <summary>
/// The app bar shows who is signed in and signs out with a protected POST form, never with a link.
/// Not a <see cref="UiTest"/>: the layout brings its own MudBlazor providers.
/// </summary>
public sealed class MainLayoutTests : BunitContext, IAsyncLifetime
{
    private const string Token = "test-antiforgery-token";

    public MainLayoutTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        UiTest.UseLanguage("en");
        Services.AddMudServices();
        Services.AddLocalization(o => o.ResourcesPath = "Resources");
        Services.AddSingleton<AntiforgeryStateProvider>(new FixedAntiforgery());
        Auth = AddAuthorization();
        Auth.SetAuthorized(UiTest.SignedInUser);
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
    }

    private Bunit.TestDoubles.BunitAuthorizationContext Auth { get; }

    public Task InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync() => await DisposeAsync();

    private IRenderedComponent<MainLayout> RenderLayout() =>
        Render<MainLayout>(p => p.Add(l => l.Body, (RenderFragment)(b => b.AddContent(0, "page"))));

    [Fact]
    public void Shows_the_signed_in_user()
    {
        var cut = RenderLayout();

        Assert.Equal(UiTest.SignedInUser, cut.Find("[data-testid=signed-in-user]").TextContent.Trim());
    }

    [Fact]
    public void Sign_out_is_a_post_form_with_an_antiforgery_token()
    {
        var cut = RenderLayout();

        var form = cut.Find("form[data-testid=sign-out]");
        Assert.Equal("post", form.GetAttribute("method"), ignoreCase: true);
        Assert.Equal("/account/logout", form.GetAttribute("action"));
        Assert.Equal(Token, form.QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value"));
        Assert.NotNull(form.QuerySelector("button[type=submit]"));
        Assert.Empty(cut.FindAll("a[href*='logout']"));
    }

    [Fact]
    public void User_name_from_the_sign_in_is_shown_as_text_not_markup()
    {
        Auth.SetAuthorized("<img src=x onerror=alert(1)>@example.com");

        var cut = RenderLayout();

        Assert.Empty(cut.FindAll("img"));
        Assert.Contains("<img src=x", cut.Find("[data-testid=signed-in-user]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Nobody_signed_in_shows_no_user_and_no_sign_out()
    {
        Auth.SetNotAuthorized();

        var cut = RenderLayout();

        Assert.Empty(cut.FindAll("[data-testid=signed-in-user]"));
        Assert.Empty(cut.FindAll("form[data-testid=sign-out]"));
    }

    [Fact]
    public void Readers_get_the_inbox_link_but_not_the_user_list()
    {
        Auth.SetPolicies(EInvoice.Web.Security.Policies.ReadInvoices);

        var cut = RenderLayout();

        Assert.NotEmpty(cut.FindAll("a[data-testid=nav-inbox]"));
        Assert.Empty(cut.FindAll("a[data-testid=nav-users]"));
    }

    [Fact]
    public void Admin_gets_the_user_list_link_but_not_the_inbox()
    {
        Auth.SetPolicies(EInvoice.Web.Security.Policies.ManageUsers);

        var cut = RenderLayout();

        Assert.Equal("/admin/users", cut.Find("a[data-testid=nav-users]").GetAttribute("href"));
        Assert.Empty(cut.FindAll("a[data-testid=nav-inbox]"));
    }

    [Fact]
    public void Readers_get_the_dashboard_link_admins_do_not()
    {
        Auth.SetPolicies(EInvoice.Web.Security.Policies.ReadInvoices);
        Assert.Equal("/dashboard", RenderLayout().Find("a[data-testid=nav-dashboard]").GetAttribute("href"));
    }

    [Fact]
    public void Admins_get_no_dashboard_link()
    {
        Auth.SetPolicies(EInvoice.Web.Security.Policies.ManageUsers);
        Assert.Empty(RenderLayout().FindAll("a[data-testid=nav-dashboard]"));
    }

    [Fact]
    public void Approvers_get_the_approvals_link()
    {
        Auth.SetPolicies(EInvoice.Web.Security.Policies.ReadInvoices, EInvoice.Web.Security.Policies.DecideInvoices);

        Assert.Equal("/approvals", RenderLayout().Find("a[data-testid=nav-approvals]").GetAttribute("href"));
    }

    [Fact]
    public void Readers_who_do_not_decide_get_no_approvals_link()
    {
        Auth.SetPolicies(EInvoice.Web.Security.Policies.ReadInvoices);

        Assert.Empty(RenderLayout().FindAll("a[data-testid=nav-approvals]"));
    }

    [Fact]
    public void Shows_the_ready_marker_once_the_first_interactive_render_is_done()
    {
        // Browser tests wait for it: before it, buttons are server-rendered HTML that does nothing yet.
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));

        Assert.NotEmpty(RenderLayout().FindAll("[data-testid=app-ready]"));
    }

    [Fact]
    public void Shows_no_ready_marker_while_prerendering()
    {
        SetRendererInfo(new RendererInfo("Static", isInteractive: false));

        Assert.Empty(RenderLayout().FindAll("[data-testid=app-ready]"));
    }

    private sealed class FixedAntiforgery : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken GetAntiforgeryToken() => new(Token, "__RequestVerificationToken");
    }
}
