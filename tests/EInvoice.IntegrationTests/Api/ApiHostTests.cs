using System.Net;
using EInvoice.Application.Queries;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace EInvoice.IntegrationTests.Api;

/// <summary>
/// How the host behaves around the endpoints: errors never leak internals, OpenAPI is a development tool,
/// and wrong settings stop the application at startup instead of at the first request. No database needed.
/// </summary>
public sealed class ApiHostTests
{
    // Never opened by these tests.
    private const string UnusedConnection = "Server=127.0.0.1,1;Database=EInvoice;User Id=x;Password=y;Encrypt=True";

    private sealed class ExplodingQueries : IInvoiceQueries
    {
        public const string Secret = "Server=prod-sql;Password=hunter2";

        public Task<InvoicePage> ListAsync(InvoiceListQuery query, CancellationToken cancellationToken) => throw new InvalidOperationException(Secret);

        public Task<InvoiceDetail?> GetAsync(Guid id, CancellationToken cancellationToken) => throw new InvalidOperationException(Secret);

        public Task<StoredDocument?> GetDocumentAsync(Guid id, CancellationToken cancellationToken) => throw new InvalidOperationException(Secret);
    }

    [Fact]
    public async Task Unhandled_error_is_a_500_problem_without_internals()
    {
        await using var factory = new ApiFactory(ApiFactory.Settings(UnusedConnection), services: s =>
        {
            s.RemoveAll<IInvoiceQueries>();
            s.AddScoped<IInvoiceQueries, ExplodingQueries>();
        });
        using var client = factory.CreateApiClient();

        using var response = await client.GetAsync(new Uri("/api/invoices", UriKind.Relative));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("hunter2", body, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(InvalidOperationException), body, StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", body, StringComparison.Ordinal); // no stack trace
    }

    [Theory]
    [InlineData("Development", HttpStatusCode.OK)]
    [InlineData("Production", HttpStatusCode.NotFound)]
    public async Task OpenApi_document_exists_only_in_Development(string environment, HttpStatusCode expected)
    {
        await using var factory = new ApiFactory(ApiFactory.Settings(UnusedConnection), environment);
        using var client = factory.CreateApiClient();

        using var response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));

        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.OK)
        {
            Assert.Contains("/api/invoices", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("ConnectionStrings:EInvoice", null)]
    [InlineData("ConnectionStrings:EInvoice", " ")]
    [InlineData("Intake:MaxDocumentBytes", "0")]
    [InlineData("Intake:MaxDocumentBytes", "209715201")] // over 200 MB: not an invoice
    [InlineData("Kosit:BaseUrl", null)]
    [InlineData("Kosit:BaseUrl", "not a url")]
    [InlineData("Kosit:BaseUrl", "file:///etc/passwd")]
    [InlineData("Kosit:TimeoutSeconds", "0")]
    [InlineData("Matching:PriceTolerancePercent", "-1")]
    [InlineData("Matching:PriceTolerancePercent", "101")]
    public async Task Invalid_setting_stops_the_application_at_startup(string key, string? value)
    {
        var settings = ApiFactory.Settings(UnusedConnection);
        settings[key] = value;
        await using var factory = new ApiFactory(settings);

        var error = Record.Exception(() => factory.CreateApiClient());

        Assert.NotNull(error);
        var validation = Unwrap(error).OfType<OptionsValidationException>().FirstOrDefault();
        Assert.True(validation is not null, $"expected an options validation error, got {error.GetType().Name}");
        Assert.Contains(key.Split(':')[^1], string.Join(" ", validation.Failures), StringComparison.Ordinal);
    }

    /// <summary>AI settings are checked only when AI is switched on; then every one of them must be right.</summary>
    [Theory]
    [InlineData("Ai:Model", null)]
    [InlineData("Ai:Model", " ")]
    [InlineData("Ai:OllamaUrl", null)]
    [InlineData("Ai:OllamaUrl", "not a url")]
    [InlineData("Ai:OllamaUrl", "file:///etc/passwd")]
    [InlineData("Ai:TimeoutSeconds", "0")]
    [InlineData("Ai:TimeoutSeconds", "601")]
    public async Task Invalid_ai_setting_stops_the_application_when_ai_is_on(string key, string? value)
    {
        var settings = ApiFactory.Settings(UnusedConnection);
        settings["Ai:Enabled"] = "true";
        settings["Ai:OllamaUrl"] = "http://127.0.0.1:11434/";
        settings["Ai:Model"] = "qwen2.5:7b";
        settings[key] = value;
        await using var factory = new ApiFactory(settings);

        var error = Record.Exception(() => factory.CreateApiClient());

        Assert.NotNull(error);
        var validation = Unwrap(error).OfType<OptionsValidationException>().FirstOrDefault();
        Assert.True(validation is not null, $"expected an options validation error, got {error.GetType().Name}");
        Assert.Contains(key.Split(':')[^1], string.Join(" ", validation.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ai_settings_are_not_needed_while_ai_is_off()
    {
        var settings = ApiFactory.Settings(UnusedConnection);
        settings["Ai:Enabled"] = "false";
        settings["Ai:OllamaUrl"] = "not a url"; // ignored while off
        await using var factory = new ApiFactory(settings);

        using var client = factory.CreateApiClient();

        Assert.NotNull(client);
    }

    private static IEnumerable<Exception> Unwrap(Exception error)
    {
        for (var e = error; e is not null; e = e.InnerException)
        {
            yield return e;
            if (e is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions.SelectMany(Unwrap))
                {
                    yield return inner;
                }
            }
        }
    }
}
