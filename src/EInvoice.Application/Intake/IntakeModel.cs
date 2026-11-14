using EInvoice.Application.Validation;
using EInvoice.Domain.Invoices;

namespace EInvoice.Application.Intake;

/// <param name="FileName">As sent by the uploader; only used, sanitised, for display.</param>
public sealed record UploadedFile(string? FileName, byte[] Content);

public enum IntakeOutcome
{
    /// <summary>The invoice exists now, in whatever status the pipeline reached (Matched, Exception or Failed).</summary>
    Created = 1,
    Empty = 2,
    TooLarge = 3,
    UnsupportedType = 4,

    /// <summary>The same file was uploaded before; nothing new is stored.</summary>
    DuplicateFile = 5,

    /// <summary>The vendor already has an invoice with this number; nothing is stored.</summary>
    DuplicateInvoiceNumber = 6,
}

/// <param name="InvoiceId">The new invoice, or for <see cref="IntakeOutcome.DuplicateFile"/> the existing one.</param>
/// <param name="Detail">A safe explanation for refusals.</param>
public sealed record IntakeResult(IntakeOutcome Outcome, Guid? InvoiceId = null, InvoiceStatus? Status = null, string? Detail = null);

public sealed record IntakeOptions(long MaxDocumentBytes)
{
    public static IntakeOptions Default { get; } = new(MaxDocumentBytes: 20 * 1024 * 1024);
}

/// <summary>Persistence for the upload pipeline. The unique indexes behind it are the backstop for parallel uploads.</summary>
public interface IIntakeStore
{
    Task<Guid?> FindInvoiceIdByDocumentHashAsync(byte[] sha256, CancellationToken cancellationToken);

    /// <summary>Stores a new invoice with its original file and findings in one transaction.</summary>
    /// <exception cref="DuplicateDocumentException">The same file was stored in the meantime.</exception>
    /// <exception cref="DuplicateInvoiceNumberException">The vendor's invoice number was stored in the meantime.</exception>
    Task AddAsync(Invoice invoice, InvoiceDocument document, IReadOnlyList<ValidationFinding> findings, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public sealed class DuplicateDocumentException : Exception
{
    public DuplicateDocumentException()
        : base("The same file has already been stored.")
    {
    }
}

public sealed class DuplicateInvoiceNumberException : Exception
{
    public DuplicateInvoiceNumberException()
        : base("The vendor already has an invoice with this number.")
    {
    }
}

/// <summary>The upload pipeline as the API and the UI see it; implemented by <see cref="InvoiceIntake"/>.</summary>
public interface IInvoiceIntake
{
    Task<IntakeResult> SubmitAsync(UploadedFile file, string submittedBy, CancellationToken cancellationToken);
}
