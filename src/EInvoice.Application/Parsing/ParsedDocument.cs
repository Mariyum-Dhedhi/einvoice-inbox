namespace EInvoice.Application.Parsing;

/// <summary>Reads an invoice file into <see cref="ParsedDocument"/>. Implementations never throw for bad input; they return a failure.</summary>
public interface IInvoiceDocumentParser
{
    ParseResult Parse(Stream content);
}

public enum DocumentSyntax
{
    /// <summary>OASIS UBL 2.1 Invoice.</summary>
    Ubl = 1,

    /// <summary>UN/CEFACT Cross Industry Invoice (also the XML inside ZUGFeRD).</summary>
    Cii = 2,

    /// <summary>A plain PDF whose fields were extracted by a language model and then checked (package 13). Not an e-invoice syntax.</summary>
    Pdf = 3,
}

/// <summary>
/// The invoice exactly as the supplier stated it, mapped to EN 16931 business terms (BT-n).
/// Nothing here is validated or trusted yet: totals may not add up, the IBAN may be wrong,
/// the seller may be unknown. Validation (package 5) checks it; vendor lookup happens outside the parser.
/// </summary>
public sealed record ParsedDocument(
    DocumentSyntax Syntax,
    string InvoiceTypeCode,                      // BT-3
    string InvoiceNumber,                        // BT-1
    DateOnly IssueDate,                          // BT-2
    string Currency,                             // BT-5
    string? BuyerReference,                      // BT-10 (Leitweg-ID for public buyers)
    string? PurchaseOrderReference,              // BT-13
    string SellerName,                           // BT-27
    string? SellerVatId,                         // BT-31, spaces removed
    IReadOnlyList<string> PaymentAccountIbans,   // BT-84, spaces removed, not yet checked
    decimal SumOfLineNetAmounts,                 // BT-106
    decimal NetTotal,                            // BT-109
    decimal TaxTotal,                            // BT-110
    decimal GrossTotal,                          // BT-112
    IReadOnlyList<ParsedDocumentLine> Lines,
    IReadOnlyList<VatBreakdownEntry> VatBreakdown); // BG-23

/// <param name="Position">1, 2, 3 ... in document order. Line ids (BT-126) are free text, so they cannot be the line number.</param>
/// <param name="UnitPrice">Net price (BT-146) divided by base quantity (BT-149), at full precision.</param>
/// <param name="PurchaseOrderLineReference">BT-132 as stated; free text, mapped to an order line later.</param>
public sealed record ParsedDocumentLine(
    int Position,
    string LineId,                               // BT-126
    string ItemName,                             // BT-153
    decimal Quantity,                            // BT-129
    decimal UnitPrice,
    decimal NetAmount,                           // BT-131
    decimal TaxRatePercent,                      // BT-152, 0 when not stated
    string? PurchaseOrderLineReference,          // BT-132
    decimal LineAllowances,                      // sum of BG-27 (BT-136), not price discounts (BT-147)
    decimal LineCharges);                        // sum of BG-28 (BT-141)

/// <summary>One VAT category of the invoice (BG-23).</summary>
public sealed record VatBreakdownEntry(
    string CategoryCode,                         // BT-118, for example S (standard), AE (reverse charge), E (exempt)
    decimal RatePercent,                         // BT-119, 0 when not stated
    decimal TaxableAmount,                       // BT-116
    decimal TaxAmount);                          // BT-117

public enum ParseErrorCode
{
    TooLarge = 1,
    Malformed = 2,
    DtdNotAllowed = 3,
    UnsupportedDocument = 4,
    MissingField = 5,
    InvalidValue = 6,
}

/// <param name="Message">Safe to log and show: names the business term, never repeats document content.</param>
public sealed record ParseError(ParseErrorCode Code, string Message);

public sealed record ParseResult
{
    private ParseResult(ParsedDocument? document, ParseError? error)
    {
        Document = document;
        Error = error;
    }

    public ParsedDocument? Document { get; }

    public ParseError? Error { get; }

    public bool Succeeded => Document is not null;

    public static ParseResult Success(ParsedDocument document) => new(document ?? throw new ArgumentNullException(nameof(document)), null);

    public static ParseResult Failure(ParseErrorCode code, string message) => new(null, new ParseError(code, message));
}
