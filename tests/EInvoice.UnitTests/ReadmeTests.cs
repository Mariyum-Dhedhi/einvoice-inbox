using System.Buffers.Binary;
using System.Text.RegularExpressions;

namespace EInvoice.UnitTests;

/// <summary>
/// The README is the project's front page: it opens with a picture of the review screen (made by the browser journey
/// Readme_screenshot_an_approver_reviews_a_price_variance), says what is done as a checklist, and links only to files
/// that exist.
/// </summary>
public sealed partial class ReadmeTests
{
    private const string Screenshot = "docs/images/review-match.png";

    private static readonly string Readme = File.ReadAllText(Path.Combine(TestPaths.RepositoryRoot, "README.md"));

    /// <summary>Everything before the first section heading: what a visitor sees without scrolling.</summary>
    private static string Top => Readme[..Readme.IndexOf("\n## ", StringComparison.Ordinal)];

    [GeneratedRegex(@"\]\((?<target>[^)\s]+)\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"^## Progress\r?\n(?<body>(?:.*\r?\n)*?)(?=## )", RegexOptions.Multiline)]
    private static partial Regex ProgressSection();

    [Fact]
    public void The_review_screen_is_shown_before_the_first_section()
    {
        // An image with a description (for screen readers and when the picture does not load), not just a link.
        Assert.Matches(@"!\[[^\]]{10,}\]\(" + Regex.Escape(Screenshot) + @"\)", Top);
    }

    [Fact]
    public void The_screenshot_is_a_png_as_wide_as_the_journey_makes_it()
    {
        var bytes = File.ReadAllBytes(Path.Combine(TestPaths.RepositoryRoot, Screenshot));

        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A], bytes[..8]);
        Assert.Equal(1440, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4))); // IHDR width
    }

    [Fact]
    public void Every_relative_link_points_to_a_file_or_folder_that_exists()
    {
        var targets = Link().Matches(Readme)
            .Select(match => match.Groups["target"].Value)
            .Where(target => !target.Contains("://", StringComparison.Ordinal) && !target.StartsWith('#'))
            .Select(target => target.Split('#')[0])
            .ToList();

        Assert.Contains(Screenshot, targets);
        var missing = targets.Where(target =>
            !File.Exists(Path.Combine(TestPaths.RepositoryRoot, target)) && !Directory.Exists(Path.Combine(TestPaths.RepositoryRoot, target)));
        Assert.Empty(missing);
    }

    [Fact]
    public void Progress_is_a_checklist_not_a_status_line()
    {
        Assert.DoesNotContain("**Status:**", Readme, StringComparison.Ordinal); // the old "in development" line

        var section = ProgressSection().Match(Readme);
        Assert.True(section.Success, "README has no '## Progress' section");
        var items = section.Groups["body"].Value.Split('\n')
            .Select(line => line.TrimEnd('\r'))
            .Where(line => line.StartsWith("- ", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(items);
        Assert.All(items, item => Assert.Matches(@"^- \[[ x]\] \S", item));
        Assert.Contains(items, item => item.StartsWith("- [x]", StringComparison.Ordinal));
    }
}
