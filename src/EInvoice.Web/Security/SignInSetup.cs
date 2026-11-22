using EInvoice.Application.Audit;
using EInvoice.Infrastructure.Identity;
using EInvoice.Infrastructure.Persistence;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace EInvoice.Web.Security;

/// <summary>
/// Sign-in with ASP.NET Core Identity and a cookie. Everything is closed unless an endpoint says otherwise
/// (fallback policy). The API answers 401/403 as problem details; pages send the browser to the login page.
/// </summary>
public static class SignInSetup
{
    /// <summary>"__Host-": the browser only accepts it over HTTPS, for this host, for the whole site (no Domain).</summary>
    public const string CookieName = "__Host-einvoice";

    public const string LoginPath = "/account/login";
    public const string LogoutPath = "/account/logout";
    public const string AccessDeniedPath = "/account/access-denied";

    /// <summary>
    /// The default scheme: the ERP sync's API key when the request carries one ("Authorization: ApiKey ..."), else the
    /// sign-in cookie. A request never gets both.
    /// </summary>
    public const string CookieOrApiKey = nameof(CookieOrApiKey);

    internal static IServiceCollection AddEInvoiceSignIn(this IServiceCollection services)
    {
        services.AddAuthentication(o =>
            {
                o.DefaultScheme = CookieOrApiKey;
                o.DefaultSignInScheme = IdentityConstants.ExternalScheme;
            })
            .AddPolicyScheme(CookieOrApiKey, null, p => p.ForwardDefaultSelector = context =>
                SyncApiKeyHandler.Claims(context.Request) ? SyncApiKeyHandler.SchemeName : IdentityConstants.ApplicationScheme)
            .AddScheme<AuthenticationSchemeOptions, SyncApiKeyHandler>(SyncApiKeyHandler.SchemeName, null);
        services.AddAuthentication().AddIdentityCookies();
        services.AddOptions<SyncSettings>().BindConfiguration(SyncSettings.Section).ValidateOnStart();
        services.AddSingleton<IValidateOptions<SyncSettings>, SyncSettingsValidator>();

        services.AddIdentityCore<AppUser>(o =>
            {
                // Length over composition rules (NIST SP 800-63B): long passphrases, no forced digits or symbols.
                o.Password.RequiredLength = 12;
                o.Password.RequireDigit = false;
                o.Password.RequireLowercase = false;
                o.Password.RequireUppercase = false;
                o.Password.RequireNonAlphanumeric = false;
                o.Lockout.AllowedForNewUsers = true;
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                o.SignIn.RequireConfirmedAccount = false; // accounts are created by an administrator, not self-registered
                o.User.RequireUniqueEmail = false;
            })
            .AddRoles<AppRole>()
            .AddEntityFrameworkStores<EInvoiceDbContext>()
            .AddSignInManager();

        services.ConfigureApplicationCookie(cookie =>
        {
            cookie.Cookie.Name = CookieName;
            cookie.Cookie.HttpOnly = true;
            cookie.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            // Lax: cross-site POSTs carry no cookie (forgery protection for the API); a link from an e-mail still works.
            cookie.Cookie.SameSite = SameSiteMode.Lax;
            cookie.ExpireTimeSpan = TimeSpan.FromHours(8); // a working day, then sign in again
            cookie.SlidingExpiration = false;
            cookie.LoginPath = LoginPath;
            cookie.AccessDeniedPath = AccessDeniedPath;
            cookie.Events.OnRedirectToLogin = context => Answer(context, StatusCodes.Status401Unauthorized);
            cookie.Events.OnRedirectToAccessDenied = context => Answer(context, StatusCodes.Status403Forbidden);
        });

        // Every request checks the account's security stamp: signed out elsewhere, locked or changed means no access now.
        services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.Zero);

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
            .AddEInvoicePolicies();

        // Pages: the sign-in as a cascading value, re-checked every minute while a page stays open.
        services.AddCascadingAuthenticationState();
        services.AddScoped<AuthenticationStateProvider, SignInRevalidator>();

        services.AddOptions<DemoUserSettings>().BindConfiguration(DemoUserSettings.Section).ValidateOnStart();
        services.AddSingleton<IValidateOptions<DemoUserSettings>, DemoUserSettingsValidator>();
        services.AddHostedService<DemoUserSeeder>();
        return services;
    }

    /// <summary>The logout form's target: POST only, with an antiforgery token; ends every session of the account.</summary>
    internal static RouteHandlerBuilder MapSignOut(this IEndpointRouteBuilder app) =>
        app.MapPost(LogoutPath, async (HttpContext context, IAntiforgery antiforgery, UserManager<AppUser> users, SignInManager<AppUser> signIn, IAuditLog audit, TimeProvider time) =>
        {
            if (!await antiforgery.IsRequestValidAsync(context).ConfigureAwait(false))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The form has expired. Reload the page and try again.");
            }

            if (await users.GetUserAsync(context.User).ConfigureAwait(false) is { } user)
            {
                await users.UpdateSecurityStampAsync(user).ConfigureAwait(false); // copies of the cookie stop working too
            }

            var name = SignedInUser.Name(context.User);
            await audit.AppendAsync(new AuditDraft(time.GetUtcNow(), name, AuditActions.SignedOut, $"User/{name}", "{}"), context.RequestAborted).ConfigureAwait(false);

            await signIn.SignOutAsync().ConfigureAwait(false);
            return Results.LocalRedirect(LoginPath);
        }).RequireAuthorization(); // any signed-in user, stated rather than left to the default

    /// <summary>Only addresses on this site ("/..."), never "//host", "/\host" or a full URL: no open redirect.</summary>
    public static string SafeReturnUrl(string? returnUrl) =>
        returnUrl is { Length: > 0 } url && url[0] == '/'
            && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'))
            && !url.Any(char.IsControl)
            ? url
            : "/";

    private static Task Answer(RedirectContext<CookieAuthenticationOptions> context, int status)
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.StatusCode = status; // the status-code middleware writes the problem details
        }
        else
        {
            context.Response.Redirect(context.RedirectUri);
        }

        return Task.CompletedTask;
    }
}
