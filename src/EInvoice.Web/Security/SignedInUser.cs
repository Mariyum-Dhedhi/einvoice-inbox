using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace EInvoice.Web.Security;

/// <summary>
/// The name recorded as submitter and actor: the signed-in user's name, never a placeholder.
/// Fails closed: without a signed-in user nothing is recorded (the endpoints require sign-in anyway).
/// </summary>
public static class SignedInUser
{
    public static string Name(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.Identity is { IsAuthenticated: true, Name: { Length: > 0 } name }
            ? name
            : throw new InvalidOperationException("No signed-in user.");
    }

    public static async Task<string> NameAsync(Task<AuthenticationState> state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return Name((await state.ConfigureAwait(false)).User);
    }
}
