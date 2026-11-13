using EInvoice.Application.Validation;
using EInvoice.UnitTests.Parsing;
using static EInvoice.UnitTests.Validation.TestDocuments;

namespace EInvoice.UnitTests.Validation;

/// <summary>Rules on the document alone: arithmetic and IBAN syntax. Each rule has a passing and a failing case.</summary>
public class DocumentRuleTests
{
    [Fact]
    public void Consistent_document_passes_every_document_rule()
    {
        Assert.Empty(DocumentRules.Check(Valid()));
    }

    [Theory]
    [MemberData(nameof(KositCorpusTests.AllSamples), MemberType = typeof(KositCorpusTests))]
    public void Every_official_KoSIT_sample_passes_every_document_rule(string fileName)
    {
        // A rule that rejects a valid official invoice is a bug in the rule.
        var findings = DocumentRules.Check(KositCorpusTests.Parse(fileName));

        Assert.True(findings.Count == 0, string.Join("; ", findings.Select(f => $"{f.RuleCode}: {f.Message}")));
    }

    // LINE_SUM (BR-CO-10)
    [Fact]
    public void Line_sum_fails_when_lines_do_not_add_up_to_BT106()
    {
        var doc = Valid() with { SumOfLineNetAmounts = 200.01m };

        var finding = Assert.Single(DocumentRules.Check(doc));
        Assert.Equal(RuleCodes.LineSum, finding.RuleCode);
        Assert.Equal(FindingSeverity.Review, finding.Severity);
    }

    // TOTALS (BR-CO-15)
    [Fact]
    public void Totals_fail_when_net_plus_vat_is_not_gross()
    {
        var doc = Valid() with { GrossTotal = 238.01m };

        var finding = Assert.Single(DocumentRules.Check(doc));
        Assert.Equal(RuleCodes.Totals, finding.RuleCode);
        Assert.Equal(FindingSeverity.Review, finding.Severity);
    }

    // LINE_AMOUNT
    [Fact]
    public void Line_amount_fails_when_quantity_times_price_does_not_give_the_line_amount()
    {
        var doc = Valid();
        doc = doc with
        {
            Lines = [doc.Lines[0] with { NetAmount = 101.00m }, doc.Lines[1]],
            SumOfLineNetAmounts = 201.00m,
            NetTotal = 201.00m,
            TaxTotal = 38.19m,
            GrossTotal = 239.19m,
            VatBreakdown = [new("S", 19m, 201.00m, 38.19m)],
        };

        var finding = Assert.Single(DocumentRules.Check(doc));
        Assert.Equal(RuleCodes.LineAmount, finding.RuleCode);
        Assert.Equal(1, finding.LinePosition);
    }

    [Theory]
    [InlineData(10, 0, 90.00)]   // 100 - 10 allowance
    [InlineData(0, 5, 105.00)]   // 100 + 5 charge
    [InlineData(10, 5, 95.00)]
    public void Line_amount_includes_line_allowances_and_charges(double allowances, double charges, double net)
    {
        var line = Line(1, quantity: 2m, unitPrice: 50m, net: (decimal)net, allowances: (decimal)allowances, charges: (decimal)charges);
        var doc = Valid() with
        {
            Lines = [line],
            SumOfLineNetAmounts = (decimal)net,
            NetTotal = (decimal)net,
            TaxTotal = Math.Round((decimal)net * 0.19m, 2),
            GrossTotal = (decimal)net + Math.Round((decimal)net * 0.19m, 2),
            VatBreakdown = [new("S", 19m, (decimal)net, Math.Round((decimal)net * 0.19m, 2))],
        };

        Assert.Empty(DocumentRules.Check(doc));
    }

    [Fact]
    public void Line_amount_works_for_negative_quantities()
    {
        // A credit line inside an invoice, as in KoSIT case 01.20.
        var doc = Valid() with
        {
            Lines = [Line(1, quantity: 3m, unitPrice: 100m, net: 300.00m), Line(2, quantity: -1m, unitPrice: 100m, net: -100.00m)],
            SumOfLineNetAmounts = 200.00m,
        };

        Assert.Empty(DocumentRules.Check(doc));
    }

    // VAT_BREAKDOWN
    [Fact]
    public void Vat_breakdown_fails_when_a_category_amount_is_not_base_times_rate()
    {
        var doc = Valid() with { VatBreakdown = [new("S", 19m, 200.00m, 37.00m)], TaxTotal = 37.00m, GrossTotal = 237.00m };

        var finding = Assert.Single(DocumentRules.Check(doc));
        Assert.Equal(RuleCodes.VatBreakdown, finding.RuleCode);
    }

    [Fact]
    public void Vat_breakdown_fails_when_categories_do_not_add_up_to_BT110()
    {
        var doc = Valid() with { VatBreakdown = [new("S", 19m, 100.00m, 19.00m), new("S", 19m, 100.00m, 19.00m)], TaxTotal = 38.01m, GrossTotal = 238.01m };

        Assert.Contains(RuleCodes.VatBreakdown, DocumentRules.Check(doc).Codes());
    }

    [Fact]
    public void Vat_breakdown_fails_when_missing()
    {
        Assert.Contains(RuleCodes.VatBreakdown, DocumentRules.Check(Valid() with { VatBreakdown = [] }).Codes());
    }

    [Fact]
    public void Vat_breakdown_accepts_one_cent_rounding_difference()
    {
        // 33.33 x 19% = 6.3327 -> 6.33; a supplier that rounds per line may state 6.34.
        var doc = Valid() with
        {
            Lines = [Line(1, quantity: 1m, unitPrice: 33.33m, net: 33.33m)],
            SumOfLineNetAmounts = 33.33m,
            NetTotal = 33.33m,
            TaxTotal = 6.34m,
            GrossTotal = 39.67m,
            VatBreakdown = [new("S", 19m, 33.33m, 6.34m)],
        };

        Assert.Empty(DocumentRules.Check(doc));
    }

    // IBAN_CHECKSUM
    [Fact]
    public void Iban_checksum_fails_for_a_mistyped_iban_and_does_not_echo_it()
    {
        var doc = Valid() with { PaymentAccountIbans = ["DE72000000000000001002"] }; // last digit changed

        var finding = Assert.Single(DocumentRules.Check(doc));
        Assert.Equal(RuleCodes.IbanChecksum, finding.RuleCode);
        Assert.Equal(FindingSeverity.Blocking, finding.Severity);
        Assert.DoesNotContain("DE72", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Invoice_without_any_iban_passes_the_checksum_rule()
    {
        Assert.Empty(DocumentRules.Check(Valid() with { PaymentAccountIbans = [] }));
    }
}
