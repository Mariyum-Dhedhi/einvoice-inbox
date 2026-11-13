using EInvoice.Application.Parsing;
using EInvoice.Application.Validation;
using EInvoice.Domain.Shared;

namespace EInvoice.UnitTests.Validation;

/// <summary>A small, internally consistent synthetic invoice that passes every rule; tests break one thing at a time.</summary>
internal static class TestDocuments
{
    public const string VatId = "DE000000001";
    public const string ApprovedIban = "DE72000000000000001001";
    public static readonly Guid VendorId = Guid.Parse("00000000-0000-0000-0001-000000000001");

    public static ParsedDocument Valid() => new(
        DocumentSyntax.Ubl,
        InvoiceTypeCode: "380",
        InvoiceNumber: "RE-2026-0100",
        IssueDate: new DateOnly(2026, 11, 2),
        Currency: "EUR",
        BuyerReference: null,
        PurchaseOrderReference: "PO-0001",
        SellerName: "Beispiel Bürobedarf GmbH",
        SellerVatId: VatId,
        PaymentAccountIbans: [ApprovedIban],
        SumOfLineNetAmounts: 200.00m,
        NetTotal: 200.00m,
        TaxTotal: 38.00m,
        GrossTotal: 238.00m,
        Lines:
        [
            Line(1, quantity: 2m, unitPrice: 50m, net: 100.00m),
            Line(2, quantity: 3m, unitPrice: 33.333333m, net: 100.00m), // 99.999999 rounds to 100.00
        ],
        VatBreakdown: [new VatBreakdownEntry("S", 19m, 200.00m, 38.00m)]);

    public static ParsedDocumentLine Line(int position, decimal quantity, decimal unitPrice, decimal net,
        decimal allowances = 0m, decimal charges = 0m, decimal rate = 19m) =>
        new(position, position.ToString(System.Globalization.CultureInfo.InvariantCulture), $"Item {position}",
            quantity, unitPrice, net, rate, null, LineAllowances: allowances, LineCharges: charges);

    public static VendorSnapshot ActiveVendor(params string[] ibans) =>
        new(VendorId, "V-1001", IsActive: true, (ibans.Length == 0 ? [ApprovedIban] : ibans).Select(Iban.Parse).ToArray());

    public static MasterDataFacts CleanFacts() => new(ActiveVendor(), InvoiceNumberExists: false, PossibleDuplicates: 0);

    public static IReadOnlyList<string> Codes(this IEnumerable<ValidationFinding> findings) =>
        findings.Select(f => f.RuleCode).ToList();
}
