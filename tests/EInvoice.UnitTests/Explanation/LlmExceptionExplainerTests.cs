using EInvoice.Application.Explanation;
using EInvoice.Application.Validation;
using EInvoice.Infrastructure.Explanation;
using EInvoice.UnitTests.Extraction;
using Microsoft.Extensions.AI;
using static EInvoice.UnitTests.Explanation.ExplanationTestData;

namespace EInvoice.UnitTests.Explanation;

/// <summary>
/// Which numbers an explanation may contain: only numbers from the structured result (in any common rounding and
/// notation). Anything else is treated as invented, and the whole explanation is not shown.
/// </summary>
public class ExplanationNumbersTests
{
    private static readonly ExplanationFacts Facts = ExplanationFacts.From(Exception(matches: [Price(2)]));

    [Theory]
    [InlineData("Line 2 is 2.5 % above the order price (+40.00 EUR).")]
    [InlineData("The invoice price of 820 EUR is higher than the ordered 800.00 EUR.")]
    [InlineData("That is 2.50% over the 2% tolerance.")]
    [InlineData("Ask the supplier for a credit note.")] // no numbers at all
    public void Numbers_from_the_result_are_allowed_in_common_forms(string text)
    {
        Assert.Empty(ExplanationNumbers.Unknown(text, Facts));
    }

    [Theory]
    [InlineData("Line 2 is 7.5 % above the order price.", "7.5")]
    [InlineData("The difference is 120.00 EUR.", "120.00")]
    [InlineData("Line 3 is affected.", "3")]
    [InlineData("Pay within 14 days.", "14")]
    [InlineData("The invoice total is 1,904.01 EUR.", "1,904.01")]
    public void Any_other_number_is_unknown(string text, string invented)
    {
        Assert.Contains(invented, ExplanationNumbers.Unknown(text, Facts));
    }

    [Fact]
    public void Totals_and_quantities_of_the_invoice_are_known()
    {
        var facts = ExplanationFacts.From(Exception(matches: [Quantity()]));

        Assert.Empty(ExplanationNumbers.Unknown("10 were invoiced but only 6 received; 4 are missing. The gross total is 1,904.00 EUR.", facts));
    }

    [Fact]
    public void Facts_contain_no_supplier_text()
    {
        var json = ExplanationFacts.From(Exception(matches: [Price()], findings: [Finding(RuleCodes.Totals)])).ToJson();

        Assert.DoesNotContain(SupplierText, json, StringComparison.Ordinal); // no invoice number, no item names
        Assert.Contains("PriceVariance", json, StringComparison.Ordinal);
        Assert.Contains("TOTALS", json, StringComparison.Ordinal);
    }
}

/// <summary>
/// The AI explanation: a suggestion in plain language, from structured facts only, number-checked, and replaced by
/// the rule-based text whenever anything about it is not right.
/// </summary>
public class LlmExceptionExplainerTests
{
    private const string Good = """{ "explanation": "Line 2 was billed at 820.00 EUR, 2.5 % above the order price of 800.00 EUR. That is 40.00 EUR more than ordered, above the 2 % tolerance.", "suggestedStep": "Ask the supplier for a credit note, or approve it if the price increase was agreed." }""";

    private static (LlmExceptionExplainer Explainer, ScriptedModel Model) With(Func<string, string> reply)
    {
        var model = new ScriptedModel(reply);
        return (new LlmExceptionExplainer(model), model);
    }

    private static Task<Application.Explanation.Explanation> Explain(LlmExceptionExplainer explainer) =>
        explainer.ExplainAsync(Exception(matches: [Price(2)]), CancellationToken.None);

    [Fact]
    public async Task Checked_reply_is_shown_as_an_ai_suggestion()
    {
        var (explainer, _) = With(_ => Good);

        var result = await Explain(explainer);

        Assert.Equal(ExplanationSource.Ai, result.Source);
        Assert.StartsWith("Line 2 was billed", result.Text, StringComparison.Ordinal);
        Assert.Contains("credit note", result.SuggestedStep, StringComparison.Ordinal);
        Assert.True(explainer.UsesAi);
    }

    [Fact]
    public async Task Model_gets_structured_facts_only_no_tools_and_no_randomness()
    {
        var (explainer, model) = With(_ => Good);

        await explainer.ExplainAsync(Exception(matches: [Price(2)], findings: [Finding(RuleCodes.Totals)]), CancellationToken.None);

        var (messages, options) = Assert.Single(model.Calls);
        var sent = string.Join("\n", messages.Select(m => m.Text));
        Assert.DoesNotContain(SupplierText, sent, StringComparison.Ordinal);
        Assert.Contains("PriceVariance", sent, StringComparison.Ordinal);
        Assert.True(options!.Tools is null or { Count: 0 });
        Assert.Equal(0f, options.Temperature);
        Assert.IsType<ChatResponseFormatJson>(options.ResponseFormat);
        Assert.Contains("suggest", model.LastSystemText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot change", model.LastSystemText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Explanation_with_an_invented_number_is_replaced_by_the_rule_based_text()
    {
        var (explainer, _) = With(_ => Good.Replace("2.5 %", "7.5 %", StringComparison.Ordinal));

        var result = await Explain(explainer);

        Assert.Equal(ExplanationSource.Rules, result.Source);
        Assert.Contains("number", result.FallbackReason, StringComparison.Ordinal);
        Assert.DoesNotContain("7.5", result.Text + result.SuggestedStep + result.FallbackReason, StringComparison.Ordinal);
        Assert.Equal(RuleBasedExplainer.Explain(Exception(matches: [Price(2)])).Text, result.Text);
    }

    [Fact]
    public async Task Invented_number_in_the_suggested_step_counts_too()
    {
        var (explainer, _) = With(_ => Good.Replace("Ask the supplier", "Wait 30 days, then ask the supplier", StringComparison.Ordinal));

        Assert.Equal(ExplanationSource.Rules, (await Explain(explainer)).Source);
    }

    [Theory]
    [InlineData("Sure! Here is the explanation: the price is too high.")]
    [InlineData("""{ "explanation": "x", "suggestedStep": "y", "approve": true }""")]
    [InlineData("""{ "explanation": "x" }""")]
    [InlineData("""{ "explanation": "", "suggestedStep": "y" }""")]
    [InlineData("")]
    public async Task Reply_that_is_not_exactly_the_expected_json_falls_back(string reply)
    {
        var (explainer, _) = With(_ => reply);

        var result = await Explain(explainer);

        Assert.Equal(ExplanationSource.Rules, result.Source);
        Assert.NotNull(result.FallbackReason);
    }

    [Fact]
    public async Task Overlong_reply_falls_back()
    {
        var (explainer, _) = With(_ => $$"""{ "explanation": "{{new string('a', 2001)}}", "suggestedStep": "y" }""");

        Assert.Equal(ExplanationSource.Rules, (await Explain(explainer)).Source);
    }

    [Fact]
    public async Task Model_that_cannot_be_reached_falls_back()
    {
        var (explainer, _) = With(_ => throw new HttpRequestException("connection refused"));

        var result = await Explain(explainer);

        Assert.Equal(ExplanationSource.Rules, result.Source);
        Assert.DoesNotContain("connection refused", result.FallbackReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invoice_without_problems_needs_no_model_call()
    {
        var (explainer, model) = With(_ => Good);

        var result = await explainer.ExplainAsync(Exception(matches: [Full()]), CancellationToken.None);

        Assert.Empty(model.Calls);
        Assert.Equal(ExplanationSource.Rules, result.Source);
    }

    [Fact]
    public void Explanation_has_nothing_that_could_carry_a_decision()
    {
        Assert.Equal(["FallbackReason", "Source", "SuggestedStep", "Text"],
            typeof(Application.Explanation.Explanation).GetProperties().Select(p => p.Name).Order(StringComparer.Ordinal));
    }
}

/// <summary>With AI off, the explainer is the rule-based one, and the page offers no AI button.</summary>
public class RuleBasedExceptionExplainerTests
{
    [Fact]
    public async Task Gives_the_rule_based_text_and_says_it_uses_no_ai()
    {
        var explainer = new RuleBasedExceptionExplainer();

        var result = await explainer.ExplainAsync(Exception(matches: [Price()]), CancellationToken.None);

        Assert.False(explainer.UsesAi);
        Assert.Equal(ExplanationSource.Rules, result.Source);
        Assert.Null(result.FallbackReason);
    }
}
