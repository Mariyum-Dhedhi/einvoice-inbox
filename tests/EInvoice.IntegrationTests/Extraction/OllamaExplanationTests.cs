using EInvoice.Application.Explanation;
using EInvoice.Application.Queries;
using EInvoice.Domain.Invoices;
using EInvoice.Domain.Matching;
using EInvoice.Infrastructure.Explanation;
using OllamaSharp;

namespace EInvoice.IntegrationTests.Extraction;

/// <summary>
/// The exception explainer with the real model: it explains a price variance in words, and whatever it writes,
/// no number reaches the page that is not in the match result. Local only (Category=Ollama), never in CI.
/// </summary>
[Trait("Category", "Ollama")]
public sealed class OllamaExplanationTests
{
    private static InvoiceDetail PriceException() => new(
        Guid.NewGuid(), InvoiceStatus.Exception, SourceFormat.XRechnung, "clerk@test", "RE-2026-4711", Guid.NewGuid(), "Beispiel Bürobedarf GmbH",
        new DateOnly(2026, 11, 2), "PO-0815", "EUR", 1640.00m, 311.60m, 1951.60m,
        [new InvoiceLineView(1, "Office cleaning, monthly", 2m, 820.00m, 1640.00m, 19m, 1)],
        [],
        [],
        [new MatchLineView(1, 1, MatchOutcome.PriceVariance, 820.00m, 800.00m, 2.5m, 40.00m, 2m, 2m, 0m, 2.00m, DateTimeOffset.UnixEpoch)],
        null);

    [Fact]
    public async Task Real_model_explains_a_price_variance_with_checked_numbers_only()
    {
        var url = Environment.GetEnvironmentVariable("EINVOICE_OLLAMA_URL") ?? throw new InvalidOperationException("Set EINVOICE_OLLAMA_URL.");
        var modelName = Environment.GetEnvironmentVariable("EINVOICE_OLLAMA_MODEL") ?? throw new InvalidOperationException("Set EINVOICE_OLLAMA_MODEL.");
        using var model = new OllamaApiClient(new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromMinutes(5) }, modelName);
        var invoice = PriceException();

        var result = await new LlmExceptionExplainer(model).ExplainAsync(invoice, CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(result.Text));
        Assert.False(string.IsNullOrWhiteSpace(result.SuggestedStep));
        if (result.Source == ExplanationSource.Ai)
        {
            Assert.Empty(ExplanationNumbers.Unknown(result.Text + " " + result.SuggestedStep, ExplanationFacts.From(invoice)));
        }
        else
        {
            Assert.NotNull(result.FallbackReason); // a fallback always says why
        }
    }
}
