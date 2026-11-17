using System.Globalization;
using EInvoice.Web.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Localization;

namespace EInvoice.Web.Localization;

/// <summary>
/// The two languages of the UI. The first visit follows the browser (Accept-Language); a language the app does not
/// speak gets German, the language of the market. A person's choice is kept in a cookie for a year. Amounts and dates
/// stay in German formats in both languages (Formats): they are German invoices.
/// The API is not translated: its problem details are for programs.
/// </summary>
public static class Languages
{
    public const string German = "de";
    public const string English = "en";

    /// <summary>Host-only and HTTPS-only (__Host-): no other site or subdomain can set or read it.</summary>
    public const string CookieName = "__Host-einvoice-culture";

    /// <summary>The switch's form target: POST with an antiforgery token, like sign-out.</summary>
    public const string SwitchPath = "/account/language";

    public static IReadOnlyList<string> Supported { get; } = [German, English];

    /// <summary>The language this request is answered in ("de" or "en").</summary>
    public static string Current => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

    internal static IServiceCollection AddEInvoiceLanguages(this IServiceCollection services)
    {
        services.AddLocalization(options => options.ResourcesPath = "Resources");
        services.Configure<RequestLocalizationOptions>(options =>
        {
            options.SetDefaultCulture(German)
                .AddSupportedCultures([.. Supported])
                .AddSupportedUICultures([.. Supported]);
            options.FallBackToParentCultures = true;   // de-DE, de-AT -> de
            options.FallBackToParentUICultures = true;
            // The choice first, then the browser; the query string is not read (a link cannot switch someone's language).
            options.RequestCultureProviders =
            [
                new CookieRequestCultureProvider { CookieName = CookieName },
                new AcceptLanguageHeaderRequestCultureProvider(),
            ];
        });
        return services;
    }

    internal static RouteHandlerBuilder MapLanguageSwitch(this IEndpointRouteBuilder app) =>
        app.MapPost(SwitchPath, async (HttpContext context, IAntiforgery antiforgery, TimeProvider time) =>
        {
            if (!await antiforgery.IsRequestValidAsync(context).ConfigureAwait(false))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The form has expired. Reload the page and try again.");
            }

            var form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
            var language = form["culture"].ToString();
            if (!Supported.Contains(language, StringComparer.Ordinal))
            {
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "This language is not offered.");
            }

            context.Response.Cookies.Append(
                CookieName,
                CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(language)),
                new CookieOptions
                {
                    Secure = true,
                    HttpOnly = true,              // no script needs it
                    SameSite = SameSiteMode.Lax,  // sent when someone follows a link into the app
                    Path = "/",
                    Expires = time.GetUtcNow().AddDays(365),
                    IsEssential = true,
                });
            return Results.LocalRedirect(SignInSetup.SafeReturnUrl(form["returnUrl"].ToString()));
        }).AllowAnonymous(); // the login page has the switch too
}
