using Microsoft.AspNetCore.Identity;

namespace EInvoice.Infrastructure.Identity;

/// <summary>
/// A person who signs in. Identity keeps the password only as a salted hash and counts failed attempts for lockout.
/// The user name is what invoices record as submitter and actor, so it is never changed once used.
/// </summary>
public sealed class AppUser : IdentityUser<Guid>
{
    public AppUser()
    {
        Id = Guid.NewGuid(); // created by the code, like every other key
    }
}

/// <summary>One of the four roles in <see cref="EInvoice.Application.Security.Roles"/>; created with the schema.</summary>
public sealed class AppRole : IdentityRole<Guid>
{
}
