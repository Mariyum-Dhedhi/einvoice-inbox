namespace EInvoice.UnitTests;

internal static class TestPaths
{
    public static string RepositoryRoot { get; } = FindRoot();

    public static string KositCorpus => Path.Combine(RepositoryRoot, "testdata", "xrechnung", "kosit");

    public static string MaliciousCorpus => Path.Combine(RepositoryRoot, "testdata", "xrechnung", "malicious");

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "EInvoice.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root (EInvoice.slnx) not found.");
    }
}
