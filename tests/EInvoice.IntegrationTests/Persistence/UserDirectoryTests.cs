using EInvoice.Application.Security;
using EInvoice.Infrastructure.Identity;
using EInvoice.IntegrationTests.Api;
using EInvoice.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace EInvoice.IntegrationTests.Persistence;

/// <summary>The admin's read-only user list: every account with its roles and lockout, in a defined order.</summary>
[Trait("Category", "Database")]
[Collection(SqlServerCollection.Name)]
public sealed class UserDirectoryTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task Lists_accounts_by_name_with_their_roles_and_lockout()
    {
        await using var factory = new ApiFactory(ApiFactory.Settings(fixture.AppConnectionString));
        var prefix = $"dir-{Guid.NewGuid():N}";
        var now = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        await CreateAsync(factory, $"{prefix}-c@example.com", lockedUntil: null, Roles.Auditor, Roles.Approver);
        await CreateAsync(factory, $"{prefix}-a@example.com", lockedUntil: now.AddMinutes(10), Roles.Clerk);
        await CreateAsync(factory, $"{prefix}-b@example.com", lockedUntil: now.AddMinutes(-10)); // lockout over, no role

        await using var db = fixture.CreateContext();
        var directory = new EfUserDirectory(db, new FixedTime(now));
        var mine = (await directory.ListAsync(CancellationToken.None)).Where(u => u.UserName.StartsWith(prefix, StringComparison.Ordinal)).ToList();

        Assert.Equal(new[] { $"{prefix}-a@example.com", $"{prefix}-b@example.com", $"{prefix}-c@example.com" }, mine.Select(u => u.UserName));
        Assert.Equal(new[] { Roles.Clerk }, mine[0].Roles);
        Assert.Equal(now.AddMinutes(10), mine[0].LockedUntil);
        Assert.Empty(mine[1].Roles);
        Assert.Null(mine[1].LockedUntil); // an expired lockout is no lockout
        Assert.Equal(new[] { Roles.Approver, Roles.Auditor }, mine[2].Roles); // roles in a fixed order too
        Assert.Null(mine[2].LockedUntil);
    }

    [Fact]
    public async Task Shows_no_password_hash_or_security_stamp()
    {
        // The summary type itself carries nothing secret: only name, roles and lockout.
        var properties = typeof(UserSummary).GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal);

        Assert.Equal(new[] { "LockedUntil", "Roles", "UserName" }, properties);
        await Task.CompletedTask;
    }

    private static async Task CreateAsync(ApiFactory factory, string userName, DateTimeOffset? lockedUntil, params string[] roles)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = new AppUser { UserName = userName };
        Assert.True((await users.CreateAsync(user, $"Pw-{Guid.NewGuid():N}")).Succeeded);
        foreach (var role in roles)
        {
            Assert.True((await users.AddToRoleAsync(user, role)).Succeeded);
        }

        if (lockedUntil is not null)
        {
            Assert.True((await users.SetLockoutEndDateAsync(user, lockedUntil)).Succeeded);
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
