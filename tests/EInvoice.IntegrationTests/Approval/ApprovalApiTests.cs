using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using EInvoice.Application.Approval;
using EInvoice.Domain.Invoices;
using EInvoice.Infrastructure.Identity;
using EInvoice.IntegrationTests.Api;
using EInvoice.IntegrationTests.Infrastructure;
using EInvoice.IntegrationTests.Matching;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace EInvoice.IntegrationTests.Approval;

/// <summary>
/// POST /api/invoices/{id}/approve and /reject against the database: the version from the detail goes back with the
/// decision; every refusal is problem details with an "outcome" and a safe explanation.
/// </summary>
[Trait("Category", "Database")]
[Collection(SqlServerCollection.Name)]
public sealed class ApprovalApiTests(SqlServerFixture fixture) : IAsyncLifetime
{
    private const string ProblemJson = "application/problem+json";
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);

    private ApiFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new ApiFactory(ApiFactory.Settings(fixture.AppConnectionString));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private HttpClient ApproverClient(string name = "approver@test", decimal? limit = null)
    {
        var client = _factory.CreateApiClient(name, "Approver");
        if (limit is { } l)
        {
            client.DefaultRequestHeaders.Add(TestUserHandler.ApprovalLimitHeader, l.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return client;
    }

    /// <summary>A committed invoice in the given status (Matched or Exception) with a unique gross amount near <paramref name="gross"/>.</summary>
    private async Task<Guid> InvoiceAsync(InvoiceStatus status = InvoiceStatus.Matched, decimal gross = 119m)
    {
        await using var db = fixture.CreateContext();
        var unitPrice = Math.Round(gross / 1.19m, 2) + (Random.Shared.Next(1, 10_000) / 10_000m);
        var scenario = await new MatchScenario(db).OrderLine(1, 1, unitPrice).Received(1, 1).SaveOrderAsync();
        var invoice = await scenario.AddInvoiceAsync((1, 1, unitPrice, 1));
        if (status == InvoiceStatus.Matched)
        {
            invoice.TransitionTo(InvoiceStatus.Validated, "system", Now);
        }

        invoice.TransitionTo(status, "system", Now);
        await db.SaveChangesAsync();
        return invoice.Id;
    }

    private static async Task<string> VersionAsync(HttpClient client, Guid id)
    {
        using var detail = await client.GetAsync(new Uri($"/api/invoices/{id}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, detail.StatusCode);
        return (await detail.JsonAsync()).GetProperty("version").GetString()!;
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, Guid id, string decision, object body) =>
        client.PostAsJsonAsync(new Uri($"/api/invoices/{id}/{decision}", UriKind.Relative), body);

    private static async Task AssertProblem(HttpResponseMessage response, HttpStatusCode status, string outcome)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal(ProblemJson, response.Content.Headers.ContentType?.MediaType);
        var problem = await response.JsonAsync();
        Assert.Equal(outcome, problem.GetProperty("outcome").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));
        var text = problem.ToString();
        Assert.DoesNotContain("System.", text, StringComparison.Ordinal); // no .NET type names
        Assert.DoesNotContain(" at ", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Approving_with_the_version_from_the_detail_approves_and_changes_the_version()
    {
        var id = await InvoiceAsync();
        using var approver = ApproverClient();
        var version = await VersionAsync(approver, id);

        using var response = await PostAsync(approver, id, "approve", new { version });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.JsonAsync();
        Assert.Equal((id, "Approved"), (body.GetProperty("invoiceId").GetGuid(), body.GetProperty("status").GetString()));
        using var detail = await approver.GetAsync(new Uri($"/api/invoices/{id}", UriKind.Relative));
        var json = await detail.JsonAsync();
        Assert.Equal("Approved", json.GetProperty("status").GetString());
        Assert.NotEqual(version, json.GetProperty("version").GetString());
        var last = json.GetProperty("history").EnumerateArray().Last();
        Assert.Equal(("Approved", "approver@test"), (last.GetProperty("to").GetString(), last.GetProperty("actor").GetString()));
    }

    [Fact]
    public async Task Rejecting_stores_the_reason()
    {
        var id = await InvoiceAsync(InvoiceStatus.Exception);
        using var approver = ApproverClient();

        using var response = await PostAsync(approver, id, "reject", new { version = await VersionAsync(approver, id), reason = "Goods were never ordered." });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var detail = await approver.GetAsync(new Uri($"/api/invoices/{id}", UriKind.Relative));
        var last = (await detail.JsonAsync()).GetProperty("history").EnumerateArray().Last();
        Assert.Equal(("Rejected", "Goods were never ordered."), (last.GetProperty("to").GetString(), last.GetProperty("reason").GetString()));
    }

    [Fact]
    public async Task Rejecting_without_a_reason_is_a_400_with_the_outcome()
    {
        var id = await InvoiceAsync();
        using var approver = ApproverClient();

        using var response = await PostAsync(approver, id, "reject", new { version = await VersionAsync(approver, id) });

        await AssertProblem(response, HttpStatusCode.BadRequest, nameof(DecisionOutcome.ReasonRequired));
    }

    [Fact]
    public async Task A_decision_without_a_version_is_a_400()
    {
        var id = await InvoiceAsync();
        using var approver = ApproverClient();

        using var response = await PostAsync(approver, id, "approve", new { reason = "fine" });

        await AssertProblem(response, HttpStatusCode.BadRequest, nameof(DecisionOutcome.VersionRequired));
    }

    [Fact]
    public async Task An_outdated_version_is_a_409_conflict()
    {
        var id = await InvoiceAsync();
        using var approver = ApproverClient();
        var version = await VersionAsync(approver, id);
        (await PostAsync(approver, id, "reject", new { version, reason = "First decision." })).Dispose();

        using var response = await PostAsync(approver, id, "approve", new { version });

        await AssertProblem(response, HttpStatusCode.Conflict, nameof(DecisionOutcome.Conflict));
    }

    [Fact]
    public async Task Approving_ones_own_upload_is_a_403()
    {
        var id = await InvoiceAsync(); // submitted by clerk@test
        using var sameAccount = ApproverClient(name: "CLERK@test");

        using var response = await PostAsync(sameAccount, id, "approve", new { version = await VersionAsync(sameAccount, id) });

        await AssertProblem(response, HttpStatusCode.Forbidden, nameof(DecisionOutcome.OwnInvoice));
    }

    [Fact]
    public async Task Over_the_approvers_limit_is_a_403_that_names_the_limit()
    {
        var id = await InvoiceAsync(gross: 600m);
        using var approver = ApproverClient(limit: 500m);

        using var response = await PostAsync(approver, id, "approve", new { version = await VersionAsync(approver, id) });

        await AssertProblem(response, HttpStatusCode.Forbidden, nameof(DecisionOutcome.OverLimit));
        Assert.Contains("500.00 EUR", (await response.JsonAsync()).GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_limit_on_the_account_the_configured_default_applies()
    {
        var id = await InvoiceAsync(gross: 12_000m); // the test default is 10,000.00 EUR
        using var approver = ApproverClient();

        using var response = await PostAsync(approver, id, "approve", new { version = await VersionAsync(approver, id) });

        await AssertProblem(response, HttpStatusCode.Forbidden, nameof(DecisionOutcome.OverLimit));
    }

    [Fact]
    public async Task A_limit_stored_on_a_real_account_is_used_after_signing_in()
    {
        var id = await InvoiceAsync(gross: 300m);
        var (userName, password) = await TestUsers.CreateAsync(_factory, "Approver");
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = (await users.FindByNameAsync(userName))!;
            Assert.True((await users.AddClaimAsync(user, new Claim(ApprovalLimits.ClaimType, "250.00"))).Succeeded);
        }

        using var browser = _factory.CreateApiClient(user: null);
        (await SignInForm.SignInAsync(browser, userName, password)).Dispose();
        using var response = await PostAsync(browser, id, "approve", new { version = await VersionAsync(browser, id) });

        await AssertProblem(response, HttpStatusCode.Forbidden, nameof(DecisionOutcome.OverLimit));
    }

    [Fact]
    public async Task An_unknown_invoice_is_a_404()
    {
        using var approver = ApproverClient();

        using var response = await PostAsync(approver, Guid.NewGuid(), "approve", new { version = "AAAAAAAAB9E=" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task An_already_decided_invoice_is_a_409_not_allowed()
    {
        var id = await InvoiceAsync();
        using var approver = ApproverClient();
        (await PostAsync(approver, id, "approve", new { version = await VersionAsync(approver, id) })).Dispose();

        using var response = await PostAsync(approver, id, "reject", new { version = await VersionAsync(approver, id), reason = "Changed my mind." });

        await AssertProblem(response, HttpStatusCode.Conflict, nameof(DecisionOutcome.NotAllowed));
    }
}
