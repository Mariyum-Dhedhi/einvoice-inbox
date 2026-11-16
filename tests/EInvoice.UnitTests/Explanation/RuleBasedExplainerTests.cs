using EInvoice.Application.Explanation;
using EInvoice.Application.Validation;
using static EInvoice.UnitTests.Explanation.ExplanationTestData;

namespace EInvoice.UnitTests.Explanation;

/// <summary>
/// The rule-based explanation: always available (also with AI switched off), built only from codes and numbers.
/// It is also what is shown whenever an AI explanation is not trusted.
/// </summary>
public class RuleBasedExplainerTests
{
    public static TheoryData<string> RuleCodeList() => new(AllRuleCodes());

    [Fact]
    public void Price_variance_names_line_prices_difference_and_tolerance_and_suggests_a_step()
    {
        var issue = Assert.Single(RuleBasedExplainer.Issues(Exception(matches: [Price(2)])));

        Assert.Equal(2, issue.Line);
        Assert.Contains("Line 2", issue.Text, StringComparison.Ordinal);
        Assert.Contains("820.00 EUR", issue.Text, StringComparison.Ordinal);
        Assert.Contains("800.00 EUR", issue.Text, StringComparison.Ordinal);
        Assert.Contains("2.5 %", issue.Text, StringComparison.Ordinal);
        Assert.Contains("40.00 EUR", issue.Text, StringComparison.Ordinal);
        Assert.Contains("2 %", issue.Text, StringComparison.Ordinal); // the tolerance
        Assert.Contains("credit note", issue.Step, StringComparison.Ordinal);
    }

    [Fact]
    public void Quantity_variance_names_invoiced_and_available_quantities()
    {
        var issue = Assert.Single(RuleBasedExplainer.Issues(Exception(matches: [Quantity()])));

        Assert.Contains("10", issue.Text, StringComparison.Ordinal);
        Assert.Contains("6", issue.Text, StringComparison.Ordinal);
        Assert.False(string.IsNullOrWhiteSpace(issue.Step));
    }

    [Fact]
    public void Each_problem_outcome_has_its_own_explanation_and_full_matches_need_none()
    {
        var issues = RuleBasedExplainer.Issues(Exception(matches: [Price(1), Quantity(2), NoOrder(3), NoReceipt(4), Full(5)]));

        Assert.Equal([1, 2, 3, 4], issues.Select(i => i.Line!.Value));
        Assert.Equal(4, issues.Select(i => i.Text).Distinct().Count());
        Assert.Equal(4, issues.Select(i => i.Step).Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(RuleCodeList))]
    public void Every_rule_code_has_an_explanation_and_a_step(string code)
    {
        var issue = Assert.Single(RuleBasedExplainer.Issues(Exception(findings: [Finding(code)])));

        Assert.Equal(code, issue.Code);
        Assert.False(string.IsNullOrWhiteSpace(issue.Text));
        Assert.False(string.IsNullOrWhiteSpace(issue.Step));
        Assert.DoesNotContain(code, issue.Step, StringComparison.Ordinal); // steps are words for a clerk, not codes
        Assert.DoesNotContain(RuleBasedExplainer.GenericStep, issue.Step, StringComparison.Ordinal); // its own step, not the fallback
    }

    [Fact]
    public void Finding_explanation_quotes_the_rules_own_message()
    {
        var issue = Assert.Single(RuleBasedExplainer.Issues(Exception(findings: [Finding(RuleCodes.VatBreakdown)])));

        Assert.Contains("adds up to 20.64", issue.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Blocking_findings_come_first_and_say_the_invoice_can_only_be_rejected()
    {
        var issues = RuleBasedExplainer.Issues(Exception(
            matches: [Price()],
            findings: [Finding(RuleCodes.Totals), Finding(RuleCodes.IbanChecksum, FindingSeverity.Blocking, "Payment account 1 (BT-84) is not a valid IBAN.")]));

        Assert.Equal(RuleCodes.IbanChecksum, issues[0].Code);
        Assert.Contains("reject", issues[0].Step, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Explanation_combines_the_issues_and_says_it_is_rule_based()
    {
        var explanation = RuleBasedExplainer.Explain(Exception(matches: [Price(1), NoReceipt(2)]));

        Assert.Equal(ExplanationSource.Rules, explanation.Source);
        Assert.Contains("Line 1", explanation.Text, StringComparison.Ordinal);
        Assert.Contains("Line 2", explanation.Text, StringComparison.Ordinal);
        Assert.Null(explanation.FallbackReason);
    }

    [Fact]
    public void Never_repeats_supplier_text()
    {
        var explanation = RuleBasedExplainer.Explain(Exception(matches: [Price()], findings: [Finding(RuleCodes.HiddenText)]));

        Assert.DoesNotContain(SupplierText, explanation.Text + explanation.SuggestedStep, StringComparison.Ordinal);
    }
}
