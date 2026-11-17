using System.Net;
using Microsoft.AspNetCore.Hosting;
using EInvoice.Application.Intake;
using EInvoice.IntegrationTests.Api;
using EInvoice.IntegrationTests.Infrastructure;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace EInvoice.IntegrationTests.E2E;

/// <summary>
/// The real application on Kestrel against the test database, and a real Chromium. Each journey signs in through the
/// login form like a person, with its own accounts, and fails on any browser console error or failed request.
/// Runs as Development, where the static web assets of packages (MudBlazor) are served from the build output.
/// Needs Chromium once: pwsh tests/EInvoice.IntegrationTests/bin/Debug/net10.0/playwright.ps1 install chromium.
/// </summary>
public abstract class BrowserJourney(SqlServerFixture fixture) : IAsyncLifetime
{
    private readonly List<IBrowserContext> _contexts = [];
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;

    protected SqlServerFixture Fixture { get; } = fixture;

    private protected ApiFactory App { get; private set; } = null!;

    protected string BaseUrl { get; private set; } = string.Empty;

    /// <summary>Everything that went wrong in any browser of this journey.</summary>
    protected List<string> Problems { get; } = [];

    public async Task InitializeAsync()
    {
        App = new ApiFactory(ApiFactory.Settings(Fixture.AppConnectionString), environment: "Development");
        // HTTPS like the real app (any free port, the ASP.NET Core development certificate): the sign-in and antiforgery
        // cookies are Secure, and antiforgery refuses to work over plain HTTP.
        App.UseKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps()));
        App.StartServer();
        BaseUrl = App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync();
    }

    public async Task DisposeAsync()
    {
        foreach (var context in _contexts)
        {
            await context.DisposeAsync();
        }

        await _browser.DisposeAsync();
        _playwright.Dispose();
        await App.DisposeAsync();
    }

    /// <summary>A fresh browser (own cookies), as a different person at a different desk would have.</summary>
    /// <param name="locale">The browser's language; English unless a journey is about the language.</param>
    protected async Task<IPage> NewBrowserAsync(string locale = "en-US")
    {
        // The development certificate is not trusted on a CI machine; it only protects a loopback connection here.
        var context = await _browser.NewContextAsync(new() { IgnoreHTTPSErrors = true, Locale = locale });
        _contexts.Add(context);
        var page = await context.NewPageAsync();
        page.Console += (_, message) =>
        {
            if (message.Type == "error")
            {
                Problems.Add($"console: {message.Text}");
            }
        };
        page.PageError += (_, error) => Problems.Add($"page: {error}");
        page.RequestFailed += (_, request) => Problems.Add($"request: {request.Url}");
        return page;
    }

    /// <summary>Signs in through the login form, coming from <paramref name="start"/>, and waits for the page to be usable.</summary>
    protected async Task SignInAsync(IPage page, (string UserName, string Password) account, string start = "/")
    {
        await page.GotoAsync(BaseUrl.TrimEnd('/') + start);
        await page.WaitForURLAsync("**/account/login**");
        await page.GetByLabel("User name").FillAsync(account.UserName);
        await page.GetByLabel("Password").FillAsync(account.Password);
        await page.GetByRole(AriaRole.Button, new() { Name = "Sign in" }).ClickAsync();
        await Expect(page.Locator("[data-testid=signed-in-user]")).ToHaveTextAsync(account.UserName);
        await ReadyAsync(page);
    }

    protected static async Task SignOutAsync(IPage page)
    {
        await page.Locator(".mud-appbar form[data-testid=sign-out] button").ClickAsync();
        await page.WaitForURLAsync("**/account/login**");
    }

    /// <summary>
    /// Waits until the page is interactive: Blazor first shows server-rendered HTML whose buttons do nothing until the
    /// live connection is up. The layout shows its marker only after its first interactive render.
    /// </summary>
    protected static Task ReadyAsync(IPage page) => Expect(page.Locator("[data-testid=app-ready]")).ToBeAttachedAsync();

    protected Task<(string UserName, string Password)> AccountAsync(string role) => TestUsers.CreateAsync(App, role);

    /// <summary>Puts an invoice into the system as a clerk would by uploading it (the journeys under test start later).</summary>
    protected async Task<IntakeResult> SubmitAsync(byte[] content, string fileName, string submittedBy)
    {
        await using var scope = App.Services.CreateAsyncScope();
        var intake = scope.ServiceProvider.GetRequiredService<IInvoiceIntake>();
        return await intake.SubmitAsync(new UploadedFile(fileName, content), submittedBy, CancellationToken.None);
    }
}
