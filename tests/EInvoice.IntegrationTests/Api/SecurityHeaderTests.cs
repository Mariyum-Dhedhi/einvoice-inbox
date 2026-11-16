using System.Net;

namespace EInvoice.IntegrationTests.Api;

/// <summary>
/// The browser-side protections every response carries, without a database. The ZAP scan in CI (tests/zap) found
/// them missing; these tests say exactly what is sent, so a change shows up here before it shows up in a scan.
/// The browser journeys (E2E) fail on any console error, which includes a content security policy violation: the
/// policy is strict and the app still works.
/// </summary>
public sealed class SecurityHeaderTests
{
    // Never opened by these tests.
    private const string UnusedConnection = "Server=127.0.0.1,1;Database=EInvoice;User Id=x;Password=y;Encrypt=True";

    /// <summary>A page, an API answer, a static file and an unknown address: all go through the same headers.</summary>
    public static TheoryData<string> Responses => new()
    {
        "/account/login",
        "/api/invoices",
        "/app.css",
        "/no-such-page",
    };

    /// <summary>
    /// Content only from this site; no plug-ins; not framed by anyone; forms post only here; no base-URL tricks.
    /// Styles allow inline: MudBlazor positions and colours components with style attributes (tests/zap/rules.tsv).
    /// connect-src 'self' covers the Blazor WebSocket (same origin).
    /// </summary>
    private static readonly Dictionary<string, string> Policy = new()
    {
        ["default-src"] = "'self'",
        ["script-src"] = "'self'",
        ["style-src"] = "'self' 'unsafe-inline'",
        ["img-src"] = "'self' data:",
        ["font-src"] = "'self'",
        ["connect-src"] = "'self'",
        ["object-src"] = "'none'",
        ["base-uri"] = "'self'",
        ["form-action"] = "'self'",
        ["frame-ancestors"] = "'none'",
    };

    [Theory]
    [MemberData(nameof(Responses))]
    public async Task Every_response_carries_one_strict_content_security_policy(string path)
    {
        using var response = await GetAsync(path);

        // Exactly one header: two policies would both apply, and a reader of one would not see the other.
        var header = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        var directives = header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.Split(' ', 2))
            .ToDictionary(d => d[0], d => d.Length > 1 ? d[1] : string.Empty);
        Assert.Equal(Policy.OrderBy(p => p.Key), directives.OrderBy(p => p.Key));
    }

    [Theory]
    [MemberData(nameof(Responses))]
    public async Task Every_response_isolates_the_page_and_switches_off_what_it_does_not_use(string path)
    {
        using var response = await GetAsync(path);

        Assert.Equal("no-referrer", Single(response, "Referrer-Policy"));        // invoice ids in addresses stay here
        Assert.Equal("same-origin", Single(response, "Cross-Origin-Opener-Policy"));
        Assert.Equal("same-origin", Single(response, "Cross-Origin-Resource-Policy"));
        Assert.Equal("require-corp", Single(response, "Cross-Origin-Embedder-Policy"));
        Assert.Equal(
            "camera=(), microphone=(), geolocation=(), payment=(), usb=(), clipboard-read=(), fullscreen=()",
            Single(response, "Permissions-Policy"));
        Assert.Equal("nosniff", Single(response, "X-Content-Type-Options"));
    }

    [Fact]
    public async Task The_antiforgery_cookie_is_host_only_and_sent_over_https_only()
    {
        using var response = await GetAsync("/account/login");

        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.Contains("antiforgery", StringComparison.OrdinalIgnoreCase));
        Assert.StartsWith("__Host-", cookie, StringComparison.Ordinal); // browsers then insist on Secure, Path=/ and no Domain
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Outside_development_browsers_are_told_to_use_https_for_a_year()
    {
        using var response = await GetAsync("/account/login", environment: "Production");

        Assert.Equal("max-age=31536000", Single(response, "Strict-Transport-Security"));
    }

    [Fact]
    public async Task In_development_https_is_not_pinned()
    {
        // A browser keeps HSTS for the host: pinned on localhost, it would break every other local project on http.
        using var response = await GetAsync("/account/login", environment: "Development");

        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }

    private static string Single(HttpResponseMessage response, string name) =>
        Assert.Single(response.Headers.TryGetValues(name, out var values) ? values : response.Content.Headers.TryGetValues(name, out var content) ? content : []);

    private static async Task<HttpResponseMessage> GetAsync(string path, string environment = "Production")
    {
        await using var factory = new ApiFactory(ApiFactory.Settings(UnusedConnection), environment);
        using var client = factory.CreateApiClient(user: null);
        var response = await client.GetAsync(path);
        Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        await response.Content.LoadIntoBufferAsync();
        return response;
    }
}
