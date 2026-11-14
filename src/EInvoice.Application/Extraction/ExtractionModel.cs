using EInvoice.Application.Parsing;

namespace EInvoice.Application.Extraction;

/// <summary>
/// Reads a plain PDF invoice into the same <see cref="ParsedDocument"/> the XML parsers produce, with a confidence
/// per field. Implementations never throw for bad input and never decide anything: the result is checked by the
/// same validation rules and the same matching as every other invoice.
/// </summary>
public interface IInvoiceExtractor
{
    Task<ExtractionResult> ExtractAsync(ReadOnlyMemory<byte> pdf, CancellationToken cancellationToken);
}

public enum ExtractionErrorCode
{
    /// <summary>Not a PDF, damaged, or encrypted.</summary>
    Unreadable = 1,

    /// <summary>The PDF has no visible text (for example a scan); there is nothing to extract from.</summary>
    NoText = 2,

    TooLarge = 3,

    /// <summary>The model could not be reached or did not answer in time. Never replaced by a guess.</summary>
    ModelUnavailable = 4,

    /// <summary>The model's reply was not the expected JSON, or held a value that cannot be an invoice value.</summary>
    InvalidModelOutput = 5,

    /// <summary>A value an invoice must have was not found.</summary>
    MissingField = 6,
}

/// <param name="Message">Safe to log and show: names the business term, never repeats document or model text.</param>
public sealed record ExtractionError(ExtractionErrorCode Code, string Message);

/// <summary>Whether one extracted value is backed by the document.</summary>
/// <param name="Term">EN 16931 business term, for example BT-112.</param>
/// <param name="LinePosition">The invoice line (1, 2, ...) for line terms; null for header terms.</param>
/// <param name="Reason">Why the value is not confident; null when it is. Never repeats the value.</param>
public sealed record FieldConfidence(string Term, int? LinePosition, bool Confident, string? Reason);

/// <summary>Holds a document, its field confidences and a flag. There is deliberately nothing here that could carry a decision.</summary>
public sealed class ExtractionResult
{
    private ExtractionResult(ParsedDocument? document, ExtractionError? error, IReadOnlyList<FieldConfidence> fields, bool hiddenTextFound)
    {
        Document = document;
        Error = error;
        Fields = fields;
        HiddenTextFound = hiddenTextFound;
    }

    public ParsedDocument? Document { get; }

    public ExtractionError? Error { get; }

    public IReadOnlyList<FieldConfidence> Fields { get; }

    /// <summary>The PDF contains text that cannot be seen on the page. It was not given to the model; a person should look at the file.</summary>
    public bool HiddenTextFound { get; }

    public bool Succeeded => Document is not null;

    /// <summary>Fields a person has to confirm.</summary>
    public IReadOnlyList<FieldConfidence> LowConfidenceFields => Fields.Where(f => !f.Confident).ToList();

    public static ExtractionResult Success(ParsedDocument document, IReadOnlyList<FieldConfidence> fields, bool hiddenTextFound) =>
        new(document ?? throw new ArgumentNullException(nameof(document)), null, fields ?? throw new ArgumentNullException(nameof(fields)), hiddenTextFound);

    public static ExtractionResult Failure(ExtractionError error, bool hiddenTextFound = false) =>
        new(null, error ?? throw new ArgumentNullException(nameof(error)), [], hiddenTextFound);
}

/// <summary>Gets the text a person would read out of a PDF. Implemented with a PDF library in Infrastructure.</summary>
public interface IPdfTextReader
{
    PdfTextResult Read(ReadOnlyMemory<byte> pdf);
}

/// <param name="Text">Visible text, one line per text line, pages in order.</param>
/// <param name="HiddenTextFound">Text that is in the file but not visible on the page was found and left out.</param>
public sealed record PdfTextResult(string? Text, bool HiddenTextFound, int PageCount, ExtractionError? Error)
{
    public bool Succeeded => Error is null && Text is not null;

    public static PdfTextResult Failure(ExtractionErrorCode code, string message, bool hiddenTextFound = false, int pageCount = 0) =>
        new(null, hiddenTextFound, pageCount, new ExtractionError(code, message));
}

public sealed record PdfLimits(int MaxPages, int MaxCharacters)
{
    /// <summary>An invoice is a few pages; anything far beyond that is not sent to a model.</summary>
    public static PdfLimits Default { get; } = new(MaxPages: 20, MaxCharacters: 60_000);
}
