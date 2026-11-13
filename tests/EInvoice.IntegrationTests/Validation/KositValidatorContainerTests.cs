using System.Text;
using EInvoice.Application.Validation;
using EInvoice.Infrastructure.Validation;

namespace EInvoice.IntegrationTests.Validation;

/// <summary>
/// Against the real KoSIT validator container (docker compose up -d kosit-validator).
/// Every official sample must be accepted, and a sample with a wrong total must be rejected for the right rule.
/// Requires EINVOICE_KOSIT_URL, for example http://127.0.0.1:8081/.
/// </summary>
[Trait("Category", "Kosit")]
public sealed class KositValidatorContainerTests : IDisposable
{
    private readonly HttpClient _http;
    private readonly KositValidatorClient _client;

    public KositValidatorContainerTests()
    {
        var url = Environment.GetEnvironmentVariable("EINVOICE_KOSIT_URL")
            ?? throw new InvalidOperationException("Set EINVOICE_KOSIT_URL to run validator tests (see db/README.md).");
        _http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(60) };
        _client = new KositValidatorClient(_http);
    }

    public void Dispose() => _http.Dispose();

    private static string Corpus => Path.Combine(RepositoryRoot(), "testdata", "xrechnung", "kosit");

    public static TheoryData<string> AllSamples()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(Corpus, "*.xml").Order())
        {
            data.Add(Path.GetFileName(file));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllSamples))]
    public async Task Every_official_sample_is_accepted(string fileName)
    {
        var result = await _client.CheckAsync(await File.ReadAllBytesAsync(Path.Combine(Corpus, fileName)), CancellationToken.None);

        Assert.True(result.Outcome == ConformanceOutcome.Accepted, $"{fileName}: {result.Outcome} {string.Join(", ", result.ErrorCodes)}");
    }

    [Fact]
    public async Task Sample_with_a_wrong_gross_total_is_rejected_for_BR_CO_15()
    {
        var xml = (await File.ReadAllTextAsync(Path.Combine(Corpus, "01.01a-INVOICE_ubl.xml")))
            .Replace("<cbc:TaxInclusiveAmount currencyID=\"EUR\">336.9</cbc:TaxInclusiveAmount>",
                     "<cbc:TaxInclusiveAmount currencyID=\"EUR\">346.9</cbc:TaxInclusiveAmount>", StringComparison.Ordinal);

        var result = await _client.CheckAsync(Encoding.UTF8.GetBytes(xml), CancellationToken.None);

        Assert.Equal(ConformanceOutcome.Rejected, result.Outcome);
        Assert.Contains("BR-CO-15", result.ErrorCodes);
    }

    [Fact]
    public async Task Health_endpoint_reports_up()
    {
        var health = await _http.GetStringAsync(new Uri("server/health", UriKind.Relative));

        Assert.Contains(">UP<", health, StringComparison.Ordinal);
        Assert.Contains("1.6.3", health, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EInvoice.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
