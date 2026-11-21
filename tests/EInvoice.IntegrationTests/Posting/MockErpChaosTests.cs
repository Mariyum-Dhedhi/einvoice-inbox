using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using EInvoice.Application.Posting;
using EInvoice.Infrastructure.Posting;

namespace EInvoice.IntegrationTests.Posting;

/// <summary>
/// The mock ERP's chaos mode: a share of requests gets trouble instead of a booking. It is off by default and can be set
/// from the mock's configuration (Chaos:*) or at run time (_mock/chaos). The kinds of trouble are: throttle (429 with
/// Retry-After), error (500), unavailable (503 with Retry-After), slow (the right answer, late), and malformed (booked,
/// but the answer is not JSON). Only malformed books anything, as a real ERP that lost its reply would.
/// </summary>
public sealed class MockErpChaosTests : IDisposable
{
    private readonly MockErpFactory _erp = new();

    public void Dispose() => _erp.Dispose();

    private static async Task<HttpResponseMessage> PostAsync(HttpClient http, string number, Guid? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "data/VendorInvoices")
        {
            Content = JsonContent.Create(ErpContractTests.Sample(number), options: HttpErpConnector.Json),
        };
        request.Headers.Add("Idempotency-Key", (key ?? Guid.NewGuid()).ToString());
        return await http.SendAsync(request);
    }

    [Fact]
    public async Task Off_by_default()
    {
        using var http = _erp.Client();
        for (var i = 0; i < 50; i++)
        {
            using var response = await PostAsync(http, $"RE-{i}");
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        var stats = await _erp.StatsAsync();
        Assert.Equal(0, stats.GetProperty("chaos").EnumerateObject().Sum(kind => kind.Value.GetInt32()));
    }

    [Fact]
    public async Task Throttle_answers_429_with_retry_after_and_books_nothing()
    {
        await _erp.ChaosAsync(1.0, ["throttle"], retryAfterSeconds: 7);
        using var http = _erp.Client();

        using var response = await PostAsync(http, "RE-1");

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(7), response.Headers.RetryAfter?.Delta);
        Assert.Empty(await _erp.PostingsAsync());
    }

    [Theory]
    [InlineData("error", HttpStatusCode.InternalServerError, false)]
    [InlineData("unavailable", HttpStatusCode.ServiceUnavailable, true)]
    public async Task Error_and_unavailable_book_nothing(string kind, HttpStatusCode status, bool retryAfter)
    {
        await _erp.ChaosAsync(1.0, [kind]);
        using var http = _erp.Client();

        using var response = await PostAsync(http, "RE-1");

        Assert.Equal(status, response.StatusCode);
        Assert.Equal(retryAfter, response.Headers.RetryAfter is not null);
        Assert.Empty(await _erp.PostingsAsync());
    }

    [Fact]
    public async Task Slow_answers_right_but_late()
    {
        await _erp.ChaosAsync(1.0, ["slow"], slowMs: 300);
        using var http = _erp.Client();

        var clock = Stopwatch.StartNew();
        using var response = await PostAsync(http, "RE-1");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.True(clock.ElapsedMilliseconds >= 300, $"answered after {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Malformed_books_and_then_answers_something_that_is_not_json()
    {
        await _erp.ChaosAsync(1.0, ["malformed"]);
        using var http = _erp.Client();
        var key = Guid.NewGuid();

        using var response = await PostAsync(http, "RE-1", key);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => System.Text.Json.JsonDocument.Parse(response.Content.ReadAsStringAsync().Result));
        Assert.Single(await _erp.PostingsAsync()); // the retry with the same key gets the same document
    }

    [Fact]
    public async Task Our_connector_reads_every_kind_of_chaos_as_a_try_later()
    {
        using var http = _erp.Client(TimeSpan.FromSeconds(5));
        var connector = new HttpErpConnector(http);
        var outcomes = new Dictionary<string, ErpOutcome>();
        foreach (var kind in new[] { "throttle", "error", "unavailable", "malformed" })
        {
            await _erp.ChaosAsync(1.0, [kind]);
            outcomes[kind] = (await connector.PostAsync(Guid.NewGuid(), ErpContractTests.Sample($"RE-{kind}"), CancellationToken.None)).Outcome;
        }

        Assert.Equal(ErpOutcome.Throttled, outcomes["throttle"]);
        Assert.All(new[] { "error", "unavailable", "malformed" }, kind => Assert.Equal(ErpOutcome.Transient, outcomes[kind]));
    }

    [Fact]
    public async Task About_the_configured_share_of_requests_gets_chaos()
    {
        await _erp.ChaosAsync(0.3, ["throttle", "error", "unavailable"], seed: 42);
        using var http = _erp.Client();

        var troubled = 0;
        for (var i = 0; i < 500; i++)
        {
            using var response = await PostAsync(http, $"RE-{i}");
            troubled += response.IsSuccessStatusCode ? 0 : 1;
        }

        Assert.InRange(troubled, 120, 180); // 30 % of 500 is 150
        var stats = await _erp.StatsAsync();
        Assert.Equal(troubled, stats.GetProperty("chaos").EnumerateObject().Sum(kind => kind.Value.GetInt32()));
    }

    [Fact]
    public async Task Chaos_can_be_switched_on_in_the_mocks_configuration()
    {
        using var erp = new MockErpFactory(new Dictionary<string, string?>
        {
            ["Chaos:Share"] = "1",
            ["Chaos:Kinds:0"] = "unavailable",
            ["Chaos:RetryAfterSeconds"] = "3",
        });
        using var http = erp.Client();

        using var response = await PostAsync(http, "RE-1");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(3), response.Headers.RetryAfter?.Delta);
    }

    [Theory]
    [InlineData(-0.1, null)]
    [InlineData(1.1, null)]
    [InlineData(0.5, "earthquake")]
    public async Task Chaos_settings_that_make_no_sense_are_refused(double share, string? kind)
    {
        using var client = _erp.Client();

        using var response = await client.PostAsJsonAsync("_mock/chaos", new { share, kinds = kind is null ? null : new[] { kind }, seed = 1, retryAfterSeconds = 2, slowMs = 0 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task The_mock_counts_how_many_requests_it_had_at_once()
    {
        await _erp.ChaosAsync(1.0, ["slow"], slowMs: 300);
        using var http = _erp.Client();

        var responses = await Task.WhenAll(Enumerable.Range(0, 3).Select(i => PostAsync(http, $"RE-{i}")));
        foreach (var response in responses)
        {
            response.Dispose();
        }

        Assert.Equal(3, (await _erp.StatsAsync()).GetProperty("maxInFlight").GetInt32());
    }
}
