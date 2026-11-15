using EInvoice.Application.Security;
using EInvoice.Infrastructure.Identity;
using EInvoice.IntegrationTests.Api;
using EInvoice.IntegrationTests.Infrastructure;
using EInvoice.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EInvoice.IntegrationTests.Persistence;

/// <summary>
/// Users and roles in the database built from the db/ scripts: the roles come with the schema, the app user can
/// manage accounts, demo users appear only when asked for, and a changed account ends open pages' sign-in.
/// </summary>
[Trait("Category", "Database")]
[Collection(SqlServerCollection.Name)]
public sealed class IdentityStoreTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task The_four_roles_come_with_the_schema()
    {
        await using var db = fixture.CreateContext();

        var roles = await db.Roles.Select(r => r.Name!).OrderBy(n => n).ToListAsync();

        Assert.Equal(Roles.All.Order(StringComparer.Ordinal), roles);
        Assert.Equal(new[] { "Admin", "Approver", "Auditor", "Clerk" }, Roles.All.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Passwords_are_stored_only_as_hashes()
    {
        await using var factory = new ApiFactory(ApiFactory.Settings(fixture.AppConnectionString));
        var (userName, password) = await TestUsers.CreateAsync(factory, Roles.Clerk);

        var stored = await TestUsers.FindAsync(factory, userName);

        Assert.NotNull(stored.PasswordHash);
        Assert.DoesNotContain(password, stored.PasswordHash, StringComparison.Ordinal);
        Assert.StartsWith("AQAAAA", stored.PasswordHash, StringComparison.Ordinal); // Identity v3 format: PBKDF2 with salt
    }

    [Fact]
    public async Task Demo_users_are_created_once_per_role_and_can_sign_in()
    {
        var password = $"Demo-{Guid.NewGuid():N}";
        var settings = ApiFactory.Settings(fixture.AppConnectionString);
        settings["DemoUsers:Password"] = password;

        // Started twice, as after a restart: still one account per demo user.
        for (var start = 0; start < 2; start++)
        {
            await using var factory = new ApiFactory(settings, environment: "Development");
            using var client = factory.CreateApiClient(user: null); // starts the host and its seeder
        }

        await using var check = new ApiFactory(ApiFactory.Settings(fixture.AppConnectionString));
        await using var scope = check.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        Assert.Equal(Roles.All.Order(StringComparer.Ordinal), DemoUserSeeder.Users.Select(u => u.Role).Order(StringComparer.Ordinal));
        foreach (var (userName, role) in DemoUserSeeder.Users)
        {
            Assert.EndsWith("@example.com", userName, StringComparison.Ordinal); // reserved for examples: never a real mailbox
            var user = await users.FindByNameAsync(userName);
            Assert.NotNull(user);
            Assert.Equal(new[] { role }, await users.GetRolesAsync(user));
            Assert.True(await users.CheckPasswordAsync(user, password));
        }

        Assert.Equal(DemoUserSeeder.Users.Count, await users.Users.CountAsync(u => DemoUserSeeder.Users.Select(d => d.UserName.ToUpperInvariant()).Contains(u.NormalizedUserName!)));
    }

    [Fact]
    public async Task An_open_page_loses_its_sign_in_when_the_account_changes()
    {
        await using var factory = new ApiFactory(ApiFactory.Settings(fixture.AppConnectionString));
        var (userName, _) = await TestUsers.CreateAsync(factory, Roles.Clerk);
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var signIn = scope.ServiceProvider.GetRequiredService<SignInManager<AppUser>>();
        var user = (await users.FindByNameAsync(userName))!;
        var principal = await signIn.CreateUserPrincipalAsync(user);
        var revalidator = ActivatorUtilities.CreateInstance<SignInRevalidator>(scope.ServiceProvider);

        Assert.True(await revalidator.IsStillValidAsync(principal, CancellationToken.None));

        await users.UpdateSecurityStampAsync(user); // signed out elsewhere, password changed, or locked by an admin

        Assert.False(await revalidator.IsStillValidAsync(principal, CancellationToken.None));
    }

    [Fact]
    public async Task An_open_page_loses_its_sign_in_when_the_account_is_deleted()
    {
        await using var factory = new ApiFactory(ApiFactory.Settings(fixture.AppConnectionString));
        var (userName, _) = await TestUsers.CreateAsync(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        var user = (await users.FindByNameAsync(userName))!;
        var principal = await scope.ServiceProvider.GetRequiredService<SignInManager<AppUser>>().CreateUserPrincipalAsync(user);
        var revalidator = ActivatorUtilities.CreateInstance<SignInRevalidator>(scope.ServiceProvider);

        await users.DeleteAsync(user);

        Assert.False(await revalidator.IsStillValidAsync(principal, CancellationToken.None));
    }
}
