using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using EInvoice.Application.Purchasing;
using EInvoice.Application.Sync;
using EInvoice.IntegrationTests.Infrastructure;
using EInvoice.IntegrationTests.Posting;
using EInvoice.MockErp;

namespace EInvoice.IntegrationTests.Sync;

/// <summary>
/// The mock ERP's master data (package 16c), the source the Go sync reads: GET data/Vendors, data/PurchaseOrders and
/// data/ProductReceipts, OData style. $filter=modifiedDateTime ge &lt;time&gt; (nothing else), $orderby=modifiedDateTime,id
/// (the only order, also when not asked), $top 1 to 1000 (default 1000), and an absolute @odata.nextLink while more
/// records follow. The link carries a $skiptoken with the last (modifiedDateTime, id) read: the next page starts strictly
/// after it (keyset paging), so a record that changes during a run cannot shift the pages. By id: data/PurchaseOrders('id').
/// Anything else is 400: the sync must not believe it filtered when the ERP ignored the filter.
/// Seeded at start from a fixed seed (MasterData:*): 10,000 orders by default, every record within the app's rules.
/// Data calls need a token and go through chaos mode like postings.
/// </summary>
public sealed class MockErpMasterDataTests : IDisposable
{
    private readonly MockErpFactory _erp = new();

    public void Dispose() => _erp.Dispose();

    private static MockErpFactory Small(int vendors = 3, int orders = 14, int seed = 16) => new(new Dictionary<string, string?>
    {
        ["MasterData:Vendors"] = vendors.ToString(CultureInfo.InvariantCulture),
        ["MasterData:PurchaseOrders"] = orders.ToString(CultureInfo.InvariantCulture),
        ["MasterData:Seed"] = seed.ToString(CultureInfo.InvariantCulture),
    });

    private static async Task<JsonElement> PageAsync(HttpClient http, string url)
    {
        using var response = await http.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Every record of a set, following nextLink, with the number of pages it took.</summary>
    private static async Task<(List<JsonElement> Records, int Pages)> AllAsync(HttpClient http, string url)
    {
        var records = new List<JsonElement>();
        var pages = 0;
        string? next = url;
        while (next is not null)
        {
            var page = await PageAsync(http, next);
            pages++;
            records.AddRange(page.GetProperty("value").EnumerateArray().Select(r => r.Clone()));
            next = page.TryGetProperty("@odata.nextLink", out var link) ? link.GetString() : null;
            Assert.True(pages < 1000, "nextLink never ends");
        }

        return (records, pages);
    }

    private static string Id(JsonElement record) => record.GetProperty("id").GetString()!;

    private static DateTimeOffset Modified(JsonElement record) => record.GetProperty("modifiedDateTime").GetDateTimeOffset();

    private static string Filter(DateTimeOffset since) =>
        "$filter=" + Uri.EscapeDataString("modifiedDateTime ge " + since.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture));

    // ---- Access and seed --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("data/Vendors")]
    [InlineData("data/PurchaseOrders")]
    [InlineData("data/ProductReceipts")]
    [InlineData("data/PurchaseOrders('PO-00001')")]
    public async Task Master_data_needs_a_token(string url)
    {
        using var anonymous = _erp.AnonymousClient();

        using var response = await anonymous.GetAsync(url);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task By_default_the_mock_holds_10000_orders_with_their_vendors_and_receipts()
    {
        using var http = _erp.Client();

        var (orders, pages) = await AllAsync(http, "data/PurchaseOrders");
        var (vendors, _) = await AllAsync(http, "data/Vendors");
        var (receipts, _) = await AllAsync(http, "data/ProductReceipts");

        Assert.Equal(10_000, orders.Count);
        Assert.Equal(10, pages); // $top defaults to 1000
        Assert.Equal(10_000, orders.Select(Id).Distinct().Count());
        Assert.InRange(vendors.Count, 100, 1000);
        Assert.InRange(receipts.Count, 5_000, 10_000);
    }

    [Fact]
    public async Task The_same_seed_gives_the_same_records()
    {
        using var other = new MockErpFactory();
        using var http = _erp.Client();
        using var otherHttp = other.Client();

        var first = await PageAsync(http, "data/PurchaseOrders?$top=50");
        var second = await PageAsync(otherHttp, "data/PurchaseOrders?$top=50");

        // Times are relative to the start, so compare everything but them.
        static string WithoutTimes(JsonElement page) => string.Join("\n", page.GetProperty("value").EnumerateArray().Select(r =>
            $"{Id(r)}|{r.GetProperty("purchaseOrderNumber")}|{r.GetProperty("vendorId")}|{r.GetProperty("status")}|{r.GetProperty("lines").GetRawText()}"));
        Assert.Equal(WithoutTimes(first), WithoutTimes(second));
    }

    [Fact]
    public async Task Every_seeded_record_follows_the_apps_rules()
    {
        using var http = _erp.Client();
        var (vendors, _) = await AllAsync(http, "data/Vendors");
        var (orders, _) = await AllAsync(http, "data/PurchaseOrders");
        var (receipts, _) = await AllAsync(http, "data/ProductReceipts");
        var number = new Regex("^[A-Za-z0-9][A-Za-z0-9._/-]*$");
        var vatId = new Regex("^[A-Z]{2}[A-Za-z0-9]{2,18}$");
        var now = DateTimeOffset.UtcNow;
        var vendorIds = vendors.Select(Id).ToHashSet(StringComparer.Ordinal);

        Assert.All(vendors, v =>
        {
            Assert.True(ErpSync.IsErpId(Id(v)));
            Assert.Matches(number, v.GetProperty("vendorAccountNumber").GetString()!);
            Assert.InRange(v.GetProperty("name").GetString()!.Length, 1, ErpSync.MaxVendorNameLength);
            Assert.True(v.GetProperty("vatNumber").ValueKind == JsonValueKind.Null || vatId.IsMatch(v.GetProperty("vatNumber").GetString()!));
            Assert.Contains(v.GetProperty("status").GetString(), new[] { "Active", "Blocked" });
            Assert.True(Modified(v) < now);
        });

        var ordered = new Dictionary<string, Dictionary<int, decimal>>(StringComparer.Ordinal);
        Assert.All(orders, o =>
        {
            Assert.True(ErpSync.IsErpId(Id(o)));
            Assert.Matches(number, o.GetProperty("purchaseOrderNumber").GetString()!);
            Assert.InRange(o.GetProperty("purchaseOrderNumber").GetString()!.Length, 1, PurchasingLimits.MaxNumberLength);
            Assert.Contains(o.GetProperty("vendorId").GetString()!, vendorIds);
            Assert.Equal("EUR", o.GetProperty("currencyCode").GetString());
            Assert.Contains(o.GetProperty("status").GetString(), new[] { "Open", "Closed" });
            Assert.True(DateOnly.Parse(o.GetProperty("orderDate").GetString()!, CultureInfo.InvariantCulture) <= DateOnly.FromDateTime(Modified(o).UtcDateTime));
            var lines = o.GetProperty("lines").EnumerateArray().ToList();
            Assert.InRange(lines.Count, 1, PurchasingLimits.MaxLines);
            Assert.Equal(lines.Count, lines.Select(l => l.GetProperty("lineNumber").GetInt32()).Distinct().Count());
            Assert.All(lines, l => Assert.True(l.GetProperty("quantity").GetDecimal() > 0 && l.GetProperty("unitPrice").GetDecimal() >= 0));
            ordered[Id(o)] = lines.ToDictionary(l => l.GetProperty("lineNumber").GetInt32(), l => l.GetProperty("quantity").GetDecimal());
        });

        var received = new Dictionary<(string Order, int Line), decimal>();
        Assert.All(receipts, r =>
        {
            Assert.True(ErpSync.IsErpId(Id(r)));
            Assert.Matches(number, r.GetProperty("productReceiptNumber").GetString()!);
            var order = r.GetProperty("purchaseOrderId").GetString()!;
            Assert.True(ordered.ContainsKey(order));
            foreach (var line in r.GetProperty("lines").EnumerateArray())
            {
                var key = (order, line.GetProperty("purchaseOrderLineNumber").GetInt32());
                Assert.True(ordered[order].ContainsKey(key.Item2));
                received[key] = received.GetValueOrDefault(key) + line.GetProperty("quantity").GetDecimal();
            }
        });
        Assert.All(received, r => Assert.True(r.Value <= ordered[r.Key.Order][r.Key.Line], "a receipt exceeds its order"));

        // The statuses the sync carries over are there to carry.
        Assert.Contains(vendors, v => v.GetProperty("status").GetString() == "Blocked");
        Assert.Contains(orders, o => o.GetProperty("status").GetString() == "Closed");
    }

    [Fact]
    public async Task Sizes_come_from_the_settings_and_zero_is_empty()
    {
        using var empty = Small(vendors: 0, orders: 0);
        using var http = empty.Client();

        foreach (var set in new[] { "Vendors", "PurchaseOrders", "ProductReceipts" })
        {
            var page = await PageAsync(http, $"data/{set}");
            Assert.Empty(page.GetProperty("value").EnumerateArray());
            Assert.False(page.TryGetProperty("@odata.nextLink", out _));
        }
    }

    // ---- Paging -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Records_come_by_change_time_then_id()
    {
        using var http = _erp.Client();

        var (orders, _) = await AllAsync(http, "data/PurchaseOrders");

        var expected = orders.OrderBy(Modified).ThenBy(Id, StringComparer.Ordinal).Select(Id).ToList();
        Assert.Equal(expected, orders.Select(Id));
    }

    [Fact]
    public async Task Small_pages_followed_to_the_end_give_every_record_once_in_the_same_order()
    {
        using var mock = Small(orders: 50);
        using var http = mock.Client();

        var (whole, _) = await AllAsync(http, "data/PurchaseOrders");
        var (paged, pages) = await AllAsync(http, "data/PurchaseOrders?$top=7");

        Assert.Equal(8, pages); // 7 x 7 + 1
        Assert.Equal(whole.Select(Id), paged.Select(Id));
    }

    [Fact]
    public async Task A_last_page_of_exactly_top_records_has_no_next_link()
    {
        using var mock = Small(orders: 14);
        using var http = mock.Client();

        var first = await PageAsync(http, "data/PurchaseOrders?$top=7");
        var second = await PageAsync(http, first.GetProperty("@odata.nextLink").GetString()!);

        Assert.Equal(7, second.GetProperty("value").GetArrayLength());
        Assert.False(second.TryGetProperty("@odata.nextLink", out _));
    }

    [Fact]
    public async Task The_next_link_is_absolute_and_keeps_the_filter_and_the_page_size()
    {
        using var mock = Small(orders: 20);
        using var http = mock.Client();
        var since = DateTimeOffset.UtcNow.AddYears(-1);

        var page = await PageAsync(http, $"data/PurchaseOrders?{Filter(since)}&$top=5");

        var next = new Uri(page.GetProperty("@odata.nextLink").GetString()!);
        Assert.True(next.IsAbsoluteUri);
        Assert.Equal("/data/PurchaseOrders", next.AbsolutePath);
        var query = Uri.UnescapeDataString(next.Query);
        Assert.Contains("$filter=modifiedDateTime ge ", query, StringComparison.Ordinal);
        Assert.Contains("$top=5", query, StringComparison.Ordinal);
        Assert.Contains("$skiptoken=", query, StringComparison.Ordinal);
    }

    // ---- Filter -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_filter_keeps_records_changed_at_or_after_the_time()
    {
        using var http = _erp.Client();
        var (all, _) = await AllAsync(http, "data/PurchaseOrders");
        var since = Modified(all[all.Count / 2]); // a record's own time: ge includes it

        var (changed, _) = await AllAsync(http, $"data/PurchaseOrders?{Filter(since)}");

        Assert.Equal(all.Where(r => Modified(r) >= since).Select(Id), changed.Select(Id));
        Assert.Contains(Id(all[all.Count / 2]), changed.Select(Id));
    }

    [Fact]
    public async Task A_filter_after_every_change_gives_an_empty_page()
    {
        using var http = _erp.Client();

        var page = await PageAsync(http, $"data/Vendors?{Filter(DateTimeOffset.UtcNow.AddMinutes(1))}");

        Assert.Empty(page.GetProperty("value").EnumerateArray());
        Assert.False(page.TryGetProperty("@odata.nextLink", out _));
    }

    [Theory]
    [InlineData("data/PurchaseOrders?$top=0")]
    [InlineData("data/PurchaseOrders?$top=1001")]
    [InlineData("data/PurchaseOrders?$top=ten")]
    [InlineData("data/PurchaseOrders?$filter=status eq 'Open'")]
    [InlineData("data/PurchaseOrders?$filter=modifiedDateTime gt 2026-01-01T00:00:00Z")]
    [InlineData("data/PurchaseOrders?$filter=modifiedDateTime ge yesterday")]
    [InlineData("data/PurchaseOrders?$orderby=id")]
    [InlineData("data/PurchaseOrders?$skip=10")]
    [InlineData("data/PurchaseOrders?$expand=lines")]
    [InlineData("data/PurchaseOrders?$skiptoken=not-a-token")]
    [InlineData("data/Invoices")]
    public async Task Anything_the_mock_does_not_understand_is_400_not_ignored(string url)
    {
        using var http = _erp.Client();

        using var response = await http.GetAsync(url);

        Assert.True(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound, $"{url}: {response.StatusCode}");
        if (!url.Contains("Invoices", StringComparison.Ordinal))
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    [Fact]
    public async Task The_order_by_the_mock_uses_anyway_may_be_asked_for()
    {
        using var http = _erp.Client();

        using var response = await http.GetAsync("data/Vendors?$orderby=" + Uri.EscapeDataString("modifiedDateTime,id"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- By id ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task One_record_by_its_id_and_404_for_an_unknown_one()
    {
        using var http = _erp.Client();
        var first = (await PageAsync(http, "data/PurchaseOrders?$top=1")).GetProperty("value")[0];

        var one = await PageAsync(http, $"data/PurchaseOrders('{Id(first)}')");
        using var unknown = await http.GetAsync("data/PurchaseOrders('PO-99999999')");

        Assert.Equal(first.GetRawText(), one.GetRawText());
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
    }

    [Theory]
    [InlineData("data/Vendors")]
    [InlineData("data/ProductReceipts")]
    public async Task Vendors_and_receipts_are_found_by_id_too(string set)
    {
        using var http = _erp.Client();
        var first = (await PageAsync(http, $"{set}?$top=1")).GetProperty("value")[0];

        var one = await PageAsync(http, $"{set}('{Id(first)}')");

        Assert.Equal(first.GetRawText(), one.GetRawText());
    }

    // ---- The committed examples (contracts/d365) --------------------------------------------------------------------

    private static string Example(string name) => File.ReadAllText(Path.Combine(SqlServerFixture.RepositoryRoot(), "contracts", "d365", name));

    [Theory]
    [InlineData("vendors.page.example.json", typeof(ODataPage<ErpVendor>))]
    [InlineData("purchase-orders.page.example.json", typeof(ODataPage<ErpPurchaseOrder>))]
    [InlineData("product-receipts.page.example.json", typeof(ODataPage<ErpProductReceipt>))]
    public void The_mock_reads_and_writes_each_example_exactly(string file, Type page)
    {
        var example = Example(file);

        var read = JsonSerializer.Deserialize(example, page, Ledger.Json); // strict: no field more, none missing

        // Both through the same writer, so only content can differ (not how a character is escaped).
        static string Normal(string json) => JsonSerializer.Serialize(JsonDocument.Parse(json).RootElement);
        Assert.Equal(Normal(example), Normal(JsonSerializer.Serialize(read, page, MasterData.Json)));
    }

    [Theory]
    [InlineData("vendors.page.example.json", "data/Vendors")]
    [InlineData("purchase-orders.page.example.json", "data/PurchaseOrders")]
    [InlineData("product-receipts.page.example.json", "data/ProductReceipts")]
    public async Task What_the_mock_serves_has_the_shape_of_the_example(string file, string url)
    {
        using var http = _erp.Client();
        var served = (await PageAsync(http, url + "?$top=1")).GetProperty("value")[0];
        var example = JsonDocument.Parse(Example(file)).RootElement.GetProperty("value")[0];

        Assert.Equal(Shape(example), Shape(served));
    }

    /// <summary>Property names and value kinds, nested; null counts as any kind (a VAT id may be missing).</summary>
    private static string Shape(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "{" + string.Join(",", element.EnumerateObject().Select(p => p.Name + ":" + Shape(p.Value))) + "}",
        JsonValueKind.Array => "[" + (element.GetArrayLength() > 0 ? Shape(element[0]) : string.Empty) + "]",
        JsonValueKind.Null or JsonValueKind.String => "text",
        JsonValueKind.Number => "number",
        _ => element.ValueKind.ToString(),
    };

    // ---- Chaos ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Chaos_reaches_master_data_reads_too()
    {
        await _erp.ChaosAsync(1.0, ["throttle"], retryAfterSeconds: 3);
        using var http = _erp.Client();

        using var response = await http.GetAsync("data/PurchaseOrders?$top=5");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(3), response.Headers.RetryAfter?.Delta);
    }

    [Fact]
    public async Task A_malformed_page_is_not_json()
    {
        await _erp.ChaosAsync(1.0, ["malformed"]);
        using var http = _erp.Client();

        using var response = await http.GetAsync("data/Vendors?$top=5");
        var body = await response.Content.ReadAsStringAsync();

        Assert.ThrowsAny<JsonException>(() => JsonDocument.Parse(body));
    }
}
