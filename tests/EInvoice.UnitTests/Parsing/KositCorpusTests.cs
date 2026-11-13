using EInvoice.Application.Parsing;
using EInvoice.Infrastructure.Parsing;

namespace EInvoice.UnitTests.Parsing;

/// <summary>
/// Every official KoSIT XRechnung 3.0.2 sample must parse. Instead of hand-labelling 66 files, each result is
/// checked against calculation rules from EN 16931 that every valid invoice satisfies: if a field were mapped
/// from the wrong element, these sums would not add up.
/// </summary>
public class KositCorpusTests
{
    private static readonly XRechnungParser Parser = new();

    public static TheoryData<string> AllSamples()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(TestPaths.KositCorpus, "*.xml").Order())
        {
            data.Add(Path.GetFileName(file));
        }

        return data;
    }

    public static TheoryData<string> CaseNumbers()
    {
        var data = new TheoryData<string>();
        foreach (var number in Directory.EnumerateFiles(TestPaths.KositCorpus, "*_ubl.xml").Select(f => Path.GetFileName(f)[..5]).Order())
        {
            data.Add(number);
        }

        return data;
    }

    internal static ParsedDocument Parse(string fileName)
    {
        using var stream = File.OpenRead(Path.Combine(TestPaths.KositCorpus, fileName));
        var result = Parser.Parse(stream);
        Assert.True(result.Succeeded, $"{fileName}: {result.Error?.Code} {result.Error?.Message}");
        return result.Document!;
    }

    [Fact]
    public void Corpus_contains_all_66_samples_as_33_ubl_cii_pairs()
    {
        Assert.Equal(66, AllSamples().Count);
        Assert.Equal(33, CaseNumbers().Count);
    }

    [Theory]
    [MemberData(nameof(AllSamples))]
    public void Sample_parses_and_satisfies_EN16931_calculation_rules(string fileName)
    {
        var doc = Parse(fileName);

        Assert.False(string.IsNullOrWhiteSpace(doc.InvoiceNumber));
        Assert.Contains(doc.InvoiceTypeCode, new[] { "380", "384", "389" });
        Assert.Equal(3, doc.Currency.Length);
        Assert.NotEmpty(doc.Lines);
        Assert.Equal(Enumerable.Range(1, doc.Lines.Count), doc.Lines.Select(l => l.Position));

        // BR-CO-10: sum of invoice line net amounts (BT-106) = sum of the lines (BT-131).
        Assert.Equal(doc.SumOfLineNetAmounts, doc.Lines.Sum(l => l.NetAmount));

        // BR-CO-15: invoice total with VAT (BT-112) = total without VAT (BT-109) + total VAT (BT-110).
        Assert.Equal(doc.GrossTotal, doc.NetTotal + doc.TaxTotal);
    }

    /// <summary>
    /// Fields where the official UBL and CII files genuinely contain different values (checked by hand, 2026-11-13,
    /// suite release v2026-08-31). Each side must still return exactly what its own file says.
    /// </summary>
    private static readonly Dictionary<(string Case, string Field), (string Ubl, string Cii)> KnownUpstreamDifferences = new()
    {
        // 01.21: UBL seller VAT id is "DE 123456789", CII is "DE152338654".
        [("01.21", "SellerVatId")] = ("DE123456789", "DE152338654"),
    };

    [Theory]
    [MemberData(nameof(CaseNumbers))]
    public void Ubl_and_cii_versions_of_the_same_case_give_the_same_result(string caseNumber)
    {
        var ubl = Parse($"{caseNumber}a-INVOICE_ubl.xml");
        var cii = Parse($"{caseNumber}a-INVOICE_uncefact.xml");

        if (KnownUpstreamDifferences.TryGetValue((caseNumber, "SellerVatId"), out var vat))
        {
            Assert.Equal(vat.Ubl, ubl.SellerVatId);
            Assert.Equal(vat.Cii, cii.SellerVatId);
            cii = cii with { SellerVatId = ubl.SellerVatId };
        }

        Assert.Equal(DocumentSyntax.Ubl, ubl.Syntax);
        Assert.Equal(DocumentSyntax.Cii, cii.Syntax);
        // Field by field, so a failure names the field (records would compare the lists by reference anyway).
        var differences = new List<string>();
        void Compare<T>(string field, T u, T c)
        {
            if (!EqualityComparer<T>.Default.Equals(u, c))
            {
                differences.Add($"{field}: UBL '{u}' vs CII '{c}'");
            }
        }

        Compare("InvoiceTypeCode", ubl.InvoiceTypeCode, cii.InvoiceTypeCode);
        Compare("InvoiceNumber", ubl.InvoiceNumber, cii.InvoiceNumber);
        Compare("IssueDate", ubl.IssueDate, cii.IssueDate);
        Compare("Currency", ubl.Currency, cii.Currency);
        Compare("BuyerReference", ubl.BuyerReference, cii.BuyerReference);
        Compare("PurchaseOrderReference", ubl.PurchaseOrderReference, cii.PurchaseOrderReference);
        Compare("SellerName", ubl.SellerName, cii.SellerName);
        Compare("SellerVatId", ubl.SellerVatId, cii.SellerVatId);
        Compare("PaymentAccountIbans", string.Join(",", ubl.PaymentAccountIbans), string.Join(",", cii.PaymentAccountIbans));
        Compare("SumOfLineNetAmounts", ubl.SumOfLineNetAmounts, cii.SumOfLineNetAmounts);
        Compare("NetTotal", ubl.NetTotal, cii.NetTotal);
        Compare("TaxTotal", ubl.TaxTotal, cii.TaxTotal);
        Compare("GrossTotal", ubl.GrossTotal, cii.GrossTotal);
        Compare("Lines.Count", ubl.Lines.Count, cii.Lines.Count);
        foreach (var (u, c) in ubl.Lines.Zip(cii.Lines))
        {
            Compare($"Line {u.Position}", u, c);
        }

        Assert.True(differences.Count == 0, string.Join(Environment.NewLine, differences));
    }
}
