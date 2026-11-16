using System.Net;
using EInvoice.Application.Extraction;
using EInvoice.Infrastructure.Extraction;
using EInvoice.IntegrationTests.Infrastructure;
using EInvoice.IntegrationTests.Matching;
using EInvoice.UnitTests.Extraction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EInvoice.IntegrationTests.Api;

/// <summary>PDF uploads over HTTP: accepted when AI is on, 415 when it is off, 503 when the model is down.</summary>
[Trait("Category", "Database")]
[Collection(SqlServerCollection.Name)]
public sealed class PdfApiTests(SqlServerFixture fixture)
{
    // Committed invoices: an amount no other test uses.
    private static int _orders;

    private sealed class DownModel : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("connection refused");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private ApiFactory Factory(IChatClient? model)
    {
        var settings = ApiFactory.Settings(fixture.AppConnectionString);
        return new ApiFactory(settings, services: model is null ? null : s =>
        {
            s.RemoveAll<IInvoiceExtractor>();
            s.AddSingleton<IInvoiceExtractor>(new LlmInvoiceExtractor(new PdfTextReader(PdfLimits.Default), model));
        });
    }

    private async Task<byte[]> PdfForNewOrderAsync()
    {
        var price = 2000.4125m + Interlocked.Increment(ref _orders);
        await using var db = fixture.CreateContext();
        var scenario = await new MatchScenario(db).OrderLine(1, 10, price).Received(1, 10).SaveOrderAsync();
        return new SyntheticPdf { PurchaseOrder = scenario.PurchaseOrderNumber, InvoiceNumber = $"RE-A-{Guid.NewGuid():N}"[..20] }
            .Line("HDMI cable 2 m", 10m, price, orderLine: "1").Build();
    }

    private static async Task<HttpResponseMessage> PostAsync(ApiFactory factory, byte[] pdf)
    {
        using var client = factory.CreateApiClient();
        return await client.PostAsync(new Uri("/api/invoices", UriKind.Relative), ApiHttp.Upload(pdf, "Rechnung.pdf"));
    }

    [Fact]
    public async Task Zugferd_pdf_is_accepted_with_ai_off_because_it_carries_its_own_xml()
    {
        await using var factory = Factory(model: null); // AI off: the default
        var price = 3000.4125m + Interlocked.Increment(ref _orders);
        string number;
        byte[] pdf;
        await using (var db = fixture.CreateContext())
        {
            var scenario = await new MatchScenario(db).OrderLine(1, 10, price).Received(1, 10).SaveOrderAsync();
            var builder = new EInvoice.IntegrationTests.TestData.CiiInvoiceBuilder
            {
                Profile = EInvoice.IntegrationTests.TestData.CiiInvoiceBuilder.Profiles.XRechnung,
                PurchaseOrderNumber = scenario.PurchaseOrderNumber,
            }.Line("HDMI cable 2 m", 10m, price, orderLine: "1");
            number = builder.InvoiceNumber;
            pdf = EInvoice.IntegrationTests.TestData.ZugferdPdf.Invoice(builder.Build(), "xrechnung.xml");
        }

        using var response = await PostAsync(factory, pdf);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.JsonAsync()).GetProperty("invoiceId").GetGuid();
        using var client = factory.CreateApiClient();
        var detail = await (await client.GetAsync(new Uri($"/api/invoices/{id}", UriKind.Relative))).JsonAsync();
        Assert.Equal(("Matched", "Zugferd", number), (detail.GetProperty("status").GetString(), detail.GetProperty("sourceFormat").GetString(), detail.GetProperty("invoiceNumber").GetString()));
        Assert.Equal("application/pdf", detail.GetProperty("document").GetProperty("contentType").GetString());
    }

    [Fact]
    public async Task Pdf_is_accepted_and_matched_when_ai_is_on()
    {
        await using var factory = Factory(new FakeInvoiceModel());

        using var response = await PostAsync(factory, await PdfForNewOrderAsync());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.JsonAsync()).GetProperty("invoiceId").GetGuid();
        using var client = factory.CreateApiClient();
        var detail = await (await client.GetAsync(new Uri($"/api/invoices/{id}", UriKind.Relative))).JsonAsync();
        Assert.Equal("Matched", detail.GetProperty("status").GetString());
        Assert.Equal("Pdf", detail.GetProperty("sourceFormat").GetString());
        Assert.Equal("application/pdf", detail.GetProperty("document").GetProperty("contentType").GetString());
    }

    [Fact]
    public async Task Pdf_is_415_when_ai_is_off()
    {
        await using var factory = Factory(model: null); // the settings leave AI off

        using var response = await PostAsync(factory, await PdfForNewOrderAsync());

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Contains("switched off", (await response.JsonAsync()).GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Model_down_is_503_with_retry_after_and_nothing_stored()
    {
        await using var factory = Factory(new DownModel());

        using var response = await PostAsync(factory, await PdfForNewOrderAsync());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.NotNull(response.Headers.RetryAfter);
        var problem = await response.JsonAsync();
        Assert.Equal("ExtractionUnavailable", problem.GetProperty("outcome").GetString());
        Assert.False(problem.TryGetProperty("invoiceId", out _));
        Assert.DoesNotContain("connection refused", problem.ToString(), StringComparison.Ordinal);
    }
}
