using EInvoice.Application.Validation;
using static EInvoice.UnitTests.Validation.TestDocuments;

namespace EInvoice.UnitTests.Validation;

/// <summary>The validator gathers master data through an interface and combines document and master-data rules.</summary>
public class InvoiceValidatorTests
{
    private sealed class FakeData : IValidationDataSource
    {
        public VendorSnapshot? Vendor { get; init; } = ActiveVendor();

        public bool NumberExists { get; init; }

        public int Duplicates { get; init; }

        public List<string> Calls { get; } = [];

        public Task<VendorSnapshot?> FindVendorByVatIdAsync(string vatId, CancellationToken cancellationToken)
        {
            Calls.Add($"vendor:{vatId}");
            return Task.FromResult(Vendor);
        }

        public Task<bool> InvoiceNumberExistsAsync(Guid vendorId, string invoiceNumber, CancellationToken cancellationToken)
        {
            Calls.Add($"number:{invoiceNumber}");
            return Task.FromResult(NumberExists);
        }

        public Task<int> CountPossibleDuplicatesAsync(Guid vendorId, decimal grossTotal, DateOnly issueDate, string invoiceNumber, CancellationToken cancellationToken)
        {
            Calls.Add("duplicates");
            return Task.FromResult(Duplicates);
        }
    }

    private sealed class FakeConformance(ConformanceResult result) : IConformanceValidator
    {
        public int Calls { get; private set; }

        public Task<ConformanceResult> CheckAsync(ReadOnlyMemory<byte> xml, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private static readonly byte[] SourceXml = "<Invoice/>"u8.ToArray();

    private static InvoiceValidator Validator(FakeData data, IConformanceValidator? conformance = null) =>
        new(data, conformance ?? new FakeConformance(ConformanceResult.Accepted));

    // CONFORMANCE
    [Fact]
    public async Task Accepted_by_the_KoSIT_validator_adds_no_finding()
    {
        var conformance = new FakeConformance(ConformanceResult.Accepted);

        var result = await Validator(new FakeData(), conformance).ValidateAsync(Valid(), SourceXml);

        Assert.True(result.IsValid);
        Assert.Equal(1, conformance.Calls);
    }

    [Fact]
    public async Task Rejected_by_the_KoSIT_validator_is_blocking_and_names_the_rules()
    {
        var conformance = new FakeConformance(ConformanceResult.Rejected(["BR-CO-15", "BR-CO-16"]));

        var result = await Validator(new FakeData(), conformance).ValidateAsync(Valid(), SourceXml);

        var finding = Assert.Single(result.Findings);
        Assert.Equal(RuleCodes.Conformance, finding.RuleCode);
        Assert.Equal(FindingSeverity.Blocking, finding.Severity);
        Assert.Contains("BR-CO-15, BR-CO-16", finding.Message, StringComparison.Ordinal);
    }

    // CONFORMANCE_UNAVAILABLE
    [Fact]
    public async Task Validator_down_fails_closed_to_a_human_review()
    {
        var conformance = new FakeConformance(ConformanceResult.Unavailable);

        var result = await Validator(new FakeData(), conformance).ValidateAsync(Valid(), SourceXml);

        var finding = Assert.Single(result.Findings);
        Assert.Equal(RuleCodes.ConformanceUnavailable, finding.RuleCode);
        Assert.Equal(FindingSeverity.Review, finding.Severity);
    }

    [Fact]
    public async Task Without_source_xml_the_conformance_check_is_not_applicable()
    {
        // Plain PDFs read by AI have no XML for the KoSIT validator; the document rules still recompute everything.
        var conformance = new FakeConformance(ConformanceResult.Unavailable);

        var result = await Validator(new FakeData(), conformance).ValidateAsync(Valid(), sourceXml: null);

        Assert.True(result.IsValid);
        Assert.Equal(0, conformance.Calls);
    }

    [Fact]
    public async Task Clean_invoice_has_no_findings()
    {
        var result = await Validator(new FakeData()).ValidateAsync(Valid());

        Assert.True(result.IsValid);
        Assert.False(result.HasBlocking);
    }

    [Fact]
    public async Task Document_and_master_data_findings_are_combined()
    {
        var data = new FakeData { NumberExists = true };

        var result = await Validator(data).ValidateAsync(Valid() with { GrossTotal = 1m });

        Assert.Equal(new[] { RuleCodes.Totals, RuleCodes.DuplicateNumber }.Order(), result.Findings.Codes().Order());
        Assert.True(result.HasBlocking);
    }

    [Fact]
    public async Task Without_a_vat_id_no_vendor_lookup_is_made()
    {
        var data = new FakeData();

        var result = await Validator(data).ValidateAsync(Valid() with { SellerVatId = null });

        Assert.Empty(data.Calls);
        Assert.Equal([RuleCodes.VendorUnknown], result.Findings.Codes());
    }

    [Fact]
    public async Task Unknown_vendor_skips_the_duplicate_lookups()
    {
        var data = new FakeData { Vendor = null };

        await Validator(data).ValidateAsync(Valid());

        Assert.Equal([$"vendor:{VatId}"], data.Calls);
    }

    [Fact]
    public async Task Blocking_findings_are_listed_before_review_findings()
    {
        var data = new FakeData { Duplicates = 1, NumberExists = true };

        var result = await Validator(data).ValidateAsync(Valid());

        Assert.Equal(FindingSeverity.Blocking, result.Findings[0].Severity);
        Assert.Equal(FindingSeverity.Review, result.Findings[^1].Severity);
    }
}
