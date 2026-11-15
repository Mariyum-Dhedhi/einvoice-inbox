using EInvoice.Application.Security;
using EInvoice.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EInvoice.Infrastructure.Identity;

public sealed class EfUserDirectory(EInvoiceDbContext db, TimeProvider time) : IUserDirectory
{
    public async Task<IReadOnlyList<UserSummary>> ListAsync(CancellationToken cancellationToken)
    {
        var users = await db.Users.AsNoTracking()
            .OrderBy(u => u.NormalizedUserName)
            .ThenBy(u => u.Id)
            .Select(u => new { u.Id, u.UserName, u.LockoutEnd })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var memberships = await (from link in db.UserRoles.AsNoTracking()
                                 join role in db.Roles.AsNoTracking() on link.RoleId equals role.Id
                                 select new { link.UserId, role.Name })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var rolesByUser = memberships.ToLookup(m => m.UserId, m => m.Name!);

        var now = time.GetUtcNow();
        return users
            .Select(u => new UserSummary(
                u.UserName!,
                rolesByUser[u.Id].Order(StringComparer.Ordinal).ToList(),
                u.LockoutEnd > now ? u.LockoutEnd : null))
            .ToList();
    }
}
