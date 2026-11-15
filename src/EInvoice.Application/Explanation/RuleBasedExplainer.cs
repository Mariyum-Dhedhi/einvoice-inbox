using System.Globalization;
using EInvoice.Application.Queries;
using EInvoice.Application.Validation;
using EInvoice.Domain.Matching;

namespace EInvoice.Application.Explanation;

/// <summary>
/// Explains an exception from its reason codes and numbers, with fixed wording: blocking findings first, then the
/// other findings, then the match lines that did not match. Uses only the rules' own messages and the match
/// numbers, never text from the document. Always available, and the fallback for any AI explanation.
/// </summary>
public static class RuleBasedExplainer
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private static readonly Dictionary<string, string> Steps = new(StringComparer.Ordinal)
    {
        [RuleCodes.Conformance] = "Reject the invoice and ask the supplier for one that meets the e-invoice standard.",
        [RuleCodes.ConformanceUnavailable] = "Check the invoice yourself, or wait until the validator can be reached again.",
        [RuleCodes.LineSum] = "Ask the supplier for a corrected invoice; do not approve amounts that do not add up.",
        [RuleCodes.Totals] = "Ask the supplier for a corrected invoice; do not approve totals that do not add up.",
        [RuleCodes.LineAmount] = "Compare the line with the order and ask the supplier to correct the line amount.",
        [RuleCodes.VatBreakdown] = "Check the VAT with the supplier; a wrong VAT amount cannot be deducted as input tax.",
        [RuleCodes.IbanChecksum] = "Reject the invoice: the bank account is not valid, so it cannot be paid.",
        [RuleCodes.IbanNotApproved] = "Confirm the bank account with the supplier using a phone number you already know before approving; a changed account can be payment fraud.",
        [RuleCodes.VendorUnknown] = "Check who the seller is; add them to the vendor master data if they are a real supplier, otherwise reject.",
        [RuleCodes.VendorInactive] = "Find out why the vendor was deactivated before approving anything from them.",
        [RuleCodes.DuplicateNumber] = "Reject the invoice: this vendor already has an invoice with this number.",
        [RuleCodes.PossibleDuplicate] = "Compare it with the other invoice of the same amount and date before approving, so it is not paid twice.",
        [RuleCodes.ExtractionUnconfirmed] = "Open the original PDF and confirm the named field.",
        [RuleCodes.HiddenText] = "Open the original PDF and check it carefully: hidden text can be an attempt to manipulate automatic processing.",
    };

    public static IReadOnlyList<ExplainedIssue> Issues(InvoiceDetail invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        var issues = invoice.Findings
            .OrderByDescending(f => f.Severity) // stable: Blocking first, otherwise the rules' order
            .Select(f => new ExplainedIssue(f.RuleCode, f.LinePosition, f.Message, StepFor(f)))
            .ToList();
        issues.AddRange(invoice.MatchResults
            .Where(m => m.Outcome != MatchOutcome.FullMatch)
            .OrderBy(m => m.InvoiceLineNumber)
            .Select(m => Explain(m, invoice.Currency ?? string.Empty)));
        return issues;
    }

    public static Explanation Explain(InvoiceDetail invoice)
    {
        var issues = Issues(invoice);
        return issues.Count == 0
            ? new Explanation("Nothing to explain: no problems were found.", "No action is needed.", ExplanationSource.Rules)
            : new Explanation(
                string.Join('\n', issues.Select(i => i.Text)),
                string.Join('\n', issues.Select(i => i.Step).Distinct(StringComparer.Ordinal)),
                ExplanationSource.Rules);
    }

    private static string StepFor(FindingView finding)
    {
        var step = Steps.GetValueOrDefault(finding.RuleCode, "Read the finding and decide whether the invoice can be approved.");
        return finding.Severity == FindingSeverity.Blocking && !step.Contains("reject", StringComparison.OrdinalIgnoreCase)
            ? $"This finding blocks payment: reject the invoice unless it can be corrected. {step}"
            : step;
    }

    private static ExplainedIssue Explain(MatchLineView m, string currency) => m.Outcome switch
    {
        MatchOutcome.PriceVariance => new(
            nameof(MatchOutcome.PriceVariance),
            m.InvoiceLineNumber,
            string.Create(Invariant,
                $"Line {m.InvoiceLineNumber} was invoiced at {Price(m.InvoiceUnitPrice, currency)} per unit, {Percent(m.PriceVariancePercent)} above the order price of {Price(m.OrderUnitPrice, currency)}. That is {Money(m.PriceVarianceAmount, currency)} more in total, over the {Percent(m.PriceTolerancePercent)} tolerance."),
            "Ask the supplier for a credit note, or approve it if the price increase was agreed."),
        MatchOutcome.QuantityVariance => new(
            nameof(MatchOutcome.QuantityVariance),
            m.InvoiceLineNumber,
            string.Create(Invariant,
                $"Line {m.InvoiceLineNumber}: {Quantity(m.QuantityInvoiced)} invoiced, but only {Quantity(m.QuantityAvailable)} received and not yet invoiced."),
            "Check whether the rest was delivered: book the goods receipt and match again, or ask the supplier to correct the quantity."),
        MatchOutcome.NoPurchaseOrder => new(
            nameof(MatchOutcome.NoPurchaseOrder),
            m.InvoiceLineNumber,
            string.Create(Invariant, $"Line {m.InvoiceLineNumber} could not be linked to a purchase order line."),
            "Find the purchase order and ask the supplier to quote it, or approve without an order if your rules allow it."),
        _ => new(
            nameof(MatchOutcome.NoGoodsReceipt),
            m.InvoiceLineNumber,
            string.Create(Invariant, $"Line {m.InvoiceLineNumber}: nothing has been received yet for order line {m.PurchaseOrderLineNumber}."),
            "Check with goods receiving, and match again once the goods receipt is booked."),
    };

    private static string Price(decimal? value, string currency) => value is { } v ? $"{v.ToString("#,##0.00####", Invariant)} {currency}".Trim() : "-";

    private static string Money(decimal? value, string currency) => value is { } v ? $"{v.ToString("#,##0.00", Invariant)} {currency}".Trim() : "-";

    private static string Percent(decimal? value) => value is { } v ? $"{v.ToString("0.##", Invariant)} %" : "-";

    private static string Quantity(decimal? value) => value is { } v ? v.ToString("#,##0.####", Invariant) : "-";
}
