using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using EInvoice.Application.Security;
using EInvoice.Application.Sync;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Options;

namespace EInvoice.Web.Security;

/// <summary>
/// Sync:*. ApiKeySha256 is the SHA-256 (64 hex digits) of the ERP sync's API key; the key itself is only with the sync
/// (an environment variable of its container). Not set: no key works, the sync API is closed.
/// </summary>
public sealed class SyncSettings
{
    public const string Section = "Sync";

    public string? ApiKeySha256 { get; set; }
}

internal sealed class SyncSettingsValidator : IValidateOptions<SyncSettings>
{
    public ValidateOptionsResult Validate(string? name, SyncSettings options) =>
        options.ApiKeySha256 is not { Length: > 0 } hash || (hash.Length == 64 && hash.All(Uri.IsHexDigit))
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail("Sync:ApiKeySha256 must be the SHA-256 of the key: 64 hexadecimal digits (or not set, to close the sync API).");
}

/// <summary>
/// "Authorization: ApiKey &lt;key&gt;": the ERP sync, a program. The key's SHA-256 is compared in constant time with the
/// configured one; a match signs the request in as "erp-sync" with the Integration role, for this request only. No user
/// account is involved, so no person can sign in this way, and the role cannot be given to an account. A refusal is
/// 401 or 403 problem details, never a redirect, and never repeats what was sent.
/// </summary>
public sealed class SyncApiKeyHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<SyncSettings> settings,
    ProblemDetailsFactory problems)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";

    /// <summary>Does the request say it is the sync? Then this scheme decides about it, nothing else.</summary>
    public static bool Claims(HttpRequest request) =>
        request.Headers.Authorization.ToString() is var value
        && (value.Equals(SchemeName, StringComparison.OrdinalIgnoreCase) || value.StartsWith(SchemeName + " ", StringComparison.OrdinalIgnoreCase));

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Claims(Request))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var key = Request.Headers.Authorization.ToString()[SchemeName.Length..].Trim();
        if (key.Length == 0 || settings.Value.ApiKeySha256 is not { Length: 64 } configured)
        {
            return Task.FromResult(AuthenticateResult.Fail("No API key, or the sync API is closed."));
        }

        var expected = Convert.FromHexString(configured);
        var given = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        if (!CryptographicOperations.FixedTimeEquals(given, expected))
        {
            return Task.FromResult(AuthenticateResult.Fail("Wrong API key."));
        }

        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, ErpSync.Actor), new Claim(ClaimTypes.Role, Roles.Integration)], SchemeName, ClaimTypes.Name, ClaimTypes.Role);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = SchemeName;
        return WriteAsync(StatusCodes.Status401Unauthorized, "Send the sync's API key as 'Authorization: ApiKey <key>'.");
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        WriteAsync(StatusCodes.Status403Forbidden, "The sync's API key gives access to the sync API only.");

    private Task WriteAsync(int status, string detail)
    {
        var problem = problems.CreateProblemDetails(Context, status, detail: detail);
        Response.StatusCode = status;
        return Response.WriteAsJsonAsync(problem, (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json");
    }
}
