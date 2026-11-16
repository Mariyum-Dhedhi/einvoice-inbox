using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EInvoice.Application.Posting;
using EInvoice.Infrastructure.Posting;
using EInvoice.IntegrationTests.Infrastructure;
using EInvoice.MockErp;
using Microsoft.AspNetCore.Mvc.Testing;

namespace EInvoice.IntegrationTests.Posting;

/// <summary>Starts the mock ERP in memory; its own project, which knows nothing of ours but the JSON on the wire.</summary>
internal sealed class MockErpFactory : WebApplicationFactory<MockErpProgram>
{
    /// <summary>
    /// Starts the mock now. The factory starts lazily and not thread-safely: two workers asking for a client at the same
    /// moment would each start their own mock, with separate books.
    /// </summary>
    public MockErpFactory()
    {
        _ = Server;
    }

    public HttpClient Client(TimeSpan? timeout = null)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("http://localhost/") });
        client.Timeout = timeout ?? TimeSpan.FromSeconds(10);
        return client;
    }

    /// <summary>The next <paramref name="count"/> postings fail with <paramref name="status"/>; after booking if asked (a lost reply).</summary>
    public async Task FailNextAsync(int status, int count = 1, bool afterBooking = false, int delayMs = 0)
    {
        using var client = Client();
        using var response = await client.PostAsJsonAsync("_mock/failures", new { status, count, afterBooking, delayMs });
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<JsonElement>> PostingsAsync()
    {
        using var client = Client();
        return await client.GetFromJsonAsync<List<JsonElement>>("_mock/postings") ?? [];
    }
}

/// <summary>
/// The contract between our connector and the ERP: the JSON we send is exactly what the ERP accepts (no field more,
/// none missing), the committed example documents it, and one idempotency key books once. If either side renames or
/// drops a field, these tests fail before anything reaches a real system.
/// </summary>
public sealed class ErpContractTests : IDisposable
{
    private readonly MockErpFactory _erp = new();

    public void Dispose() => _erp.Dispose();

    private static ErpVendorInvoice Sample(string number = "RE-2026-0001") => new(
        number, "V-1001", new DateOnly(2026, 11, 2), "EUR", 100.00m, 19.00m, 119.00m, "PO-1001",
        [new ErpVendorInvoiceLine(1, "Office chair", 2m, 50.00m, 100.00m, 19m, 1)]);

    private static string ExamplePath => Path.Combine(SqlServerFixture.RepositoryRoot(), "contracts", "d365", "vendor-invoice.example.json");

    [Fact]
    public async Task What_our_connector_sends_is_accepted_and_booked()
    {
        using var http = _erp.Client();
        var connector = new HttpErpConnector(http);
        var key = Guid.NewGuid();

        var result = await connector.PostAsync(key, Sample(), CancellationToken.None);

        Assert.Equal(ErpOutcome.Posted, result.Outcome);
        Assert.Matches("^VI-[0-9]{6}$", result.DocumentNumber);
        var booked = Assert.Single(await _erp.PostingsAsync());
        Assert.Equal(key.ToString(), booked.GetProperty("idempotencyKey").GetString());
        Assert.Equal("RE-2026-0001", booked.GetProperty("invoice").GetProperty("invoiceNumber").GetString());
        Assert.Equal(119.00m, booked.GetProperty("invoice").GetProperty("totalAmount").GetDecimal());
    }

    [Fact]
    public async Task The_committed_example_is_what_both_sides_understand()
    {
        var example = await File.ReadAllTextAsync(ExamplePath);

        // Our type reads it with no field left over or missing ...
        var ours = JsonSerializer.Deserialize<ErpVendorInvoice>(example, HttpErpConnector.Json);
        Assert.NotNull(ours);
        Assert.Equal(JsonSerializer.Serialize(JsonDocument.Parse(example).RootElement), JsonSerializer.Serialize(ours, HttpErpConnector.Json));

        // ... and the ERP accepts it as it is.
        using var http = _erp.Client();
        using var request = new HttpRequestMessage(HttpMethod.Post, "data/VendorInvoices") { Content = new StringContent(example, Encoding.UTF8, "application/json") };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var response = await http.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Our_side_notices_when_the_contract_gains_a_field_we_do_not_send()
    {
        // If the ERP's example ever asks for more than we send, reading it must fail here, not in production.
        var example = await File.ReadAllTextAsync(ExamplePath);
        var grown = "{\"paymentTerms\":\"30 days\"," + example.TrimStart()[1..];

        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize<ErpVendorInvoice>(grown, HttpErpConnector.Json));
    }

    private async Task<HttpStatusCode> PostRawAsync(string json)
    {
        using var http = _erp.Client();
        using var request = new HttpRequestMessage(HttpMethod.Post, "data/VendorInvoices") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var response = await http.SendAsync(request);
        return response.StatusCode;
    }

    [Fact]
    public async Task The_erp_refuses_a_field_it_does_not_know()
    {
        var json = JsonSerializer.Serialize(Sample(), HttpErpConnector.Json);

        var status = await PostRawAsync("{\"unexpected\":1," + json[1..]);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Empty(await _erp.PostingsAsync());
    }

    [Fact]
    public async Task The_erp_refuses_a_payload_with_a_field_missing()
    {
        var json = JsonSerializer.Serialize(Sample(), HttpErpConnector.Json)
            .Replace("\"invoiceNumber\":\"RE-2026-0001\",", string.Empty, StringComparison.Ordinal);

        var status = await PostRawAsync(json);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Empty(await _erp.PostingsAsync());
    }

    [Fact]
    public async Task The_erp_refuses_a_posting_without_an_idempotency_key()
    {
        using var http = _erp.Client();
        using var response = await http.PostAsJsonAsync("data/VendorInvoices", Sample(), HttpErpConnector.Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task One_idempotency_key_books_once_and_answers_the_same_document_again()
    {
        using var http = _erp.Client();
        var connector = new HttpErpConnector(http);
        var key = Guid.NewGuid();

        var first = await connector.PostAsync(key, Sample(), CancellationToken.None);
        var again = await connector.PostAsync(key, Sample(), CancellationToken.None);

        Assert.Equal((ErpOutcome.Posted, first.DocumentNumber), (again.Outcome, again.DocumentNumber));
        Assert.Single(await _erp.PostingsAsync());
    }

    // ---- How the connector classifies answers -------------------------------------------------------------------------

    [Theory]
    [InlineData(503)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(429)]
    [InlineData(408)]
    public async Task Temporary_failures_are_worth_another_try(int status)
    {
        await _erp.FailNextAsync(status);
        using var http = _erp.Client();

        var result = await new HttpErpConnector(http).PostAsync(Guid.NewGuid(), Sample(), CancellationToken.None);

        Assert.Equal(ErpOutcome.Transient, result.Outcome);
        Assert.Contains(status.ToString(System.Globalization.CultureInfo.InvariantCulture), result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_rejection_by_the_erp_is_final_and_says_why()
    {
        using var http = _erp.Client();

        // The mock's company books in EUR only: a business rejection, not a technical failure.
        var result = await new HttpErpConnector(http).PostAsync(Guid.NewGuid(), Sample() with { Currency = "USD" }, CancellationToken.None);

        Assert.Equal(ErpOutcome.Rejected, result.Outcome);
        Assert.Contains("422", result.Reason, StringComparison.Ordinal);
        Assert.Contains("currency", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.Reason!.Length <= 200); // fits the status history; a long ERP answer is cut, not stored whole
    }

    [Fact]
    public async Task No_answer_in_time_is_temporary()
    {
        await _erp.FailNextAsync(status: 201, delayMs: 3000); // books, but too late for us
        using var http = _erp.Client(timeout: TimeSpan.FromMilliseconds(500));

        var result = await new HttpErpConnector(http).PostAsync(Guid.NewGuid(), Sample(), CancellationToken.None);

        Assert.Equal(ErpOutcome.Transient, result.Outcome);
    }

    [Fact]
    public async Task An_unreachable_erp_is_temporary()
    {
        using var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:9/"), Timeout = TimeSpan.FromSeconds(5) };

        var result = await new HttpErpConnector(http).PostAsync(Guid.NewGuid(), Sample(), CancellationToken.None);

        Assert.Equal(ErpOutcome.Transient, result.Outcome);
    }

    [Fact]
    public async Task A_lost_reply_is_temporary_and_the_retry_books_nothing_twice()
    {
        await _erp.FailNextAsync(status: 500, afterBooking: true); // the ERP booked it, but we never heard back
        using var http = _erp.Client();
        var connector = new HttpErpConnector(http);
        var key = Guid.NewGuid();

        var lost = await connector.PostAsync(key, Sample(), CancellationToken.None);
        var retried = await connector.PostAsync(key, Sample(), CancellationToken.None);

        Assert.Equal((ErpOutcome.Transient, ErpOutcome.Posted), (lost.Outcome, retried.Outcome));
        Assert.Single(await _erp.PostingsAsync());
    }
}
