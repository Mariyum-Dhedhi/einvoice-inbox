using System.Net;
using System.Text;
using EInvoice.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace EInvoice.IntegrationTests.Api;

/// <summary>
/// In the public demo the API keeps the upload limit too (Demo:UploadsPerHourPerUser): over it, 429 problem details
/// with the outcome name, and nothing stored. Without demo mode there is no such limit.
/// </summary>
[Trait("Category", "Database")]
[Collection(SqlServerCollection.Name)]
public sealed class DemoUploadLimitTests(SqlServerFixture fixture)
{
    // Unreadable but accepted (stored as Failed): enough to count, and unique so it is never a duplicate.
    private static byte[] Broken() => Encoding.UTF8.GetBytes($"<Invoice>{Guid.NewGuid():N}");

    private async Task<int> InvoicesBy(string user)
    {
        await using var db = fixture.CreateContext();
        return await db.Invoices.CountAsync(i => i.SubmittedBy == user);
    }

    [Fact]
    public async Task Over_the_limit_the_api_answers_429_with_the_outcome_and_stores_nothing()
    {
        await using var factory = new ApiFactory(DemoSignInTests.With(fixture.AppConnectionString, ("Demo:Enabled", "true"), ("Demo:UploadsPerHourPerUser", "1")));
        var user = $"limit-{Guid.NewGuid():N}@test";
        using var clerk = factory.CreateApiClient(user, "Clerk");
        using var first = await clerk.PostAsync(new Uri("/api/invoices", UriKind.Relative), ApiHttp.Upload(Broken()));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var second = await clerk.PostAsync(new Uri("/api/invoices", UriKind.Relative), ApiHttp.Upload(Broken()));

        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal("application/problem+json", second.Content.Headers.ContentType?.MediaType);
        Assert.Equal("TooManyUploads", (await second.JsonAsync()).GetProperty("outcome").GetString());
        Assert.Equal(1, await InvoicesBy(user));
    }

    [Fact]
    public async Task Without_demo_mode_the_setting_changes_nothing()
    {
        await using var factory = new ApiFactory(DemoSignInTests.With(fixture.AppConnectionString, ("Demo:UploadsPerHourPerUser", "1")));
        var user = $"limit-{Guid.NewGuid():N}@test";
        using var clerk = factory.CreateApiClient(user, "Clerk");

        for (var i = 0; i < 3; i++)
        {
            using var response = await clerk.PostAsync(new Uri("/api/invoices", UriKind.Relative), ApiHttp.Upload(Broken()));
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        Assert.Equal(3, await InvoicesBy(user));
    }
}
