using System.Text.Json.Serialization;
using EInvoice.Web.Components;
using EInvoice.Web.Configuration;
using EInvoice.Web.Security;
using MudBlazor.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEInvoice();
builder.Services.AddProblemDetails();
builder.Services.AddControllers()
    .AddJsonOptions(json => json.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter())); // statuses as names, not numbers
builder.Services.AddOpenApi();
builder.Services.AddMudServices();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// The antiforgery cookie: host-only (__Host-), HTTPS only, never sent with requests from other sites.
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "__Host-einvoice-antiforgery";
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.HttpOnly = true;
});

// HTTPS only, for a year (outside Development). Localhost is not excluded: outside Development the app never runs
// there, and the tests ask for the header on it.
builder.Services.AddHsts(options =>
{
    options.MaxAge = TimeSpan.FromDays(365);
    options.ExcludedHosts.Clear();
});

var app = builder.Build();

// Every response: the browser protections (SecurityHeaders: content security policy, no sniffing, isolation).
// API responses hold invoice data: never cache them.
app.Use((context, next) =>
{
    context.Response.OnStarting(() =>
    {
        SecurityHeaders.Apply(context.Response.Headers);
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            context.Response.Headers.CacheControl = "no-store";
        }

        return Task.CompletedTask;
    });
    return next(context);
});

// API errors are problem details (no stack trace, no exception text); the UI keeps its error pages.
app.UseWhen(
    context => context.Request.Path.StartsWithSegments("/api"),
    api =>
    {
        api.UseExceptionHandler();
        api.UseStatusCodePages();
    });
app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/api"),
    ui =>
    {
        if (!app.Environment.IsDevelopment())
        {
            ui.UseExceptionHandler("/Error", createScopeForErrors: true);
        }

        ui.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
    });

if (!app.Environment.IsDevelopment())
{
    app.UseHsts(); // a year (AddHsts above); never in Development, where the browser would pin localhost for every project
}

app.UseHttpsRedirection();

// Who is calling, then whether they may: every endpoint requires sign-in unless it allows anonymous callers.
app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi(); // /openapi/v1.json; the API description is a development aid, not published
}

app.MapControllers();
app.MapStaticAssets().AllowAnonymous(); // stylesheets and scripts of the login page
app.MapSignOut();
app.MapRazorComponents<App>()
    // Blazor would add a second policy (frame-ancestors 'self'); SecurityHeaders sends the one complete policy.
    .AddInteractiveServerRenderMode(options => options.ContentSecurityFrameAncestorsPolicy = null);

app.Run();

/// <summary>Visible to the API tests (WebApplicationFactory).</summary>
public partial class Program;
