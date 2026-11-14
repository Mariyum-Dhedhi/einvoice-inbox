using System.Text;
using EInvoice.Application.Extraction;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;

namespace EInvoice.Infrastructure.Extraction;

/// <summary>
/// Reads the text of a PDF the way a person reading the page would: only letters that are actually visible, joined
/// into lines from top to bottom and left to right. Letters that are in the file but cannot be seen (white, not
/// rendered, microscopic) are left out and reported, because that is where instructions for a model get hidden.
/// Only text is read: scripts, forms, attachments and links in the PDF are never opened or run.
/// </summary>
public sealed class PdfTextReader(PdfLimits limits) : IPdfTextReader
{
    /// <summary>Smaller than this (in points) cannot be read on paper or screen.</summary>
    private const double SmallestReadableSize = 2.0;

    /// <summary>Colour components at or above this are white on a white page.</summary>
    private const double White = 0.98;

    /// <summary>Letters whose baselines differ by less than this are on the same line.</summary>
    private const double LineTolerance = 2.0;

    private static readonly byte[] Signature = "%PDF-"u8.ToArray();

    public PdfTextResult Read(ReadOnlyMemory<byte> pdf)
    {
        if (!pdf.Span.StartsWith(Signature))
        {
            return PdfTextResult.Failure(ExtractionErrorCode.Unreadable, "The file is not a PDF.");
        }

        try
        {
            // Strict parsing: a damaged file is refused, not "repaired" into something nobody sent.
            using var document = PdfDocument.Open(pdf.ToArray(), new ParsingOptions { UseLenientParsing = false });
            if (document.IsEncrypted)
            {
                return PdfTextResult.Failure(ExtractionErrorCode.Unreadable, "The PDF is encrypted.");
            }

            var pages = document.NumberOfPages;
            if (pages > limits.MaxPages)
            {
                return PdfTextResult.Failure(ExtractionErrorCode.TooLarge, $"The PDF has more than {limits.MaxPages} pages.", pageCount: pages);
            }

            var text = new StringBuilder();
            var hidden = false;
            foreach (var page in document.GetPages())
            {
                var visible = new List<Letter>();
                foreach (var letter in page.Letters)
                {
                    if (IsVisible(letter))
                    {
                        visible.Add(letter);
                    }
                    else if (!string.IsNullOrWhiteSpace(letter.Value))
                    {
                        hidden = true;
                    }
                }

                foreach (var line in Lines(visible))
                {
                    if (text.Length + line.Length > limits.MaxCharacters)
                    {
                        return PdfTextResult.Failure(ExtractionErrorCode.TooLarge, $"The PDF has more than {limits.MaxCharacters} characters of text.", hidden, pages);
                    }

                    text.Append(line).Append('\n');
                }
            }

            return text.Length == 0
                ? PdfTextResult.Failure(ExtractionErrorCode.NoText, "The PDF has no readable text (it may be a scan).", hidden, pages)
                : new PdfTextResult(text.ToString().TrimEnd('\n'), hidden, pages, null);
        }
#pragma warning disable CA1031 // The PDF library throws many exception types for damaged input; every one of them means "unreadable".
        catch (Exception)
#pragma warning restore CA1031
        {
            return PdfTextResult.Failure(ExtractionErrorCode.Unreadable, "The PDF could not be read.");
        }
    }

    private static bool IsVisible(Letter letter)
    {
        if (letter.RenderingMode is TextRenderingMode.Neither or TextRenderingMode.NeitherClip || letter.PointSize < SmallestReadableSize)
        {
            return false;
        }

        if (letter.Color is not { } colour)
        {
            return true;
        }

        var (r, g, b) = colour.ToRGBValues();
        return !(r >= White && g >= White && b >= White);
    }

    /// <summary>Top to bottom, left to right. A gap between letters that is wider than a third of the font size is a space.</summary>
    private static IEnumerable<string> Lines(List<Letter> letters)
    {
        var rows = new List<List<Letter>>();
        foreach (var letter in letters.OrderByDescending(l => l.StartBaseLine.Y))
        {
            if (rows.Count > 0 && Math.Abs(rows[^1][0].StartBaseLine.Y - letter.StartBaseLine.Y) <= LineTolerance)
            {
                rows[^1].Add(letter);
            }
            else
            {
                rows.Add([letter]);
            }
        }

        foreach (var row in rows)
        {
            var line = new StringBuilder();
            Letter? previous = null;
            foreach (var letter in row.OrderBy(l => l.StartBaseLine.X))
            {
                if (previous is not null && !EndsWithSpace(line) && !string.IsNullOrWhiteSpace(letter.Value)
                    && letter.StartBaseLine.X - previous.EndBaseLine.X > letter.PointSize / 3)
                {
                    line.Append(' ');
                }

                line.Append(string.IsNullOrWhiteSpace(letter.Value) ? (EndsWithSpace(line) ? string.Empty : " ") : letter.Value);
                previous = letter;
            }

            var result = line.ToString().Trim();
            if (result.Length > 0)
            {
                yield return result;
            }
        }
    }

    private static bool EndsWithSpace(StringBuilder line) => line.Length == 0 || line[^1] == ' ';
}
