using EInvoice.Application.Parsing;
using EInvoice.Domain.Shared;
using static EInvoice.Application.Validation.Findings;

namespace EInvoice.Application.Validation;

/// <summary>Rules that compare the document with master data. Pure: the facts are loaded by <see cref="InvoiceValidator"/>.</summary>
public static class MasterDataRules
{
    public static IReadOnlyList<ValidationFinding> Check(ParsedDocument document, MasterDataFacts facts)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(facts);

        // VENDOR_UNKNOWN: without a vendor the other rules have nothing to compare with.
        if (facts.Vendor is not { } vendor)
        {
            return [Blocking(RuleCodes.VendorUnknown, "The seller VAT identifier (BT-31) is missing or does not belong to any vendor.")];
        }

        var findings = new List<ValidationFinding>();

        // VENDOR_INACTIVE
        if (!vendor.IsActive)
        {
            findings.Add(Blocking(RuleCodes.VendorInactive, $"Vendor {vendor.Number} is inactive."));
        }

        // IBAN_NOT_APPROVED: bank-detail fraud control. Invalid IBANs are reported by IBAN_CHECKSUM, not twice.
        var approved = vendor.ApprovedIbans.Select(i => i.Value).ToHashSet(StringComparer.Ordinal);
        foreach (var (text, n) in document.PaymentAccountIbans.Select((v, i) => (v, i + 1)))
        {
            if (Iban.TryParse(text, out var iban) && !approved.Contains(iban.Value))
            {
                findings.Add(Review(RuleCodes.IbanNotApproved,
                    $"Payment account {n} (BT-84, {iban}) is not an approved bank account of vendor {vendor.Number}."));
            }
        }

        // DUPLICATE_NUMBER
        if (facts.InvoiceNumberExists)
        {
            findings.Add(Blocking(RuleCodes.DuplicateNumber, $"Vendor {vendor.Number} already has an invoice with this number."));
        }

        // POSSIBLE_DUPLICATE
        if (facts.PossibleDuplicates > 0)
        {
            findings.Add(Review(RuleCodes.PossibleDuplicate,
                $"{facts.PossibleDuplicates} other invoice(s) from vendor {vendor.Number} have the same gross amount and issue date."));
        }

        return findings;
    }
}
