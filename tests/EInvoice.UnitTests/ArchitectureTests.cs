using System.Xml.Linq;

namespace EInvoice.UnitTests;

/// <summary>
/// Guards the rule "dependencies point inward only":
/// Web -> Infrastructure -> Application -> Domain, and Domain references nothing.
/// Reads the project files directly, so an unused reference is caught too.
/// </summary>
public class ArchitectureTests
{
    [Theory]
    [InlineData("EInvoice.Domain", new string[0])]
    [InlineData("EInvoice.Application", new[] { "EInvoice.Domain" })]
    [InlineData("EInvoice.Infrastructure", new[] { "EInvoice.Application", "EInvoice.Domain" })]
    public void Project_references_only_inner_layers(string project, string[] allowed)
    {
        var references = ProjectReferences(project);

        Assert.All(references, r => Assert.Contains(r, allowed));
    }

    [Fact]
    public void Domain_has_no_package_references()
    {
        var packages = LoadProject("EInvoice.Domain")
            .Descendants("PackageReference")
            .Select(e => (string?)e.Attribute("Include"));

        Assert.Empty(packages);
    }

    private static IEnumerable<string> ProjectReferences(string project) =>
        LoadProject(project)
            .Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension(((string?)e.Attribute("Include") ?? string.Empty).Replace('\\', '/')));

    private static XDocument LoadProject(string project)
    {
        var path = Path.Combine(RepositoryRoot(), "src", project, project + ".csproj");
        return XDocument.Load(path);
    }

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EInvoice.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root (EInvoice.slnx) not found.");
    }
}
