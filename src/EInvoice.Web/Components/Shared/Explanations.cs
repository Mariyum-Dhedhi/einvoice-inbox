using EInvoice.Application.Explanation;
using EInvoice.Application.Queries;
using EInvoice.Application.Validation;
using EInvoice.Domain.Matching;
using Microsoft.Extensions.Localization;

namespace EInvoice.Web.Components.Shared;

/// <summary>
/// The rule-based explanation in the page's language. Which problems are explained, and in which order, comes from
/// <see cref="RuleBasedExplainer.Issues"/> (one place for that); the words come from the texts by code. A finding keeps
/// its message as recorded (English, with the amounts and BT numbers the rule found) under its translated rule name.
/// Numbers use the German formats, as everywhere on the page.
/// </summary>
public static class Explanations
{
    public sealed record Text(IReadOnlyList<string> Problems, IReadOnlyList<string> Steps);

    public static Text For(IStringLocalizer t, InvoiceDetail invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        var problems = new List<string>();
        var steps = new List<string>();
        var currency = invoice.Currency;

        foreach (var issue in RuleBasedExplainer.Issues(invoice))
        {
            if (Enum.TryParse<MatchOutcome>(issue.Code, out var outcome) && invoice.MatchResults.FirstOrDefault(
                    m => m.Outcome == outcome && m.InvoiceLineNumber == issue.Line) is { } line)
            {
                problems.Add(Match(t, line, currency));
                steps.Add(t[$"Explain.{outcome}.Step"]);
                continue;
            }

            var finding = invoice.Findings.First(f => f.RuleCode == issue.Code && f.Message == issue.Text);
            problems.Add($"{Labels.Rule(t, finding.RuleCode)}: {finding.Message}");
            if (finding.Severity == FindingSeverity.Blocking)
            {
                steps.Add(t["Explain.Blocking"]);
            }

            var step = t[$"Rule.{finding.RuleCode}.Step"];
            steps.Add(step.ResourceNotFound ? t["Explain.GenericStep"] : step.Value);
        }

        return problems.Count == 0
            ? new Text([t["Explain.Nothing"]], [t["Explain.NothingStep"]])
            : new Text(problems, steps.Distinct(StringComparer.Ordinal).ToList());
    }

    private static string Match(IStringLocalizer t, MatchLineView m, string? currency) => m.Outcome switch
    {
        MatchOutcome.PriceVariance => t["Explain.PriceVariance.Text",
            m.InvoiceLineNumber,
            Formats.UnitPrice(m.InvoiceUnitPrice, currency),
            Formats.Percent(m.PriceVariancePercent),
            Formats.UnitPrice(m.OrderUnitPrice, currency),
            Formats.Money(m.PriceVarianceAmount, currency),
            Formats.Rate(m.PriceTolerancePercent)],
        MatchOutcome.QuantityVariance => t["Explain.QuantityVariance.Text",
            m.InvoiceLineNumber, Formats.Quantity(m.QuantityInvoiced), Formats.Quantity(m.QuantityAvailable)],
        MatchOutcome.NoPurchaseOrder => t["Explain.NoPurchaseOrder.Text", m.InvoiceLineNumber],
        _ => t["Explain.NoGoodsReceipt.Text", m.InvoiceLineNumber, m.PurchaseOrderLineNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "–"],
    };
}
