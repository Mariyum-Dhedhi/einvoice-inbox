namespace EInvoice.Domain.Invoices;

/// <summary>
/// The original uploaded file, stored unchanged. Kept for audit and shown next to the extracted data;
/// never executed and never served with a content type that a browser would render.
/// </summary>
public sealed class InvoiceDocument
{
    public InvoiceDocument(Guid invoiceId, string fileName, string contentType, byte[] content, byte[] sha256, DateTimeOffset receivedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(sha256);
        if (sha256.Length != 32)
        {
            throw new ArgumentException("A SHA-256 hash has 32 bytes.", nameof(sha256));
        }

        InvoiceId = invoiceId;
        FileName = fileName;
        ContentType = contentType;
        Content = content;
        Sha256 = sha256;
        SizeBytes = content.Length;
        ReceivedAt = receivedAt;
    }

    /// <summary>For EF Core when loading from the database.</summary>
    private InvoiceDocument()
    {
        FileName = null!;
        ContentType = null!;
        Content = null!;
        Sha256 = null!;
    }

    public Guid InvoiceId { get; }

    /// <summary>Sanitised display name only; never used as a path.</summary>
    public string FileName { get; }

    /// <summary>Detected from the content, not taken from the upload.</summary>
    public string ContentType { get; }

    public byte[] Content { get; }

    public byte[] Sha256 { get; }

    public int SizeBytes { get; }

    public DateTimeOffset ReceivedAt { get; }
}
