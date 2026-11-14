using System.Text;
using EInvoice.Application.Intake;

namespace EInvoice.UnitTests.Intake;

/// <summary>
/// Checks on an upload before anything is parsed or stored. The type is decided from the bytes,
/// never from the file name or the content type the browser claims.
/// </summary>
public class UploadCheckTests
{
    [Theory]
    [InlineData("<?xml version=\"1.0\"?><Invoice/>")]
    [InlineData("<Invoice/>")]
    [InlineData("  \r\n\t<Invoice/>")]                 // leading whitespace
    [InlineData("\uFEFF<?xml version=\"1.0\"?><a/>")] // UTF-8 byte order mark
    public void Xml_is_detected_from_the_content(string text)
    {
        Assert.Equal(DetectedContent.Xml, ContentSniffer.Detect(Encoding.UTF8.GetBytes(text)));
    }

    [Fact]
    public void Utf16_xml_with_byte_order_mark_is_detected()
    {
        Assert.Equal(DetectedContent.Xml, ContentSniffer.Detect(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("<Invoice/>")).ToArray()));
    }

    [Fact]
    public void Pdf_is_detected_from_its_signature()
    {
        Assert.Equal(DetectedContent.Pdf, ContentSniffer.Detect("%PDF-1.7\n%âãÏÓ"u8.ToArray()));
    }

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })] // PNG
    [InlineData(new byte[] { 0x4D, 0x5A, 0x90, 0x00 })]                          // Windows executable
    [InlineData(new byte[] { 0x50, 0x4B, 0x03, 0x04 })]                          // ZIP
    public void Other_binary_content_is_unknown(byte[] content)
    {
        Assert.Equal(DetectedContent.Unknown, ContentSniffer.Detect(content));
    }

    [Theory]
    [InlineData("Hello, this is an invoice")]
    [InlineData("")]
    [InlineData("   ")]
    public void Plain_text_and_empty_content_are_unknown(string text)
    {
        Assert.Equal(DetectedContent.Unknown, ContentSniffer.Detect(Encoding.UTF8.GetBytes(text)));
    }

    [Theory]
    [InlineData("invoice.xml", "invoice.xml")]
    [InlineData(@"C:\Users\clerk\Desktop\invoice.xml", "invoice.xml")]   // path from old browsers
    [InlineData("../../etc/passwd", "passwd")]                          // path traversal
    [InlineData("in\u0000vo\u001Fice.xml", "invoice.xml")]               // control characters
    [InlineData("<script>.xml", "_script_.xml")]
    [InlineData("", "upload")]
    [InlineData(null, "upload")]
    [InlineData("..", "upload")]
    public void File_names_are_reduced_to_a_safe_display_name(string? input, string expected)
    {
        Assert.Equal(expected, UploadNames.Sanitize(input));
    }

    [Fact]
    public void Long_file_names_are_cut_to_255_characters()
    {
        Assert.Equal(255, UploadNames.Sanitize(new string('a', 400) + ".xml").Length);
    }

    [Theory]
    [InlineData("2", 2)]
    [InlineData(" 3 ", 3)]
    [InlineData("6171175.1", null)] // free text in BT-132: not an order line number
    [InlineData("A1", null)]
    [InlineData("0", null)]
    [InlineData("-1", null)]
    [InlineData(null, null)]
    public void Order_line_references_become_line_numbers_only_when_they_are_positive_integers(string? reference, int? expected)
    {
        Assert.Equal(expected, OrderLineReferences.ToLineNumber(reference));
    }
}
