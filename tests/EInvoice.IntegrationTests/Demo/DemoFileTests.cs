using EInvoice.IntegrationTests.Infrastructure;
using EInvoice.IntegrationTests.TestData;

namespace EInvoice.IntegrationTests.Demo;

/// <summary>
/// demo/invoices holds exactly what DemoInvoices and DemoAiInvoices build, byte for byte. To change a demo file, change
/// the builder and run this test once with EINVOICE_WRITE_DEMO_FILES=1, which writes the files before comparing.
/// </summary>
public sealed class DemoFileTests
{
    private static string Folder => Path.Combine(SqlServerFixture.RepositoryRoot(), "demo", "invoices");

    private static IReadOnlyList<(string FileName, byte[] Content)> All =>
        [.. DemoInvoices.All.Select(d => (d.FileName, d.Content)), .. DemoAiInvoices.All.Select(d => (d.FileName, d.Content))];

    [Fact]
    public void Committed_demo_files_are_what_the_generator_builds()
    {
        if (Environment.GetEnvironmentVariable("EINVOICE_WRITE_DEMO_FILES") == "1")
        {
            Directory.CreateDirectory(Folder);
            foreach (var (fileName, content) in All)
            {
                File.WriteAllBytes(Path.Combine(Folder, fileName), content);
            }
        }

        Assert.Equal(All.Select(d => d.FileName).Order(), Directory.GetFiles(Folder).Select(Path.GetFileName).Order());
        Assert.All(All, demo => Assert.True(
            demo.Content.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(Folder, demo.FileName))),
            $"{demo.FileName} differs from what the generator builds"));
    }

    [Fact]
    public void Every_letterhead_supplier_has_a_logo_of_its_own()
    {
        var logos = DemoAiInvoices.All.Where(d => d.Render is not null).Select(d =>
        {
            using var pdf = UglyToad.PdfPig.PdfDocument.Open(d.Content);
            return Convert.ToHexString(Assert.Single(pdf.GetPage(1).GetImages()).RawBytes);
        }).ToList();

        Assert.True(logos.Count >= 5, "13 to 17 are letterhead samples");
        Assert.Equal(logos.Count, logos.Distinct().Count());
    }

    [Fact]
    public void The_new_bank_account_sample_announces_the_change_and_prints_the_unapproved_iban()
    {
        var demo = DemoAiInvoices.All.Single(d => d.FileName == "17-ai-letterhead-new-bank-account.pdf");
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(demo.Content);
        var text = string.Join(" ", pdf.GetPage(1).GetWords().Select(w => w.Text));

        Assert.Contains("Unsere Bankverbindung hat sich geaendert.", text, StringComparison.Ordinal);
        Assert.Contains(string.Join(' ', DemoAiInvoices.NewBankAccount.Chunk(4).Select(c => new string(c))), text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_ai_samples_are_plain_pdfs_without_an_embedded_invoice()
    {
        // With an embedded XML the app would read that instead (ZUGFeRD) and the model would never run.
        Assert.All(DemoAiInvoices.All, demo =>
        {
            Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(demo.Content, 0, 5), StringComparison.Ordinal);
            Assert.DoesNotContain("EmbeddedFile", System.Text.Encoding.Latin1.GetString(demo.Content), StringComparison.Ordinal);
        });
    }
}
