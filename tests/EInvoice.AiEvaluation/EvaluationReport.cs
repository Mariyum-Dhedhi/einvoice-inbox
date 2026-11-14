using System.Globalization;
using System.Text;

namespace EInvoice.AiEvaluation;

/// <param name="Provider">ollama, or oracle for a harness check.</param>
/// <param name="IsMeasurement">True only for a real model; everything else is marked as not a measurement.</param>
public sealed record RunInfo(string Provider, string Model, string? ModelDigest, string? OllamaVersion, DateTimeOffset RunAt, bool IsMeasurement);

/// <summary>
/// The results as markdown, headline first: how many invoices ended where they should, and how many reached Matched
/// with a wrong value. Every number carries the model, its digest, the Ollama version and the date it was measured.
/// </summary>
public static class EvaluationReport
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string ToMarkdown(RunInfo info, IReadOnlyList<CaseScore> scores)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(scores);
        var md = new StringBuilder();
        void Line(string text = "") => md.Append(text).Append('\n');

        Line("## AI extraction evaluation");
        Line();
        if (!info.IsMeasurement)
        {
            Line($"> **NOT A MEASUREMENT.** Harness check with the `{info.Provider}` provider: these numbers say nothing about a model.");
            Line();
        }

        Line(string.Create(Invariant,
            $"Model `{info.Model}`{(info.ModelDigest is null ? string.Empty : $" (digest `{info.ModelDigest}`)")}{(info.OllamaVersion is null ? string.Empty : $", Ollama {info.OllamaVersion}")}, " +
            $"run {info.RunAt.UtcDateTime:yyyy-MM-dd HH:mm} UTC, {scores.Count} synthetic PDF invoices (`testdata/ai-eval`)."));
        Line();

        var correct = scores.Count(s => s.OutcomeCorrect);
        Line(string.Create(Invariant, $"- Correct end result (status and reason): **{correct} / {scores.Count}** ({Percent(correct, scores.Count)})"));
        Line(string.Create(Invariant, $"- Wrong values that reached Matched: **{scores.Count(s => s.WrongValueReachedMatched)}**"));
        Line();

        Line("| Exception type | Cases | Correct end result | Field accuracy | Wrong values not flagged |");
        Line("|---|---:|---:|---:|---:|");
        foreach (var group in scores.GroupBy(s => s.Case.Type))
        {
            var fields = group.SelectMany(s => s.Fields).ToList();
            Line(string.Create(Invariant,
                $"| {group.Key} | {group.Count()} | {group.Count(s => s.OutcomeCorrect)} / {group.Count()} | {Percent(fields.Count(f => f.Correct), fields.Count)} | {group.Sum(s => s.UnflaggedWrongFields)} |"));
        }

        var all = scores.SelectMany(s => s.Fields).ToList();
        Line(string.Create(Invariant,
            $"| **All** | {scores.Count} | {correct} / {scores.Count} | {Percent(all.Count(f => f.Correct), all.Count)} | {scores.Sum(s => s.UnflaggedWrongFields)} |"));
        Line();

        var injection = scores.Where(s => s.InjectionResisted is not null).ToList();
        Line(string.Create(Invariant, $"- Injection resisted: {injection.Count(s => s.InjectionResisted == true)} / {injection.Count}"));
        var seconds = scores.Select(s => s.Duration.TotalSeconds).Order().ToList();
        if (seconds.Count > 0)
        {
            Line(string.Create(Invariant, $"- Seconds per invoice: Median {Median(seconds):0.0}, max {seconds[^1]:0.0}"));
        }

        var misses = scores.Where(s => !s.OutcomeCorrect || s.WrongValueReachedMatched || s.UnflaggedWrongFields > 0).ToList();
        if (misses.Count > 0)
        {
            Line();
            Line("### Cases that need a look");
            Line();
            foreach (var s in misses)
            {
                var wrong = string.Join("; ", s.Fields.Where(f => !f.Correct).Select(f => string.Create(Invariant,
                    $"{f.Name}{(f.Line is null ? string.Empty : $" (line {f.Line})")}: expected `{f.Expected ?? "-"}`, read `{f.Actual ?? "nothing"}`{(f.Flagged ? " (flagged)" : string.Empty)}")));
                var detail = s.ExtractionError is { } error ? $"extraction failed ({error}), nothing was read" : (wrong.Length == 0 ? "no wrong values" : wrong);
                Line(string.Create(Invariant,
                    $"- **{s.Case.Id}** {s.Case.Type}, layout {s.Case.Layout}: end result {(s.OutcomeCorrect ? "as expected" : "NOT as expected")}; {detail}"));
            }
        }

        return md.ToString();
    }

    private static string Percent(int part, int whole) =>
        whole == 0 ? "-" : string.Create(Invariant, $"{100.0 * part / whole:0.0} %");

    private static double Median(List<double> sorted) =>
        sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2;
}
