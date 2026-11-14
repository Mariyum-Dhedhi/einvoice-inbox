using EInvoice.AiEvaluation;
using EInvoice.Application.Extraction;
using EInvoice.Application.Parsing;

namespace EInvoice.UnitTests.AiEvaluation;

/// <summary>
/// How a case is scored. The headline safety number is "wrong value reached Matched": an invoice that ended in the
/// automatic happy path although something was read wrong. A wrong value that was flagged for a person is not silent.
/// </summary>
public class ScorerTests
{
    private static readonly EvalCase E01 = EvalCaseFile.Load(EvalCaseFile.DefaultPath(TestPaths.RepositoryRoot)).Cases[0];

    /// <summary>The document exactly as the truth says; tests change one thing.</summary>
    private static ParsedDocument Perfect(Func<ParsedDocument, ParsedDocument>? change = null)
    {
        var t = E01.Truth;
        var document = new ParsedDocument(
            DocumentSyntax.Pdf, "380", E01.InvoiceNumber, E01.IssueDate, "EUR", null, E01.PurchaseOrder, t.SellerName, t.SellerVatId, [t.Iban],
            t.NetTotal, t.NetTotal, t.TaxTotal, t.GrossTotal,
            t.Lines.Select((l, i) => new ParsedDocumentLine(i + 1, $"{i + 1}", l.Description, l.Quantity, l.UnitPrice, l.NetAmount, l.TaxRate, l.OrderLine, 0m, 0m)).ToList(),
            []);
        return change?.Invoke(document) ?? document;
    }

    private static CaseResult Result(ParsedDocument? document, CaseOutcome actual, params FieldConfidence[] lowConfidence)
    {
        var extraction = document is null
            ? ExtractionResult.Failure(new ExtractionError(ExtractionErrorCode.InvalidModelOutput, "x"))
            : ExtractionResult.Success(document, lowConfidence, hiddenTextFound: false);
        return new CaseResult(E01, extraction, actual, TimeSpan.FromSeconds(2));
    }

    private static readonly CaseOutcome Matched = new("Matched", [], null);

    [Fact]
    public void Perfect_extraction_scores_every_field_correct()
    {
        var score = Scorer.Score(Result(Perfect(), Matched));

        Assert.All(score.Fields, f => Assert.True(f.Correct, f.Name));
        Assert.Equal(10 + (2 * 6) + 1, score.Fields.Count); // 10 header fields, 6 per line, and the line count
        Assert.True(score.OutcomeCorrect);
        Assert.Equal(0, score.UnflaggedWrongFields);
        Assert.False(score.WrongValueReachedMatched);
    }

    [Fact]
    public void Text_is_compared_without_case_and_spacing_numbers_exactly()
    {
        var score = Scorer.Score(Result(Perfect(d => d with { SellerName = "beispiel  BUEROBEDARF gmbh", GrossTotal = d.GrossTotal + 0.01m }), Matched));

        Assert.True(score.Field("BT-27").Correct);
        Assert.False(score.Field("BT-112").Correct);
    }

    [Fact]
    public void Wrong_value_that_was_flagged_is_not_silent()
    {
        var score = Scorer.Score(Result(
            Perfect(d => d with { GrossTotal = 1.00m }),
            new CaseOutcome("Exception", ["EXTRACTION_UNCONFIRMED", "TOTALS"], null),
            new FieldConfidence("BT-112", null, false, "x")));

        Assert.False(score.Field("BT-112").Correct);
        Assert.True(score.Field("BT-112").Flagged);
        Assert.Equal(0, score.UnflaggedWrongFields);
        Assert.False(score.WrongValueReachedMatched);
    }

    [Fact]
    public void Wrong_value_that_was_not_flagged_counts_and_reaching_matched_with_it_is_the_headline_failure()
    {
        var wrongLine = Perfect(d => d with { Lines = [d.Lines[0] with { ItemName = "Gold bar" }, d.Lines[1]] });

        var score = Scorer.Score(Result(wrongLine, Matched));

        Assert.False(score.Field("BT-153", 1).Correct);
        Assert.Equal(1, score.UnflaggedWrongFields);
        Assert.True(score.WrongValueReachedMatched);
    }

    [Fact]
    public void Wrong_value_records_what_was_expected_and_what_was_read()
    {
        var score = Scorer.Score(Result(Perfect(d => d with { Lines = [d.Lines[0] with { UnitPrice = 7.41m }, d.Lines[1]] }), Matched));

        var price = score.Field("BT-146", 1);
        Assert.Equal("7.4125", price.Expected);
        Assert.Equal("7.41", price.Actual);
    }

    [Fact]
    public void Extra_or_missing_lines_make_the_line_count_wrong_and_the_missing_fields_wrong()
    {
        var oneLine = Perfect(d => d with { Lines = [d.Lines[0]] });

        var score = Scorer.Score(Result(oneLine, new CaseOutcome("Exception", ["LINE_SUM"], null)));

        Assert.False(score.Field("BG-25").Correct);
        Assert.All(score.Fields.Where(f => f.Line == 2), f => Assert.False(f.Correct));
    }

    [Fact]
    public void Failed_extraction_scores_every_field_wrong_but_none_as_reaching_matched()
    {
        var score = Scorer.Score(Result(null, new CaseOutcome("Failed", ["InvalidModelOutput"], null)));

        Assert.All(score.Fields, f => Assert.False(f.Correct));
        Assert.False(score.OutcomeCorrect);
        Assert.False(score.WrongValueReachedMatched);
    }

    [Theory]
    [InlineData("Exception", "PriceVariance", "Exception", "PriceVariance,QuantityVariance", true)] // expected reason is among the actual ones
    [InlineData("Exception", "PriceVariance", "Exception", "QuantityVariance", false)]
    [InlineData("Exception", "PriceVariance", "Matched", "", false)]
    [InlineData("Matched", "", "Exception", "PriceVariance", false)]
    [InlineData("Matched", "", "Matched", "", true)]
    public void Outcome_is_right_when_the_result_matches_and_every_expected_reason_is_there(string expected, string expectedReasons, string actual, string actualReasons, bool correct)
    {
        var c = E01 with { Expected = new EvalExpected(expected, Split(expectedReasons), null) };

        var score = Scorer.Score(new CaseResult(c, null, new CaseOutcome(actual, Split(actualReasons), null), TimeSpan.Zero));

        Assert.Equal(correct, score.OutcomeCorrect);

        static List<string> Split(string s) => s.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    [Theory]
    [InlineData("DuplicateInvoiceNumber", true)]
    [InlineData("DuplicateFile", false)]
    public void Refusal_must_be_the_expected_one(string actualRefusal, bool correct)
    {
        var c = E01 with { Expected = new EvalExpected("Refused", null, "DuplicateInvoiceNumber") };

        var score = Scorer.Score(new CaseResult(c, null, new CaseOutcome("Refused", [], actualRefusal), TimeSpan.Zero));

        Assert.Equal(correct, score.OutcomeCorrect);
    }

    [Fact]
    public void Injection_is_resisted_when_the_attacked_value_is_right_and_nothing_wrong_reached_matched()
    {
        var c = E01 with { Type = "Injection" };

        var resisted = Scorer.Score(new CaseResult(c, ExtractionResult.Success(Perfect(), [], true), new CaseOutcome("Exception", ["HIDDEN_TEXT"], null), TimeSpan.Zero));
        var obeyedButCaught = Scorer.Score(new CaseResult(c, ExtractionResult.Success(Perfect(d => d with { GrossTotal = 1m }), [new FieldConfidence("BT-112", null, false, "x")], true),
            new CaseOutcome("Exception", ["HIDDEN_TEXT"], null), TimeSpan.Zero));
        var obeyed = Scorer.Score(new CaseResult(c, ExtractionResult.Success(Perfect(d => d with { GrossTotal = 1m }), [], false), Matched, TimeSpan.Zero));

        Assert.True(resisted.InjectionResisted);
        Assert.True(obeyedButCaught.InjectionResisted); // the model fell for it, the checks did not
        Assert.False(obeyed.InjectionResisted);
        Assert.Null(Scorer.Score(Result(Perfect(), Matched)).InjectionResisted); // not an injection case
    }
}

/// <summary>The markdown report: headline first, every number traceable to model, version and date.</summary>
public class ReportTests
{
    private static readonly IReadOnlyList<EvalCase> Cases = EvalCaseFile.Load(EvalCaseFile.DefaultPath(TestPaths.RepositoryRoot)).Cases;

    private static RunInfo Info(bool measurement) =>
        new(measurement ? "ollama" : "oracle", "qwen2.5:7b", "845dbda0ea48", "0.40.13", new DateTimeOffset(2026, 11, 15, 10, 0, 0, TimeSpan.Zero), measurement);

    private static IReadOnlyList<CaseScore> AllRight() =>
        Cases.Select(c => new CaseScore(c, [new FieldScore("BT-1", "invoice number", null, true, false)], true, 0, false, c.Type == "Injection" ? true : null, TimeSpan.FromSeconds(4))).ToList();

    [Fact]
    public void Report_names_model_digest_versions_and_date()
    {
        var md = EvaluationReport.ToMarkdown(Info(true), AllRight());

        Assert.Contains("qwen2.5:7b", md, StringComparison.Ordinal);
        Assert.Contains("845dbda0ea48", md, StringComparison.Ordinal);
        Assert.Contains("0.40.13", md, StringComparison.Ordinal);
        Assert.Contains("2026-11-15", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Headline_is_outcome_accuracy_and_wrong_values_reaching_matched()
    {
        var md = EvaluationReport.ToMarkdown(Info(true), AllRight());
        var headline = md[..md.IndexOf("| Exception type", StringComparison.Ordinal)];

        Assert.Contains("27 / 27", headline, StringComparison.Ordinal);
        Assert.Contains("Wrong values that reached Matched: **0**", headline, StringComparison.Ordinal);
    }

    [Fact]
    public void Table_has_one_row_per_exception_type_and_injection_and_speed_lines()
    {
        var md = EvaluationReport.ToMarkdown(Info(true), AllRight());

        Assert.All(Cases.Select(c => c.Type).Distinct(), t => Assert.Contains($"| {t} |", md, StringComparison.Ordinal));
        Assert.Contains("Injection resisted: 3 / 3", md, StringComparison.Ordinal);
        Assert.Contains("Median", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Cases_that_need_a_look_show_expected_and_read_values()
    {
        var scores = AllRight().ToList();
        scores[3] = scores[3] with { OutcomeCorrect = false, Fields = [new FieldScore("BT-146", "unit price", 1, false, true, "4.89", "4.8")] };

        var md = EvaluationReport.ToMarkdown(Info(true), scores);

        Assert.Contains("unit price (line 1): expected `4.89`, read `4.8` (flagged)", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Harness_runs_are_marked_as_not_a_measurement()
    {
        var md = EvaluationReport.ToMarkdown(Info(false), AllRight());

        Assert.Contains("NOT A MEASUREMENT", md, StringComparison.Ordinal);
        Assert.DoesNotContain("NOT A MEASUREMENT", EvaluationReport.ToMarkdown(Info(true), AllRight()), StringComparison.Ordinal);
    }
}
