using EInvoice.Application.Parsing;
using EInvoice.Domain.Shared;
using static EInvoice.Application.Validation.Findings;

namespace EInvoice.Application.Validation;

/// <summary>
/// Rules on the document alone. They recompute what the supplier stated instead of trusting it, so they also protect
/// formats the KoSIT validator never sees (plain PDFs read by AI). Every official KoSIT sample must pass them.
/// </summary>
public static class DocumentRules
{
    /// <summary>Allowed difference when a supplier rounds per line instead of per total.</summary>
    public const decimal RoundingTolerance = 0.01m;

    public static IReadOnlyList<ValidationFinding> Check(ParsedDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var findings = new List<ValidationFinding>();

        // LINE_SUM (BR-CO-10)
        var lineSum = document.Lines.Sum(l => l.NetAmount);
        if (lineSum != document.SumOfLineNetAmounts)
        {
            findings.Add(Review(RuleCodes.LineSum,
                $"The invoice lines add up to {Amount(lineSum)}, but the stated sum of lines (BT-106) is {Amount(document.SumOfLineNetAmounts)}."));
        }

        // TOTALS (BR-CO-15)
        if (document.NetTotal + document.TaxTotal != document.GrossTotal)
        {
            findings.Add(Review(RuleCodes.Totals,
                $"Net {Amount(document.NetTotal)} + VAT {Amount(document.TaxTotal)} = {Amount(document.NetTotal + document.TaxTotal)}, but the stated gross total (BT-112) is {Amount(document.GrossTotal)}."));
        }

        // LINE_AMOUNT: quantity x net price - allowances + charges
        foreach (var line in document.Lines)
        {
            var expected = decimal.Round(line.Quantity * line.UnitPrice, 2, MidpointRounding.AwayFromZero) - line.LineAllowances + line.LineCharges;
            if (Math.Abs(expected - line.NetAmount) > RoundingTolerance)
            {
                findings.Add(Review(RuleCodes.LineAmount,
                    $"Line {line.Position}: quantity x price - allowances + charges = {Amount(expected)}, but the stated line amount is {Amount(line.NetAmount)}.",
                    line.Position));
            }
        }

        // VAT_BREAKDOWN: each category is base x rate, and the categories add up to BT-110
        if (document.VatBreakdown.Count == 0)
        {
            findings.Add(Review(RuleCodes.VatBreakdown, "The invoice states no VAT breakdown (BG-23)."));
        }
        else
        {
            foreach (var (vat, i) in document.VatBreakdown.Select((v, i) => (v, i + 1)))
            {
                var expected = decimal.Round(vat.TaxableAmount * vat.RatePercent / 100m, 2, MidpointRounding.AwayFromZero);
                if (Math.Abs(expected - vat.TaxAmount) > RoundingTolerance)
                {
                    findings.Add(Review(RuleCodes.VatBreakdown,
                        $"VAT breakdown {i}: {Amount(vat.TaxableAmount)} at {Amount(vat.RatePercent)}% is {Amount(expected)}, but {Amount(vat.TaxAmount)} is stated."));
                }
            }

            var vatSum = document.VatBreakdown.Sum(v => v.TaxAmount);
            if (vatSum != document.TaxTotal)
            {
                findings.Add(Review(RuleCodes.VatBreakdown,
                    $"The VAT breakdown adds up to {Amount(vatSum)}, but the stated VAT total (BT-110) is {Amount(document.TaxTotal)}."));
            }
        }

        // IBAN_CHECKSUM: never echo the value; it is untrusted text
        foreach (var (iban, n) in document.PaymentAccountIbans.Select((v, i) => (v, i + 1)))
        {
            if (!Iban.TryParse(iban, out _))
            {
                findings.Add(Blocking(RuleCodes.IbanChecksum, $"Payment account {n} (BT-84) is not a valid IBAN."));
            }
        }

        return findings;
    }
}
