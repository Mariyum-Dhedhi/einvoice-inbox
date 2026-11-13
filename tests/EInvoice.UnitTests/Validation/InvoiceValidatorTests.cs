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

    [Fact]
    public async Task Clean_invoice_has_no_findings()
    {
        var result = await new InvoiceValidator(new FakeData()).ValidateAsync(Valid());

        Assert.True(result.IsValid);
        Assert.False(result.HasBlocking);
    }

    [Fact]
    public async Task Document_and_master_data_findings_are_combined()
    {
        var data = new FakeData { NumberExists = true };

        var result = await new InvoiceValidator(data).ValidateAsync(Valid() with { GrossTotal = 1m });

        Assert.Equal(new[] { RuleCodes.Totals, RuleCodes.DuplicateNumber }.Order(), result.Findings.Codes().Order());
        Assert.True(result.HasBlocking);
    }

    [Fact]
    public async Task Without_a_vat_id_no_vendor_lookup_is_made()
    {
        var data = new FakeData();

        var result = await new InvoiceValidator(data).ValidateAsync(Valid() with { SellerVatId = null });

        Assert.Empty(data.Calls);
        Assert.Equal([RuleCodes.VendorUnknown], result.Findings.Codes());
    }

    [Fact]
    public async Task Unknown_vendor_skips_the_duplicate_lookups()
    {
        var data = new FakeData { Vendor = null };

        await new InvoiceValidator(data).ValidateAsync(Valid());

        Assert.Equal([$"vendor:{VatId}"], data.Calls);
    }

    [Fact]
    public async Task Blocking_findings_are_listed_before_review_findings()
    {
        var data = new FakeData { Duplicates = 1, NumberExists = true };

        var result = await new InvoiceValidator(data).ValidateAsync(Valid());

        Assert.Equal(FindingSeverity.Blocking, result.Findings[0].Severity);
        Assert.Equal(FindingSeverity.Review, result.Findings[^1].Severity);
    }
}
