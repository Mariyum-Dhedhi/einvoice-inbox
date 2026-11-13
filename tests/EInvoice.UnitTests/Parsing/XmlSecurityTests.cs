using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using EInvoice.Application.Parsing;
using EInvoice.Infrastructure.Parsing;

namespace EInvoice.UnitTests.Parsing;

/// <summary>
/// Untrusted XML must never make the server read files, call URLs, or exhaust memory,
/// and error messages must never repeat the input.
/// </summary>
public class XmlSecurityTests
{
    private static readonly XRechnungParser Parser = new();

    private static ParseResult ParseFile(string fileName)
    {
        using var stream = File.OpenRead(Path.Combine(TestPaths.MaliciousCorpus, fileName));
        return Parser.Parse(stream);
    }

    private static ParseResult ParseText(string xml, XRechnungParser? parser = null) =>
        (parser ?? Parser).Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

    [Theory]
    [InlineData("xxe-local-file.xml")]
    [InlineData("xxe-network.xml")]
    [InlineData("billion-laughs.xml")]
    [InlineData("external-dtd.xml")]
    public void Any_DOCTYPE_is_refused(string fileName)
    {
        var result = ParseFile(fileName);

        Assert.False(result.Succeeded);
        Assert.Equal(ParseErrorCode.DtdNotAllowed, result.Error!.Code);
    }

    [Theory]
    [InlineData("malformed.xml", ParseErrorCode.Malformed)]
    [InlineData("not-an-invoice.xml", ParseErrorCode.UnsupportedDocument)]
    [InlineData("credit-note.xml", ParseErrorCode.UnsupportedDocument)]
    public void Bad_or_unsupported_documents_are_refused_with_a_reason(string fileName, ParseErrorCode expected)
    {
        var result = ParseFile(fileName);

        Assert.False(result.Succeeded);
        Assert.Equal(expected, result.Error!.Code);
    }

    [Fact]
    public void Xxe_cannot_read_a_local_file()
    {
        var secretFile = Path.GetTempFileName();
        const string Secret = "SECRET-MARKER-7f3a9c";
        File.WriteAllText(secretFile, Secret);
        try
        {
            // A real attack: an otherwise complete, valid invoice whose buyer reference is an external entity.
            // A vulnerable parser would succeed and return the secret file's content as BT-10.
            var valid = File.ReadAllText(Path.Combine(TestPaths.KositCorpus, "01.01a-INVOICE_ubl.xml"));
            var xml = valid
                .Replace(
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?>",
                    $"<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<!DOCTYPE ubl:Invoice [<!ENTITY xxe SYSTEM \"{new Uri(secretFile).AbsoluteUri}\">]>",
                    StringComparison.Ordinal)
                .Replace("<cbc:BuyerReference>04011000-12345-03</cbc:BuyerReference>", "<cbc:BuyerReference>&xxe;</cbc:BuyerReference>", StringComparison.Ordinal);
            Assert.Contains("&xxe;", xml, StringComparison.Ordinal);

            var result = ParseText(xml);

            Assert.False(result.Succeeded, "The parser accepted a document with an external entity.");
            Assert.Equal(ParseErrorCode.DtdNotAllowed, result.Error!.Code);
            Assert.DoesNotContain(Secret, result.Error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(secretFile);
        }
    }

    [Fact]
    public async Task Xxe_cannot_make_a_network_request()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var xml = $"""
            <?xml version="1.0"?>
            <!DOCTYPE ubl:Invoice SYSTEM "http://127.0.0.1:{port}/invoice.dtd">
            <ubl:Invoice xmlns:ubl="urn:oasis:names:specification:ubl:schema:xsd:Invoice-2"/>
            """;

        var result = ParseText(xml);
        var accept = listener.AcceptTcpClientAsync();
        var connected = await Task.WhenAny(accept, Task.Delay(500)) == accept;

        Assert.False(result.Succeeded);
        Assert.False(connected, "The parser opened a network connection while reading untrusted XML.");
    }

    [Fact]
    public void Document_over_the_size_limit_is_refused_without_reading_it_all()
    {
        var parser = new XRechnungParser(new XmlLimits(MaxDocumentBytes: 1024));
        var padding = new string(' ', 2048);
        var xml = $"<ubl:Invoice xmlns:ubl=\"urn:oasis:names:specification:ubl:schema:xsd:Invoice-2\">{padding}</ubl:Invoice>";

        var result = ParseText(xml, parser);

        Assert.False(result.Succeeded);
        Assert.Equal(ParseErrorCode.TooLarge, result.Error!.Code);
    }

    [Fact]
    public void Default_limit_accepts_every_KoSIT_sample()
    {
        var largest = Directory.EnumerateFiles(TestPaths.KositCorpus, "*.xml").Max(f => new FileInfo(f).Length);

        Assert.True(largest < XmlLimits.Default.MaxDocumentBytes);
    }

    [Fact]
    public void Missing_required_field_is_named_but_no_document_content_is_echoed()
    {
        var doc = XDocument.Load(Path.Combine(TestPaths.KositCorpus, "01.01a-INVOICE_ubl.xml"));
        XNamespace cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
        doc.Root!.Element(cbc + "ID")!.Remove();

        var result = ParseText(doc.ToString());

        Assert.False(result.Succeeded);
        Assert.Equal(ParseErrorCode.MissingField, result.Error!.Code);
        Assert.Contains("BT-1", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_amount_is_reported_without_echoing_the_value()
    {
        var doc = XDocument.Load(Path.Combine(TestPaths.KositCorpus, "01.01a-INVOICE_ubl.xml"));
        XNamespace cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";
        doc.Descendants(cbc + "PayableAmount").Single().Value = "<script>alert(1)</script>";
        doc.Descendants(cbc + "TaxInclusiveAmount").Single().Value = "<script>alert(1)</script>";

        var result = ParseText(doc.ToString());

        Assert.False(result.Succeeded);
        Assert.Equal(ParseErrorCode.InvalidValue, result.Error!.Code);
        Assert.DoesNotContain("script", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Empty_input_is_malformed()
    {
        var result = ParseText(string.Empty);

        Assert.False(result.Succeeded);
        Assert.Equal(ParseErrorCode.Malformed, result.Error!.Code);
    }
}
