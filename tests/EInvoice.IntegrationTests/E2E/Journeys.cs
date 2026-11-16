using System.Text.RegularExpressions;
using EInvoice.Application.Intake;
using EInvoice.Domain.Invoices;
using EInvoice.IntegrationTests.Infrastructure;
using EInvoice.IntegrationTests.Matching;
using EInvoice.IntegrationTests.TestData;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace EInvoice.IntegrationTests.E2E;

/// <summary>
/// Package 12's "done when": five journeys through the real application in a real browser, one per role and duty.
/// Each journey starts from its own committed data (own order, own amounts) and its own accounts, so they can run in
/// any order next to every other test.
/// </summary>
[Trait("Category", "E2E")]
[Collection(SqlServerCollection.Name)]
public sealed partial class Journeys(SqlServerFixture fixture) : BrowserJourney(fixture)
{
    /// <summary>A committed order for seeded vendor 1 over 10 units at a price no other test uses.</summary>
    private async Task<(string OrderNumber, decimal Price)> OrderAsync(bool received = true)
    {
        var price = 4m + (Random.Shared.Next(1, 1_000_000) / 10_000m);
        await using var db = Fixture.CreateContext();
        var scenario = new MatchScenario(db).OrderLine(1, 10, price);
        if (received)
        {
            scenario.Received(1, 10);
        }

        await scenario.SaveOrderAsync();
        return (scenario.PurchaseOrderNumber, price);
    }

    /// <summary>
    /// The queue is oldest first, so a new invoice can be on a later page when many are waiting (as when the whole
    /// suite shares the database): go on with "Next" until it shows up, as a person would.
    /// </summary>
    private static async Task<ILocator> FindInQueueAsync(IPage page, string invoiceNumber)
    {
        var row = page.Locator("[data-testid=queue-row]", new() { HasText = invoiceNumber });
        for (var pages = 0; pages < 200; pages++)
        {
            await Expect(page.Locator("[data-testid=queue-row]").First).ToBeVisibleAsync();
            if (await row.CountAsync() > 0)
            {
                return row;
            }

            await page.Locator("[data-testid=page-next]").ClickAsync();
            await page.WaitForURLAsync($"**/approvals?page={pages + 2}");

            // The address changes at once, the rows a moment later: wait until the page shows page n (its "Previous"
            // link points to n - 1), or the old rows would be read again.
            await Expect(page.Locator("[data-testid=page-prev]")).ToHaveAttributeAsync("href", pages == 0 ? "/approvals" : $"/approvals?page={pages + 1}");
        }

        throw new InvalidOperationException($"{invoiceNumber} is not in the approval queue.");
    }

    private async Task UploadAsync(IPage page, string fileName, string mimeType, byte[] content)
    {
        await Expect(page.Locator("input[type=file]")).ToBeEnabledAsync();
        await page.Locator("input[type=file]").SetInputFilesAsync(new FilePayload { Name = fileName, MimeType = mimeType, Buffer = content });
        await page.WaitForURLAsync("**/invoices/*");
        await ReadyAsync(page);
    }

    // ---- 1. Clerk: XRechnung upload ----------------------------------------------------------------------------------

    [Fact]
    public async Task Journey_1_a_clerk_uploads_an_xrechnung_and_sees_it_matched()
    {
        var (order, price) = await OrderAsync();
        var invoice = new XRechnungBuilder { PurchaseOrderNumber = order }.Line("HDMI cable 2 m", 10m, price, orderLine: "1");
        var clerk = await AccountAsync("Clerk");
        var page = await NewBrowserAsync();

        await SignInAsync(page, clerk);
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Inbox" })).ToBeVisibleAsync();
        await UploadAsync(page, "invoice.xml", "application/xml", invoice.Build());

        await Expect(page.Locator("h1")).ToHaveTextAsync(invoice.InvoiceNumber);
        await Expect(page.Locator("[data-testid=status]").First).ToHaveAttributeAsync("data-status", "Matched");
        await Expect(page.Locator("[data-testid=match-row]")).ToHaveAttributeAsync("data-outcome", "FullMatch");

        // Signing out ends the session: the inbox is closed again.
        await SignOutAsync(page);
        await page.GotoAsync(BaseUrl);
        await page.WaitForURLAsync("**/account/login?**");
        Assert.Empty(Problems);
    }

    // ---- 2. Clerk: ZUGFeRD upload ------------------------------------------------------------------------------------

    [Fact]
    public async Task Journey_2_a_clerk_uploads_a_zugferd_pdf_and_it_is_read_from_its_xml()
    {
        var (order, price) = await OrderAsync();
        var invoice = new CiiInvoiceBuilder { Profile = CiiInvoiceBuilder.Profiles.XRechnung, PurchaseOrderNumber = order }
            .Line("HDMI cable 2 m", 10m, price, orderLine: "1");
        var pdf = ZugferdPdf.Invoice(invoice.Build(), "xrechnung.xml");
        var clerk = await AccountAsync("Clerk");
        var page = await NewBrowserAsync();

        await SignInAsync(page, clerk);
        await UploadAsync(page, "Rechnung.pdf", "application/pdf", pdf); // AI is off: the XML inside is enough

        await Expect(page.Locator("h1")).ToHaveTextAsync(invoice.InvoiceNumber);
        await Expect(page.Locator("[data-testid=status]").First).ToHaveAttributeAsync("data-status", "Matched");
        await Expect(page.Locator("[data-testid=zugferd]")).ToContainTextAsync("XML in PDF");
        await Expect(page.Locator("[data-testid=ai-extracted]")).ToHaveCountAsync(0);
        await Expect(page.Locator("[data-testid=file-name]")).ToHaveTextAsync("Rechnung.pdf"); // the original is kept

        await page.GetByRole(AriaRole.Link, new() { Name = "Inbox" }).First.ClickAsync();
        var row = page.Locator("[data-testid=invoice-row]", new() { HasText = invoice.InvoiceNumber });
        await Expect(row.Locator("[data-testid=zugferd-badge]")).ToBeVisibleAsync();
        Assert.Empty(Problems);
    }

    // ---- 3. Approver: approve from the queue -------------------------------------------------------------------------

    [Fact]
    public async Task Journey_3_an_approver_approves_a_matched_invoice_from_the_queue()
    {
        var (order, price) = await OrderAsync();
        var invoice = new XRechnungBuilder { PurchaseOrderNumber = order }.Line("HDMI cable 2 m", 10m, price, orderLine: "1");
        var clerk = await AccountAsync("Clerk");
        var submitted = await SubmitAsync(invoice.Build(), "invoice.xml", clerk.UserName);
        Assert.Equal(InvoiceStatus.Matched, submitted.Status);
        var approver = await AccountAsync("Approver");
        var page = await NewBrowserAsync();

        await SignInAsync(page, approver);
        await page.WaitForURLAsync("**/approvals");
        var row = await FindInQueueAsync(page, invoice.InvoiceNumber);
        await Expect(row.Locator("[data-testid=decision-state]")).ToHaveAttributeAsync("data-state", "ready");
        await row.GetByRole(AriaRole.Link, new() { Name = invoice.InvoiceNumber }).ClickAsync();
        await page.WaitForURLAsync($"**/invoices/{submitted.InvoiceId}");
        await ReadyAsync(page);

        await page.Locator("[data-testid=approve]").ClickAsync();

        await Expect(page.Locator("[data-testid=decision-result]")).ToContainTextAsync("Approved");
        await Expect(page.Locator("[data-testid=status]").First).ToHaveAttributeAsync("data-status", "Approved");
        await Expect(page.Locator("[data-testid=history-entry]").Last).ToContainTextAsync(approver.UserName);

        await page.GetByRole(AriaRole.Link, new() { Name = "Approvals" }).ClickAsync();
        await ReadyAsync(page);
        await Expect(page.Locator("[data-testid=queue-row]", new() { HasText = invoice.InvoiceNumber })).ToHaveCountAsync(0);
        Assert.Empty(Problems);
    }

    // ---- 4. Approver rejects an exception; auditor checks the trail ---------------------------------------------------

    [Fact]
    public async Task Journey_4_an_approver_rejects_an_exception_and_an_auditor_finds_it_in_the_verified_trail()
    {
        var (order, price) = await OrderAsync(received: false); // nothing received yet: an exception
        var invoice = new XRechnungBuilder { PurchaseOrderNumber = order }.Line("HDMI cable 2 m", 10m, price, orderLine: "1");
        var clerk = await AccountAsync("Clerk");
        var submitted = await SubmitAsync(invoice.Build(), "invoice.xml", clerk.UserName);
        Assert.Equal(InvoiceStatus.Exception, submitted.Status);
        var approver = await AccountAsync("Approver");
        var reason = $"Goods never arrived, supplier informed ({Guid.NewGuid():N})"[..60];

        var approverPage = await NewBrowserAsync();
        await SignInAsync(approverPage, approver);
        await (await FindInQueueAsync(approverPage, invoice.InvoiceNumber))
            .GetByRole(AriaRole.Link, new() { Name = invoice.InvoiceNumber }).ClickAsync();
        await approverPage.WaitForURLAsync($"**/invoices/{submitted.InvoiceId}");
        await ReadyAsync(approverPage);
        await Expect(approverPage.Locator("[data-testid=reject]")).ToBeDisabledAsync(); // a reason first
        await approverPage.Locator("[data-testid=decision-reason] textarea").FillAsync(reason);
        await approverPage.Locator("[data-testid=reject]").ClickAsync();
        await Expect(approverPage.Locator("[data-testid=status]").First).ToHaveAttributeAsync("data-status", "Rejected");
        await SignOutAsync(approverPage);

        var auditor = await AccountAsync("Auditor");
        var auditorPage = await NewBrowserAsync();
        await SignInAsync(auditorPage, auditor);
        await auditorPage.GetByRole(AriaRole.Link, new() { Name = "Audit" }).ClickAsync();
        await ReadyAsync(auditorPage);

        var rejection = auditorPage.Locator("[data-testid=audit-row]", new() { HasText = $"Invoice/{submitted.InvoiceId}" })
            .Filter(new() { HasText = "Rejected" });
        await Expect(rejection).ToHaveCountAsync(1);
        await Expect(rejection).ToContainTextAsync(approver.UserName);
        await Expect(rejection).ToContainTextAsync(reason);

        await auditorPage.Locator("[data-testid=verify]").ClickAsync();
        await Expect(auditorPage.Locator("[data-testid=verify-result]")).ToHaveAttributeAsync("data-state", "intact");
        await Expect(auditorPage.Locator("[data-testid=verify-result]")).ToContainTextAsync(Hash());
        Assert.Empty(Problems);
    }

    // ---- 5. Admins: a new bank account under the two-person rule ------------------------------------------------------

    [Fact]
    public async Task Journey_5_one_admin_proposes_a_bank_account_and_only_a_second_admin_can_confirm_it()
    {
        var vendor = await VendorAsync(); // its own: seeded vendors stay as other tests expect them
        var iban = TestIbans.New();
        var grouped = string.Join(' ', iban.Chunk(4).Select(c => new string(c)));
        var first = await AccountAsync("Admin");
        var second = await AccountAsync("Admin");

        var firstPage = await NewBrowserAsync();
        await SignInAsync(firstPage, first);
        await firstPage.WaitForURLAsync("**/admin/users");
        await OpenVendorAsync(firstPage, vendor);
        await firstPage.Locator("[data-testid=new-iban] input").FillAsync(iban);
        await firstPage.Locator("[data-testid=propose]").ClickAsync();
        await Expect(firstPage.Locator("[data-testid=account-result]")).ToContainTextAsync("waits for a second admin");
        var proposed = firstPage.Locator("[data-testid=account-row]", new() { HasText = grouped });
        await Expect(proposed.Locator("[data-testid=account-status]")).ToHaveAttributeAsync("data-status", "pending");
        await Expect(proposed.Locator("[data-testid=confirm]")).ToHaveCountAsync(0); // not by the proposer
        await Expect(proposed).ToContainTextAsync("another admin confirms");
        await SignOutAsync(firstPage);

        var secondPage = await NewBrowserAsync();
        await SignInAsync(secondPage, second);
        await OpenVendorAsync(secondPage, vendor);
        var row = secondPage.Locator("[data-testid=account-row]", new() { HasText = grouped });
        await row.Locator("[data-testid=confirm]").ClickAsync();

        await Expect(row.Locator("[data-testid=account-status]")).ToHaveAttributeAsync("data-status", "active");
        await Expect(row.Locator("[data-testid=account-status]")).ToContainTextAsync($"confirmed by {second.UserName}");
        Assert.Empty(Problems);
    }

    private async Task<string> VendorAsync()
    {
        await using var db = Fixture.CreateContext();
        var vendor = new EInvoice.Domain.Vendors.Vendor($"J{Guid.NewGuid():N}"[..12], "Journey Lieferant GmbH", null);
        db.Vendors.Add(vendor);
        await db.SaveChangesAsync();
        return vendor.Number;
    }

    private async Task OpenVendorAsync(IPage page, string number)
    {
        await page.GetByRole(AriaRole.Link, new() { Name = "Vendors" }).ClickAsync();
        await ReadyAsync(page);
        await page.GetByRole(AriaRole.Link, new() { Name = number, Exact = true }).ClickAsync();
        await page.WaitForURLAsync("**/admin/vendors/*");
        await ReadyAsync(page);
    }

    [GeneratedRegex("[0-9a-f]{64}")]
    private static partial Regex Hash();
}
