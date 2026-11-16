using System.Text;
using EInvoice.Application.Parsing;
using EInvoice.Application.Extraction;
using EInvoice.Infrastructure.Parsing;
using EInvoice.IntegrationTests.TestData;
using EInvoice.UnitTests.Extraction;

namespace EInvoice.IntegrationTests.Parsing;

/// <summary>
/// Finding the invoice XML inside a ZUGFeRD / Factur-X PDF: only under its official file names, exactly one, and
/// within the size limit. Anything else is reported, never guessed. The XML itself is read later by the parser.
/// </summary>
public sealed class EmbeddedInvoiceReaderTests
{
    private static readonly byte[] Xml = new CiiInvoiceBuilder().Line("HDMI cable 2 m", 10m, 7.4125m, orderLine: "1").Build();

    private static PdfEmbeddedInvoiceReader Reader(long maxBytes = 1024 * 1024) => new(PdfLimits.Default, maxBytes);

    [Theory]
    [InlineData("factur-x.xml")]          // ZUGFeRD 2.1+ / Factur-X
    [InlineData("zugferd-invoice.xml")]   // ZUGFeRD 2.0
    [InlineData("xrechnung.xml")]         // ZUGFeRD profile XRECHNUNG
    [InlineData("Factur-X.XML")]          // names are compared ignoring case
    public void Finds_the_invoice_under_its_official_names(string name)
    {
        var found = Reader().Find(ZugferdPdf.Invoice(Xml, name));

        Assert.Equal(EmbeddedInvoiceStatus.Found, found.Status);
        Assert.Equal(Xml, found.Xml);
        Assert.Equal(name, found.FileName);
    }

    [Fact]
    public void Other_attachments_next_to_the_invoice_are_ignored()
    {
        var pdf = ZugferdPdf.Build("Rechnung", ("delivery-note.xml", Encoding.UTF8.GetBytes("<note/>")), ("factur-x.xml", Xml), ("terms.txt", "AGB"u8.ToArray()));

        var found = Reader().Find(pdf);

        Assert.Equal((EmbeddedInvoiceStatus.Found, "factur-x.xml"), (found.Status, found.FileName));
    }

    [Fact]
    public void An_xml_attachment_under_another_name_is_not_taken_for_the_invoice()
    {
        // Guessing would let any attached XML become the invoice.
        var found = Reader().Find(ZugferdPdf.Build("Rechnung", ("invoice-data.xml", Xml)));

        Assert.Equal(EmbeddedInvoiceStatus.None, found.Status);
        Assert.Null(found.Xml);
    }

    [Fact]
    public void Two_invoice_xmls_are_ambiguous_and_neither_is_chosen()
    {
        var found = Reader().Find(ZugferdPdf.Build("Rechnung", ("factur-x.xml", Xml), ("zugferd-invoice.xml", Xml)));

        Assert.Equal(EmbeddedInvoiceStatus.Ambiguous, found.Status);
        Assert.Null(found.Xml);
    }

    [Fact]
    public void A_plain_pdf_has_no_embedded_invoice()
    {
        Assert.Equal(EmbeddedInvoiceStatus.None, Reader().Find(new SyntheticPdf().Build()).Status);
        Assert.Equal(EmbeddedInvoiceStatus.None, Reader().Find(ZugferdPdf.Build("Rechnung")).Status);
    }

    [Fact]
    public void An_embedded_invoice_over_the_size_limit_is_not_read()
    {
        var found = Reader(maxBytes: Xml.Length - 1).Find(ZugferdPdf.Invoice(Xml));

        Assert.Equal(EmbeddedInvoiceStatus.TooLarge, found.Status);
        Assert.Null(found.Xml);
    }

    [Fact]
    public void A_broken_pdf_is_unreadable_not_a_crash()
    {
        var pdf = ZugferdPdf.Invoice(Xml);
        var broken = pdf[..(pdf.Length / 2)];

        Assert.Equal(EmbeddedInvoiceStatus.Unreadable, Reader().Find(broken).Status);
    }
}
