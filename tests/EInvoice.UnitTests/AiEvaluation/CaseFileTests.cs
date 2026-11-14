using EInvoice.AiEvaluation;
using EInvoice.Domain.Shared;

namespace EInvoice.UnitTests.AiEvaluation;

/// <summary>
/// The evaluation's answer key is checked like code: the approved case mix, synthetic data only, and no accidental
/// overlaps between cases that would change an expected answer (for example an unintended possible duplicate).
/// </summary>
public class CaseFileTests
{
    private static readonly EvalCaseFile CaseFile = EvalCaseFile.Load(EvalCaseFile.DefaultPath(TestPaths.RepositoryRoot));

    private static IReadOnlyList<EvalCase> Cases => CaseFile.Cases;

    [Fact]
    public void Has_the_27_approved_cases_in_order()
    {
        Assert.Equal(Enumerable.Range(1, 27).Select(i => $"E{i:00}"), Cases.Select(c => c.Id));
    }

    [Fact]
    public void Case_mix_is_the_approved_one()
    {
        var counts = Cases.GroupBy(c => c.Type).ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(
            new Dictionary<string, int>
            {
                ["FullMatch"] = 6, ["PriceVariance"] = 3, ["QuantityVariance"] = 3, ["NoPurchaseOrder"] = 2, ["NoGoodsReceipt"] = 2,
                ["WrongTotals"] = 3, ["IbanProblem"] = 2, ["Duplicate"] = 2, ["UnknownVendor"] = 1, ["Injection"] = 3,
            },
            counts);
    }

    [Fact]
    public void Every_layout_is_used_often_and_every_multi_case_type_uses_more_than_one()
    {
        Assert.All(Cases.GroupBy(c => c.Layout), g => Assert.True(g.Count() >= 8, $"layout {g.Key}"));
        Assert.Equal(["A", "B", "C"], Cases.Select(c => c.Layout).Distinct().Order(StringComparer.Ordinal));
        Assert.All(Cases.GroupBy(c => c.Type).Where(g => g.Count() > 1), g => Assert.True(g.Select(c => c.Layout).Distinct().Count() > 1, g.Key));
    }

    [Fact]
    public void Every_case_has_a_complete_expected_answer()
    {
        Assert.All(Cases, c =>
        {
            Assert.Contains(c.Expected.Result, new[] { "Matched", "Exception", "Refused" });
            if (c.Expected.Result == "Exception")
            {
                Assert.NotEmpty(c.Expected.Reasons!);
            }

            if (c.Expected.Result == "Refused")
            {
                Assert.False(string.IsNullOrWhiteSpace(c.Expected.Refusal));
            }
        });
    }

    [Fact]
    public void Data_is_synthetic_seeded_active_vendors_and_bank_code_zero()
    {
        Assert.All(Cases.Where(c => c.Vendor is not null), c => Assert.InRange(c.Vendor!.Value, 1, 19));
        Assert.All(Cases, c => Assert.Equal("00000000", c.Truth.Iban[4..12]));
        Assert.All(Cases.Where(c => c.Vendor is null), c => Assert.DoesNotContain(c.Truth.SellerVatId, SeedVendors.All.Select(v => v.VatId)));
    }

    [Fact]
    public void Ibans_are_valid_except_the_one_meant_to_be_mistyped()
    {
        Assert.All(Cases, c => Assert.Equal(c.Id != "E21", Iban.TryParse(c.Truth.Iban, out _)));
    }

    [Fact]
    public void Only_the_intended_cases_share_vendor_amount_and_date()
    {
        var clashes = Cases
            .Where(c => c.Vendor is not null)
            .GroupBy(c => (c.Vendor, c.Truth.GrossTotal, c.IssueDate))
            .Where(g => g.Count() > 1)
            .Select(g => string.Join("+", g.Select(c => c.Id)))
            .ToList();

        Assert.Equal(["E02+E23"], clashes);
    }

    [Fact]
    public void Only_the_intended_cases_share_vendor_and_invoice_number()
    {
        var clashes = Cases
            .GroupBy(c => (c.Vendor, c.InvoiceNumber))
            .Where(g => g.Count() > 1)
            .Select(g => string.Join("+", g.Select(c => c.Id)))
            .ToList();

        Assert.Equal(["E01+E22"], clashes);
    }

    [Fact]
    public void Order_references_on_lines_point_at_order_lines()
    {
        Assert.All(Cases.Where(c => c.Order is not null), c =>
            Assert.All(c.Lines.Where(l => l.OrderLine is not null), l =>
                Assert.Contains(c.Order!.Lines, o => o.Line.ToString(System.Globalization.CultureInfo.InvariantCulture) == l.OrderLine)));
    }

    [Fact]
    public void Truth_follows_what_is_printed_including_the_supplier_mistakes()
    {
        var e17 = Cases.Single(c => c.Id == "E17").Truth;
        var e19 = Cases.Single(c => c.Id == "E19").Truth;
        var e01 = Cases.Single(c => c.Id == "E01").Truth;

        Assert.Equal((185.00m, 40.15m, 225.15m), (e17.NetTotal, e17.TaxTotal, e17.GrossTotal));
        Assert.Equal(78.60m, e19.Lines[0].NetAmount); // printed, not 24 x 3.20
        Assert.Equal((78.60m, 14.93m, 93.53m), (e19.NetTotal, e19.TaxTotal, e19.GrossTotal));
        Assert.Equal((233.73m, 25.25m, 258.98m), (e01.NetTotal, e01.TaxTotal, e01.GrossTotal)); // 74.13 + 159.60; VAT per rate: 14.08 + 11.17
        Assert.Equal("Beispiel Buerobedarf GmbH", e01.SellerName); // printed without umlauts (standard PDF fonts)
        Assert.Equal(("DE000000001", "DE72000000000000001001"), (e01.SellerVatId, e01.Iban));
    }
}
