using System.Net;
using System.Text;
using EInvoice.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace EInvoice.IntegrationTests.Api;

/// <summary>
/// The real login and logout forms against Identity in the test database: the cookie, the answers to wrong
/// passwords, lockout, antiforgery, return addresses, and that signing out really ends the session.
/// Every test creates its own user, so tests never share an account or its failed-attempt counter.
/// </summary>
[Trait("Category", "Database")]
[Collection(SqlServerCollection.Name)]
public sealed class SignInTests(SqlServerFixture fixture) : IAsyncLifetime
{
    private ApiFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new ApiFactory(ApiFactory.Settings(fixture.AppConnectionString));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private HttpClient Browser() => _factory.CreateApiClient(user: null);

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string path) => client.GetAsync(new Uri(path, UriKind.Relative));

    [Fact]
    public async Task Right_password_signs_in_and_returns_to_the_requested_page()
    {
        var (user, password) = await TestUsers.CreateAsync(_factory);
        using var browser = Browser();

        using var response = await SignInForm.SignInAsync(browser, user, password, returnUrl: "/?status=Exception");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/?status=Exception", SignInForm.Target(response));
        var cookie = SignInForm.Cookie(response, "__Host-einvoice");
        Assert.NotNull(cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("path=/", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", cookie, StringComparison.OrdinalIgnoreCase);

        using var api = await GetAsync(browser, "/api/invoices");
        Assert.Equal(HttpStatusCode.OK, api.StatusCode);
    }

    [Theory]
    [InlineData("Clerk", "/")]
    [InlineData("Approver", "/approvals")] // the queue of invoices waiting for them
    [InlineData("Auditor", "/")]
    [InlineData("Admin", "/admin/users")] // the admin has no invoice pages: starts on the user list
    [InlineData("Buyer", "/purchasing")] // the buyer has no invoice pages either: starts on the orders
    public async Task Without_a_return_address_each_role_starts_on_its_own_page(string role, string start)
    {
        var (user, password) = await TestUsers.CreateAsync(_factory, role);
        using var browser = Browser();

        using var response = await SignInForm.SignInAsync(browser, user, password);

        Assert.Equal(start, SignInForm.Target(response));
    }

    [Fact]
    public async Task An_admin_who_asked_for_a_page_still_goes_there()
    {
        // Where they wanted to go wins; the access check there decides, not the login page.
        var (user, password) = await TestUsers.CreateAsync(_factory, "Admin");
        using var browser = Browser();

        using var response = await SignInForm.SignInAsync(browser, user, password, returnUrl: "/account/access-denied");

        Assert.Equal("/account/access-denied", SignInForm.Target(response));
    }

    private async Task<List<(string Action, string Actor)>> AuditFor(string subject)
    {
        await using var db = fixture.CreateContext();
        return (await db.AuditEvents.AsNoTracking().Where(e => e.Subject == subject).OrderBy(e => e.Sequence)
            .Select(e => new { e.Action, e.Actor }).ToListAsync()).Select(e => (e.Action, e.Actor)).ToList();
    }

    [Fact]
    public async Task Signing_in_and_out_is_audited()
    {
        var (user, password) = await TestUsers.CreateAsync(_factory);
        using var browser = Browser();

        (await SignInForm.SignInAsync(browser, user, password)).Dispose();
        (await SignInForm.SignOutAsync(browser)).Dispose();

        Assert.Equal(new[] { ("SignedIn", user), ("SignedOut", user) }, await AuditFor($"User/{user}"));
    }

    [Fact]
    public async Task Failed_sign_ins_and_the_lockout_are_audited_but_never_the_password()
    {
        var (user, _) = await TestUsers.CreateAsync(_factory);
        using var browser = Browser();
        for (var i = 0; i < 5; i++)
        {
            (await SignInForm.SignInAsync(browser, user, "wrong-password-123")).Dispose();
        }

        (await SignInForm.SignInAsync(browser, user, "wrong-password-123")).Dispose(); // already locked

        var events = await AuditFor($"User/{user}");
        Assert.Equal(4, events.Count(e => e.Action == "SignInFailed"));
        Assert.Equal(2, events.Count(e => e.Action == "SignInLockedOut")); // the fifth attempt locks, the sixth meets the lock
        await using var db = fixture.CreateContext();
        Assert.False(await db.AuditEvents.AnyAsync(e => e.Details.Contains("wrong-password-123")));
    }

    [Fact]
    public async Task A_sign_in_for_an_unknown_name_is_audited_under_that_name()
    {
        var typed = $"nobody-{Guid.NewGuid():N}@example.com";
        using var browser = Browser();

        (await SignInForm.SignInAsync(browser, typed, "some-password-123")).Dispose();

        Assert.Equal(new[] { ("SignInFailed", typed) }, await AuditFor($"User/{typed}"));
    }

    [Fact]
    public async Task Unknown_user_wrong_password_and_locked_account_get_the_same_answer()
    {
        var (locked, lockedPassword) = await TestUsers.CreateAsync(_factory);
        using (var attacker = Browser())
        {
            for (var i = 0; i < 5; i++)
            {
                (await SignInForm.SignInAsync(attacker, locked, "wrong-password-123")).Dispose();
            }
        }

        var (user, _) = await TestUsers.CreateAsync(_factory);
        var answers = new List<(HttpStatusCode Status, string? Error, bool Cookie)>();
        foreach (var (name, password) in new[] { ($"nobody-{Guid.NewGuid():N}@example.com", "some-password-123"), (user, "wrong-password-123"), (locked, lockedPassword) })
        {
            using var browser = Browser();
            using var response = await SignInForm.SignInAsync(browser, name, password);
            answers.Add((response.StatusCode, await SignInForm.ErrorAsync(response), SignInForm.Cookie(response, "__Host-einvoice") is not null));
        }

        // Nobody can find out from the answer whether an account exists or is locked.
        Assert.All(answers, a => Assert.Equal((HttpStatusCode.OK, SignInForm.FailureMessage, false), a));
    }

    [Theory]
    [InlineData("a-wrong-password-4711")] // the form is shown again with the error
    [InlineData("")]                      // with an empty user name: shown again with a validation message
    public async Task A_shown_again_form_never_contains_the_password(string userName)
    {
        var (user, _) = await TestUsers.CreateAsync(_factory);
        const string password = "typed-secret-0815-xyz";
        using var browser = Browser();

        using var response = await SignInForm.SignInAsync(browser, userName.Length == 0 ? userName : user, password);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(password, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account_even_against_the_right_one()
    {
        var (user, password) = await TestUsers.CreateAsync(_factory);
        using var browser = Browser();
        for (var i = 0; i < 5; i++)
        {
            (await SignInForm.SignInAsync(browser, user, "wrong-password-123")).Dispose();
        }

        using var response = await SignInForm.SignInAsync(browser, user, password);

        Assert.Null(SignInForm.Cookie(response, "__Host-einvoice"));
        var stored = await TestUsers.FindAsync(_factory, user);
        Assert.True(stored.LockoutEnd > DateTimeOffset.UtcNow.AddMinutes(14), "locked for about 15 minutes");
    }

    [Fact]
    public async Task Four_wrong_passwords_then_the_right_one_still_signs_in()
    {
        var (user, password) = await TestUsers.CreateAsync(_factory);
        using var browser = Browser();
        for (var i = 0; i < 4; i++)
        {
            (await SignInForm.SignInAsync(browser, user, "wrong-password-123")).Dispose();
        }

        using var response = await SignInForm.SignInAsync(browser, user, password);

        Assert.NotNull(SignInForm.Cookie(response, "__Host-einvoice"));
        Assert.Equal(0, (await TestUsers.FindAsync(_factory, user)).AccessFailedCount); // the counter starts again
    }

    [Fact]
    public async Task Login_without_antiforgery_token_is_refused()
    {
        var (user, password) = await TestUsers.CreateAsync(_factory);
        using var browser = Browser();

        using var response = await SignInForm.SignInAsync(browser, user, password, withToken: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(SignInForm.Cookie(response, "__Host-einvoice"));
    }

    [Theory]
    [InlineData("https://evil.example/")]
    [InlineData("//evil.example/")]
    [InlineData("/\\evil.example/")]
    [InlineData("javascript:alert(1)")]
    public async Task Return_address_on_another_site_is_ignored(string returnUrl)
    {
        var (user, password) = await TestUsers.CreateAsync(_factory);
        using var browser = Browser();

        using var response = await SignInForm.SignInAsync(browser, user, password, returnUrl);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", SignInForm.Target(response));
    }

    [Fact]
    public async Task Sign_out_ends_the_session_and_the_old_cookie_no_longer_works()
    {
        var (user, password) = await TestUsers.CreateAsync(_factory);
        using var browser = Browser();
        using var signIn = await SignInForm.SignInAsync(browser, user, password);
        var oldCookie = SignInForm.Cookie(signIn, "__Host-einvoice")!.Split(';')[0];

        using var signOut = await SignInForm.SignOutAsync(browser);

        Assert.Equal(HttpStatusCode.Redirect, signOut.StatusCode);
        Assert.StartsWith(SignInForm.LoginPath, SignInForm.Target(signOut), StringComparison.Ordinal);
        using (var afterwards = await GetAsync(browser, "/api/invoices"))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, afterwards.StatusCode);
        }

        // A copy of the cookie taken before signing out (another tab, a stolen copy) is worthless too.
        using var replay = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = ApiFactory.BaseAddress, AllowAutoRedirect = false, HandleCookies = false });
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/invoices");
        request.Headers.Add("Cookie", oldCookie);
        using var replayed = await replay.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, replayed.StatusCode);
    }

    [Fact]
    public async Task Sign_out_without_antiforgery_token_is_refused()
    {
        var (user, password) = await TestUsers.CreateAsync(_factory);
        using var browser = Browser();
        (await SignInForm.SignInAsync(browser, user, password)).Dispose();

        using var response = await SignInForm.SignOutAsync(browser, withToken: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var still = await GetAsync(browser, "/api/invoices");
        Assert.Equal(HttpStatusCode.OK, still.StatusCode); // another site cannot sign the user out either
    }

    [Fact]
    public async Task Uploads_record_the_signed_in_user_not_anonymous()
    {
        var (user, password) = await TestUsers.CreateAsync(_factory);
        using var browser = Browser();
        (await SignInForm.SignInAsync(browser, user, password)).Dispose();
        var unreadable = Encoding.UTF8.GetBytes($"<Invoice><!-- {Guid.NewGuid()} --><unclosed></Invoice>"); // stored as Failed

        using var upload = await browser.PostAsync(new Uri("/api/invoices", UriKind.Relative), ApiHttp.Upload(unreadable));
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);
        var id = (await upload.JsonAsync()).GetProperty("invoiceId").GetGuid();

        using var detail = await GetAsync(browser, $"/api/invoices/{id}");
        var json = await detail.JsonAsync();
        Assert.Equal(user, json.GetProperty("submittedBy").GetString());
        var history = json.GetProperty("history").EnumerateArray().ToList();
        Assert.Equal(user, history[0].GetProperty("actor").GetString()); // received from this user
        Assert.Equal("system", history[^1].GetProperty("actor").GetString()); // the automatic check that failed it
        Assert.DoesNotContain(history, change => change.GetProperty("actor").GetString() == "anonymous");
    }
}
