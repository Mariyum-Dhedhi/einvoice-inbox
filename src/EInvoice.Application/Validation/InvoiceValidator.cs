using EInvoice.Application.Parsing;
using static EInvoice.Application.Validation.Findings;

namespace EInvoice.Application.Validation;

/// <summary>Runs every validation rule on a parsed document. Blocking findings come first.</summary>
public sealed class InvoiceValidator(IValidationDataSource data, IConformanceValidator conformance)
{
    /// <param name="sourceXml">
    /// The original XML for the KoSIT conformance check. Null for documents that have no XML (plain PDFs read by AI);
    /// for those the document rules still recompute every total.
    /// </param>
    public async Task<ValidationResult> ValidateAsync(ParsedDocument document, ReadOnlyMemory<byte>? sourceXml = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        var findings = new List<ValidationFinding>();

        // CONFORMANCE / CONFORMANCE_UNAVAILABLE: fail closed. A validator that cannot be reached sends the invoice to a human.
        if (sourceXml is { } xml)
        {
            var check = await conformance.CheckAsync(xml, cancellationToken).ConfigureAwait(false);
            switch (check.Outcome)
            {
                case ConformanceOutcome.Rejected:
                    findings.Add(Blocking(RuleCodes.Conformance, check.ErrorCodes.Count == 0
                        ? "The KoSIT validator rejected the document (EN 16931 / XRechnung 3.0.2)."
                        : $"The KoSIT validator rejected the document (EN 16931 / XRechnung 3.0.2). Failed rules: {string.Join(", ", check.ErrorCodes)}."));
                    break;
                case ConformanceOutcome.Unavailable:
                    findings.Add(Review(RuleCodes.ConformanceUnavailable,
                        "The KoSIT validator could not be reached, so EN 16931 / XRechnung conformance was not checked."));
                    break;
            }
        }

        findings.AddRange(DocumentRules.Check(document));

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
