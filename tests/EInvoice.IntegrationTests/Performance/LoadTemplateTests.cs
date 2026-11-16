using EInvoice.IntegrationTests.Infrastructure;

namespace EInvoice.IntegrationTests.Performance;

/// <summary>
/// The load test uploads only valid invoices: tests/load/invoice-template.xml is what LoadInvoiceTemplate builds, and
/// filled in it is exactly the builder's invoice (whose conformance XRechnungBuilderKositTests checks with KoSIT).
/// To change the template, change LoadInvoiceTemplate and run this once with EINVOICE_WRITE_LOAD_FILES=1.
/// </summary>
public sealed class LoadTemplateTests
{
    private static string TemplatePath => Path.Combine(SqlServerFixture.RepositoryRoot(), "tests", "load", "invoice-template.xml");

    [Fact]
    public void Committed_template_is_what_the_generator_builds()
    {
        if (Environment.GetEnvironmentVariable("EINVOICE_WRITE_LOAD_FILES") == "1")
        {
            File.WriteAllBytes(TemplatePath, LoadInvoiceTemplate.Bytes(LoadInvoiceTemplate.Build()));
        }

        Assert.Equal(LoadInvoiceTemplate.Bytes(LoadInvoiceTemplate.Build()), File.ReadAllBytes(TemplatePath));
    }

    [Theory]
    [InlineData(1)]   // VAT 0.9291: rounded up
    [InlineData(7)]   // VAT 6.5037: rounded down
    [InlineData(13)]  // the quantity received for PO-0101 line 1
    [InlineData(250)]
    [InlineData(999)]
    public void Filled_template_is_the_invoice_the_builder_makes(int quantity)
    {
        var date = new DateOnly(2026, 3, 9);
        var filled = LoadInvoiceTemplate.Fill(LoadInvoiceTemplate.Build(), "LOAD-1-42", date, quantity);

        Assert.Equal(LoadInvoiceTemplate.Lf(LoadInvoiceTemplate.Invoice("LOAD-1-42", date, quantity).BuildXml()), filled);
    }
}
