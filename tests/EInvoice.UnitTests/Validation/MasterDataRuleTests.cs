using EInvoice.Application.Validation;
using static EInvoice.UnitTests.Validation.TestDocuments;

namespace EInvoice.UnitTests.Validation;

/// <summary>Rules that compare the document with master data: vendor, approved bank accounts, duplicates.</summary>
public class MasterDataRuleTests
{
    [Fact]
    public void Known_active_vendor_with_approved_iban_and_new_number_passes()
    {
        Assert.Empty(MasterDataRules.Check(Valid(), CleanFacts()));
    }

    // VENDOR_UNKNOWN
    [Fact]
    public void Unknown_vendor_is_blocking_and_skips_the_vendor_specific_rules()
    {
        var findings = MasterDataRules.Check(Valid(), CleanFacts() with { Vendor = null });

        var finding = Assert.Single(findings);
        Assert.Equal(RuleCodes.VendorUnknown, finding.RuleCode);
        Assert.Equal(FindingSeverity.Blocking, finding.Severity);
    }

    [Fact]
    public void Missing_seller_vat_id_is_an_unknown_vendor()
    {
        var findings = MasterDataRules.Check(Valid() with { SellerVatId = null }, CleanFacts() with { Vendor = null });

        Assert.Equal([RuleCodes.VendorUnknown], findings.Codes());
    }

    [Fact]
    public void Unknown_vendor_message_does_not_echo_the_stated_vat_id()
    {
        var finding = Assert.Single(MasterDataRules.Check(Valid() with { SellerVatId = "<script>x</script>" }, CleanFacts() with { Vendor = null }));

        Assert.DoesNotContain("script", finding.Message, StringComparison.OrdinalIgnoreCase);
    }

    // VENDOR_INACTIVE
    [Fact]
    public void Inactive_vendor_is_blocking()
    {
        var facts = CleanFacts() with { Vendor = ActiveVendor() with { IsActive = false } };

        var finding = Assert.Single(MasterDataRules.Check(Valid(), facts));
        Assert.Equal(RuleCodes.VendorInactive, finding.RuleCode);
        Assert.Equal(FindingSeverity.Blocking, finding.Severity);
    }

    // IBAN_NOT_APPROVED
    [Fact]
    public void Iban_that_is_not_an_approved_account_of_the_vendor_needs_review()
    {
        var facts = CleanFacts() with { Vendor = ActiveVendor("DE45000000000000001002") };

        var finding = Assert.Single(MasterDataRules.Check(Valid(), facts));
        Assert.Equal(RuleCodes.IbanNotApproved, finding.RuleCode);
        Assert.Equal(FindingSeverity.Review, finding.Severity);
        Assert.Contains("V-1001", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Iban_comparison_ignores_spaces_and_case()
    {
        var doc = Valid() with { PaymentAccountIbans = ["de72 0000 0000 0000 0010 01"] };

        Assert.Empty(MasterDataRules.Check(doc, CleanFacts()));
    }

    [Fact]
    public void Every_stated_iban_must_be_approved()
    {
        var doc = Valid() with { PaymentAccountIbans = [ApprovedIban, "DE45000000000000001002"] };

        Assert.Equal([RuleCodes.IbanNotApproved], MasterDataRules.Check(doc, CleanFacts()).Codes());
    }

    [Fact]
    public void Invalid_iban_is_left_to_the_checksum_rule_and_not_reported_twice()
    {
        var doc = Valid() with { PaymentAccountIbans = ["DE72000000000000001002"] };

        Assert.Empty(MasterDataRules.Check(doc, CleanFacts()));
    }

    // DUPLICATE_NUMBER
    [Fact]
    public void Existing_invoice_number_for_the_vendor_is_blocking()
    {
        var finding = Assert.Single(MasterDataRules.Check(Valid(), CleanFacts() with { InvoiceNumberExists = true }));

        Assert.Equal(RuleCodes.DuplicateNumber, finding.RuleCode);
        Assert.Equal(FindingSeverity.Blocking, finding.Severity);
    }

    // POSSIBLE_DUPLICATE
    [Fact]
    public void Same_vendor_amount_and_date_under_another_number_needs_review()
    {
        var finding = Assert.Single(MasterDataRules.Check(Valid(), CleanFacts() with { PossibleDuplicates = 1 }));

        Assert.Equal(RuleCodes.PossibleDuplicate, finding.RuleCode);
        Assert.Equal(FindingSeverity.Review, finding.Severity);
    }

    [Fact]
    public void Several_problems_are_all_reported()
    {
        var facts = new MasterDataFacts(ActiveVendor("DE45000000000000001002") with { IsActive = false }, InvoiceNumberExists: true, PossibleDuplicates: 2);

        Assert.Equal(
            new[] { RuleCodes.VendorInactive, RuleCodes.IbanNotApproved, RuleCodes.DuplicateNumber, RuleCodes.PossibleDuplicate }.Order(),
            MasterDataRules.Check(Valid(), facts).Codes().Order());
    }
}
