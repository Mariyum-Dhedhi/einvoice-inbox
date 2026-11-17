using System.Net;
using System.Text.RegularExpressions;
using EInvoice.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace EInvoice.IntegrationTests.Api;

/// <summary>
/// Drives the real login and logout forms over HTTP, the way a browser does: read the page, take its antiforgery
/// token, post the form. The client's cookie jar keeps the antiforgery and sign-in cookies.
/// </summary>
internal static partial class SignInForm
{
    public const string LoginPath = "/account/login";
    public const string LogoutPath = "/account/logout";

    /// <summary>The generic answer to every failed sign-in: it never tells whether the user exists or is locked out.</summary>
    public const string FailureMessage = "Sign-in failed. Check your user name and password, or try again later.";

    public static async Task<HttpResponseMessage> SignInAsync(
        HttpClient client, string userName, string password, string? returnUrl = null, bool withToken = true)
    {
        var form = new Dictionary<string, string>
        {
            ["_handler"] = "login",
            ["Input.UserName"] = userName,
            ["Input.Password"] = password,
        };
        if (withToken)
        {
            form["__RequestVerificationToken"] = await TokenAsync(client, LoginPath);
        }

        var path = returnUrl is null ? LoginPath : $"{LoginPath}?ReturnUrl={Uri.EscapeDataString(returnUrl)}";
        return await client.PostAsync(new Uri(path, UriKind.Relative), new FormUrlEncodedContent(form));
    }

    /// <summary>Posts the logout form of the layout. The token is read from a page as the signed-in user sees it.</summary>
    public static async Task<HttpResponseMessage> SignOutAsync(HttpClient client, bool withToken = true)
    {
        var form = new Dictionary<string, string>();
        if (withToken)
        {
            form["__RequestVerificationToken"] = await TokenAsync(client, "/");
        }

        return await client.PostAsync(new Uri(LogoutPath, UriKind.Relative), new FormUrlEncodedContent(form));
    }

    /// <summary>The text of the login page's error message, or null when it shows none.</summary>
    public static async Task<string?> ErrorAsync(HttpResponseMessage response)
    {
        var match = Error().Match(await response.Content.ReadAsStringAsync());
        return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value).Trim() : null;
    }

    /// <summary>Where a redirect goes, as path and query. Fails when it leaves this site.</summary>
    public static string Target(HttpResponseMessage response)
    {
        var location = response.Headers.Location ?? throw new InvalidOperationException($"No redirect ({(int)response.StatusCode}).");
        if (!location.IsAbsoluteUri)
        {
            return location.OriginalString;
        }

        Assert.Equal(ApiFactory.BaseAddress.Authority, location.Authority);
        return location.PathAndQuery;
    }

    public static string? Cookie(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(v => v.StartsWith(name + "=", StringComparison.Ordinal))
            : null;

    /// <summary>The antiforgery token of a page's forms, as a browser would post it back.</summary>
    public static async Task<string> TokenAsync(HttpClient client, string page)
    {
        using var response = await client.GetAsync(new Uri(page, UriKind.Relative));
        var html = await response.Content.ReadAsStringAsync();
        var match = Token().Match(html);
        Assert.True(match.Success, $"{page} has no antiforgery token ({(int)response.StatusCode})");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    [GeneratedRegex(@"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""")]
    private static partial Regex Token();

    [GeneratedRegex("""data-testid="login-error"[^>]*>([^<]*)<""")]
    private static partial Regex Error();
}

/// <summary>Real users in the test database, with unique names and random passwords, created through Identity.</summary>
internal static class TestUsers
{
    /// <summary>A new account with the given roles; without any given, a clerk (like the default test client).</summary>
    public static async Task<(string UserName, string Password)> CreateAsync(ApiFactory factory, params string[] roles)
    {
        if (roles.Length == 0)
        {
            roles = ["Clerk"];
        }

        var userName = $"user-{Guid.NewGuid():N}@example.com";
        var password = $"Pw-{Guid.NewGuid():N}";
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = new AppUser { UserName = userName };
        Succeeded(await users.CreateAsync(user, password));
        foreach (var role in roles)
        {
            Succeeded(await users.AddToRoleAsync(user, role));
        }

        return (userName, password);
    }

    public static async Task<AppUser> FindAsync(ApiFactory factory, string userName)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        return await users.FindByNameAsync(userName) ?? throw new InvalidOperationException("Test user not found.");
    }

    private static void Succeeded(IdentityResult result) =>
        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Code)));
}
