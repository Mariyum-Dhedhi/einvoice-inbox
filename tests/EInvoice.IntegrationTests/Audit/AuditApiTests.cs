using System.Net;
using EInvoice.IntegrationTests.Api;
using EInvoice.IntegrationTests.Infrastructure;

namespace EInvoice.IntegrationTests.Audit;

/// <summary>GET /api/audit/verify: the auditor's check of the whole chain, with what to note down outside the system.</summary>
[Trait("Category", "Database")]
[Collection(SqlServerCollection.Name)]
public sealed class AuditApiTests(SqlServerFixture fixture) : IAsyncLifetime
{
    private ApiFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new ApiFactory(ApiFactory.Settings(fixture.AppConnectionString));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Verify_reports_an_intact_chain_with_its_length_and_latest_hash()
    {
        using var clerk = _factory.CreateApiClient(); // any signed-in action leaves events; uploading one is enough
        using (await clerk.PostAsync(new Uri("/api/invoices", UriKind.Relative), ApiHttp.Upload(System.Text.Encoding.UTF8.GetBytes($"<x><!-- {Guid.NewGuid()} --><broken></x>"))))
        {
        }

        using var auditor = _factory.CreateApiClient("auditor@test", "Auditor");
        using var response = await auditor.GetAsync(new Uri("/api/audit/verify", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.JsonAsync();
        Assert.True(json.GetProperty("isIntact").GetBoolean());
        Assert.True(json.GetProperty("count").GetInt64() > 0);
        Assert.Equal(json.GetProperty("count").GetInt64(), json.GetProperty("lastSequence").GetInt64());
        Assert.Matches("^[0-9a-f]{64}$", json.GetProperty("lastHash").GetString());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, json.GetProperty("brokenAt").ValueKind);
    }
}
