using EInvoice.AiEvaluation;
using EInvoice.IntegrationTests.Infrastructure;

namespace EInvoice.IntegrationTests.AiEvaluation;

/// <summary>
/// Checks the evaluation itself before any model is measured with it. The oracle answers every case with the
/// perfect extraction, so the real pipeline must reach every expected answer: a failure here means the answer key,
/// the case setup or the scoring is wrong, not a model. Runs in CI (no model needed).
/// </summary>
[Trait("Category", "Database")]
[Collection(SqlServerCollection.Name)]
public sealed class OracleRunTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task Perfect_extraction_reaches_every_expected_answer()
    {
        var cases = EvalCaseFile.Load(EvalCaseFile.DefaultPath(SqlServerFixture.RepositoryRoot())).Cases;

        var results = await new EvaluationRunner(fixture.AppConnectionString)
            .RunAsync(cases, EvalPdfRenderer.Render, c => new OracleModel(c), CancellationToken.None);
        var scores = results.Select(Scorer.Score).ToList();

        Assert.Equal(27, scores.Count);
        Assert.All(scores, s => Assert.True(s.OutcomeCorrect, $"{s.Case.Id} ({s.Case.Type}): expected {s.Case.Expected.Result} {string.Join(",", s.Case.Expected.Reasons ?? [])}{s.Case.Expected.Refusal}, got {Describe(results.Single(r => r.Case.Id == s.Case.Id).Actual)}"));
        Assert.All(scores.Where(s => s.Case.Expected.Result != "Refused"), s => Assert.All(s.Fields, f => Assert.True(f.Correct, $"{s.Case.Id} {f.Name} line {f.Line}")));
        Assert.All(scores, s => Assert.False(s.WrongValueReachedMatched, s.Case.Id));
        Assert.All(scores.Where(s => s.Case.Type == "Injection"), s => Assert.True(s.InjectionResisted, s.Case.Id));
    }

    private static string Describe(CaseOutcome outcome) => $"{outcome.Result} {string.Join(",", outcome.Reasons)}{outcome.Refusal}";
}
