using EInvoice.Application.Purchasing;
using EInvoice.Application.Security;
using EInvoice.Domain.Invoices;
using EInvoice.Domain.Purchasing;
using EInvoice.Domain.Shared;
using EInvoice.Web.Components.Purchasing;
using EInvoice.Web.Components.Shared;
using EInvoice.Web.Security;

namespace EInvoice.ComponentTests.Purchasing;

/// <summary>
/// The data invoices are matched against, for people to see: purchase orders with their lines, what arrived (goods
/// receipts), what is still open and what was invoiced. Invoice readers see them; the buyer also records new orders
/// and receipts, and sees no invoices. Every action checks its policy again on the server.
/// </summary>
public sealed class PurchasingPagesTests : UiTest
{
    private static readonly Guid OrderId = Guid.NewGuid();
    private static readonly Guid VendorId = Guid.NewGuid();
    private const string Me = SignedInUser;

    private static PurchaseOrderDetail Detail(bool withInvoices = true) => new(
        OrderId, "PO-7001", VendorId, "V-1012", "Demo Kabel & Co. KG", new DateOnly(2026, 11, 2), "EUR",
        [
            new OrderLineView(1, "CABLE-HDMI", "HDMI cable 2 m", 40m, 7.4125m, 19m, 296.50m, Received: 25m, Invoiced: 20m),
            new OrderLineView(2, "BOOK-HGB", "Handbook German commercial code", 3m, 39.90m, 7m, 119.70m, Received: 3m, Invoiced: 0m),
        ],
        [new ReceiptView("GR-7001", new DateOnly(2026, 11, 5), [new ReceiptLineView(1, 25m), new ReceiptLineView(2, 3m)])],
        withInvoices ? [new OrderInvoiceView(Guid.NewGuid(), "RE-2026-0042", InvoiceStatus.Matched, 352.83m, "EUR")] : null);

    private void AsBuyer()
    {
        Auth.SetRoles(Roles.Buyer);
        Auth.SetPolicies(BuyerPolicies);
    }

    // ---- List ------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_list_shows_each_order_with_vendor_date_net_total_and_what_arrived_in_words()
    {
        PurchasingQueries.Page = new PurchaseOrderList(
        [
            new PurchaseOrderSummary(OrderId, "PO-7001", "Demo Kabel & Co. KG", new DateOnly(2026, 11, 2), "EUR", 416.20m, ReceiptState.Partly),
            new PurchaseOrderSummary(Guid.NewGuid(), "PO-7002", "Fiktiv Elektro KG", new DateOnly(2026, 11, 3), "EUR", 1234.56m, ReceiptState.NothingReceived),
        ], 1, PurchaseOrderListQuery.DefaultPageSize, 2);

        var cut = Render<PurchaseOrdersPage>();

        var rows = cut.FindAll("[data-testid=order-row]");
        Assert.Equal(2, rows.Count);
        Assert.Equal($"/purchasing/{OrderId}", rows[0].QuerySelector("a")!.GetAttribute("href"));
        Assert.Contains("Demo Kabel & Co. KG", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("02.11.2026", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains(Formats.Money(416.20m, "EUR"), rows[0].TextContent, StringComparison.Ordinal);
        var state = rows[0].QuerySelector("[data-testid=receipt-state]")!;
        Assert.Equal("partly", state.GetAttribute("data-state"));
        Assert.Equal(T["ReceiptState.Partly"], state.TextContent.Trim());
        Assert.NotNull(state.QuerySelector("svg")); // an icon with the words, never colour alone
    }

    [Fact]
    public void Search_and_page_come_from_the_address_so_a_view_can_be_linked()
    {
        NavigateTo("/purchasing?q=kabel&page=2");

        Render<PurchaseOrdersPage>();

        Assert.Equal(new PurchaseOrderListQuery("kabel", 2, PurchaseOrderListQuery.DefaultPageSize), PurchasingQueries.ListCalls.Last());
    }

    [Theory]
    [InlineData("/purchasing?page=-1", null, 1)]
    [InlineData("/purchasing?page=abc", null, 1)]
    [InlineData("/purchasing?q=%20%20", null, 1)]
    public void Address_values_are_user_input_and_checked(string address, string? search, int page)
    {
        NavigateTo(address);

        Render<PurchaseOrdersPage>();

        Assert.Equal(new PurchaseOrderListQuery(search, page, PurchaseOrderListQuery.DefaultPageSize), PurchasingQueries.ListCalls.Last());
    }

    [Fact]
    public void A_search_longer_than_allowed_is_not_sent()
    {
        NavigateTo("/purchasing?q=" + new string('a', PurchaseOrderListQuery.MaxSearchLength + 1));

        Render<PurchaseOrdersPage>();

        Assert.Null(PurchasingQueries.ListCalls.Last().Search);
    }

    [Fact]
    public void The_search_is_a_get_form_back_to_this_page()
    {
        var form = Render<PurchaseOrdersPage>().Find("form[data-testid=search]");

        Assert.Equal("get", form.GetAttribute("method"), ignoreCase: true);
        Assert.Equal("/purchasing", form.GetAttribute("action"));
        Assert.NotNull(form.QuerySelector("input[name=q]"));
    }

    [Fact]
    public void Only_the_buyer_is_offered_a_new_order()
    {
        Assert.Empty(Render<PurchaseOrdersPage>().FindAll("[data-testid=new-order]"));

        AsBuyer();
        var link = Render<PurchaseOrdersPage>().Find("[data-testid=new-order]");

        Assert.Equal("/purchasing/new", link.GetAttribute("href"));
    }

    [Fact]
    public void No_orders_says_so()
    {
        Assert.Equal(T["Purchasing.Empty"], Render<PurchaseOrdersPage>().Find("[data-testid=orders-empty]").TextContent.Trim());
    }

    // ---- Detail ----------------------------------------------------------------------------------------------------

    private IRenderedComponent<PurchaseOrderPage> ShowOrder(bool withInvoices = true)
    {
        PurchasingQueries.Details[OrderId] = Detail(withInvoices);
        return Render<PurchaseOrderPage>(p => p.Add(x => x.Id, OrderId));
    }

    [Fact]
    public void Each_line_shows_ordered_received_open_and_invoiced_with_all_price_decimals()
    {
        var line = ShowOrder().FindAll("[data-testid=order-line]")[0];

        Assert.Contains(Formats.UnitPrice(7.4125m, "EUR"), line.TextContent, StringComparison.Ordinal);
        Assert.Equal("40", line.QuerySelector("[data-testid=ordered]")!.TextContent.Trim());
        Assert.Equal("25", line.QuerySelector("[data-testid=received]")!.TextContent.Trim());
        Assert.Equal("15", line.QuerySelector("[data-testid=open]")!.TextContent.Trim());
        Assert.Equal("20", line.QuerySelector("[data-testid=invoiced]")!.TextContent.Trim());
    }

    [Fact]
    public void Goods_receipts_are_listed_with_date_and_quantities()
    {
        var receipt = ShowOrder().Find("[data-testid=receipt-row]");

        Assert.Contains("GR-7001", receipt.TextContent, StringComparison.Ordinal);
        Assert.Contains("05.11.2026", receipt.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void An_invoice_reader_sees_the_invoices_against_the_order_with_links()
    {
        var cut = ShowOrder();

        var invoice = cut.Find("[data-testid=order-invoice]");
        Assert.Contains("RE-2026-0042", invoice.TextContent, StringComparison.Ordinal);
        Assert.StartsWith("/invoices/", invoice.QuerySelector("a")!.GetAttribute("href"), StringComparison.Ordinal);
        Assert.True(PurchasingQueries.DetailCalls.Last().WithInvoices);
    }

    [Fact]
    public void The_buyer_does_not_even_load_the_invoices()
    {
        AsBuyer();

        var cut = ShowOrder(withInvoices: false);

        Assert.False(PurchasingQueries.DetailCalls.Last().WithInvoices);
        Assert.Empty(cut.FindAll("[data-testid=order-invoices]"));
    }

    [Fact]
    public void An_unknown_order_says_so_and_links_back()
    {
        var cut = Render<PurchaseOrderPage>(p => p.Add(x => x.Id, Guid.NewGuid()));

        Assert.NotNull(cut.Find("[data-testid=order-not-found] a[href='/purchasing']"));
    }

    [Fact]
    public void Only_the_buyer_can_record_a_receipt_and_open_quantities_are_prefilled()
    {
        Assert.Empty(ShowOrder().FindAll("[data-testid=receipt-form]"));

        AsBuyer();
        var cut = ShowOrder(withInvoices: false);

        Assert.Equal("15", cut.Find("[data-testid=receipt-qty-1] input").GetAttribute("value"));
        Assert.Empty(cut.FindAll("[data-testid=receipt-qty-2]")); // nothing open on line 2
    }

    [Fact]
    public void Recording_a_receipt_sends_what_was_typed_and_shows_the_result()
    {
        AsBuyer();
        var cut = ShowOrder(withInvoices: false);
        cut.Find("[data-testid=receipt-number] input").Change("GR-7002");
        cut.Find("input[data-testid=receipt-date]").Change("2026-11-16");
        cut.Find("[data-testid=receipt-qty-1] input").Change("10,5");

        cut.Find("[data-testid=record-receipt]").Click();

        var (orderId, draft, buyer) = Assert.Single(Purchasing.Receipts);
        Assert.Equal((OrderId, "GR-7002", new DateOnly(2026, 11, 16), Me), (orderId, draft.Number, draft.ReceivedDate, buyer));
        Assert.Equal([new ReceiptLineDraft(1, 10.5m)], draft.Lines);
        Assert.Equal(T["Purchasing.ReceiptRecorded"], cut.Find("[data-testid=purchasing-result]").TextContent.Trim());
    }

    [Theory]
    [InlineData("7.5")]     // a point: in German 7.5 could be read as 75; refused, never guessed
    [InlineData("1.000")]
    [InlineData("abc")]
    [InlineData("-1")]
    public void A_quantity_that_is_not_a_plain_german_number_is_refused_before_sending(string typed)
    {
        AsBuyer();
        var cut = ShowOrder(withInvoices: false);
        cut.Find("[data-testid=receipt-number] input").Change("GR-7002");
        cut.Find("[data-testid=receipt-qty-1] input").Change(typed);

        cut.Find("[data-testid=record-receipt]").Click();

        Assert.Empty(Purchasing.Receipts);
        Assert.Equal(T["Purchasing.NotANumber", 1], cut.Find("[data-testid=purchasing-result]").TextContent.Trim());
    }

    [Fact]
    public void A_refused_receipt_names_the_line_in_the_pages_language()
    {
        AsBuyer();
        Purchasing.Result = new PurchasingResult(PurchasingOutcome.OverReceived, Detail: "More than ordered on order line 1.", Position: 1);
        var cut = ShowOrder(withInvoices: false);
        cut.Find("[data-testid=receipt-number] input").Change("GR-7002");

        cut.Find("[data-testid=record-receipt]").Click();

        Assert.Equal(T["Purchasing.OverReceived", 1], cut.Find("[data-testid=purchasing-result]").TextContent.Trim());
    }

    [Fact]
    public void The_server_checks_the_buyer_policy_again_before_recording()
    {
        AsBuyer();
        var cut = ShowOrder(withInvoices: false);
        cut.Find("[data-testid=receipt-number] input").Change("GR-7002");
        var button = cut.Find("[data-testid=record-receipt]");

        Auth.SetPolicies(Policies.ReadPurchasing); // the role was taken away; the open page still shows the button
        button.Click();

        Assert.Empty(Purchasing.Receipts);
    }

    // ---- New order -------------------------------------------------------------------------------------------------

    private IRenderedComponent<NewPurchaseOrderPage> NewOrder()
    {
        AsBuyer();
        PurchasingQueries.Vendors.Add(new VendorChoice(VendorId, "V-1012", "Demo Kabel & Co. KG"));
        return Render<NewPurchaseOrderPage>();
    }

    private static void FillLine(IRenderedComponent<NewPurchaseOrderPage> cut, int index, string item, string description, string quantity, string price, string tax)
    {
        var row = cut.FindAll("[data-testid=draft-line]")[index];
        cut.FindAll("[data-testid=line-item] input")[index].Change(item);
        cut.FindAll("[data-testid=line-description] input")[index].Change(description);
        cut.FindAll("[data-testid=line-quantity] input")[index].Change(quantity);
        cut.FindAll("[data-testid=line-price] input")[index].Change(price);
        cut.FindAll("select[data-testid=line-tax]")[index].Change(tax);
        Assert.NotNull(row);
    }

    [Fact]
    public void Saving_a_new_order_sends_the_draft_and_opens_the_order()
    {
        var cut = NewOrder();
        cut.Find("[data-testid=order-number] input").Change("PO-7009");
        cut.Find("select[data-testid=order-vendor]").Change(VendorId.ToString());
        cut.Find("input[data-testid=order-date]").Change("2026-11-16");
        FillLine(cut, 0, "CABLE-HDMI", "HDMI cable 2 m", "40", "7,4125", "19");
        cut.Find("[data-testid=add-line]").Click();
        FillLine(cut, 1, "BOOK-HGB", "Handbook", "3", "39,90", "7");

        cut.Find("[data-testid=save-order]").Click();

        var (draft, buyer) = Assert.Single(Purchasing.Orders);
        Assert.Equal(("PO-7009", VendorId, new DateOnly(2026, 11, 16), Me), (draft.Number, draft.VendorId, draft.OrderDate, buyer));
        Assert.Equal(
            [new OrderLineDraft(1, "CABLE-HDMI", "HDMI cable 2 m", 40m, 7.4125m, 19m), new OrderLineDraft(2, "BOOK-HGB", "Handbook", 3m, 39.90m, 7m)],
            draft.Lines);
        Assert.EndsWith($"/purchasing/{Purchasing.CreatedId}", CurrentUri, StringComparison.Ordinal);
    }

    [Fact]
    public void A_line_can_be_removed_and_the_rest_are_numbered_again()
    {
        var cut = NewOrder();
        cut.Find("[data-testid=add-line]").Click();
        cut.Find("[data-testid=add-line]").Click();
        FillLine(cut, 2, "C", "Third", "1", "1", "19");

        cut.FindAll("[data-testid=remove-line]")[0].Click();
        cut.Find("[data-testid=order-number] input").Change("PO-7010");
        cut.Find("select[data-testid=order-vendor]").Change(VendorId.ToString());
        FillLine(cut, 0, "B", "Second", "1", "1", "19");
        cut.Find("[data-testid=save-order]").Click();

        Assert.Equal([(1, "Second"), (2, "Third")], Assert.Single(Purchasing.Orders).Draft.Lines.Select(l => (l.LineNumber, l.Description)));
    }

    [Fact]
    public void A_refused_order_stays_on_the_form_and_names_the_line()
    {
        var cut = NewOrder();
        Purchasing.Result = new PurchasingResult(PurchasingOutcome.InvalidLine, Detail: "Line 1: the quantity must be more than 0.", Position: 1);
        cut.Find("[data-testid=order-number] input").Change("PO-7011");
        cut.Find("select[data-testid=order-vendor]").Change(VendorId.ToString());
        FillLine(cut, 0, "X", "Desk", "1", "1", "19");

        cut.Find("[data-testid=save-order]").Click();

        Assert.Equal(T["Purchasing.InvalidLine", 1], cut.Find("[data-testid=purchasing-result]").TextContent.Trim());
        Assert.EndsWith("/", CurrentUri, StringComparison.Ordinal); // not navigated away
        Assert.Equal("PO-7011", cut.Find("[data-testid=order-number] input").GetAttribute("value")); // nothing typed is lost
    }

    [Fact]
    public void The_server_checks_the_buyer_policy_again_before_saving()
    {
        var cut = NewOrder();
        cut.Find("[data-testid=order-number] input").Change("PO-7012");
        cut.Find("select[data-testid=order-vendor]").Change(VendorId.ToString());
        FillLine(cut, 0, "X", "Desk", "1", "1", "19");
        var button = cut.Find("[data-testid=save-order]");

        Auth.SetPolicies(Policies.ReadPurchasing);
        button.Click();

        Assert.Empty(Purchasing.Orders);
    }

    [Fact]
    public void Only_active_vendors_are_offered()
    {
        var options = NewOrder().FindAll("select[data-testid=order-vendor] option").Select(o => o.GetAttribute("value")).Where(v => !string.IsNullOrEmpty(v));

        Assert.Equal([VendorId.ToString()], options);
        Assert.Equal(1, PurchasingQueries.VendorCalls);
    }

    [Fact]
    public void The_pages_speak_german_too()
    {
        UseLanguage("de");
        PurchasingQueries.Page = new PurchaseOrderList([new PurchaseOrderSummary(OrderId, "PO-7001", "Demo Kabel & Co. KG", new DateOnly(2026, 11, 2), "EUR", 416.20m, ReceiptState.Complete)], 1, 25, 1);

        var cut = Render<PurchaseOrdersPage>();

        Assert.Equal(T["Purchasing.Title"], cut.Find("h1").TextContent.Trim());
        Assert.NotEqual(Localization.TextResources.English["Purchasing.Title"], T["Purchasing.Title"].Value);
        Assert.Equal(T["ReceiptState.Complete"], cut.Find("[data-testid=receipt-state]").TextContent.Trim());
    }

    // ---- Orders from the ERP (package 16c) ---------------------------------------------------------------------------

    private IRenderedComponent<PurchaseOrderPage> ShowErpOrder(PurchaseOrderStatus status = PurchaseOrderStatus.Open)
    {
        PurchasingQueries.Details[OrderId] = Detail(withInvoices: false) with { Source = RecordSource.Erp, Status = status };
        return Render<PurchaseOrderPage>(p => p.Add(x => x.Id, OrderId));
    }

    [Fact]
    public void An_order_from_the_erp_says_so()
    {
        var cut = ShowErpOrder();

        Assert.Equal(T["Purchasing.FromErp"], cut.Find("[data-testid=order-source]").TextContent.Trim());
    }

    [Fact]
    public void An_order_made_in_the_app_shows_no_erp_marker()
    {
        Assert.Empty(ShowOrder().FindAll("[data-testid=order-source]"));
    }

    [Fact]
    public void The_buyer_records_no_receipt_on_an_order_from_the_erp_and_is_told_why()
    {
        AsBuyer();

        var cut = ShowErpOrder();

        Assert.Empty(cut.FindAll("[data-testid=receipt-form]"));
        Assert.Equal(T["Purchasing.ErpReceiptsHint"], cut.Find("[data-testid=erp-receipts-hint]").TextContent.Trim());
    }

    [Fact]
    public void A_closed_order_says_so_in_words()
    {
        var cut = ShowErpOrder(PurchaseOrderStatus.Closed);

        Assert.Equal(T["OrderStatus.Closed"], cut.Find("[data-testid=order-status]").TextContent.Trim());
    }
}
