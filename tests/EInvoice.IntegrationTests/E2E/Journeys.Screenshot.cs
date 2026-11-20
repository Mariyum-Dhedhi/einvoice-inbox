using EInvoice.IntegrationTests.Api;
using EInvoice.IntegrationTests.Matching;
using EInvoice.IntegrationTests.TestData;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace EInvoice.IntegrationTests.E2E;

public sealed partial class Journeys
{
    /// <summary>The README's picture; checked in by hand (EINVOICE_SCREENSHOT_DIR=docs/images), never in CI.</summary>
    internal const string ScreenshotFile = "review-match.png";

    /// <summary>
    /// Where the README's screenshot comes from: an approver opens an invoice whose price is above the order's, and sees
    /// the three-way match, the reason in plain words and the decision that still needs one. Runs in CI like every
    /// journey, so the picture can always be made again; the file goes to EINVOICE_SCREENSHOT_DIR, else the test output.
    /// </summary>
    [Fact]
    public async Task Readme_screenshot_an_approver_reviews_a_price_variance()
    {
        // Readable values for the picture, still unique (a fresh number each run), and an amount and date no other
        // committed test uses for this vendor (POSSIBLE_DUPLICATE).
        var unique = Random.Shared.Next(100_000, 1_000_000);
        await using var db = Fixture.CreateContext();
        var scenario = new MatchScenario(db, orderNumber: $"PO-{unique}").OrderLine(1, 12, 18.90m).Received(1, 12);
        await scenario.SaveOrderAsync();
        var invoice = new XRechnungBuilder
        {
            InvoiceNumber = $"RE-2026-{unique}",
            IssueDate = new DateOnly(2026, 11, 16),
            PurchaseOrderNumber = scenario.PurchaseOrderNumber,
        }.Line("HDMI cable 2 m", 12m, 19.95m, orderLine: "1");
        var submitted = await SubmitAsync(invoice.Build(), "invoice.xml", $"clerk-{unique:x}@example.com");
        var approver = await TestUsers.CreateNamedAsync(App, $"approver-{unique:x}@example.com", "Approver");
        var page = await NewBrowserAsync();
        await page.SetViewportSizeAsync(1440, 900);

        await SignInAsync(page, approver, start: $"/invoices/{submitted.InvoiceId}");

        await Expect(page.Locator("h1")).ToHaveTextAsync(invoice.InvoiceNumber);
        await Expect(page.Locator("[data-testid=status]").First).ToHaveAttributeAsync("data-status", "Exception");
        await Expect(page.Locator("[data-testid=match-row]")).ToHaveAttributeAsync("data-outcome", "PriceVariance");
        await Expect(page.Locator("[data-testid=explanation]")).ToBeVisibleAsync();
        await Expect(page.Locator("[data-testid=decision]")).ToBeVisibleAsync();

        var folder = Environment.GetEnvironmentVariable("EINVOICE_SCREENSHOT_DIR") is { Length: > 0 } configured
            ? Path.GetFullPath(configured)
            : Path.Combine(AppContext.BaseDirectory, "screenshots");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, ScreenshotFile);
        await page.ScreenshotAsync(new() { Path = file, FullPage = true, Animations = ScreenshotAnimations.Disabled });

        Assert.True(new FileInfo(file).Length > 10_000, "the screenshot is (nearly) empty");
        Assert.Empty(Problems);
    }
}
