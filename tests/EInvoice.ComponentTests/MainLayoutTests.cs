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
        Services.AddMudServices();
        Services.AddSingleton<AntiforgeryStateProvider>(new FixedAntiforgery());
        Auth = AddAuthorization();
        Auth.SetAuthorized(UiTest.SignedInUser);
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

    private sealed class FixedAntiforgery : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken GetAntiforgeryToken() => new(Token, "__RequestVerificationToken");
    }
}
