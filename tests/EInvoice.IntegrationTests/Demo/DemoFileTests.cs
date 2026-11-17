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
