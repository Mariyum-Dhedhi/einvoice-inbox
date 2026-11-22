using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EInvoice.Application.Audit;
using EInvoice.Application.Purchasing;
using EInvoice.Domain.Purchasing;
using EInvoice.Domain.Shared;
using EInvoice.Domain.Vendors;
using EInvoice.Infrastructure.Purchasing;
using EInvoice.IntegrationTests.Api;
using EInvoice.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace EInvoice.IntegrationTests.Sync;

/// <summary>
/// The ERP sync's way in (package 16c): PUT /api/sync/vendors|purchase-orders|goods-receipts/{erpId} creates or updates
/// the record with that ERP id, keeps the business rules, and audits each real change as "ERP sync". The same body
/// again changes nothing and writes no event, so re-reading an overlap is harmless. Answers the sync can branch on:
/// 201 Created, 200 Updated or Unchanged; 409 when a record it depends on is not there yet (keep it and try again next
/// run); 422 when the record itself is refused (skip it); 400 when the request is not this contract. Watermarks and
/// open failures are kept per entity. Data is committed (the API has its own context), under unique ERP ids and numbers.
/// </summary>
[Trait("Category", "Database")]
[Collection(SqlServerCollection.Name)]
public sealed class SyncApiTests(SqlServerFixture fixture) : IAsyncLifetime
{
    private readonly string _run = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private ApiFactory _app = null!;
    private HttpClient _sync = null!;

    public Task InitializeAsync()
    {
        _app = new ApiFactory(ApiFactory.Settings(fixture.AppConnectionString));
        _sync = _app.CreateSyncClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _sync.Dispose();
        await _app.DisposeAsync();
    }

    private string Id(string what) => $"{what}-{_run}";

    private string VendorNumber(int n = 1) => $"K-{_run}-{n}";

    private static object VendorBody(string number, string name = "Nordlicht Papier GmbH", string? vatId = null, string status = "Active") =>
        new { number, name, vatId, status };

    private static object OrderLine(int line, decimal quantity = 10m, decimal unitPrice = 4.5m) =>
        new { lineNumber = line, itemNumber = $"ITEM-{line}", description = $"Item {line}", quantity, unitPrice, taxRatePercent = 19m };

    private object OrderBody(string vendorErpId, object[]? lines = null, string status = "Open", string currency = "EUR", string? number = null) =>
        new { number = number ?? $"PO-{_run}", vendorErpId, orderDate = "2026-11-02", currency, status, lines = lines ?? [OrderLine(1), OrderLine(2)] };

    private object ReceiptBody(string orderErpId, decimal quantity = 4m, string? number = null) =>
        new { number = number ?? $"GR-{_run}", purchaseOrderErpId = orderErpId, receivedDate = "2026-11-03", lines = new[] { new { purchaseOrderLineNumber = 1, quantity } } };

    private async Task<(HttpStatusCode Status, JsonElement Body)> PutAsync(string path, object body)
    {
        using var response = await _sync.PutAsJsonAsync(path, body);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }

    private static string Outcome(JsonElement body) => body.GetProperty("outcome").GetString()!;

    private async Task<string> VendorAsync(string? erpId = null, int n = 1)
    {
        erpId ??= Id("V");
        var (status, body) = await PutAsync($"/api/sync/vendors/{erpId}", VendorBody(VendorNumber(n)));
        Assert.True(status == HttpStatusCode.Created, body.ToString());
        return erpId;
    }

    private async Task<string> OrderAsync(string vendorErpId, string? erpId = null)
    {
        erpId ??= Id("PO");
        var (status, body) = await PutAsync($"/api/sync/purchase-orders/{erpId}", OrderBody(vendorErpId));
        Assert.True(status == HttpStatusCode.Created, body.ToString());
        return erpId;
    }

    private async Task<List<(string Action, string Actor, string Details)>> EventsAsync(string erpId)
    {
        await using var db = fixture.CreateContext();
        return [.. (await db.AuditEvents.AsNoTracking().Where(e => e.Details.Contains(erpId)).OrderBy(e => e.Sequence)
            .Select(e => new { e.Action, e.Actor, e.Details }).ToListAsync()).Select(e => (e.Action, e.Actor, e.Details))];
    }

    // ---- Vendors ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_vendor_is_created_then_the_same_body_again_changes_nothing_and_writes_no_event()
    {
        var erpId = Id("V");

        var (created, first) = await PutAsync($"/api/sync/vendors/{erpId}", VendorBody(VendorNumber(), vatId: null));
        var (again, second) = await PutAsync($"/api/sync/vendors/{erpId}", VendorBody(VendorNumber(), vatId: null));

        Assert.Equal((HttpStatusCode.Created, "Created"), (created, Outcome(first)));
        Assert.Equal((HttpStatusCode.OK, "Unchanged"), (again, Outcome(second)));
        Assert.Equal(first.GetProperty("id").GetGuid(), second.GetProperty("id").GetGuid());
        var events = await EventsAsync(erpId);
        var created1 = Assert.Single(events);
        Assert.Equal((AuditActions.VendorSyncedFromErp, "erp-sync"), (created1.Action, created1.Actor));
        Assert.Contains("\"source\":\"ERP sync\"", created1.Details, StringComparison.Ordinal);
        Assert.Contains("\"change\":\"Created\"", created1.Details, StringComparison.Ordinal);

        await using var db = fixture.CreateContext();
        var vendor = await db.Vendors.AsNoTracking().SingleAsync(v => v.ErpId == erpId);
        Assert.Equal((RecordSource.Erp, VendorNumber(), true), (vendor.Source, vendor.Number, vendor.IsActive));
    }

    [Fact]
    public async Task A_changed_vendor_is_updated_and_audited_and_a_blocked_one_becomes_inactive()
    {
        var erpId = await VendorAsync();

        var (status, body) = await PutAsync($"/api/sync/vendors/{erpId}", VendorBody(VendorNumber(), name: "Nordlicht Papier AG", status: "Blocked"));

        Assert.Equal((HttpStatusCode.OK, "Updated"), (status, Outcome(body)));
        await using var db = fixture.CreateContext();
        var vendor = await db.Vendors.AsNoTracking().SingleAsync(v => v.ErpId == erpId);
        Assert.Equal(("Nordlicht Papier AG", false), (vendor.Name, vendor.IsActive));
        Assert.Contains("\"change\":\"Updated\"", (await EventsAsync(erpId))[^1].Details, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_vendor_number_that_belongs_to_a_vendor_made_in_the_app_is_refused()
    {
        var (status, body) = await PutAsync($"/api/sync/vendors/{Id("V")}", VendorBody("V-1001")); // seeded, made in the app

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "NumberTaken"), (status, Outcome(body)));
        Assert.Equal("application/problem+json", (await _sync.PutAsJsonAsync($"/api/sync/vendors/{Id("V")}", VendorBody("V-1001"))).Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task A_vendor_cannot_change_its_number()
    {
        var erpId = await VendorAsync();

        var (status, body) = await PutAsync($"/api/sync/vendors/{erpId}", VendorBody(VendorNumber(2)));

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "NumberChanged"), (status, Outcome(body)));
    }

    // ---- Purchase orders --------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_order_is_created_then_the_same_body_again_changes_nothing()
    {
        var vendor = await VendorAsync();
        var erpId = Id("PO");

        var (created, _) = await PutAsync($"/api/sync/purchase-orders/{erpId}", OrderBody(vendor));
        var (again, body) = await PutAsync($"/api/sync/purchase-orders/{erpId}", OrderBody(vendor, [OrderLine(2), OrderLine(1)]));

        Assert.Equal(HttpStatusCode.Created, created);
        Assert.Equal((HttpStatusCode.OK, "Unchanged"), (again, Outcome(body)));
        Assert.Equal([AuditActions.PurchaseOrderSyncedFromErp], (await EventsAsync(erpId)).Select(e => e.Action));
        await using var db = fixture.CreateContext();
        var order = await db.PurchaseOrders.AsNoTracking().SingleAsync(o => o.ErpId == erpId);
        Assert.Equal((RecordSource.Erp, PurchaseOrderStatus.Open, 2), (order.Source, order.Status, order.Lines.Count));
    }

    [Fact]
    public async Task Changed_lines_and_a_closed_status_are_taken_over()
    {
        var vendor = await VendorAsync();
        var erpId = await OrderAsync(vendor);

        var (status, body) = await PutAsync($"/api/sync/purchase-orders/{erpId}", OrderBody(vendor, [OrderLine(1, quantity: 12m), OrderLine(3)], status: "Closed"));

        Assert.Equal((HttpStatusCode.OK, "Updated"), (status, Outcome(body)));
        await using var db = fixture.CreateContext();
        var order = await db.PurchaseOrders.AsNoTracking().SingleAsync(o => o.ErpId == erpId);
        Assert.Equal(PurchaseOrderStatus.Closed, order.Status);
        Assert.Equal([(1, 12m), (3, 10m)], order.Lines.Select(l => (l.LineNumber, l.Quantity)));
        Assert.Equal(2, (await EventsAsync(erpId)).Count);
    }

    [Fact]
    public async Task An_order_whose_vendor_is_not_there_yet_is_409_so_the_sync_keeps_it_for_the_next_run()
    {
        var (status, body) = await PutAsync($"/api/sync/purchase-orders/{Id("PO")}", OrderBody(Id("NOVENDOR")));

        Assert.Equal((HttpStatusCode.Conflict, "VendorNotFound"), (status, Outcome(body)));
        await using var db = fixture.CreateContext();
        Assert.False(await db.PurchaseOrders.AnyAsync(o => o.ErpId == Id("PO")));
    }

    [Theory]
    [InlineData("currency")]
    [InlineData("line")]
    [InlineData("vendor")]
    [InlineData("number")]
    [InlineData("local number")]
    public async Task An_order_the_rules_refuse_is_422_and_changes_nothing(string problem)
    {
        var vendor = await VendorAsync();
        var other = await VendorAsync(Id("V2"), n: 2);
        var erpId = await OrderAsync(vendor);
        var (body, expected) = problem switch
        {
            "currency" => (OrderBody(vendor, currency: "USD"), "InvalidRecord"),
            "line" => (OrderBody(vendor, [OrderLine(1, quantity: -1m)]), "InvalidRecord"),
            "vendor" => (OrderBody(other), "VendorChanged"),
            "number" => (OrderBody(vendor, number: $"PO-{_run}-X"), "NumberChanged"),
            _ => (OrderBody(vendor, number: "PO-0101"), "NumberTaken"), // a seeded order made in the app
        };
        var path = problem == "local number" ? $"/api/sync/purchase-orders/{Id("PO-NEW")}" : $"/api/sync/purchase-orders/{erpId}";

        var (status, answer) = await PutAsync(path, body);

        Assert.Equal((HttpStatusCode.UnprocessableEntity, expected), (status, Outcome(answer)));
        Assert.Single(await EventsAsync(erpId)); // only its creation
    }

    [Fact]
    public async Task An_order_line_cannot_go_below_what_was_received()
    {
        var vendor = await VendorAsync();
        var order = await OrderAsync(vendor);
        Assert.Equal(HttpStatusCode.Created, (await PutAsync($"/api/sync/goods-receipts/{Id("GR")}", ReceiptBody(order, quantity: 8m))).Status);

        var (status, body) = await PutAsync($"/api/sync/purchase-orders/{order}", OrderBody(vendor, [OrderLine(1, quantity: 7m), OrderLine(2)]));

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "OrderBelowReceived"), (status, Outcome(body)));
    }

    // ---- Goods receipts ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_receipt_is_created_for_its_erp_order_and_the_same_body_again_changes_nothing()
    {
        var order = await OrderAsync(await VendorAsync());
        var erpId = Id("GR");

        var (created, _) = await PutAsync($"/api/sync/goods-receipts/{erpId}", ReceiptBody(order));
        var (again, body) = await PutAsync($"/api/sync/goods-receipts/{erpId}", ReceiptBody(order));

        Assert.Equal(HttpStatusCode.Created, created);
        Assert.Equal((HttpStatusCode.OK, "Unchanged"), (again, Outcome(body)));
        Assert.Equal([AuditActions.GoodsReceiptSyncedFromErp], (await EventsAsync(erpId)).Select(e => e.Action));
        await using var db = fixture.CreateContext();
        var receipt = await db.GoodsReceipts.AsNoTracking().SingleAsync(r => r.ErpId == erpId);
        Assert.Equal((RecordSource.Erp, 4m), (receipt.Source, receipt.Lines.Single().QuantityReceived));
    }

    [Fact]
    public async Task A_receipt_whose_order_is_not_there_yet_is_409()
    {
        var (status, body) = await PutAsync($"/api/sync/goods-receipts/{Id("GR")}", ReceiptBody(Id("NOORDER")));

        Assert.Equal((HttpStatusCode.Conflict, "OrderNotFound"), (status, Outcome(body)));
    }

    [Fact]
    public async Task A_receipt_over_what_was_ordered_is_refused()
    {
        var order = await OrderAsync(await VendorAsync());

        var (status, body) = await PutAsync($"/api/sync/goods-receipts/{Id("GR")}", ReceiptBody(order, quantity: 11m));

        Assert.Equal((HttpStatusCode.UnprocessableEntity, "OverReceived"), (status, Outcome(body)));
    }

    [Fact]
    public async Task The_buyer_cannot_add_a_receipt_to_an_order_from_the_erp()
    {
        var order = await OrderAsync(await VendorAsync());
        await using var db = fixture.CreateContext();
        var orderId = (await db.PurchaseOrders.AsNoTracking().SingleAsync(o => o.ErpId == order)).Id;
        var purchasing = new PurchasingAdministration(new EfPurchasingStore(db), TimeProvider.System);

        var result = await purchasing.RecordReceiptAsync(orderId, new ReceiptDraft($"GR-{_run}-B", new DateOnly(2026, 11, 3), [new ReceiptLineDraft(1, 1m)]), "buyer@test", CancellationToken.None);

        Assert.Equal(PurchasingOutcome.ErpManaged, result.Outcome);
        Assert.False(await db.GoodsReceipts.AnyAsync(r => r.PurchaseOrderId == orderId));
    }

    // ---- The contract -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("vendors", "{\"number\":\"K-1\"}")]                         // fields missing
    [InlineData("vendors", "{\"number\":\"K-1\",\"name\":\"N\",\"vatId\":null,\"status\":\"Gone\"}")]
    [InlineData("vendors", "{\"number\":\"K-1\",\"name\":\"N\",\"vatId\":null,\"status\":\"Active\",\"extra\":1}")]
    [InlineData("vendors", "not json")]
    [InlineData("purchase-orders", "{}")]
    public async Task A_body_that_is_not_the_contract_is_400(string entity, string json)
    {
        using var response = await _sync.PutAsync($"/api/sync/{entity}/{Id("X")}", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain("Gone", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal); // never repeats the input
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("x123456789012345678901234567890123456789012345678901")] // 51 characters
    public async Task An_erp_id_that_cannot_be_stored_is_400(string erpId)
    {
        var (status, _) = await PutAsync($"/api/sync/vendors/{Uri.EscapeDataString(erpId)}", VendorBody(VendorNumber()));

        Assert.Equal(HttpStatusCode.BadRequest, status);
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_415()
    {
        using var response = await _sync.PutAsync($"/api/sync/vendors/{Id("V")}", new StringContent("number=K-1", System.Text.Encoding.UTF8, "application/x-www-form-urlencoded"));

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    // ---- Watermarks and failures ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_watermark_is_kept_per_entity()
    {
        var mark = DateTimeOffset.UtcNow.AddMinutes(-Random.Shared.Next(1, 10_000));

        using var put = await _sync.PutAsJsonAsync("/api/sync/watermarks/goods-receipts", new { value = mark });
        var got = await _sync.GetFromJsonAsync<JsonElement>("/api/sync/watermarks/goods-receipts");

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.Equal(mark, got.GetProperty("value").GetDateTimeOffset());
        Assert.Equal("goods-receipts", got.GetProperty("entity").GetString());
    }

    [Theory]
    [InlineData("GET", "/api/sync/watermarks/invoices")]
    [InlineData("GET", "/api/sync/failures/invoices")]
    [InlineData("PUT", "/api/sync/watermarks/invoices")]
    [InlineData("PUT", "/api/sync/failures/invoices/x")]
    public async Task Only_the_three_entities_exist(string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path) { Content = method == "GET" ? null : JsonContent.Create(new { }) };
        using var response = await _sync.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_failure_is_kept_counted_and_removed()
    {
        var erpId = Id("FAIL");

        using (await _sync.PutAsJsonAsync($"/api/sync/failures/purchase-orders/{erpId}", new { reason = "VendorNotFound: the vendor is not there yet." }))
        using (await _sync.PutAsJsonAsync($"/api/sync/failures/purchase-orders/{erpId}", new { reason = "VendorNotFound: the vendor is not there yet." }))
        {
        }

        var open = (await _sync.GetFromJsonAsync<JsonElement>("/api/sync/failures/purchase-orders")).EnumerateArray().Single(f => f.GetProperty("erpId").GetString() == erpId);
        Assert.Equal(2, open.GetProperty("tries").GetInt32());
        Assert.StartsWith("VendorNotFound", open.GetProperty("reason").GetString(), StringComparison.Ordinal);

        using var deleted = await _sync.DeleteAsync($"/api/sync/failures/purchase-orders/{erpId}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.DoesNotContain((await _sync.GetFromJsonAsync<JsonElement>("/api/sync/failures/purchase-orders")).EnumerateArray(), f => f.GetProperty("erpId").GetString() == erpId);
    }

    [Fact]
    public async Task A_failure_reason_longer_than_200_characters_is_refused_not_cut()
    {
        using var response = await _sync.PutAsJsonAsync($"/api/sync/failures/vendors/{Id("FAIL")}", new { reason = new string('x', 201) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
