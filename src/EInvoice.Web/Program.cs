using System.Text.Json.Serialization;
using EInvoice.Web.Components;
using EInvoice.Web.Configuration;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEInvoice();
builder.Services.AddProblemDetails();
builder.Services.AddControllers()
    .AddJsonOptions(json => json.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter())); // statuses as names, not numbers
builder.Services.AddOpenApi();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Every response: never let the browser guess a content type. API responses hold invoice data: never cache them.
app.Use((context, next) =>
{
    context.Response.OnStarting(() =>
    {
        context.Response.Headers.XContentTypeOptions = "nosniff";
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
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseAntiforgery();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi(); // /openapi/v1.json; the API description is a development aid, not published
}

app.MapControllers();
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

/// <summary>Visible to the API tests (WebApplicationFactory).</summary>
public partial class Program;
