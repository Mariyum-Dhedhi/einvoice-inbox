using System.Net;
using System.Net.Http.Headers;
using EInvoice.Application.Security;
using EInvoice.IntegrationTests.Api;
using EInvoice.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace EInvoice.IntegrationTests.Sync;

/// <summary>
/// How the sync proves who it is: "Authorization: ApiKey &lt;key&gt;" (package 16c). The app keeps only the key's SHA-256
/// (Sync:ApiKeySha256) and compares in constant time; the right key signs the caller in as "erp-sync" with the
/// Integration role, for this request only. Integration is not an account role: no user can hold it, so no person can
/// sign in with it. Without a configured hash, no key works. Anything else is 401 problem details, never a redirect.
/// </summary>
[Trait("Category", "Database")]
[Collection(SqlServerCollection.Name)]
public sealed class SyncApiKeyTests(SqlServerFixture fixture)
{
    private const string Path = "/api/sync/watermarks/vendors";

    private static HttpClient WithAuthorization(ApiFactory app, string? scheme, string? value)
    {
        var client = app.CreateApiClient(user: null);
        if (scheme is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(scheme, value);
        }

        return client;
    }

    [Fact]
    public async Task The_right_key_gets_in()
    {
        await using var app = new ApiFactory(ApiFactory.Settings(fixture.AppConnectionString));
        using var sync = app.CreateSyncClient();

        using var response = await sync.GetAsync(Path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(null, null)]                       // no credential
    [InlineData("ApiKey", "not-the-key")]
    [InlineData("ApiKey", null)]
    [InlineData("ApiKey", "")]
    [InlineData("Bearer", ApiFactory.SyncApiKey)]  // the key, under another scheme
    [InlineData("Basic", "ZXJwLXN5bmM6eA==")]
    public async Task Anything_else_is_401_problem_details(string? scheme, string? value)
    {
        await using var app = new ApiFactory(ApiFactory.Settings(fixture.AppConnectionString));
        using var client = WithAuthorization(app, scheme, value);

        using var response = await client.GetAsync(Path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain(ApiFactory.SyncApiKey, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_wrong_key_says_how_to_authenticate()
    {
        await using var app = new ApiFactory(ApiFactory.Settings(fixture.AppConnectionString));
        using var client = WithAuthorization(app, "ApiKey", "not-the-key");

        using var response = await client.GetAsync(Path);

        Assert.Equal("ApiKey", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
    }

    [Fact]
    public async Task Without_a_configured_hash_no_key_works()
    {
        var settings = ApiFactory.Settings(fixture.AppConnectionString);
        settings["Sync:ApiKeySha256"] = null;
        await using var app = new ApiFactory(settings);
        using var sync = app.CreateSyncClient();

        using var response = await sync.GetAsync(Path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("zz2d10a1c5d8b1d2c1b8f2f5e3f6c4c0c5b2b1e0d3a8b5c4d1e2f3a4b5c6d7e8f9")] // not hex
    [InlineData("2d10a1c5d8b1d2c1b8f2f5e3f6c4c0c5b2b1e0d3a8b5c4d1e2f3a4b5c6d7e8")]    // 62 characters
    public async Task A_hash_that_is_not_64_hex_digits_stops_the_application(string hash)
    {
        var settings = ApiFactory.Settings(fixture.AppConnectionString);
        settings["Sync:ApiKeySha256"] = hash;
        await using var app = new ApiFactory(settings);

        var error = Assert.IsType<OptionsValidationException>(Record.Exception(() => app.CreateApiClient()));

        Assert.Contains("Sync:ApiKeySha256", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_key_gives_the_integration_role_and_nothing_else()
    {
        // Integration may only sync: no invoices, no purchasing pages, no audit.
        await using var app = new ApiFactory(ApiFactory.Settings(fixture.AppConnectionString));
        using var sync = app.CreateSyncClient();

        foreach (var path in new[] { "/api/invoices", "/api/audit/verify" })
        {
            using var response = await sync.GetAsync(path);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
    }

    [Fact]
    public async Task Integration_is_not_a_role_an_account_can_hold()
    {
        Assert.DoesNotContain(Roles.Integration, Roles.All); // the roles people get

        await using var db = fixture.CreateContext();
        Assert.False(await db.Roles.AnyAsync(r => r.Name == Roles.Integration));
    }
}
