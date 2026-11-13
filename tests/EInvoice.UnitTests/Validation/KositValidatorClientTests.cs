using System.Net;
using System.Text;
using EInvoice.Application.Validation;
using EInvoice.Infrastructure.Validation;

namespace EInvoice.UnitTests.Validation;

/// <summary>
/// The client for the KoSIT validator container. The HTTP status is the verdict (200 accept, 406 reject);
/// the report adds the failed rule codes. Anything else fails closed: Unavailable, never "accepted".
/// The report fixtures are real responses captured from validator 1.6.3 with the XRechnung 2026-08-31 configuration.
/// </summary>
public class KositValidatorClientTests
{
    private static readonly byte[] Invoice = Encoding.UTF8.GetBytes("<Invoice/>");

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Validation", "KositReports", name));

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public byte[]? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            return await respond(request, cancellationToken);
        }
    }

    private static (KositValidatorClient Client, FakeHandler Handler) Create(HttpStatusCode status, string body = "", TimeSpan? timeout = null)
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/xml") }));
        return Create(handler, timeout);
    }

    private static (KositValidatorClient Client, FakeHandler Handler) Create(FakeHandler handler, TimeSpan? timeout = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://kosit-validator:8080/"), Timeout = timeout ?? TimeSpan.FromSeconds(30) };
        return (new KositValidatorClient(http), handler);
    }

    [Fact]
    public async Task Status_200_is_accepted()
    {
        var (client, _) = Create(HttpStatusCode.OK, Fixture("accepted-01.01-ubl.xml"));

        var result = await client.CheckAsync(Invoice, CancellationToken.None);

        Assert.Equal(ConformanceOutcome.Accepted, result.Outcome);
        Assert.Empty(result.ErrorCodes);
    }

    [Fact]
    public async Task Status_406_is_rejected_with_the_error_level_rule_codes_only()
    {
        var (client, _) = Create(HttpStatusCode.NotAcceptable, Fixture("rejected-01.01-ubl-gross-plus-10.xml"));

        var result = await client.CheckAsync(Invoice, CancellationToken.None);

        Assert.Equal(ConformanceOutcome.Rejected, result.Outcome);
        Assert.Equal(["BR-CO-15", "BR-CO-16"], result.ErrorCodes); // BR-DE-TMP-32 is level "information"
    }

    [Fact]
    public async Task Only_error_level_messages_count_not_warnings_or_information()
    {
        // The captured real report has no warning-level message, so this level is covered explicitly.
        const string Report = """
            <rep:report xmlns:rep="http://www.xoev.de/de/validator/varl/1">
              <rep:message level="warning" code="BR-DE-W1">x</rep:message>
              <rep:message level="error" code="BR-CO-15">x</rep:message>
              <rep:message level="information" code="BR-DE-TMP-32">x</rep:message>
            </rep:report>
            """;
        var (client, _) = Create(HttpStatusCode.NotAcceptable, Report);

        var result = await client.CheckAsync(Invoice, CancellationToken.None);

        Assert.Equal(["BR-CO-15"], result.ErrorCodes);
    }

    [Fact]
    public async Task Status_406_with_an_unreadable_report_is_still_rejected()
    {
        var (client, _) = Create(HttpStatusCode.NotAcceptable, "not xml");

        var result = await client.CheckAsync(Invoice, CancellationToken.None);

        Assert.Equal(ConformanceOutcome.Rejected, result.Outcome);
        Assert.Empty(result.ErrorCodes);
    }

    [Fact]
    public async Task Rule_codes_that_do_not_look_like_rule_codes_are_dropped()
    {
        const string Report = """
            <rep:report xmlns:rep="http://www.xoev.de/de/validator/varl/1">
              <rep:message level="error" code="BR-CO-15">x</rep:message>
              <rep:message level="error" code="&lt;script&gt;alert(1)&lt;/script&gt;">x</rep:message>
            </rep:report>
            """;
        var (client, _) = Create(HttpStatusCode.NotAcceptable, Report);

        var result = await client.CheckAsync(Invoice, CancellationToken.None);

        Assert.Equal(["BR-CO-15"], result.ErrorCodes);
    }

    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity)] // KoSIT: error in the scenario configuration
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Any_other_status_is_unavailable_never_accepted(HttpStatusCode status)
    {
        var (client, _) = Create(status, "<x/>");

        Assert.Equal(ConformanceOutcome.Unavailable, (await client.CheckAsync(Invoice, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Connection_failure_is_unavailable()
    {
        var (client, _) = Create(new FakeHandler((_, _) => throw new HttpRequestException("connection refused")));

        Assert.Equal(ConformanceOutcome.Unavailable, (await client.CheckAsync(Invoice, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Timeout_is_unavailable()
    {
        var handler = new FakeHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var (client, _) = Create(handler, timeout: TimeSpan.FromMilliseconds(100));

        Assert.Equal(ConformanceOutcome.Unavailable, (await client.CheckAsync(Invoice, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task Cancellation_by_the_caller_is_not_swallowed()
    {
        var handler = new FakeHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var (client, _) = Create(handler);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CheckAsync(Invoice, cts.Token));
    }

    [Fact]
    public async Task Sends_the_exact_bytes_as_application_xml_by_POST()
    {
        var (client, handler) = Create(HttpStatusCode.OK, Fixture("accepted-01.01-ubl.xml"));

        await client.CheckAsync(Invoice, CancellationToken.None);

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("application/xml", handler.Request.Content!.Headers.ContentType!.MediaType);
        Assert.Equal(Invoice, handler.Body);
    }
}
