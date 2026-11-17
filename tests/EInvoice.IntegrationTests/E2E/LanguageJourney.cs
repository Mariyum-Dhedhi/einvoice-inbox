using System.Collections;
using System.Globalization;
using System.Resources;
using EInvoice.IntegrationTests.Infrastructure;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace EInvoice.IntegrationTests.E2E;

/// <summary>
/// Package 15c's "done when": a person whose browser prefers German gets German pages, switches to English before
/// signing in, signs in, switches back to German in the app bar, and the choice survives a reload. No console error
/// on the way (the strict content security policy included).
/// </summary>
[Trait("Category", "E2E")]
[Collection(SqlServerCollection.Name)]
public sealed class LanguageJourney(SqlServerFixture fixture) : BrowserJourney(fixture)
{
    private static readonly ResourceManager Texts = new("EInvoice.Web.Resources.Texts", typeof(EInvoice.Web.Texts).Assembly);

    [Fact]
    public async Task A_clerk_switches_between_german_and_english_and_the_choice_sticks()
    {
        var clerk = await AccountAsync("Clerk");
        var page = await NewBrowserAsync(locale: "de-DE");

        // A German browser, never here before: German.
        await page.GotoAsync(BaseUrl.TrimEnd('/') + "/account/login");
        await Expect(page.Locator("html")).ToHaveAttributeAsync("lang", "de");
        await Expect(page.Locator("h1")).ToHaveTextAsync(Text("de", "Login.Title"));

        // English, then sign in with the English form.
        await page.Locator("form[data-testid=language] button[value=en]").ClickAsync();
        await Expect(page.Locator("html")).ToHaveAttributeAsync("lang", "en");
        await page.GetByLabel(Text("en", "Login.UserName")).FillAsync(clerk.UserName);
        await page.GetByLabel(Text("en", "Login.Password")).FillAsync(clerk.Password);
        await page.GetByRole(AriaRole.Button, new() { Name = Text("en", "Login.Submit") }).ClickAsync();
        await Expect(page.Locator("[data-testid=signed-in-user]")).ToHaveTextAsync(clerk.UserName);
        await ReadyAsync(page);
        await Expect(page.Locator("h1")).ToHaveTextAsync(Text("en", "Inbox.Title"));

        // German from the app bar: same page, now German.
        await page.Locator(".mud-appbar form[data-testid=language] button[value=de]").ClickAsync();
        await Expect(page.Locator("html")).ToHaveAttributeAsync("lang", "de");
        await ReadyAsync(page);
        await Expect(page.Locator("h1")).ToHaveTextAsync(Text("de", "Inbox.Title"));
        await Expect(page.Locator("[data-testid=nav-dashboard]")).ToBeVisibleAsync();

        // The choice is kept: a reload, and a page opened afterwards, stay German.
        await page.ReloadAsync();
        await ReadyAsync(page);
        await Expect(page.Locator("h1")).ToHaveTextAsync(Text("de", "Inbox.Title"));
        await page.Locator("[data-testid=nav-dashboard]").ClickAsync();
        await Expect(page.Locator("h1")).ToHaveTextAsync(Text("de", "Dashboard.Title"));

        Assert.Empty(Problems);
    }

    private static string Text(string language, string key) =>
        Texts.GetResourceSet(language == "en" ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(language), true, false)!
            .Cast<DictionaryEntry>().Single(e => (string)e.Key == key).Value as string ?? string.Empty;
}
