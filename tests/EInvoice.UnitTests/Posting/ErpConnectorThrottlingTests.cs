using System.Net;
using System.Text;
using EInvoice.Application.Posting;
using EInvoice.Infrastructure.Posting;

namespace EInvoice.UnitTests.Posting;

/// <summary>
/// How the connector reads a busy ERP. 429 is throttling (wait, but it uses up no try); 503 is a temporary failure. On
/// both, Retry-After says how long to wait, as seconds or as an HTTP date; a date in the past or a header that cannot be
/// read gives no wait, so the retry policy decides. A success whose answer cannot be read is temporary, never a crash.
/// </summary>
public sealed class ErpConnectorThrottlingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 21, 9, 0, 0, TimeSpan.Zero);

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Answer(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond());
    }

    private static Task<ErpResult> PostAsync(Func<HttpResponseMessage> respond)
    {
        var http = new HttpClient(new Answer(respond)) { BaseAddress = new Uri("http://erp.test/") };
        var invoice = new ErpVendorInvoice("RE-1", "V-1001", new DateOnly(2026, 11, 2), "EUR", 10m, 1.9m, 11.9m, null,
            [new ErpVendorInvoiceLine(1, "HDMI cable", 1m, 10m, 10m, 19m, null)]);
        return new HttpErpConnector(http, new FixedTime()).PostAsync(Guid.NewGuid(), invoice, CancellationToken.None);
    }

    private static HttpResponseMessage Status(int status, string? retryAfter = null)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        if (retryAfter is not null)
        {
            response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
        }

        return response;
    }

    [Fact]
    public async Task A_429_is_throttling_with_the_seconds_the_erp_asks_for()
    {
        var result = await PostAsync(() => Status(429, "20"));

        Assert.Equal((ErpOutcome.Throttled, TimeSpan.FromSeconds(20)), (result.Outcome, result.RetryAfter));
    }

    [Fact]
    public async Task A_429_without_retry_after_is_throttling_with_no_wait_of_its_own()
    {
        var result = await PostAsync(() => Status(429));

        Assert.Equal((ErpOutcome.Throttled, (TimeSpan?)null), (result.Outcome, result.RetryAfter));
    }

    [Fact]
    public async Task A_503_with_an_http_date_waits_until_then()
    {
        var result = await PostAsync(() => Status(503, Now.AddSeconds(90).ToString("r")));

        Assert.Equal((ErpOutcome.Transient, TimeSpan.FromSeconds(90)), (result.Outcome, result.RetryAfter));
    }

    [Theory]
    [InlineData("Fri, 20 Nov 2026 09:00:00 GMT")] // a day ago
    [InlineData("soon")]
    [InlineData("-5")]
    [InlineData("1.5")]
    public async Task A_date_in_the_past_or_an_unreadable_value_gives_no_wait(string retryAfter)
    {
        var result = await PostAsync(() => Status(429, retryAfter));

        Assert.Equal((ErpOutcome.Throttled, (TimeSpan?)null), (result.Outcome, result.RetryAfter));
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(408)]
    public async Task Retry_after_is_read_on_429_and_503_only(int status)
    {
        var result = await PostAsync(() => Status(status, "20"));

        Assert.Equal((ErpOutcome.Transient, (TimeSpan?)null), (result.Outcome, result.RetryAfter));
    }

    [Fact]
    public async Task The_connector_passes_a_long_wait_on_unchanged()
    {
        // Capping is the posting run's decision (one place), not the connector's.
        var result = await PostAsync(() => Status(429, "99999"));

        Assert.Equal(TimeSpan.FromSeconds(99999), result.RetryAfter);
    }

    [Theory]
    [InlineData("{\"documentNumber\":")]
    [InlineData("not json")]
    public async Task A_success_with_an_answer_that_cannot_be_read_is_temporary(string body)
    {
        // The ERP may have booked it; the next try with the same idempotency key gets the document number.
        var result = await PostAsync(() => new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

        Assert.Equal(ErpOutcome.Transient, result.Outcome);
        Assert.DoesNotContain("documentNumber", result.Reason, StringComparison.Ordinal); // never repeats the answer
    }
}
