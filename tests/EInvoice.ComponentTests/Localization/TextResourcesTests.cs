using System.Text.RegularExpressions;

namespace EInvoice.ComponentTests.Localization;

/// <summary>
/// Every text of the UI exists in English and in German, says the same thing with the same placeholders, and is used.
/// A text that exists in one language only fails here, not in front of a German clerk.
/// </summary>
public sealed partial class TextResourcesTests
{
    /// <summary>
    /// Texts that are the same word in both languages. Anything else that is identical was copied, not translated.
    /// Each entry is checked to really be identical, so the list cannot grow stale.
    /// </summary>
    private static readonly HashSet<string> SameInBothLanguages = new(StringComparer.Ordinal)
    {
        "App.Name",              // the product name
        "Page.Title",            // "{0} – E-Invoice Inbox"
        "Common.Name",           // "Name"
        "Purchasing.OrderStatus", // "Status"
        "Nav.Dashboard",         // "Dashboard" is used in German
        "Dashboard.Title",
        "Common.Status",         // "Status"
        "Common.Iban",           // "IBAN"
        "Detail.ZugferdChip",    // "ZUGFeRD · XML in PDF"
        "Language.German",       // each language names itself: "Deutsch"
        "Language.English",      // "English"
    };

    [Fact]
    public void Every_text_exists_in_both_languages()
    {
        var onlyEnglish = TextResources.English.Keys.Except(TextResources.German.Keys).Order(StringComparer.Ordinal).ToList();
        var onlyGerman = TextResources.German.Keys.Except(TextResources.English.Keys).Order(StringComparer.Ordinal).ToList();

        Assert.True(onlyEnglish.Count == 0, "Only in English: " + string.Join(", ", onlyEnglish));
        Assert.True(onlyGerman.Count == 0, "Only in German: " + string.Join(", ", onlyGerman));
    }

    [Fact]
    public void No_text_is_empty()
    {
        var empty = TextResources.English.Concat(TextResources.German)
            .Where(t => string.IsNullOrWhiteSpace(t.Value))
            .Select(t => t.Key)
            .ToList();

        Assert.True(empty.Count == 0, "Empty: " + string.Join(", ", empty));
    }

    [Fact]
    public void Both_languages_fill_in_the_same_placeholders()
    {
        var different = TextResources.English
            .Where(t => TextResources.German.TryGetValue(t.Key, out var german) && !Placeholders(t.Value).SetEquals(Placeholders(german)))
            .Select(t => t.Key)
            .ToList();

        Assert.True(different.Count == 0, "Placeholders differ: " + string.Join(", ", different));
    }

    [Fact]
    public void German_texts_are_translated_not_copied()
    {
        var copied = TextResources.English
            .Where(t => !SameInBothLanguages.Contains(t.Key) && TextResources.German.GetValueOrDefault(t.Key) == t.Value)
            .Select(t => t.Key)
            .ToList();
        var listedButDifferent = SameInBothLanguages
            .Where(k => TextResources.English.GetValueOrDefault(k) != TextResources.German.GetValueOrDefault(k))
            .ToList();

        Assert.True(copied.Count == 0, "Same in German as in English: " + string.Join(", ", copied));
        Assert.True(listedButDifferent.Count == 0, "Listed as the same in both languages, but not: " + string.Join(", ", listedButDifferent));
    }

    [Fact]
    public void Every_status_outcome_rule_and_role_has_its_words()
    {
        var missing = TextResources.ByCode.Where(k => !TextResources.English.ContainsKey(k)).Order(StringComparer.Ordinal).ToList();

        Assert.True(missing.Count == 0, "No text for: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_text_the_code_asks_for_exists()
    {
        var missing = UsedKeys().Where(k => !TextResources.English.ContainsKey(k)).Order(StringComparer.Ordinal).ToList();

        Assert.True(missing.Count == 0, "Asked for but not written: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_text_is_used()
    {
        var used = UsedKeys();
        var unused = TextResources.English.Keys
            .Where(k => !used.Contains(k) && !TextResources.ByCode.Contains(k))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(unused.Count == 0, "Never shown: " + string.Join(", ", unused));
    }

    [Fact]
    public void The_scan_finds_keys_the_way_the_pages_write_them()
    {
        Assert.Equal(["Inbox.Title", "Paging.Range"], Keys("""<h1>@T["Inbox.Title"]</h1> @T["Paging.Range", first, last, total]""").Order(StringComparer.Ordinal));
        Assert.Empty(Keys("""T[$"Status.{status}"]"""));
    }

    /// <summary>Every key written out literally as T["..."] (pages) or t["..."] (helpers) in the web project.</summary>
    private static HashSet<string> UsedKeys() =>
        Directory.EnumerateFiles(TextResources.WebProject, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".razor", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal))
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(f => Keys(File.ReadAllText(f)))
            .ToHashSet(StringComparer.Ordinal);

    private static IEnumerable<string> Keys(string source) => Key().Matches(source).Select(m => m.Groups[1].Value);

    private static HashSet<string> Placeholders(string text) => Placeholder().Matches(text).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex("""\b[Tt]\["([A-Za-z0-9_.]+)"[\],]""")]
    private static partial Regex Key();

    [GeneratedRegex(@"\{(\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex Placeholder();
}
