using EInvoice.IntegrationTests.Api;
using EInvoice.IntegrationTests.Infrastructure;
using EInvoice.IntegrationTests.Matching;
using EInvoice.IntegrationTests.TestData;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace EInvoice.IntegrationTests.E2E;

/// <summary>
/// The package's "done when", in a real browser: upload an invoice on the inbox page and see its result.
/// The app runs on Kestrel against the test database. Needs Chromium: pwsh bin/.../playwright.ps1 install chromium.
/// Runs as Development, where the static web assets of packages (MudBlazor) are served from the build output.
/// </summary>
[Trait("Category", "E2E")]
[Collection(SqlServerCollection.Name)]
public sealed class UploadInBrowserTests(SqlServerFixture fixture) : IAsyncLifetime
{
    private ApiFactory _factory = null!;
    private IPlaywright _playwright = null!;
    private IBrowser _browser = null!;
    private string _baseUrl = string.Empty;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory(ApiFactory.Settings(fixture.AppConnectionString), environment: "Development");
        _factory.UseKestrel(0); // any free port
        _factory.StartServer();
        _baseUrl = _factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

        _playwright = await Playwright.CreateAsync();
        _browser = await _playwright.Chromium.LaunchAsync();
    }

    public async Task DisposeAsync()
    {
        await _browser.DisposeAsync();
        _playwright.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Uploaded_invoice_shows_its_result_in_the_browser()
    {
        string invoiceNumber;
        byte[] xml;
        await using (var db = fixture.CreateContext())
        {
            var scenario = await new MatchScenario(db).OrderLine(1, 10, 3.3333m).Received(1, 10).SaveOrderAsync(); // own amount: committed
            var builder = new XRechnungBuilder { PurchaseOrderNumber = scenario.PurchaseOrderNumber }.Line("HDMI cable 2 m", 10m, 3.3333m, orderLine: "1");
            invoiceNumber = builder.InvoiceNumber;
            xml = builder.Build();
        }

        var page = await _browser.NewPageAsync();
        var problems = new List<string>();
        page.Console += (_, message) =>
        {
            if (message.Type == "error")
            {
                problems.Add($"console: {message.Text}");
            }
        };
        page.PageError += (_, error) => problems.Add($"page: {error}");
        page.RequestFailed += (_, request) => problems.Add($"request: {request.Url}");

        await page.GotoAsync(_baseUrl);
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Inbox" })).ToBeVisibleAsync();
        await Expect(page.Locator("input[type=file]")).ToBeEnabledAsync(); // the page is interactive now
        await page.Locator("input[type=file]").SetInputFilesAsync(new FilePayload { Name = "invoice.xml", MimeType = "application/xml", Buffer = xml });

        await page.WaitForURLAsync("**/invoices/*");
        await Expect(page.Locator("h1")).ToHaveTextAsync(invoiceNumber);
        await Expect(page.Locator("[data-testid=status]").First).ToHaveAttributeAsync("data-status", "Matched");
        await Expect(page.Locator("[data-testid=match-row]")).ToHaveAttributeAsync("data-outcome", "FullMatch");
        Assert.Empty(problems);
    }
}
