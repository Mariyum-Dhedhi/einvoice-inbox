using System.Globalization;
using EInvoice.Web.Components.Layout;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace EInvoice.ComponentTests.Localization;

/// <summary>
/// The language switch in the app bar: a protected POST form (like sign-out) that comes back to the same page, and
/// says which language is on in words, not colour. Not a <see cref="UiTest"/>: the layout brings its own providers.
/// </summary>
public sealed class LanguageSwitchTests : BunitContext, IAsyncLifetime
{
    public LanguageSwitchTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices();
        Services.AddLocalization(o => o.ResourcesPath = "Resources");
        Services.AddSingleton<AntiforgeryStateProvider>(new UiTest.FixedAntiforgery());
        var auth = AddAuthorization();
        auth.SetAuthorized(UiTest.SignedInUser);
        auth.SetPolicies(UiTest.ClerkPolicies);
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));
        UiTest.UseLanguage("en");
    }

    public Task InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync() => await DisposeAsync();

    private IRenderedComponent<MainLayout> RenderLayout() =>
        Render<MainLayout>(p => p.Add(l => l.Body, (RenderFragment)(b => b.AddContent(0, "page"))));

    [Fact]
    public void The_switch_posts_the_chosen_language_with_a_token_and_comes_back_to_this_page()
    {
        Services.GetRequiredService<NavigationManager>().NavigateTo("/?status=Exception&page=2");

        var form = RenderLayout().Find("form[data-testid=language]");

        Assert.Equal("post", form.GetAttribute("method"), ignoreCase: true);
        Assert.Equal("/account/language", form.GetAttribute("action"));
        Assert.Equal(UiTest.AntiforgeryToken, form.QuerySelector("input[name=__RequestVerificationToken]")?.GetAttribute("value"));
        Assert.Equal("/?status=Exception&page=2", form.QuerySelector("input[name=returnUrl]")?.GetAttribute("value"));
        var buttons = form.QuerySelectorAll("button[type=submit][name=culture]");
        Assert.Equal(["de", "en"], buttons.Select(b => b.GetAttribute("value")));
    }

    [Theory]
    [InlineData("de", "true", "false")]
    [InlineData("en", "false", "true")]
    public void The_language_that_is_on_is_marked_in_words_for_screen_readers(string language, string germanPressed, string englishPressed)
    {
        UiTest.UseLanguage(language);

        var form = RenderLayout().Find("form[data-testid=language]");

        var german = form.QuerySelector("button[value=de]")!;
        var english = form.QuerySelector("button[value=en]")!;
        Assert.Equal(germanPressed, german.GetAttribute("aria-pressed"));
        Assert.Equal(englishPressed, english.GetAttribute("aria-pressed"));
        // Each language names itself in its own language, whatever the page is in.
        Assert.Equal(TextResources.German["Language.German"], german.TextContent.Trim());
        Assert.Equal("de", german.GetAttribute("lang"));
        Assert.Equal(TextResources.English["Language.English"], english.TextContent.Trim());
        Assert.Equal("en", english.GetAttribute("lang"));
    }

    [Fact]
    public void The_app_bar_speaks_the_chosen_language()
    {
        UiTest.UseLanguage("de");

        var cut = RenderLayout();

        Assert.Equal(TextResources.German["Nav.Inbox"], cut.Find("[data-testid=nav-inbox]").TextContent.Trim());
        Assert.Equal(TextResources.German["Layout.SignOut"], cut.Find(".sign-out button").TextContent.Trim());
    }
}
