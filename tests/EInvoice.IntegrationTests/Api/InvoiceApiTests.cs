using System.Net;
using System.Text;
using System.Text.Json;
using EInvoice.Domain.Purchasing;
using EInvoice.IntegrationTests.Infrastructure;
using EInvoice.IntegrationTests.Matching;
using EInvoice.IntegrationTests.TestData;

namespace EInvoice.IntegrationTests.Api;

/// <summary>
/// The HTTP contract of /api/invoices against a real database: happy paths, every refusal, and safe errors.
/// HTTP calls commit, so every test creates its own order and invoice numbers and never assumes it is alone.
/// </summary>
[Trait("Category", "Database")]
[Collection(SqlServerCollection.Name)]
public sealed class InvoiceApiTests(SqlServerFixture fixture) : IAsyncLifetime
{
    private const string ProblemJson = "application/problem+json";

    private ApiFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new ApiFactory(ApiFactory.Settings(fixture.AppConnectionString));
        _client = _factory.CreateApiClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    // Committed invoices stay in the database for the whole run. Each one gets its own amount, so none of them
    // looks like a possible duplicate (same vendor, amount and date) of an invoice in another test.
    private static int _orders;

    /// <summary>A committed order for seeded vendor 1 (10 x a unique price and 4 x 39.90) and an invoice for it.</summary>
    private async Task<(MatchScenario Scenario, XRechnungBuilder Invoice)> OrderAsync(bool received = true)
    {
        var price = 1000.4125m + Interlocked.Increment(ref _orders);
        await using var db = fixture.CreateContext();
        var scenario = new MatchScenario(db).OrderLine(1, 10, price).OrderLine(2, 4, 39.90m);
        if (received)
        {
            scenario.Received(1, 10).Received(2, 4);
        }

        await scenario.SaveOrderAsync();
        var invoice = new XRechnungBuilder { PurchaseOrderNumber = scenario.PurchaseOrderNumber }
            .Line("HDMI cable 2 m", 10m, price, orderLine: "1")
            .Line("Handbook German commercial code", 4m, 39.90m, taxRate: 7m, orderLine: "2");
        return (scenario, invoice);
    }

    private Task<HttpResponseMessage> PostAsync(byte[] content, string fileName = "invoice.xml") =>
        _client.PostAsync(new Uri("/api/invoices", UriKind.Relative), ApiHttp.Upload(content, fileName));

    private async Task<Guid> UploadAsync(byte[] content, string fileName = "invoice.xml")
    {
        using var response = await PostAsync(content, fileName);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.JsonAsync()).GetProperty("invoiceId").GetGuid();
    }

    private static byte[] Unreadable() => Encoding.UTF8.GetBytes($"<Invoice><!-- {Guid.NewGuid()} --><unclosed></Invoice>");

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(ProblemJson, response.Content.Headers.ContentType?.MediaType);
        var problem = await response.JsonAsync();
        Assert.Equal((int)status, problem.GetProperty("status").GetInt32());
        Assert.False(problem.TryGetProperty("exception", out _));
    }

    // ---- POST /api/invoices -------------------------------------------------------------------------------------

    [Fact]
    public async Task Upload_of_a_clean_invoice_returns_201_with_location_and_status_Matched()
    {
        var (_, invoice) = await OrderAsync();

        using var response = await PostAsync(invoice.Build());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.JsonAsync();
        var id = body.GetProperty("invoiceId").GetGuid();
        Assert.Equal("Matched", body.GetProperty("status").GetString());
        Assert.Equal($"/api/invoices/{id}", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Unreadable_xml_is_accepted_as_Failed()
    {
        using var response = await PostAsync(Unreadable());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("Failed", (await response.JsonAsync()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Upload_without_a_file_field_is_400()
    {
        using var response = await _client.PostAsync(new Uri("/api/invoices", UriKind.Relative),
            ApiHttp.Upload(Encoding.UTF8.GetBytes("<Invoice/>"), field: "document"));

        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Empty_file_is_400()
    {
        using var response = await PostAsync([]);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData(ApiFactory.MaxDocumentBytes + 1)] // just over: refused by the pipeline
    [InlineData(4 * 1024 * 1024)] // far over: refused while the request is read, never buffered whole
    public async Task File_over_the_limit_is_413(long size)
    {
        var content = new byte[size];
        content.AsSpan().Fill((byte)' ');
        content[0] = (byte)'<';

        using var response = await PostAsync(content);

        await AssertProblemAsync(response, HttpStatusCode.RequestEntityTooLarge);
    }

    [Theory]
    [InlineData("%PDF-1.7\n", "invoice.pdf")]
    [InlineData("\u0089PNG\r\n\u001a\n", "invoice.xml")] // name says XML, bytes say PNG
    public async Task Pdf_or_other_content_is_415(string start, string fileName)
    {
        using var response = await PostAsync([.. Encoding.Latin1.GetBytes(start), .. new byte[64]], fileName);

        await AssertProblemAsync(response, HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task Same_file_twice_is_409_pointing_at_the_first_invoice()
    {
        var (_, invoice) = await OrderAsync();
        var xml = invoice.Build();
        var first = await UploadAsync(xml);

        using var response = await PostAsync(xml);

        await AssertProblemAsync(response, HttpStatusCode.Conflict);
        var problem = await response.JsonAsync();
        Assert.Equal("DuplicateFile", problem.GetProperty("outcome").GetString());
        Assert.Equal(first, problem.GetProperty("invoiceId").GetGuid());
    }

    [Fact]
    public async Task Same_vendor_and_number_in_a_different_file_is_409()
    {
        var (_, invoice) = await OrderAsync();
        await UploadAsync(invoice.Build());
        invoice.IssueDate = invoice.IssueDate.AddDays(1);

        using var response = await PostAsync(invoice.Build());

        await AssertProblemAsync(response, HttpStatusCode.Conflict);
        Assert.Equal("DuplicateInvoiceNumber", (await response.JsonAsync()).GetProperty("outcome").GetString());
    }

    // ---- GET /api/invoices ----------------------------------------------------------------------------------------

    [Fact]
    public async Task List_filters_by_status_newest_first()
    {
        var older = await UploadAsync(Unreadable());
        var newer = await UploadAsync(Unreadable());

        using var response = await _client.GetAsync(new Uri("/api/invoices?status=Failed&pageSize=100", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var page = await response.JsonAsync();
        var items = page.GetProperty("items").EnumerateArray().ToList();
        Assert.All(items, i => Assert.Equal("Failed", i.GetProperty("status").GetString()));
        var ids = items.Select(i => i.GetProperty("id").GetGuid()).ToList();
        Assert.True(ids.IndexOf(newer) >= 0 && ids.IndexOf(newer) < ids.IndexOf(older), "newest first");
        Assert.True(page.GetProperty("totalCount").GetInt32() >= 2);
        Assert.Equal(1, page.GetProperty("page").GetInt32());
        Assert.Equal(100, page.GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task List_pages_do_not_overlap()
    {
        await UploadAsync(Unreadable());
        await UploadAsync(Unreadable());

        var first = await (await _client.GetAsync(new Uri("/api/invoices?status=Failed&page=1&pageSize=1", UriKind.Relative))).JsonAsync();
        var second = await (await _client.GetAsync(new Uri("/api/invoices?status=Failed&page=2&pageSize=1", UriKind.Relative))).JsonAsync();

        var a = Assert.Single(first.GetProperty("items").EnumerateArray().ToList()).GetProperty("id").GetGuid();
        var b = Assert.Single(second.GetProperty("items").EnumerateArray().ToList()).GetProperty("id").GetGuid();
        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData("pageSize=101")]
    [InlineData("pageSize=0")]
    [InlineData("page=0")]
    [InlineData("status=Bogus")]
    [InlineData("status=99")] // a number that is not a status
    public async Task List_with_invalid_parameters_is_400(string query)
    {
        using var response = await _client.GetAsync(new Uri($"/api/invoices?{query}", UriKind.Relative));

        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }

    // ---- GET /api/invoices/{id} -----------------------------------------------------------------------------------

    [Fact]
    public async Task Detail_shows_content_lines_history_and_match_results()
    {
        var (scenario, invoice) = await OrderAsync();
        var id = await UploadAsync(invoice.Build(), @"C:\fakepath\Rechnung <1>.xml");

        using var response = await _client.GetAsync(new Uri($"/api/invoices/{id}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var detail = await response.JsonAsync();
        Assert.Equal(id, detail.GetProperty("id").GetGuid());
        Assert.Equal("Matched", detail.GetProperty("status").GetString());
        Assert.Equal(invoice.InvoiceNumber, detail.GetProperty("invoiceNumber").GetString());
        Assert.Equal("Beispiel Bürobedarf GmbH", detail.GetProperty("vendorName").GetString());
        Assert.Equal(scenario.PurchaseOrderNumber, detail.GetProperty("purchaseOrderNumber").GetString());
        Assert.Equal("EUR", detail.GetProperty("currency").GetString());
        Assert.Equal("anonymous", detail.GetProperty("submittedBy").GetString()); // until sign-in (package 9)

        var lines = detail.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal([1, 2], lines.Select(l => l.GetProperty("lineNumber").GetInt32()));
        Assert.Equal(4.0m, lines[1].GetProperty("quantity").GetDecimal());
        Assert.Equal(39.90m, lines[1].GetProperty("unitPrice").GetDecimal());
        Assert.Equal(0.4125m, lines[0].GetProperty("unitPrice").GetDecimal() % 1m); // four decimal places kept, not rounded to cents
        Assert.Equal(1, lines[0].GetProperty("purchaseOrderLineNumber").GetInt32());

        Assert.Equal(["Received", "Parsed", "Validated", "Matched"],
            detail.GetProperty("history").EnumerateArray().Select(h => h.GetProperty("to").GetString()));
        Assert.Empty(detail.GetProperty("findings").EnumerateArray());
        Assert.All(detail.GetProperty("matchResults").EnumerateArray(), r => Assert.Equal("FullMatch", r.GetProperty("outcome").GetString()));

        var document = detail.GetProperty("document");
        Assert.Equal("Rechnung _1_.xml", document.GetProperty("fileName").GetString());
        Assert.Equal(invoice.Build().Length, document.GetProperty("sizeBytes").GetInt32());
        Assert.False(document.TryGetProperty("content", out _)); // the file itself only through /document
    }

    [Fact]
    public async Task Detail_of_an_exception_lists_its_findings()
    {
        // Official KoSIT sample: its seller is not one of our vendors. A comment after the root element makes it a
        // different file (the original stays untouched), so other tests can still upload the original.
        var sample = await File.ReadAllBytesAsync(Path.Combine(SqlServerFixture.RepositoryRoot(), "testdata", "xrechnung", "kosit", "01.01a-INVOICE_ubl.xml"));
        var id = await UploadAsync([.. sample, .. Encoding.UTF8.GetBytes($"\n<!-- {Guid.NewGuid()} -->\n")]);

        var detail = await (await _client.GetAsync(new Uri($"/api/invoices/{id}", UriKind.Relative))).JsonAsync();

        Assert.Equal("Exception", detail.GetProperty("status").GetString());
        var finding = Assert.Single(detail.GetProperty("findings").EnumerateArray(), f => f.GetProperty("ruleCode").GetString() == "VENDOR_UNKNOWN");
        Assert.Equal("Blocking", finding.GetProperty("severity").GetString());
    }

    [Theory]
    [InlineData("/api/invoices/00000000-0000-0000-0000-000000000000")]
    [InlineData("/api/invoices/not-a-guid")]
    [InlineData("/api/invoices/00000000-0000-0000-0000-000000000000/document")]
    public async Task Unknown_invoice_is_404(string path)
    {
        using var response = await _client.GetAsync(new Uri(path, UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- GET /api/invoices/{id}/document ---------------------------------------------------------------------------

    [Fact]
    public async Task Document_download_is_the_original_bytes_as_a_non_sniffable_attachment()
    {
        var (_, invoice) = await OrderAsync();
        var xml = invoice.Build();
        var id = await UploadAsync(xml, "Rechnung <1>.xml");

        using var response = await _client.GetAsync(new Uri($"/api/invoices/{id}/document", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(xml, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/xml", response.Content.Headers.ContentType?.MediaType);
        var disposition = response.Content.Headers.ContentDisposition;
        Assert.Equal("attachment", disposition?.DispositionType);
        Assert.Equal("Rechnung _1_.xml", disposition?.FileNameStar ?? disposition?.FileName?.Trim('"'));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    // ---- POST /api/invoices/{id}/match -----------------------------------------------------------------------------

    [Fact]
    public async Task Rematch_after_late_goods_receipt_returns_Matched()
    {
        var (scenario, invoice) = await OrderAsync(received: false);
        var id = await UploadAsync(invoice.Build());
        await using (var db = fixture.CreateContext())
        {
            db.GoodsReceipts.Add(new GoodsReceipt($"G{Guid.NewGuid():N}"[..16], scenario.Order!.Id, new DateOnly(2026, 11, 19),
                [new GoodsReceiptLine(1, 10), new GoodsReceiptLine(2, 4)]));
            await db.SaveChangesAsync();
        }

        using var response = await _client.PostAsync(new Uri($"/api/invoices/{id}/match", UriKind.Relative), content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.JsonAsync();
        Assert.Equal("Matched", body.GetProperty("status").GetString());
        Assert.Empty(body.GetProperty("reasons").EnumerateArray());
    }

    [Fact]
    public async Task Rematch_still_short_returns_200_with_the_reason()
    {
        var (_, invoice) = await OrderAsync(received: false);
        var id = await UploadAsync(invoice.Build());

        using var response = await _client.PostAsync(new Uri($"/api/invoices/{id}/match", UriKind.Relative), content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.JsonAsync();
        Assert.Equal("Exception", body.GetProperty("status").GetString());
        Assert.Contains("NoGoodsReceipt", body.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    public async Task Rematch_of_a_Matched_invoice_is_409()
    {
        var (_, invoice) = await OrderAsync();
        var id = await UploadAsync(invoice.Build());

        using var response = await _client.PostAsync(new Uri($"/api/invoices/{id}/match", UriKind.Relative), content: null);

        await AssertProblemAsync(response, HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Rematch_of_an_unknown_invoice_is_404()
    {
        using var response = await _client.PostAsync(new Uri($"/api/invoices/{Guid.NewGuid()}/match", UriKind.Relative), content: null);

        await AssertProblemAsync(response, HttpStatusCode.NotFound);
    }

    // ---- Every API response ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Api_responses_carry_nosniff_and_enums_as_names()
    {
        using var response = await _client.GetAsync(new Uri("/api/invoices?pageSize=1", UriKind.Relative));

        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        foreach (var item in (await response.JsonAsync()).GetProperty("items").EnumerateArray())
        {
            Assert.Equal(JsonValueKind.String, item.GetProperty("status").ValueKind);
        }
    }
}
