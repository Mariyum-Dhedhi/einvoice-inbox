using System.Net;
using System.Text;
using EInvoice.Application.Posting;
using EInvoice.Infrastructure.Posting;

namespace EInvoice.UnitTests.Posting;

/// <summary>
/// OAuth 2.0 client credentials to the ERP, without a network.
/// - The token source fetches a token once and reuses it. It renews 60 seconds before expiry, not after a 401: a token
///   that expires on the way would otherwise fail calls that were valid when sent.
/// - One renewal at a time: twenty calls that start together cause one token request.
/// - The handler puts the token on every call. On a 401 it renews once and sends the call once more. A second 401 is
///   final ("ERP refused the credentials"), never a loop.
/// - A token endpoint that is down or answers nonsense is temporary. One that refuses our client is the same final error.
/// </summary>
public sealed class ErpTokenTests
{
    private const string Secret = "s3cr3t-for-tests-only";
    private static readonly Uri TokenUrl = new("http://erp.test/oauth/token");
    private static readonly ErpCredentials Credentials = new(TokenUrl, "einvoice", Secret, "api://erp/.default");

    private readonly MovableTime _time = new(new DateTimeOffset(2026, 11, 21, 9, 0, 0, TimeSpan.Zero));
    private readonly FakeTokenEndpoint _endpoint = new();

    private sealed class MovableTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>Issues token-1, token-2, ... for <see cref="ExpiresIn"/> seconds, or answers as told.</summary>
    private sealed class FakeTokenEndpoint : HttpMessageHandler
    {
        private int _issued;

        public int Requests => _issued + Refusals;

        public int Refusals { get; private set; }

        public int ExpiresIn { get; set; } = 3600;

        public TimeSpan Delay { get; set; }

        public Func<HttpResponseMessage>? Answer { get; set; }

        public List<string> Forms { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Delay, cancellationToken);
            lock (Forms)
            {
                Forms.Add($"{request.Method} {request.RequestUri} {request.Content!.Headers.ContentType!.MediaType} {request.Content.ReadAsStringAsync(cancellationToken).Result}");
            }

            if (Answer is not null)
            {
                Refusals++;
                return Answer();
            }

            var n = Interlocked.Increment(ref _issued);
            return Json(HttpStatusCode.OK, $$"""{"access_token":"token-{{n}}","token_type":"Bearer","expires_in":{{ExpiresIn}}}""");
        }
    }

    /// <summary>The ERP's data endpoint: answers each call with the next status, remembering the tokens it saw.</summary>
    private sealed class FakeErp(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        public List<string?> Tokens { get; } = [];

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Tokens.Add(request.Headers.Authorization is { Scheme: "Bearer" } auth ? auth.Parameter : null);
            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var status = statuses[Math.Min(Tokens.Count - 1, statuses.Length - 1)];
            return status == HttpStatusCode.Created ? Json(status, """{"documentNumber":"VI-000001"}""") : Json(status, "{}");
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private ErpTokenSource Tokens() => new(() => new HttpClient(_endpoint, disposeHandler: false), Credentials, _time);

    private static HttpErpConnector Connector(ErpTokenSource tokens, FakeErp erp) =>
        new(new HttpClient(new ErpAuthHandler(tokens) { InnerHandler = erp }) { BaseAddress = new Uri("http://erp.test/") });

    private static Task<ErpResult> PostAsync(HttpErpConnector connector) => connector.PostAsync(Guid.NewGuid(), new ErpVendorInvoice(
        "RE-1", "V-1001", new DateOnly(2026, 11, 2), "EUR", 10m, 1.9m, 11.9m, null, [new ErpVendorInvoiceLine(1, "HDMI cable", 1m, 10m, 10m, 19m, null)]), CancellationToken.None);

    // ---- The token source -------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_first_call_fetches_a_token_and_the_next_calls_reuse_it()
    {
        var tokens = Tokens();

        var first = await tokens.GetAsync(CancellationToken.None);
        var again = await tokens.GetAsync(CancellationToken.None);
        _time.Advance(TimeSpan.FromMinutes(58));
        var later = await tokens.GetAsync(CancellationToken.None);

        Assert.Equal(("token-1", "token-1", "token-1"), (first, again, later));
        Assert.Equal(1, _endpoint.Requests);
    }

    [Fact]
    public async Task The_request_is_the_client_credentials_grant_as_a_form()
    {
        await Tokens().GetAsync(CancellationToken.None);

        Assert.Equal(
            "POST http://erp.test/oauth/token application/x-www-form-urlencoded grant_type=client_credentials&client_id=einvoice&client_secret=s3cr3t-for-tests-only&scope=api%3A%2F%2Ferp%2F.default",
            Assert.Single(_endpoint.Forms));
    }

    [Fact]
    public async Task Without_a_scope_none_is_sent()
    {
        var tokens = new ErpTokenSource(() => new HttpClient(_endpoint, disposeHandler: false), Credentials with { Scope = null }, _time);

        await tokens.GetAsync(CancellationToken.None);

        Assert.DoesNotContain("scope", Assert.Single(_endpoint.Forms), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(3539, 1)] // 61 s left: reused
    [InlineData(3540, 2)] // 60 s left: renewed before the call
    [InlineData(3600, 2)] // expired
    public async Task A_token_within_60_seconds_of_expiry_is_renewed_before_the_call(int secondsLater, int expectedToken)
    {
        var tokens = Tokens();
        await tokens.GetAsync(CancellationToken.None); // token-1, for one hour

        _time.Advance(TimeSpan.FromSeconds(secondsLater));

        Assert.Equal($"token-{expectedToken}", await tokens.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Twenty_calls_at_once_without_a_token_cause_exactly_one_token_request()
    {
        _endpoint.Delay = TimeSpan.FromMilliseconds(100);
        var tokens = Tokens();

        var all = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => tokens.GetAsync(CancellationToken.None))));

        Assert.Equal(1, _endpoint.Requests);
        Assert.All(all, token => Assert.Equal("token-1", token));
    }

    [Fact]
    public async Task A_rejected_token_is_renewed_once_however_many_calls_report_it()
    {
        var tokens = Tokens();
        var old = await tokens.GetAsync(CancellationToken.None);
        _endpoint.Delay = TimeSpan.FromMilliseconds(50);

        var renewed = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => tokens.RenewAsync(old, CancellationToken.None))));

        Assert.Equal(2, _endpoint.Requests);
        Assert.All(renewed, token => Assert.Equal("token-2", token));
    }

    [Theory]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{}")]
    [InlineData(HttpStatusCode.InternalServerError, "{}")]
    [InlineData(HttpStatusCode.OK, "not json")]
    [InlineData(HttpStatusCode.OK, """{"token_type":"Bearer","expires_in":3600}""")]
    [InlineData(HttpStatusCode.OK, """{"access_token":"","token_type":"Bearer","expires_in":3600}""")]
    [InlineData(HttpStatusCode.OK, """{"access_token":"t","token_type":"MAC","expires_in":3600}""")]
    [InlineData(HttpStatusCode.OK, """{"access_token":"t","token_type":"Bearer","expires_in":0}""")]
    public async Task A_token_endpoint_that_is_down_or_answers_nonsense_is_temporary(HttpStatusCode status, string body)
    {
        _endpoint.Answer = () => Json(status, body);

        var error = await Assert.ThrowsAsync<ErpTokenUnavailableException>(() => Tokens().GetAsync(CancellationToken.None));

        Assert.Equal("Could not get a token from the ERP.", error.Message); // fixed: never repeats the answer
    }

    [Fact]
    public async Task An_unreachable_token_endpoint_is_temporary()
    {
        var tokens = new ErpTokenSource(() => throw new HttpRequestException("connection refused"), Credentials, _time);

        await Assert.ThrowsAsync<ErpTokenUnavailableException>(() => tokens.GetAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    public async Task A_token_endpoint_that_refuses_our_client_is_final(HttpStatusCode status)
    {
        _endpoint.Answer = () => Json(status, """{"error":"invalid_client"}""");

        await Assert.ThrowsAsync<ErpCredentialsRefusedException>(() => Tokens().GetAsync(CancellationToken.None));
    }

    [Fact]
    public void Credentials_never_show_their_secret()
    {
        Assert.DoesNotContain(Secret, Credentials.ToString(), StringComparison.Ordinal);
    }

    // ---- The handler and the connector ------------------------------------------------------------------------------

    [Fact]
    public async Task Every_call_carries_the_token()
    {
        var erp = new FakeErp(HttpStatusCode.Created);

        var result = await PostAsync(Connector(Tokens(), erp));

        Assert.Equal(ErpOutcome.Posted, result.Outcome);
        Assert.Equal(["token-1"], erp.Tokens);
    }

    [Fact]
    public async Task A_401_renews_the_token_once_and_sends_the_same_call_again()
    {
        var erp = new FakeErp(HttpStatusCode.Unauthorized, HttpStatusCode.Created);

        var result = await PostAsync(Connector(Tokens(), erp));

        Assert.Equal(ErpOutcome.Posted, result.Outcome);
        Assert.Equal(["token-1", "token-2"], erp.Tokens);
        Assert.Equal(erp.Bodies[0], erp.Bodies[1]);
        Assert.NotEmpty(erp.Bodies[0]);
    }

    [Fact]
    public async Task A_second_401_is_final_with_the_fixed_reason_after_exactly_two_calls()
    {
        var erp = new FakeErp(HttpStatusCode.Unauthorized);

        var result = await PostAsync(Connector(Tokens(), erp));

        Assert.Equal((ErpOutcome.Rejected, "ERP refused the credentials."), (result.Outcome, result.Reason));
        Assert.Equal(2, erp.Tokens.Count);
        Assert.Equal(2, _endpoint.Requests);
    }

    [Fact]
    public async Task A_token_endpoint_that_is_down_makes_the_posting_temporary()
    {
        _endpoint.Answer = () => Json(HttpStatusCode.ServiceUnavailable, "{}");
        var erp = new FakeErp(HttpStatusCode.Created);

        var result = await PostAsync(Connector(Tokens(), erp));

        Assert.Equal((ErpOutcome.Transient, "Could not get a token from the ERP."), (result.Outcome, result.Reason));
        Assert.Empty(erp.Tokens); // the invoice was never sent without a token
    }

    [Fact]
    public async Task A_token_endpoint_that_refuses_our_client_makes_the_posting_final()
    {
        _endpoint.Answer = () => Json(HttpStatusCode.Unauthorized, """{"error":"invalid_client"}""");
        var erp = new FakeErp(HttpStatusCode.Created);

        var result = await PostAsync(Connector(Tokens(), erp));

        Assert.Equal((ErpOutcome.Rejected, "ERP refused the credentials."), (result.Outcome, result.Reason));
        Assert.Empty(erp.Tokens);
    }
}
