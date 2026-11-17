using System.Collections;
using System.Globalization;
using System.Net;
using System.Resources;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace EInvoice.IntegrationTests.Api;

/// <summary>
/// Which language a page is in, and how a person changes it, over real HTTP without a database. The first visit
/// follows the browser's language (German or English); anything else gets German, the language of the market. The
/// choice is kept in a host-only, HTTPS-only cookie and set through a POST form with an antiforgery token.
/// </summary>
public sealed partial class LanguageTests : IAsyncLifetime
{
    // Never opened by these tests.
    private const string UnusedConnection = "Server=127.0.0.1,1;Database=EInvoice;User Id=x;Password=y;Encrypt=True";
    private const string CookieName = "__Host-einvoice-culture";
    private const string SwitchPath = "/account/language";

    private static readonly ResourceManager Texts = new("EInvoice.Web.Resources.Texts", typeof(EInvoice.Web.Texts).Assembly);

    private ApiFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new ApiFactory(ApiFactory.Settings(UnusedConnection));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Theory]
    [InlineData("de-DE,de;q=0.9,en;q=0.8", "de")]
    [InlineData("de", "de")]
    [InlineData("en-US,en;q=0.9", "en")]
    [InlineData("en-GB", "en")]
    [InlineData("fr-FR,fr;q=0.9,en;q=0.5", "en")] // the first language the app speaks
    [InlineData("fr-FR", "de")]                   // none it speaks: German
    [InlineData(null, "de")]
    public async Task The_first_visit_follows_the_browser_language_else_german(string? acceptLanguage, string expected)
    {
        using var client = Client(acceptLanguage);

        var html = WebUtility.HtmlDecode(await (await client.GetAsync("/account/login")).Content.ReadAsStringAsync());

        Assert.Equal(expected, Lang(html));
        Assert.Contains(Text(expected, "Login.Title"), html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("c=en|uic=en", "de-DE", "en")]
    [InlineData("c=de|uic=de", "en-US", "de")]
    [InlineData("c=fr|uic=fr", "en-US", "en")]   // a language the app does not speak: the browser's
    [InlineData("rubbish", "en-US", "en")]
    public async Task A_chosen_language_wins_over_the_browser(string cookie, string acceptLanguage, string expected)
    {
        using var client = Client(acceptLanguage);
        client.DefaultRequestHeaders.Add("Cookie", $"{CookieName}={Uri.EscapeDataString(cookie)}");

        var html = await (await client.GetAsync("/account/login")).Content.ReadAsStringAsync();

        Assert.Equal(expected, Lang(html));
    }

    [Fact]
    public async Task A_link_cannot_switch_the_language()
    {
        // Only the person's own choice (the form) or their browser decides; a link someone sends them does not.
        using var german = Client("de-DE");
        using var english = Client("en-US");

        var html1 = await (await german.GetAsync("/account/login?culture=en&ui-culture=en")).Content.ReadAsStringAsync();
        var html2 = await (await english.GetAsync("/account/login?culture=de&ui-culture=de")).Content.ReadAsStringAsync();

        Assert.Equal("de", Lang(html1));
        Assert.Equal("en", Lang(html2));
    }

    [Fact]
    public async Task Choosing_a_language_keeps_it_in_a_protected_cookie_and_returns_to_the_page()
    {
        using var client = Client("de-DE");
        var token = await SignInForm.TokenAsync(client, "/account/login");

        using var response = await PostAsync(client, token, "en", "/account/login?ReturnUrl=%2Fdashboard");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/account/login?ReturnUrl=%2Fdashboard", SignInForm.Target(response));
        var cookie = SignInForm.Cookie(response, CookieName);
        Assert.NotNull(cookie);
        var parts = cookie.Split(';', StringSplitOptions.TrimEntries).Select(p => p.ToLowerInvariant()).ToList();
        Assert.Equal($"{CookieName}={Uri.EscapeDataString("c=en|uic=en")}".ToLowerInvariant(), parts[0]);
        Assert.Contains("secure", parts);
        Assert.Contains("httponly", parts);
        Assert.Contains("samesite=lax", parts);
        Assert.Contains("path=/", parts);
        Assert.DoesNotContain(parts, p => p.StartsWith("domain=", StringComparison.Ordinal)); // __Host-: this host only
        var expires = DateTimeOffset.Parse(parts.Single(p => p.StartsWith("expires=", StringComparison.Ordinal))["expires=".Length..], CultureInfo.InvariantCulture);
        Assert.InRange(expires, DateTimeOffset.UtcNow.AddDays(360), DateTimeOffset.UtcNow.AddDays(366));

        // The next page is in the chosen language, although the browser still prefers German.
        var html = await (await client.GetAsync("/account/login")).Content.ReadAsStringAsync();
        Assert.Equal("en", Lang(html));
    }

    [Theory]
    [InlineData("//evil.example/")]
    [InlineData("https://evil.example/")]
    [InlineData("/\\evil.example")]
    [InlineData("")]
    public async Task The_way_back_stays_on_this_site(string returnUrl)
    {
        using var client = Client("de-DE");
        var token = await SignInForm.TokenAsync(client, "/account/login");

        using var response = await PostAsync(client, token, "en", returnUrl);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", SignInForm.Target(response));
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("de-AT")]
    [InlineData("")]
    [InlineData("en|uic=de")]
    public async Task A_language_the_app_does_not_speak_is_refused_and_nothing_is_kept(string culture)
    {
        using var client = Client("de-DE");
        var token = await SignInForm.TokenAsync(client, "/account/login");

        using var response = await PostAsync(client, token, culture, "/");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Null(SignInForm.Cookie(response, CookieName));
    }

    [Fact]
    public async Task Without_the_form_token_nothing_changes()
    {
        using var client = Client("de-DE");

        using var response = await PostAsync(client, token: null, "en", "/");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(SignInForm.Cookie(response, CookieName));
    }

    [Fact]
    public async Task The_login_page_offers_the_switch_too()
    {
        using var client = Client("en-US");

        var html = await (await client.GetAsync("/account/login")).Content.ReadAsStringAsync();

        Assert.Matches(SwitchForm(), html);
    }

    private HttpClient Client(string? acceptLanguage)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = ApiFactory.BaseAddress, AllowAutoRedirect = false });
        if (acceptLanguage is not null)
        {
            client.DefaultRequestHeaders.Add("Accept-Language", acceptLanguage);
        }

        return client;
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string? token, string culture, string returnUrl)
    {
        var form = new Dictionary<string, string> { ["culture"] = culture, ["returnUrl"] = returnUrl };
        if (token is not null)
        {
            form["__RequestVerificationToken"] = token;
        }

        return client.PostAsync(new Uri(SwitchPath, UriKind.Relative), new FormUrlEncodedContent(form));
    }

    private static string Text(string language, string key) =>
        Texts.GetResourceSet(language == "en" ? CultureInfo.InvariantCulture : CultureInfo.GetCultureInfo(language), true, false)!
            .Cast<DictionaryEntry>().Single(e => (string)e.Key == key).Value as string ?? string.Empty;

    private static string? Lang(string html) => HtmlLang().Match(html) is { Success: true } m ? m.Groups[1].Value : null;

    [GeneratedRegex("""<html lang="([^"]*)">""")]
    private static partial Regex HtmlLang();

    [GeneratedRegex(@"<form[^>]*method=""post""[^>]*action=""/account/language""[^>]*data-testid=""language""", RegexOptions.IgnoreCase)]
    private static partial Regex SwitchForm();
}
