using System.Net;
using System.Text.RegularExpressions;
using EInvoice.Infrastructure.Identity;
using EInvoice.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EInvoice.IntegrationTests.Api;

/// <summary>
/// The public demo's one-click sign-in (Demo:Enabled), over real HTTP against Identity in the test database. Each click
/// makes a new account of one role with no password at all, so visitors never share a session: one signing out (which
/// renews the security stamp) does not end another's. Off unless switched on. Runs as Production, where it is meant to run.
/// </summary>
[Trait("Category", "Database")]
[Collection(SqlServerCollection.Name)]
public sealed partial class DemoSignInTests(SqlServerFixture fixture) : IAsyncLifetime
{
    private const string DemoPath = "/account/demo";
    private const string SignInCookie = "__Host-einvoice";

    private ApiFactory _demo = null!;
    private ApiFactory _off = null!;

    public Task InitializeAsync()
    {
        _demo = new ApiFactory(With(fixture.AppConnectionString, ("Demo:Enabled", "true"), ("Demo:SignInsPerHourPerAddress", "1000")));
        _off = new ApiFactory(ApiFactory.Settings(fixture.AppConnectionString));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _demo.DisposeAsync();
        await _off.DisposeAsync();
    }

    internal static Dictionary<string, string?> With(string connection, params (string Key, string Value)[] extra)
    {
        var settings = ApiFactory.Settings(connection);
        foreach (var (key, value) in extra)
        {
            settings[key] = value;
        }

        return settings;
    }

    private static HttpClient Browser(ApiFactory factory) => factory.CreateApiClient(user: null);

    internal static async Task<HttpResponseMessage> ClickAsync(HttpClient browser, string role, bool withToken = true)
    {
        var form = new Dictionary<string, string> { ["role"] = role };
        if (withToken)
        {
            form["__RequestVerificationToken"] = await SignInForm.TokenAsync(browser, SignInForm.LoginPath);
        }

        return await browser.PostAsync(new Uri(DemoPath, UriKind.Relative), new FormUrlEncodedContent(form));
    }

    private async Task<int> DemoAccountCountAsync()
    {
        await using var db = fixture.CreateContext();
        return await db.Users.CountAsync(u => u.UserName!.StartsWith("demo-"));
    }

    [Theory]
    [InlineData("Clerk", "/")]
    [InlineData("Approver", "/approvals")]
    [InlineData("Auditor", "/")]
    [InlineData("Admin", "/admin/users")]
    [InlineData("Buyer", "/purchasing")]
    public async Task One_click_signs_in_as_a_new_account_of_that_role_on_its_start_page(string role, string start)
    {
        using var browser = Browser(_demo);

        using var response = await ClickAsync(browser, role);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(start, SignInForm.Target(response));
        Assert.NotNull(SignInForm.Cookie(response, SignInCookie));
        using var me = await browser.GetAsync(new Uri("/", UriKind.Relative)); // signed in: no way back to the login page
        Assert.NotEqual(SignInForm.LoginPath, me.Headers.Location?.AbsolutePath);
    }

    [Fact]
    public async Task The_account_has_exactly_that_role_a_reserved_name_and_no_password()
    {
        using var browser = Browser(_demo);
        using var response = await ClickAsync(browser, "Approver");

        var name = await SignedInNameAsync(browser);
        Assert.Matches(DemoName(), name);
        Assert.StartsWith("demo-approver-", name, StringComparison.Ordinal);
        await using var scope = _demo.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = await users.FindByNameAsync(name);
        Assert.NotNull(user);
        Assert.Equal(["Approver"], await users.GetRolesAsync(user));
        Assert.False(await users.HasPasswordAsync(user)); // nobody can sign in to it with a password, ever
    }

    [Fact]
    public async Task Every_click_is_a_new_account_and_one_visitor_signing_out_does_not_end_another_session()
    {
        using var first = Browser(_demo);
        using var second = Browser(_demo);
        (await ClickAsync(first, "Clerk")).Dispose();
        (await ClickAsync(second, "Clerk")).Dispose();

        Assert.NotEqual(await SignedInNameAsync(first), await SignedInNameAsync(second));

        (await SignInForm.SignOutAsync(first)).Dispose();
        using var stillIn = await second.GetAsync(new Uri("/api/invoices", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, stillIn.StatusCode);
        using var signedOut = await first.GetAsync(new Uri("/api/invoices", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, signedOut.StatusCode);
    }

    [Fact]
    public async Task The_demo_sign_in_is_audited_like_any_other()
    {
        using var browser = Browser(_demo);
        (await ClickAsync(browser, "Auditor")).Dispose();
        var name = await SignedInNameAsync(browser);

        await using var db = fixture.CreateContext();
        var events = await db.AuditEvents.AsNoTracking().Where(e => e.Subject == $"User/{name}").Select(e => new { e.Action, e.Actor }).ToListAsync();
        Assert.Equal([("SignedIn", name)], events.Select(e => (e.Action, e.Actor)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Owner")]
    [InlineData("clerk")]          // role names are exact
    [InlineData("Clerk,Approver")] // one role per account: separated duties stay separated
    public async Task An_unknown_role_is_refused_and_no_account_is_made(string role)
    {
        using var browser = Browser(_demo);
        var before = await DemoAccountCountAsync();

        using var response = await ClickAsync(browser, role);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Null(SignInForm.Cookie(response, SignInCookie));
        Assert.Equal(before, await DemoAccountCountAsync());
    }

    [Fact]
    public async Task Without_the_form_token_no_account_is_made()
    {
        using var browser = Browser(_demo);
        var before = await DemoAccountCountAsync();

        using var response = await ClickAsync(browser, "Clerk", withToken: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(SignInForm.Cookie(response, SignInCookie));
        Assert.Equal(before, await DemoAccountCountAsync());
    }

    [Fact]
    public async Task Switched_off_there_is_no_demo_sign_in_and_no_buttons()
    {
        using var browser = Browser(_off);

        using var response = await ClickAsync(browser, "Clerk");
        var page = await (await browser.GetAsync(new Uri(SignInForm.LoginPath, UriKind.Relative))).Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Null(SignInForm.Cookie(response, SignInCookie));
        Assert.DoesNotContain("data-testid=\"demo-sign-in\"", page, StringComparison.Ordinal);
        Assert.DoesNotContain("data-testid=\"demo-banner\"", page, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Switched_on_the_login_page_offers_one_form_per_role_and_says_it_is_a_demo()
    {
        using var browser = Browser(_demo);

        var page = await (await browser.GetAsync(new Uri(SignInForm.LoginPath, UriKind.Relative))).Content.ReadAsStringAsync();

        var roles = DemoForm().Matches(page).Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(["Clerk", "Approver", "Auditor", "Admin", "Buyer"], roles);
        Assert.Contains("data-testid=\"demo-banner\"", page, StringComparison.Ordinal);
        Assert.Contains("name=\"Input.Password\"", page, StringComparison.Ordinal); // the normal sign-in stays
    }

    [Fact]
    public async Task Too_many_demo_sign_ins_from_one_address_are_refused_without_making_accounts()
    {
        await using var limited = new ApiFactory(With(fixture.AppConnectionString, ("Demo:Enabled", "true"), ("Demo:SignInsPerHourPerAddress", "2")));
        using var browser = Browser(limited);
        (await ClickAsync(browser, "Clerk")).Dispose();
        (await ClickAsync(browser, "Clerk")).Dispose();
        var before = await DemoAccountCountAsync();

        using var response = await ClickAsync(browser, "Clerk");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Null(SignInForm.Cookie(response, SignInCookie));
        Assert.Equal(before, await DemoAccountCountAsync());
    }

    [Fact]
    public async Task The_password_form_is_limited_per_address_too_in_the_public_demo()
    {
        // Lockout protects one account; the limit protects the site from someone trying many names.
        await using var limited = new ApiFactory(With(fixture.AppConnectionString, ("Demo:Enabled", "true"), ("Demo:SignInsPerHourPerAddress", "2")));
        using var browser = Browser(limited);
        (await SignInForm.SignInAsync(browser, $"nobody-{Guid.NewGuid():N}@example.com", "some-password-123")).Dispose();
        (await SignInForm.SignInAsync(browser, $"nobody-{Guid.NewGuid():N}@example.com", "some-password-123")).Dispose();

        using var response = await SignInForm.SignInAsync(browser, $"nobody-{Guid.NewGuid():N}@example.com", "some-password-123");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

    /// <summary>The name the app shows for the signed-in user (the layout's signed-in marker on the inbox or start page).</summary>
    private static async Task<string> SignedInNameAsync(HttpClient browser)
    {
        foreach (var page in new[] { "/", "/approvals", "/admin/users", "/purchasing" })
        {
            var html = await (await browser.GetAsync(new Uri(page, UriKind.Relative))).Content.ReadAsStringAsync();
            if (SignedIn().Match(html) is { Success: true } m)
            {
                return WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
            }
        }

        throw new InvalidOperationException("Not signed in.");
    }

    [GeneratedRegex(@"^demo-(clerk|approver|auditor|admin|buyer)-[a-z0-9]{8}@example\.com$")]
    private static partial Regex DemoName();

    [GeneratedRegex(@"data-testid=""signed-in-user""[^>]*>([^<]*)<")]
    private static partial Regex SignedIn();

    [GeneratedRegex(@"<form[^>]*action=""/account/demo""[^>]*data-testid=""demo-sign-in""[^>]*>.*?name=""role""[^>]*value=""([A-Za-z]+)""", RegexOptions.Singleline)]
    private static partial Regex DemoForm();
}
