using System.Net.Http.Headers;
using System.Text.Json;
using EInvoice.Application.Validation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace EInvoice.IntegrationTests.Api;

/// <summary>
/// The real web application in memory. Settings go in through UseSetting, exactly like environment variables would,
/// so the same startup validation runs. The KoSIT validator is replaced by a stand-in that accepts everything.
/// Runs as Production unless a test says otherwise: error responses must be safe there.
/// </summary>
internal sealed class ApiFactory(
    IReadOnlyDictionary<string, string?> settings,
    string environment = "Production",
    Action<IServiceCollection>? services = null) : WebApplicationFactory<Program>
{
    public const long MaxDocumentBytes = 64 * 1024;

    public static Dictionary<string, string?> Settings(string connectionString) => new()
    {
        ["ConnectionStrings:EInvoice"] = connectionString,
        ["Intake:MaxDocumentBytes"] = MaxDocumentBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["Kosit:BaseUrl"] = "http://127.0.0.1:9/", // never called: replaced below
        ["Kosit:TimeoutSeconds"] = "5",
        ["Matching:PriceTolerancePercent"] = "2.00",
    };

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        foreach (var (key, value) in settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(s =>
        {
            s.RemoveAll<IConformanceValidator>();
            s.AddSingleton<IConformanceValidator, AcceptAll>();
            services?.Invoke(s);
        });
    }

    public HttpClient CreateApiClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });

    private sealed class AcceptAll : IConformanceValidator
    {
        public Task<ConformanceResult> CheckAsync(ReadOnlyMemory<byte> xml, CancellationToken cancellationToken) =>
            Task.FromResult(ConformanceResult.Accepted);
    }
}

internal static class ApiHttp
{
    public static MultipartFormDataContent Upload(byte[] content, string fileName = "invoice.xml", string field = "file")
    {
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return new MultipartFormDataContent { { file, field, fileName } };
    }

    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text).RootElement.Clone();
    }
}
