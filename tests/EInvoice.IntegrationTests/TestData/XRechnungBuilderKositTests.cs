using EInvoice.Application.Validation;
using EInvoice.Infrastructure.Parsing;
using EInvoice.Infrastructure.Validation;

namespace EInvoice.IntegrationTests.TestData;

/// <summary>
/// The synthetic invoices used by the tests and the demo must be valid XRechnung, or a "clean" test would really be
/// testing an invalid document. The real KoSIT validator checks them.
/// </summary>
[Trait("Category", "Kosit")]
public sealed class XRechnungBuilderKositTests : IDisposable
{
    private readonly HttpClient _http = new()
    {
        BaseAddress = new Uri(Environment.GetEnvironmentVariable("EINVOICE_KOSIT_URL")
            ?? throw new InvalidOperationException("Set EINVOICE_KOSIT_URL to run validator tests.")),
        Timeout = TimeSpan.FromSeconds(60),
    };

    public void Dispose() => _http.Dispose();

    [Fact]
    public async Task Built_invoice_is_accepted_by_the_KoSIT_validator()
    {
        var xml = new XRechnungBuilder { PurchaseOrderNumber = "PO-0007" }
            .Line("HDMI cable 2 m", 13m, 7.4125m, orderLine: "1")
            .Line("Screw M4 x 20, per piece", 1800m, 0.0125m, orderLine: "2")
            .Line("Handbook German commercial code", 2m, 39.90m, taxRate: 7m)
            .Build();

        var result = await new KositValidatorClient(_http).CheckAsync(xml, CancellationToken.None);

        Assert.True(result.Outcome == ConformanceOutcome.Accepted, $"{result.Outcome}: {string.Join(", ", result.ErrorCodes)}");
    }

    [Fact]
    public void Built_invoice_parses_and_passes_every_document_rule()
    {
        var xml = new XRechnungBuilder { PurchaseOrderNumber = "PO-0007" }
            .Line("HDMI cable 2 m", 13m, 7.4125m, orderLine: "1")
            .Line("Handbook German commercial code", 2m, 39.90m, taxRate: 7m)
            .Build();

        var parsed = new XRechnungParser().Parse(new MemoryStream(xml));

        Assert.True(parsed.Succeeded, parsed.Error?.Message);
        Assert.Equal("PO-0007", parsed.Document!.PurchaseOrderReference);
        Assert.Empty(DocumentRules.Check(parsed.Document));
    }
}
