using EInvoice.Application.Parsing;

namespace EInvoice.Application.Validation;

/// <summary>Runs every validation rule on a parsed document. Blocking findings come first.</summary>
public sealed class InvoiceValidator(IValidationDataSource data)
{
    public async Task<ValidationResult> ValidateAsync(ParsedDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        var findings = new List<ValidationFinding>(DocumentRules.Check(document));

        var vendor = document.SellerVatId is { } vatId
            ? await data.FindVendorByVatIdAsync(vatId, cancellationToken).ConfigureAwait(false)
            : null;

        var numberExists = false;
        var possibleDuplicates = 0;
        if (vendor is not null)
        {
            numberExists = await data.InvoiceNumberExistsAsync(vendor.Id, document.InvoiceNumber, cancellationToken).ConfigureAwait(false);
            possibleDuplicates = await data.CountPossibleDuplicatesAsync(
                vendor.Id, document.GrossTotal, document.IssueDate, document.InvoiceNumber, cancellationToken).ConfigureAwait(false);
        }

        findings.AddRange(MasterDataRules.Check(document, new MasterDataFacts(vendor, numberExists, possibleDuplicates)));

        // OrderByDescending is stable: within a severity the rule order is kept.
        return new ValidationResult(findings.OrderByDescending(f => f.Severity).ToList());
    }
}
