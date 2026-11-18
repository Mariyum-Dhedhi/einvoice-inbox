using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using EInvoice.Application.Audit;
using EInvoice.Application.Intake;
using EInvoice.Application.Security;
using EInvoice.Infrastructure.Identity;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace EInvoice.Web.Security;

/// <summary>
/// The public demo (Demo:Enabled, off by default): one-click sign-in per role, a banner on every page, and limits per
/// address and per person. Meant for Production behind a tunnel; it has nothing to do with the Development demo users
/// (DemoUsers:Password), which stay Development-only.
/// </summary>
public sealed class DemoSettings
{
    public const string Section = "Demo";

    public bool Enabled { get; set; }

    /// <summary>Sign-in attempts (password form and demo buttons together) per client address and hour.</summary>
    [Range(1, 100_000)]
    public int SignInsPerHourPerAddress { get; set; } = 20;

    /// <summary>Uploads per signed-in person and hour (UploadLimit).</summary>
    [Range(1, 100_000)]
    public int UploadsPerHourPerUser { get; set; } = 30;
}

/// <summary>
/// One click, one new account: "demo-clerk-x7k2m9qa@example.com" with exactly one role and no password, so nobody can
/// ever sign in to it with one, and visitors never share a session (signing out renews the account's security stamp,
/// which would end every other visitor's session on a shared account). The nightly reset removes the accounts.
/// </summary>
public static class DemoSignIn
{
    public const string Path = "/account/demo";

    /// <summary>The roles offered, in the order the login page shows them.</summary>
    public static IReadOnlyList<string> Roles { get; } =
        [Application.Security.Roles.Clerk, Application.Security.Roles.Approver, Application.Security.Roles.Auditor, Application.Security.Roles.Admin, Application.Security.Roles.Buyer];

    private const string NameLetters = "abcdefghijklmnopqrstuvwxyz0123456789";
    private const string SignInLimit = "demo-sign-in";

    internal static IServiceCollection AddEInvoiceDemo(this IServiceCollection services)
    {
        services.AddOptions<DemoSettings>().BindConfiguration(DemoSettings.Section).ValidateDataAnnotations().ValidateOnStart();

        // Uploads: the limit wraps the intake, so the API and the inbox page both keep it.
        // The count lives for the application (singleton); the intake per request.
        services.AddSingleton(provider => new UploadCounter(
            provider.GetRequiredService<IOptions<DemoSettings>>().Value.UploadsPerHourPerUser, provider.GetRequiredService<TimeProvider>()));
        services.Decorate<IInvoiceIntake>((inner, provider) =>
            provider.GetRequiredService<IOptions<DemoSettings>>().Value.Enabled
                ? new UploadLimit(inner, provider.GetRequiredService<UploadCounter>())
                : inner);

        // Sign-ins: POSTs to the login form and the demo buttons, per client address, only in the public demo.
        services.AddRateLimiter(limiter =>
        {
            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            {
                var demo = context.RequestServices.GetRequiredService<IOptions<DemoSettings>>().Value;
                if (!demo.Enabled || !HttpMethods.IsPost(context.Request.Method) || !IsSignIn(context.Request.Path))
                {
                    return RateLimitPartition.GetNoLimiter(string.Empty);
                }

                return RateLimitPartition.GetFixedWindowLimiter(
                    $"{SignInLimit}/{context.Connection.RemoteIpAddress ?? IPAddress.None}",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = demo.SignInsPerHourPerAddress, Window = TimeSpan.FromHours(1), QueueLimit = 0 });
            });
            limiter.OnRejected = async (rejected, cancellationToken) =>
            {
                var http = rejected.HttpContext;
                http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await http.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = http,
                    ProblemDetails = { Status = StatusCodes.Status429TooManyRequests, Title = "Too many sign-ins from this address. Try again later." },
                }).ConfigureAwait(false);
            };
        });

        return services;
    }

    private static bool IsSignIn(PathString path) =>
        path.Equals(SignInSetup.LoginPath, StringComparison.OrdinalIgnoreCase) || path.Equals(Path, StringComparison.OrdinalIgnoreCase);

    /// <summary>Always mapped (so the access table sees it); answers 404 unless the public demo is switched on.</summary>
    internal static RouteHandlerBuilder MapDemoSignIn(this IEndpointRouteBuilder app) =>
        app.MapPost(Path, async (
            HttpContext context, IOptions<DemoSettings> demo, IAntiforgery antiforgery, UserManager<AppUser> users,
            SignInManager<AppUser> signIn, IAuditLog audit, TimeProvider time) =>
        {
            if (!demo.Value.Enabled)
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "There is no public demo here.");
            }

            if (!await antiforgery.IsRequestValidAsync(context).ConfigureAwait(false))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The form has expired. Reload the page and try again.");
            }

            var form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
            // The account gets the app's own role name from the list, never the posted text.
            var posted = form["role"].ToString();
            if (Roles.FirstOrDefault(r => string.Equals(r, posted, StringComparison.Ordinal)) is not { } role)
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "This role is not offered in the demo.");
            }

            var user = new AppUser { UserName = $"demo-{role.ToLowerInvariant()}-{RandomNumberGenerator.GetString(NameLetters, 8)}@example.com" };
            Check(await users.CreateAsync(user).ConfigureAwait(false)); // no password: only this button signs in to it
            Check(await users.AddToRoleAsync(user, role).ConfigureAwait(false));
            await signIn.SignInAsync(user, isPersistent: false).ConfigureAwait(false);

            await audit.AppendAsync(new AuditDraft(time.GetUtcNow(), user.UserName, AuditActions.SignedIn, $"User/{user.UserName}", "{}"), context.RequestAborted).ConfigureAwait(false);
            return Results.LocalRedirect(StartPages.For([role]));
        }).AllowAnonymous(); // the way in for visitors of the public demo

    // Identity's error codes only.
    private static void Check(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"The demo account could not be created: {string.Join(", ", result.Errors.Select(e => e.Code))}.");
        }
    }
}

/// <summary>Where each role starts after signing in without a return address.</summary>
public static class StartPages
{
    /// <summary>The approval queue for an approver, the orders for a buyer and the user list for an admin (neither has invoice pages), else the inbox.</summary>
    public static string For(IEnumerable<string> roles)
    {
        var held = roles.ToHashSet(StringComparer.Ordinal);
        if (held.Contains(Roles.Approver))
        {
            return "/approvals"; // the queue of invoices waiting for them
        }

        var readsInvoices = held.Contains(Roles.Clerk) || held.Contains(Roles.Auditor);
        return readsInvoices ? "/"
            : held.Contains(Roles.Buyer) ? "/purchasing"
            : held.Contains(Roles.Admin) ? "/admin/users"
            : "/";
    }
}

internal static class DecorateExtensions
{
    /// <summary>Wraps the registered service (same lifetime) without a container library.</summary>
    public static IServiceCollection Decorate<TService>(this IServiceCollection services, Func<TService, IServiceProvider, TService> wrap)
        where TService : class
    {
        var original = services.LastOrDefault(d => d.ServiceType == typeof(TService))
            ?? throw new InvalidOperationException($"{typeof(TService).Name} must be registered before it is decorated.");
        services.Remove(original);
        services.Add(ServiceDescriptor.Describe(typeof(TService), provider => wrap(Create<TService>(original, provider), provider), original.Lifetime));
        return services;
    }

    private static TService Create<TService>(ServiceDescriptor original, IServiceProvider provider)
        where TService : class =>
        (TService)(original.ImplementationInstance
            ?? original.ImplementationFactory?.Invoke(provider)
            ?? ActivatorUtilities.CreateInstance(provider, original.ImplementationType!));
}
