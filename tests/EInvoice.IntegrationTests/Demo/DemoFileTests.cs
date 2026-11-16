using EInvoice.IntegrationTests.Infrastructure;
using EInvoice.IntegrationTests.TestData;

namespace EInvoice.IntegrationTests.Demo;

/// <summary>
/// demo/invoices holds exactly what DemoInvoices builds, byte for byte. To change a demo file, change DemoInvoices and
/// run this test once with EINVOICE_WRITE_DEMO_FILES=1, which writes the files before comparing.
/// </summary>
public sealed class DemoFileTests
{
    private static string Folder => Path.Combine(SqlServerFixture.RepositoryRoot(), "demo", "invoices");

    [Fact]
    public void Committed_demo_files_are_what_the_generator_builds()
    {
        if (Environment.GetEnvironmentVariable("EINVOICE_WRITE_DEMO_FILES") == "1")
        {
            Directory.CreateDirectory(Folder);
            foreach (var demo in DemoInvoices.All)
            {
                File.WriteAllBytes(Path.Combine(Folder, demo.FileName), demo.Content);
            }
        }

        Assert.Equal(DemoInvoices.All.Select(d => d.FileName).Order(), Directory.GetFiles(Folder).Select(Path.GetFileName).Order());
        Assert.All(DemoInvoices.All, demo => Assert.True(
            demo.Content.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(Folder, demo.FileName))),
            $"{demo.FileName} differs from what DemoInvoices builds"));
    }
}
