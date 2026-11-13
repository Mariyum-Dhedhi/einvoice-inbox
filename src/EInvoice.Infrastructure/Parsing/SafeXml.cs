using System.Text;
using System.Xml;
using System.Xml.Linq;
using EInvoice.Application.Parsing;

namespace EInvoice.Infrastructure.Parsing;

/// <param name="MaxDocumentBytes">Largest file accepted. KoSIT samples with attachments are about 1 MB.</param>
public sealed record XmlLimits(long MaxDocumentBytes)
{
    public static XmlLimits Default { get; } = new(MaxDocumentBytes: 20 * 1024 * 1024);
}

/// <summary>
/// The only place in the application where XML is read. Every setting below closes a known attack:
/// <list type="bullet">
/// <item>DtdProcessing.Prohibit: any DOCTYPE is an error, which stops XXE (file read, SSRF) and entity-expansion bombs.</item>
/// <item>XmlResolver = null: nothing external (DTD, schema, URL) is ever fetched.</item>
/// <item>Byte limit before parsing and MaxCharactersInDocument: oversized input cannot exhaust memory.</item>
/// <item>MaxCharactersFromEntities: a small non-zero cap (0 would mean "unlimited" in .NET).</item>
/// <item>Comments and processing instructions are dropped; they carry no invoice data.</item>
/// </list>
/// Errors never include document content, so they are safe to log and show.
/// </summary>
internal static class SafeXml
{
    public static XDocument Load(Stream content, XmlLimits limits)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(limits);

        var bytes = ReadAtMost(content, limits.MaxDocumentBytes);

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = limits.MaxDocumentBytes,
            MaxCharactersFromEntities = 1024,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            CloseInput = true,
        };

        try
        {
            using var reader = XmlReader.Create(new MemoryStream(bytes, writable: false), settings);
            return XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException)
        {
            // The exception text is not used (it can quote the input and is localised).
            // The refusal itself never depends on this classification; it only picks the error code.
            throw ContainsDoctype(bytes)
                ? new DocumentRejectedException(ParseErrorCode.DtdNotAllowed, "The document contains a DOCTYPE declaration, which is not allowed.")
                : new DocumentRejectedException(ParseErrorCode.Malformed, "The document is not well-formed XML.");
        }
    }

    /// <summary>Reads into memory but stops one byte past the limit, so an oversized upload is never read in full.</summary>
    private static byte[] ReadAtMost(Stream content, long maxBytes)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = content.Read(chunk, 0, chunk.Length)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > maxBytes)
            {
                throw new DocumentRejectedException(ParseErrorCode.TooLarge, $"The document is larger than the limit of {maxBytes} bytes.");
            }
        }

        if (buffer.Length == 0)
        {
            throw new DocumentRejectedException(ParseErrorCode.Malformed, "The document is empty.");
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Classifies a failure only. Decodes with byte-order-mark detection (the XML rules for UTF-16) and
    /// looks for the keyword, which is case-sensitive in XML.
    /// </summary>
    private static bool ContainsDoctype(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes, writable: false), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd().Contains("<!DOCTYPE", StringComparison.Ordinal);
    }
}

/// <summary>Raised inside the parser for any input it refuses; converted to a <see cref="ParseResult"/> failure at the boundary.</summary>
internal sealed class DocumentRejectedException(ParseErrorCode code, string message) : Exception(message)
{
    public ParseErrorCode Code { get; } = code;
}
