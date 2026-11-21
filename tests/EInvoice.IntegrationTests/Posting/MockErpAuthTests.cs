using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using EInvoice.Application.Posting;
using EInvoice.Infrastructure.Posting;
using EInvoice.MockErp;

namespace EInvoice.IntegrationTests.Posting;

/// <summary>
/// The mock ERP as its own token issuer, like Entra ID in front of D365. POST oauth/token takes the client credentials
/// grant as a form and answers access_token, token_type and expires_in (Auth:TokenLifetimeSeconds). Every data call
/// needs "Authorization: Bearer" with a token it issued that has not expired; else 401. Without a configured secret the
/// mock does not start. The _mock control endpoints stay open: they exist for tests only.
/// </summary>
public sealed class MockErpAuthTests : IDisposable
{
    private readonly MockErpFactory _erp = new();

    public void Dispose() => _erp.Dispose();

    private async Task<HttpResponseMessage> TokenAsync(string grantType = "client_credentials", string? clientId = null, string? secret = null)
    {
        using var client = _erp.AnonymousClient();
        return await client.PostAsync("oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = grantType,
            ["client_id"] = clientId ?? MockErpFactory.ClientId,
            ["client_secret"] = secret ?? _erp.ClientSecret,
            ["scope"] = "api://mock-erp/.default",
        }));
    }

    private async Task<HttpStatusCode> PostWithAsync(string? token)
    {
        using var client = _erp.AnonymousClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "data/VendorInvoices")
        {
            Content = JsonContent.Create(ErpContractTests.Sample(), options: HttpErpConnector.Json),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await client.SendAsync(request);
        return response.StatusCode;
    }

    [Fact]
    public async Task The_right_client_gets_a_bearer_token_for_the_configured_lifetime()
    {
        using var erp = new MockErpFactory(new Dictionary<string, string?> { ["Auth:TokenLifetimeSeconds"] = "120" });
        using var client = erp.AnonymousClient();

        using var response = await client.PostAsync("oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = MockErpFactory.ClientId,
            ["client_secret"] = erp.ClientSecret,
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(("Bearer", 120), (token.GetProperty("token_type").GetString(), token.GetProperty("expires_in").GetInt32()));
        Assert.True(token.GetProperty("access_token").GetString()!.Length >= 32);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Theory]
    [InlineData("einvoice-tests", "wrong-secret")]
    [InlineData("someone-else", null)]
    public async Task A_wrong_client_gets_401_invalid_client(string clientId, string? secret)
    {
        using var response = await TokenAsync(clientId: clientId, secret: secret);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("invalid_client", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Another_grant_type_is_refused()
    {
        using var response = await TokenAsync(grantType: "password");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("unsupported_grant_type", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("made-up-token")]
    public async Task Data_without_a_token_it_issued_is_401(string? token)
    {
        Assert.Equal(HttpStatusCode.Unauthorized, await PostWithAsync(token));
        Assert.Empty(await _erp.PostingsAsync());
    }

    [Fact]
    public async Task Data_with_its_token_is_booked()
    {
        var token = (await (await TokenAsync()).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString();

        Assert.Equal(HttpStatusCode.Created, await PostWithAsync(token));
    }

    [Fact]
    public async Task An_expired_token_is_401()
    {
        var token = (await (await TokenAsync()).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString();

        await _erp.AuthAsync(expireAll: true);

        Assert.Equal(HttpStatusCode.Unauthorized, await PostWithAsync(token));
    }

    [Fact]
    public async Task Rejecting_every_token_and_a_token_endpoint_that_is_down_can_be_planned()
    {
        await _erp.AuthAsync(tokenEndpointDown: true);
        using (var down = await TokenAsync())
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, down.StatusCode);
        }

        await _erp.AuthAsync(rejectAll: true);
        var token = (await (await TokenAsync()).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString();
        Assert.Equal(HttpStatusCode.Unauthorized, await PostWithAsync(token));
    }

    [Fact]
    public async Task The_mock_counts_token_requests_and_lists_the_tokens_it_issued()
    {
        using var http = _erp.Client();
        var connector = new HttpErpConnector(http);

        await connector.PostAsync(Guid.NewGuid(), ErpContractTests.Sample("RE-A"), CancellationToken.None);
        await connector.PostAsync(Guid.NewGuid(), ErpContractTests.Sample("RE-B"), CancellationToken.None);

        var stats = await _erp.StatsAsync();
        Assert.Equal((1, 2), (stats.GetProperty("tokenRequests").GetInt32(), stats.GetProperty("requests").GetInt32()));
        Assert.Single(await _erp.IssuedTokensAsync());
    }

    [Fact]
    public async Task Our_connector_posts_through_the_token_handler()
    {
        using var http = _erp.Client();

        var result = await new HttpErpConnector(http).PostAsync(Guid.NewGuid(), ErpContractTests.Sample(), CancellationToken.None);

        Assert.Equal(ErpOutcome.Posted, result.Outcome);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Without_a_secret_the_mock_does_not_start(string secret)
    {
        var error = Record.Exception(() => new MockErpFactory(new Dictionary<string, string?> { ["Auth:ClientSecret"] = secret }).Dispose());

        Assert.NotNull(error);
        Assert.Contains("Auth:ClientSecret", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_mock_program_is_ours_only_by_the_wire()
    {
        // Guard: the mock references none of our projects (it would not compile if it did use ErpTokenSource).
        Assert.DoesNotContain(typeof(MockErpProgram).Assembly.GetReferencedAssemblies(), a => a.Name!.StartsWith("EInvoice.", StringComparison.Ordinal));
    }
}
