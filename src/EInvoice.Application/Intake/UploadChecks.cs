using System.Globalization;
using System.Text;

namespace EInvoice.Application.Intake;

public enum DetectedContent
{
    Unknown = 0,
    Xml = 1,
    Pdf = 2,
}

/// <summary>
/// Decides the file type from its first bytes. The file name and the content type sent by the browser are
/// chosen by the uploader and are never trusted.
/// </summary>
public static class ContentSniffer
{
    private const int PrefixBytes = 1024;

    public static DetectedContent Detect(ReadOnlySpan<byte> content)
    {
        if (content.StartsWith("%PDF-"u8))
        {
            return DetectedContent.Pdf;
        }

        var prefix = content[..Math.Min(content.Length, PrefixBytes)];
        string text;
        if (prefix.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            text = Encoding.Unicode.GetString(prefix[2..]);
        }
        else if (prefix.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            text = Encoding.BigEndianUnicode.GetString(prefix[2..]);
        }
        else if (prefix.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            text = Encoding.UTF8.GetString(prefix[3..]);
        }
        else
        {
            text = Encoding.UTF8.GetString(prefix);
        }

        // XML may start with whitespace; after that the first character must be '<'.
        return text.TrimStart(' ', '\t', '\r', '\n').StartsWith('<') ? DetectedContent.Xml : DetectedContent.Unknown;
    }
}

/// <summary>
/// The uploaded file name is only kept for display. It is reduced to a plain name: no path, no control characters,
/// no characters that are special in HTML or file systems. It is never used to build a path.
/// </summary>
public static class UploadNames
{
    public const int MaxLength = 255;
    private const string Fallback = "upload";
    private static readonly char[] Replaced = ['<', '>', ':', '"', '|', '?', '*'];

    public static string Sanitize(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return Fallback;
        }

        var name = fileName.Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];

        var cleaned = new StringBuilder(name.Length);
        foreach (var c in name.Where(c => !char.IsControl(c)))
        {
            cleaned.Append(Replaced.Contains(c) ? '_' : c);
        }

        var result = cleaned.ToString().Trim().Trim('.');
        if (result.Length == 0)
        {
            return Fallback;
        }

        return result.Length > MaxLength ? result[..MaxLength] : result;
    }
}

public static class OrderLineReferences
{
    /// <summary>BT-132 is free text; only a positive whole number can be an order line number.</summary>
    public static int? ToLineNumber(string? reference) =>
        int.TryParse(reference?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var line) && line > 0 ? line : null;
}
