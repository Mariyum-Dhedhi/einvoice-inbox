using System.Text.RegularExpressions;

namespace EInvoice.ComponentTests.Localization;

/// <summary>
/// No page writes words of its own: every text comes from the resources (T["..."]), so it exists in both languages.
/// The scan reads the Razor files of the web project: text between tags, text attributes (Label, title, ...), and string
/// literals that read like words. What a page shows from data (invoice numbers, names, recorded messages) is not text
/// of the page and is not looked at.
/// </summary>
public sealed partial class HardCodedTextTests
{
    /// <summary>String literals that look like words but are values the code compares, never shown. Each says why.</summary>
    private static readonly Dictionary<string, string> NotShown = new(StringComparer.Ordinal)
    {
        ["\"Match\""] = "ReasonKpi.Kind from the KPI view, compared, not shown",
        ["\"Finding\""] = "ReasonKpi.Kind from the KPI view, compared, not shown",
        ["\"Server\""] = "RendererInfo name",
        ["\"Roboto\""] = "font family (AppTheme)",
        ["\"Helvetica Neue\""] = "font family (AppTheme)",
        ["\"Arial\""] = "font family (AppTheme)",
    };

    public static TheoryData<string> RazorFiles()
    {
        var files = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(TextResources.WebProject, "Components"), "*.razor", SearchOption.AllDirectories)
                     .Order(StringComparer.Ordinal))
        {
            files.Add(Path.GetRelativePath(TextResources.WebProject, file).Replace('\\', '/'));
        }

        return files;
    }

    [Theory]
    [MemberData(nameof(RazorFiles))]
    public void A_page_takes_every_word_from_the_texts(string file)
    {
        var found = Find(File.ReadAllText(Path.Combine(TextResources.WebProject, file))).ToList();

        Assert.True(found.Count == 0, $"Hard-coded text in {file}:\n  " + string.Join("\n  ", found));
    }

    [Fact]
    public void Component_helpers_take_every_word_from_the_texts()
    {
        var found = Directory.EnumerateFiles(Path.Combine(TextResources.WebProject, "Components"), "*.cs", SearchOption.AllDirectories)
            .SelectMany(f => Literals(StripComments(File.ReadAllText(f))).Select(l => $"{Path.GetFileName(f)}: {l}"))
            .ToList();

        Assert.True(found.Count == 0, "Hard-coded text:\n  " + string.Join("\n  ", found));
    }

    // ---- The scan itself is tested: it must find text, and must not mistake code for text. ----------------------

    [Theory]
    [InlineData("<h1>Inbox</h1>")]
    [InlineData("<MudButton OnClick=\"Go\">Match again</MudButton>")]
    [InlineData("<p>\n    You are signed in as <strong>@name</strong>, with a role.\n</p>")]
    [InlineData("<MudTextField Label=\"Reason\" />")]
    [InlineData("<MudIcon title=\"Read by AI from a PDF\" />")]
    [InlineData("<span>@(_busy ? \"Matching…\" : T[\"Detail.Rematch\"])</span>")]
    [InlineData("<td>@(h.Status == BankAccountStatus.Active ? \"Active\" : \"Pending\")</td>")]
    [InlineData("@code { private string Text => \"Ready to decide\"; }")]
    [InlineData("@code { [Required(ErrorMessage = \"Enter your user name.\")] public string Name { get; set; } }")]
    [InlineData("<span>@vendor.PendingAccounts waiting for a second admin</span>")]
    public void The_scan_finds_hard_coded_text(string razor)
    {
        Assert.NotEmpty(Find(razor));
    }

    [Theory]
    [InlineData("<h1>@T[\"Inbox.Title\"]</h1>")]
    [InlineData("<MudButton Color=\"Color.Primary\" Variant=\"Variant.Filled\" OnClick=\"() => DecideAsync(Decision.Approve)\">@T[\"Detail.Approve\"]</MudButton>")]
    [InlineData("@if (invoice.Status == InvoiceStatus.Failed && invoice.History.LastOrDefault()?.Reason is { } failure)\n{\n    <p>@failure</p>\n}")]
    [InlineData("@foreach (var line in rules.Text.Split('\\n'))\n{\n    <MudText>@line</MudText>\n}")]
    [InlineData("@{\n    var approveBlock = ApprovalRules.WhyNotApprove(approver, invoice.GrossTotal,\n        invoice.Findings.Any(f => f.Severity == FindingSeverity.Blocking));\n}")]
    [InlineData("<td class=\"num nowrap\">@Formats.Money(line.NetAmount, invoice.Currency)</td>")]
    [InlineData("<MudLink Href=\"@($\"/invoices/{invoice.Id}\")\" data-testid=\"existing-invoice\">@T[\"Inbox.OpenExisting\"]</MudLink>")]
    [InlineData("@* A comment with words in it. *@<!-- and an HTML comment -->")]
    [InlineData("<MudText Typo=\"Typo.h6\" HtmlTag=\"h2\">@T[\"Detail.History\"]</MudText>")]
    [InlineData("<td>#</td><td>–</td><button aria-label=\"@T[\"Common.Dismiss\"]\">×</button>")]
    [InlineData("else if (invoice.SourceFormat == SourceFormat.Zugferd)\n{\n}")]
    [InlineData("@inject IOptions<ApprovalSettings> ApprovalSettings\n@inject SignInManager<AppUser> SignIn")]
    [InlineData("<span data-testid=\"signed-in-user\">@context.User.Identity?.Name</span>")]
    public void The_scan_leaves_code_alone(string razor)
    {
        Assert.Empty(Find(razor));
    }

    // ---- The scanner -----------------------------------------------------------------------------------------------

    internal static IEnumerable<string> Find(string razor)
    {
        var source = Directive().Replace(Comments().Replace(razor, " "), string.Empty);
        var codeStart = source.IndexOf("@code", StringComparison.Ordinal);
        var markup = codeStart >= 0 ? source[..codeStart] : source;
        var code = codeStart >= 0 ? source[codeStart..] : string.Empty;

        foreach (var literal in Literals(StripComments(code)))
        {
            yield return literal;
        }

        foreach (Match attribute in TextAttribute().Matches(markup))
        {
            var value = attribute.Groups[2].Value;
            if (!value.StartsWith('@') && Words().IsMatch(value))
            {
                yield return attribute.Value;
            }
        }

        // Literals inside expressions in the markup: @(busy ? "Matching…" : ...).
        foreach (Match expression in MarkupExpression().Matches(markup))
        {
            foreach (var literal in Literals(expression.Value))
            {
                yield return literal;
            }
        }

        foreach (var text in Tag().Split(markup))
        {
            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length == 0 || IsCode(trimmed))
                {
                    continue;
                }

                var words = Entity().Replace(Expression().Replace(trimmed, " "), " ");
                if (Words().IsMatch(words))
                {
                    yield return trimmed;
                }
            }
        }
    }

    /// <summary>A line of C# between tags: control flow, a statement, a block brace, a directive.</summary>
    private static bool IsCode(string line) =>
        line[0] is '{' or '}' or ')'
        || line.StartsWith("@{", StringComparison.Ordinal)
        || CodeStart().IsMatch(line)
        || line.EndsWith(';')
        || line.EndsWith(',')
        || line.Contains("=>", StringComparison.Ordinal);

    private static IEnumerable<string> Literals(string code) =>
        Literal().Matches(code).Where(m => ReadsLikeWords(m.Groups[1].Value)).Select(m => m.Value).Where(l => !NotShown.ContainsKey(l));

    /// <summary>
    /// "Active", "Matching…", "Ready to decide", "with no role", "healthy.": words for people. Not "num nowrap" (CSS),
    /// "Inbox.Title" (a key), "dd.MM.yyyy" (a format) or "{bytes} B".
    /// </summary>
    private static bool ReadsLikeWords(string literal)
    {
        var text = Hole().Replace(literal, " ").Trim();
        if (text.Length == 0)
        {
            return false;
        }

        var words = StandaloneWord().Matches(text).Count(w => w.Length >= 2);
        return CapitalisedWord().IsMatch(text)
            || (text.EndsWith('…') && words >= 1)
            || words >= 3
            || (words >= 2 && char.IsUpper(text[0]))
            || (words >= 1 && !literal.Contains('{', StringComparison.Ordinal) && SentenceEnd().IsMatch(text));
    }

    private static string StripComments(string code) => LineComment().Replace(code, string.Empty);

    [GeneratedRegex(@"@\*.*?\*@|<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comments();

    /// <summary>A directive line (@inject IOptions&lt;X&gt; X, @using, @page ...): code, and its generics look like tags.</summary>
    [GeneratedRegex(@"^[ \t]*@(inject|using|page|attribute|implements|layout|inherits|rendermode|typeparam)\b.*$", RegexOptions.Multiline)]
    private static partial Regex Directive();

    /// <summary>Attributes people read: labels, helper texts, tooltips, accessible names, placeholders.</summary>
    [GeneratedRegex(@"\b(Label|HelperText|title|Title|aria-label|placeholder|alt|Text)=""([^""]*)""")]
    private static partial Regex TextAttribute();

    /// <summary>A tag, with quoted attribute values that may hold '>' (lambdas) or nested quotes from Razor.</summary>
    [GeneratedRegex("""<[^>"']*(?:"[^"]*"[^>"']*|'[^']*'[^>"']*)*>""")]
    private static partial Regex Tag();

    /// <summary>@( ... ) with up to two levels of nested parentheses.</summary>
    [GeneratedRegex(@"@\((?:[^()]|\((?:[^()]|\([^()]*\))*\))*\)")]
    private static partial Regex MarkupExpression();

    /// <summary>A Razor expression in text: @(...), @T["..."] or @name.Member(args)[index]...</summary>
    [GeneratedRegex(@"@\((?:[^()]|\((?:[^()]|\([^()]*\))*\))*\)|@[A-Za-z_][\w]*(?:(?:\??\.[A-Za-z_]\w*)|(?:\((?:[^()]|\([^()]*\))*\))|(?:\[[^\]]*\]))*")]
    private static partial Regex Expression();

    [GeneratedRegex(@"^@?(if|else|foreach|for|while|switch|case|var|return|using|inject|page|attribute|implements|layout|inherits|rendermode|code|break|await)\b")]
    private static partial Regex CodeStart();

    [GeneratedRegex(@"&[a-z]+;|&#\d+;")]
    private static partial Regex Entity();

    /// <summary>Two letters in a row: a word. Digits, dashes, "#", "×" and "–" are not words.</summary>
    [GeneratedRegex(@"\p{L}{2,}")]
    private static partial Regex Words();

    /// <summary>A string literal (escapes allowed), interpolated ones too.</summary>
    [GeneratedRegex(@"\$?""((?:[^""\\\r\n]|\\.)*)""")]
    private static partial Regex Literal();

    /// <summary>A word on its own with a small letter: not part of a key ("Inbox.Title"), a CSS class ("align-center"), a path or a format.</summary>
    [GeneratedRegex(@"(?<![\w.\-/])\p{L}*\p{Ll}\p{L}*(?![\w.\-/])")]
    private static partial Regex StandaloneWord();

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex Hole();

    [GeneratedRegex(@"^\p{Lu}\p{Ll}+$")]
    private static partial Regex CapitalisedWord();

    [GeneratedRegex(@"\p{L}{2,}[.!?:]$")]
    private static partial Regex SentenceEnd();

    [GeneratedRegex(@"^\s*///?.*$", RegexOptions.Multiline)]
    private static partial Regex LineComment();
}
