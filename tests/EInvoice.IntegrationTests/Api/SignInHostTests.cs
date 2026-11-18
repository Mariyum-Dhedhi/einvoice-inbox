using System.Net;
using EInvoice.Web.Security;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace EInvoice.IntegrationTests.Api;

/// <summary>
/// Sign-in around the endpoints, without a database: everything is closed to anonymous callers except the login page
/// and static files, the API answers 401 instead of redirecting, and the security settings are the agreed ones.
/// </summary>
public sealed class SignInHostTests
{
    // Never opened by these tests.
    private const string UnusedConnection = "Server=127.0.0.1,1;Database=EInvoice;User Id=x;Password=y;Encrypt=True";

    private static readonly string AnyId = Guid.NewGuid().ToString();

    public static TheoryData<string, string> ApiEndpoints => new()
    {
        { "GET", "/api/invoices" },
        { "GET", $"/api/invoices/{AnyId}" },
        { "GET", $"/api/invoices/{AnyId}/document" },
        { "POST", "/api/invoices" },
        { "POST", $"/api/invoices/{AnyId}/match" },
    };

    [Theory]
    [MemberData(nameof(ApiEndpoints))]
    public async Task Anonymous_api_call_is_a_401_problem_not_a_redirect(string method, string path)
    {
        await using var factory = new ApiFactory(ApiFactory.Settings(UnusedConnection));
        using var client = factory.CreateApiClient(user: null);

        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/?status=Exception&page=2")]
    [InlineData("/invoices/3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    public async Task Anonymous_page_visit_goes_to_the_login_page_and_remembers_where_it_came_from(string page)
    {
        await using var factory = new ApiFactory(ApiFactory.Settings(UnusedConnection));
        using var client = factory.CreateApiClient(user: null);

        using var response = await client.GetAsync(new Uri(page, UriKind.Relative));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.Equal(SignInForm.LoginPath, location.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString.Split('?')[0]);
        Assert.Contains($"ReturnUrl={Uri.EscapeDataString(page)}", location.OriginalString, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SignInForm.LoginPath)]
    [InlineData("/app.css")]
    public async Task Login_page_and_static_files_are_open(string path)
    {
        await using var factory = new ApiFactory(ApiFactory.Settings(UnusedConnection));
        using var client = factory.CreateApiClient(user: null);

        using var response = await client.GetAsync(new Uri(path, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Login_page_is_a_plain_form_with_labelled_fields_and_an_antiforgery_token()
    {
        await using var factory = new ApiFactory(ApiFactory.Settings(UnusedConnection));
        using var client = factory.CreateApiClient(user: null);

        var html = await client.GetStringAsync(new Uri(SignInForm.LoginPath, UriKind.Relative));

        Assert.Contains("method=\"post\"", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("name=\"__RequestVerificationToken\"", html, StringComparison.Ordinal);
        Assert.Contains("for=\"username\"", html, StringComparison.Ordinal);
        Assert.Contains("for=\"password\"", html, StringComparison.Ordinal);
        Assert.Contains("autocomplete=\"username\"", html, StringComparison.Ordinal);
        Assert.Contains("autocomplete=\"current-password\"", html, StringComparison.Ordinal);
        // Plain server-rendered HTML: an interactive (WebSocket) page could not set the sign-in cookie.
        Assert.DoesNotContain("\"type\":\"server\"", html, StringComparison.Ordinal);
        Assert.Null(await SignInForm.ErrorAsync(new HttpResponseMessage { Content = new StringContent(html) }));
    }

    [Fact]
    public async Task Sign_out_only_accepts_a_post()
    {
        await using var factory = new ApiFactory(ApiFactory.Settings(UnusedConnection));
        using var client = factory.CreateApiClient();

        using var response = await client.GetAsync(new Uri(SignInForm.LogoutPath, UriKind.Relative));

        // A link or an image cannot sign anyone out.
        Assert.True(response.StatusCode is HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotFound, $"was {(int)response.StatusCode}");
    }

    [Fact]
    public async Task Password_and_lockout_rules_are_the_agreed_ones()
    {
        await using var factory = new ApiFactory(ApiFactory.Settings(UnusedConnection));
        var identity = factory.Services.GetRequiredService<IOptions<IdentityOptions>>().Value;

        Assert.Equal(12, identity.Password.RequiredLength); // length over composition rules (NIST SP 800-63B)
        Assert.True(identity.Lockout.AllowedForNewUsers);
        Assert.Equal(5, identity.Lockout.MaxFailedAccessAttempts);
        Assert.Equal(TimeSpan.FromMinutes(15), identity.Lockout.DefaultLockoutTimeSpan);
        Assert.False(identity.SignIn.RequireConfirmedAccount); // accounts are created by an administrator, not self-registered
    }

    [Fact]
    public async Task Sign_in_cookie_is_host_only_http_only_secure_and_expires_after_a_working_day()
    {
        await using var factory = new ApiFactory(ApiFactory.Settings(UnusedConnection));
        var cookie = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);

        Assert.Equal(SignInSetup.CookieName, cookie.Cookie.Name);
        Assert.StartsWith("__Host-", cookie.Cookie.Name, StringComparison.Ordinal); // browser enforces: Secure, Path=/, no Domain
        Assert.True(cookie.Cookie.HttpOnly);
        Assert.Equal(CookieSecurePolicy.Always, cookie.Cookie.SecurePolicy);
        Assert.Equal(SameSiteMode.Lax, cookie.Cookie.SameSite);
        Assert.Equal(TimeSpan.FromHours(8), cookie.ExpireTimeSpan);
        Assert.False(cookie.SlidingExpiration);
        Assert.Equal(SignInForm.LoginPath, cookie.LoginPath);
    }

    [Fact]
    public async Task Every_request_checks_that_the_sign_in_is_still_valid()
    {
        await using var factory = new ApiFactory(ApiFactory.Settings(UnusedConnection));
        var stamp = factory.Services.GetRequiredService<IOptions<SecurityStampValidatorOptions>>().Value;

        // A signed-out, locked or changed account loses access at its next request, not up to 30 minutes later.
        Assert.Equal(TimeSpan.Zero, stamp.ValidationInterval);
    }

    [Theory]
    [InlineData("Production", "a-long-enough-demo-password")]  // demo accounts never exist outside Development
    [InlineData("Staging", "a-long-enough-demo-password")]
    [InlineData("Development", "short")]                       // the password rules apply to demo users too
    public async Task Wrong_demo_user_setting_stops_the_application_at_startup(string environment, string password)
    {
        var settings = ApiFactory.Settings(UnusedConnection);
        settings["DemoUsers:Password"] = password;
        await using var factory = new ApiFactory(settings, environment);

        var error = Record.Exception(() => factory.CreateApiClient());

        var failure = Assert.IsType<OptionsValidationException>(error);
        Assert.Contains("DemoUsers:Password", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(password, failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Demo:SignInsPerHourPerAddress", "0")]
    [InlineData("Demo:SignInsPerHourPerAddress", "-5")]
    [InlineData("Demo:SignInsPerHourPerAddress", "100001")]
    [InlineData("Demo:UploadsPerHourPerUser", "0")]
    [InlineData("Demo:UploadsPerHourPerUser", "100001")]
    public async Task A_demo_limit_out_of_range_stops_the_application_at_startup(string key, string value)
    {
        var settings = ApiFactory.Settings(UnusedConnection);
        settings["Demo:Enabled"] = "true";
        settings[key] = value;
        await using var factory = new ApiFactory(settings);

        var error = Record.Exception(() => factory.CreateApiClient());

        var failure = Assert.IsType<OptionsValidationException>(error);
        Assert.Contains(key.Split(':')[1], failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_public_demo_is_meant_for_production_and_starts_there_without_any_password()
    {
        // Demo mode is not the Development demo users: no password setting, no seeding at startup (accounts are made per click).
        var settings = ApiFactory.Settings(UnusedConnection);
        settings["Demo:Enabled"] = "true";
        await using var factory = new ApiFactory(settings, "Production");

        var error = Record.Exception(() => factory.CreateApiClient());

        Assert.Null(error);
        Assert.True(factory.Services.GetRequiredService<IOptions<EInvoice.Web.Security.DemoSettings>>().Value.Enabled);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task Without_a_demo_password_startup_does_not_touch_demo_users(string environment)
    {
        // The connection points nowhere: a seeder that tried to create demo users would stop the start.
        await using var factory = new ApiFactory(ApiFactory.Settings(UnusedConnection), environment);

        var error = Record.Exception(() => factory.CreateApiClient());

        Assert.Null(error);
        Assert.Contains(factory.Services.GetServices<IHostedService>(), s => s is DemoUserSeeder);
    }
}
