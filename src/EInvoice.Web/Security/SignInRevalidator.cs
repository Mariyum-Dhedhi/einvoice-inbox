using System.Security.Claims;
using EInvoice.Infrastructure.Identity;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace EInvoice.Web.Security;

/// <summary>
/// An interactive page keeps its connection open for hours, without new HTTP requests that would check the cookie.
/// This re-checks the account every minute: when it was signed out elsewhere, locked or deleted, the page loses its
/// sign-in and goes back to the login page.
/// </summary>
public sealed class SignInRevalidator(ILoggerFactory loggerFactory, IServiceScopeFactory scopes, IOptions<IdentityOptions> options)
    : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);

    protected override Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken) =>
        IsStillValidAsync(authenticationState.User, cancellationToken);

    /// <summary>True while the account exists and its security stamp is the one the sign-in was issued with.</summary>
    public async Task<bool> IsStillValidAsync(ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(principal);
        await using var scope = scopes.CreateAsyncScope(); // its own DbContext: never share one with the page
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        if (await users.GetUserAsync(principal).ConfigureAwait(false) is not { } user)
        {
            return false;
        }

        var issuedWith = principal.FindFirstValue(options.Value.ClaimsIdentity.SecurityStampClaimType);
        return issuedWith is not null && issuedWith == await users.GetSecurityStampAsync(user).ConfigureAwait(false);
    }
}
