using System.Net;
using EInvoice.IntegrationTests.Infrastructure;

namespace EInvoice.IntegrationTests.Api;

/// <summary>
/// Package 9's "done when": every endpoint and page of the running application is in <see cref="AccessTable"/>,
/// declares its access explicitly, and is called by every role, by a signed-in user without a role and by nobody.
/// </summary>
public sealed class EndpointInventoryTests
{
    private const string UnusedConnection = "Server=127.0.0.1,1;Database=EInvoice;User Id=x;Password=y;Encrypt=True";

    [Fact]
    public async Task Every_endpoint_and_page_is_in_the_access_table_and_nothing_else_is()
    {
        await using var factory = new ApiFactory(ApiFactory.Settings(UnusedConnection));
        using var client = factory.CreateApiClient(); // starts the host

        var found = AccessTable.Discover(factory.Services).Select(e => e.Key).ToList();

        // A new endpoint fails here until someone decides who may call it.
        Assert.Equal(AccessTable.Expected.Keys.Order(StringComparer.Ordinal), found);
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("text/plain")]
    public async Task Access_is_checked_before_the_request_format(string contentType)
    {
        // A caller who may not upload is refused as such; they do not learn what the endpoint expects (415).
        await using var factory = new ApiFactory(ApiFactory.Settings(UnusedConnection));
        using var auditor = factory.CreateApiClient("auditor@test", "Auditor");
        using var anonymous = factory.CreateApiClient(user: null);

        using var forbidden = await auditor.PostAsync(new Uri("/api/invoices", UriKind.Relative), new StringContent("{}", null, contentType));
        using var unauthorized = await anonymous.PostAsync(new Uri("/api/invoices", UriKind.Relative), new StringContent("{}", null, contentType));

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
    }

    [Fact]
    public async Task Every_endpoint_declares_its_access_instead_of_relying_on_the_default()
    {
        await using var factory = new ApiFactory(ApiFactory.Settings(UnusedConnection));
        using var client = factory.CreateApiClient();

        var silent = AccessTable.Discover(factory.Services).Where(e => !AccessTable.DeclaresAccess(e.Endpoint)).Select(e => e.Key);

        Assert.Empty(silent);
    }
}

[Trait("Category", "Database")]
[Collection(SqlServerCollection.Name)]
public sealed class RoleMatrixTests(SqlServerFixture fixture) : IAsyncLifetime
{
    private ApiFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new ApiFactory(ApiFactory.Settings(fixture.AppConnectionString));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    public static TheoryData<string, string> Cases()
    {
        var cases = new TheoryData<string, string>();
        foreach (var key in AccessTable.Expected.Keys.Order(StringComparer.Ordinal))
        {
            foreach (var caller in AccessTable.Callers)
            {
                cases.Add(key, caller ?? "(anonymous)");
            }
        }

        return cases;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Endpoint_answers_each_caller_as_the_table_says(string key, string caller)
    {
        string? role = caller == "(anonymous)" ? null : caller;
        using var client = role switch
        {
            null => _factory.CreateApiClient(user: null),
            AccessTable.NoRole => _factory.CreateApiClient($"norole-{Guid.NewGuid():N}@example.com"),
            _ => _factory.CreateApiClient($"{role.ToLowerInvariant()}-{Guid.NewGuid():N}@example.com", role),
        };
        using var request = new HttpRequestMessage(new HttpMethod(AccessTable.Method(key)), AccessTable.Url(key));
        if (request.Method == HttpMethod.Post)
        {
            request.Content = new FormUrlEncodedContent([]); // no file, no token: refused for other reasons if allowed
        }

        using var response = await client.SendAsync(request);

        var status = response.StatusCode;
        var target = response.Headers.Location is null ? null : SignInForm.Target(response);
        var isApi = !AccessTable.IsPage(key) && AccessTable.Url(key).StartsWith("/api", StringComparison.Ordinal);
        var access = AccessTable.Expected[key];

        if (access.Allows(role))
        {
            Assert.False(status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, $"{caller} should be let in, got {(int)status}");
            Assert.False(target?.StartsWith("/account/", StringComparison.Ordinal) == true, $"{caller} should be let in, was sent to {target}");
        }
        else if (role is null)
        {
            if (isApi)
            {
                Assert.Equal(HttpStatusCode.Unauthorized, status);
                Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            }
            else
            {
                Assert.Equal(HttpStatusCode.Redirect, status);
                Assert.StartsWith("/account/login?", target, StringComparison.Ordinal);
            }
        }
        else if (isApi)
        {
            Assert.Equal(HttpStatusCode.Forbidden, status);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        }
        else
        {
            Assert.Equal(HttpStatusCode.Redirect, status);
            Assert.StartsWith("/account/access-denied?", target, StringComparison.Ordinal);
        }
    }
}
