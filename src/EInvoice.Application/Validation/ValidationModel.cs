using EInvoice.Application.Parsing;
using EInvoice.Domain.Shared;

namespace EInvoice.Application.Validation;

/// <summary>Stable rule codes. They are stored with exception reasons and shown in the UI: never rename one.</summary>
public static class RuleCodes
{
    public const string Conformance = "CONFORMANCE";
    public const string ConformanceUnavailable = "CONFORMANCE_UNAVAILABLE";
    public const string LineSum = "LINE_SUM";
    public const string Totals = "TOTALS";
    public const string LineAmount = "LINE_AMOUNT";
    public const string VatBreakdown = "VAT_BREAKDOWN";
    public const string IbanChecksum = "IBAN_CHECKSUM";
    public const string IbanNotApproved = "IBAN_NOT_APPROVED";
    public const string VendorUnknown = "VENDOR_UNKNOWN";
    public const string VendorInactive = "VENDOR_INACTIVE";
    public const string DuplicateNumber = "DUPLICATE_NUMBER";
    public const string PossibleDuplicate = "POSSIBLE_DUPLICATE";
}

public enum FindingSeverity
{
    /// <summary>A human looks at it and may approve.</summary>
    Review = 1,

    /// <summary>The document cannot be paid as it is; the approver can only reject it.</summary>
    Blocking = 2,
}

/// <param name="Message">Safe to show and log: may contain amounts and master data, never free text from the document.</param>
/// <param name="LinePosition">The invoice line the finding is about, if any.</param>
public sealed record ValidationFinding(string RuleCode, FindingSeverity Severity, string Message, int? LinePosition = null);

public sealed record ValidationResult(IReadOnlyList<ValidationFinding> Findings)
{
    public bool IsValid => Findings.Count == 0;

    public bool HasBlocking => Findings.Any(f => f.Severity == FindingSeverity.Blocking);
}

/// <summary>What validation needs to know about the vendor; loaded once per invoice.</summary>
public sealed record VendorSnapshot(Guid Id, string Number, bool IsActive, IReadOnlyCollection<Iban> ApprovedIbans);

/// <param name="Vendor">Null when the seller VAT id is missing or matches no vendor.</param>
public sealed record MasterDataFacts(VendorSnapshot? Vendor, bool InvoiceNumberExists, int PossibleDuplicates);

/// <summary>Master data lookups for validation. Implemented with EF Core in Infrastructure.</summary>
public interface IValidationDataSource
{
    Task<VendorSnapshot?> FindVendorByVatIdAsync(string vatId, CancellationToken cancellationToken);

    Task<bool> InvoiceNumberExistsAsync(Guid vendorId, string invoiceNumber, CancellationToken cancellationToken);

    /// <summary>Invoices of the vendor with the same gross amount and issue date but a different number.</summary>
    Task<int> CountPossibleDuplicatesAsync(Guid vendorId, decimal grossTotal, DateOnly issueDate, string invoiceNumber, CancellationToken cancellationToken);
}

internal static class Findings
{
    public static ValidationFinding Review(string code, string message, int? line = null) => new(code, FindingSeverity.Review, message, line);

    public static ValidationFinding Blocking(string code, string message, int? line = null) => new(code, FindingSeverity.Blocking, message, line);

    public static string Amount(decimal value) => value.ToString("0.00##", System.Globalization.CultureInfo.InvariantCulture);
}
