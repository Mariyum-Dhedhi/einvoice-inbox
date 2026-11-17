using System.Globalization;
using EInvoice.IntegrationTests.TestData;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace EInvoice.IntegrationTests.E2E;

public sealed partial class Journeys
{
    // ---- 6. Buyer: the data invoices are matched against ------------------------------------------------------------

    /// <summary>
    /// A buyer records an order and its delivery through the app; a clerk's invoice for it is matched against exactly
    /// that, and the order shows the invoice. The buyer never reaches an invoice page.
    /// </summary>
    [Fact]
    public async Task Journey_6_a_buyer_records_an_order_and_its_delivery_and_an_invoice_for_it_is_matched()
    {
        var orderNumber = $"EJ-{Guid.NewGuid():N}"[..20]; // committed: not the seed's PO- pattern (Seed_data_is_present)
        var receiptNumber = $"EJR-{Guid.NewGuid():N}"[..20];
        var price = 4m + (Random.Shared.Next(1, 1_000_000) / 10_000m); // a price no other test uses (POSSIBLE_DUPLICATE)
        var buyer = await AccountAsync("Buyer");
        var clerk = await AccountAsync("Clerk");

        // The buyer starts on the orders and records a new one for seeded vendor 1.
        var buying = await NewBrowserAsync();
        await SignInAsync(buying, buyer, start: "/purchasing");
        await buying.Locator("[data-testid=new-order]").ClickAsync();
        await buying.WaitForURLAsync("**/purchasing/new");
        await ReadyAsync(buying);
        await buying.Locator("[data-testid=order-number] input").FillAsync(orderNumber);
        await buying.Locator("select[data-testid=order-vendor]").SelectOptionAsync(new SelectOptionValue { Label = "V-1001 · Beispiel Bürobedarf GmbH" });
        await buying.Locator("[data-testid=line-item] input").FillAsync("E2E-CABLE");
        await buying.Locator("[data-testid=line-description] input").FillAsync("HDMI cable 2 m");
        await buying.Locator("[data-testid=line-quantity] input").FillAsync("10");
        await buying.Locator("[data-testid=line-price] input").FillAsync(price.ToString("0.####", CultureInfo.GetCultureInfo("de-DE")));
        await buying.Locator("[data-testid=save-order]").ClickAsync();
        await Expect(buying.Locator("h1")).ToHaveTextAsync(orderNumber);
        await ReadyAsync(buying);

        // The delivery: the open quantity is filled in already.
        await Expect(buying.Locator("[data-testid=receipt-qty-1] input")).ToHaveValueAsync("10");
        await buying.Locator("[data-testid=receipt-number] input").FillAsync(receiptNumber);
        await buying.Locator("[data-testid=record-receipt]").ClickAsync();
        await Expect(buying.Locator("[data-testid=receipt-row]")).ToContainTextAsync(receiptNumber);
        await Expect(buying.Locator("[data-testid=order-line] [data-testid=open]")).ToHaveTextAsync("0");

        // No invoice pages for the buyer.
        await buying.GotoAsync(BaseUrl.TrimEnd('/') + "/");
        await buying.WaitForURLAsync("**/account/access-denied**");

        // The clerk's invoice for this order is matched against what the buyer recorded.
        var invoice = new XRechnungBuilder { PurchaseOrderNumber = orderNumber }.Line("HDMI cable 2 m", 10m, price, orderLine: "1");
        var page = await NewBrowserAsync();
        await SignInAsync(page, clerk);
        await UploadAsync(page, "invoice.xml", "application/xml", invoice.Build());
        await Expect(page.Locator("[data-testid=status]").First).ToHaveAttributeAsync("data-status", "Matched");

        // ... and the order shows it.
        await page.GotoAsync(BaseUrl.TrimEnd('/') + "/purchasing?q=" + Uri.EscapeDataString(orderNumber));
        await ReadyAsync(page);
        await page.Locator("[data-testid=order-row] a").ClickAsync();
        await Expect(page.Locator("[data-testid=order-invoice]")).ToContainTextAsync(invoice.InvoiceNumber);
        await Expect(page.Locator("[data-testid=order-line] [data-testid=invoiced]")).ToHaveTextAsync("10");

        Assert.Empty(Problems);
    }
}
