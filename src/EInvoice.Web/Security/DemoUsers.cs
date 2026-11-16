using EInvoice.Application.Security;
using EInvoice.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace EInvoice.Web.Security;

/// <summary>
/// DemoUsers:Password, from user secrets or an environment variable, never from a file in the repository.
/// Only allowed in Development; when it is not set, no demo accounts are created.
/// </summary>
public sealed class DemoUserSettings
{
    public const string Section = "DemoUsers";

    public string? Password { get; set; }
}

internal sealed class DemoUserSettingsValidator(IHostEnvironment environment) : IValidateOptions<DemoUserSettings>
{
    public ValidateOptionsResult Validate(string? name, DemoUserSettings options)
    {
        if (string.IsNullOrEmpty(options.Password))
        {
            return ValidateOptionsResult.Success;
        }

        if (!environment.IsDevelopment())
        {
            return ValidateOptionsResult.Fail("DemoUsers:Password is only allowed in Development: demo accounts must not exist anywhere else.");
        }

        return options.Password.Length < 12
            ? ValidateOptionsResult.Fail("DemoUsers:Password must have at least 12 characters, like every password.")
            : ValidateOptionsResult.Success;
    }
}

/// <summary>
/// Creates one demo account per role at startup, in Development and only when a demo password is set.
/// Existing accounts are left alone, so a restart never duplicates or resets them.
/// </summary>
public sealed class DemoUserSeeder(
    IServiceScopeFactory scopes,
    IOptions<DemoUserSettings> settings,
    IHostEnvironment environment,
    ILogger<DemoUserSeeder> logger) : IHostedService
{
    /// <summary>example.com is reserved for examples (RFC 2606): these names can never reach a real mailbox.</summary>
    public static IReadOnlyList<(string UserName, string Role)> Users { get; } =
    [
        ("clerk@example.com", Roles.Clerk),
        ("approver@example.com", Roles.Approver),
        ("auditor@example.com", Roles.Auditor),
        ("admin@example.com", Roles.Admin),
        ("admin2@example.com", Roles.Admin), // the two-person rule for bank accounts needs a second admin
    ];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var password = settings.Value.Password;
        if (!environment.IsDevelopment() || string.IsNullOrEmpty(password))
        {
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
        foreach (var (userName, role) in Users)
        {
            if (await users.FindByNameAsync(userName).ConfigureAwait(false) is not null)
            {
                continue;
            }

            var user = new AppUser { UserName = userName };
            Check(await users.CreateAsync(user, password).ConfigureAwait(false), userName);
            Check(await users.AddToRoleAsync(user, role).ConfigureAwait(false), userName);
            logger.LogInformation("Demo user {UserName} created with role {Role}.", userName, role);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // Identity's error codes only: never the password.
    private static void Check(IdentityResult result, string userName)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"Demo user {userName} could not be created: {string.Join(", ", result.Errors.Select(e => e.Code))}.");
        }
    }
}
