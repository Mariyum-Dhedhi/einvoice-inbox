using EInvoice.AiEvaluation;
using EInvoice.Application.Extraction;
using EInvoice.Application.Intake;
using EInvoice.Application.Validation;
using EInvoice.Domain.Invoices;
using EInvoice.Domain.Matching;
using EInvoice.Infrastructure.Extraction;
using EInvoice.Infrastructure.Intake;
using EInvoice.Infrastructure.Matching;
using EInvoice.Infrastructure.Parsing;
using EInvoice.Infrastructure.Persistence;
using EInvoice.Infrastructure.Validation;
using EInvoice.IntegrationTests.Infrastructure;
using EInvoice.IntegrationTests.TestData;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using OllamaSharp;

namespace EInvoice.IntegrationTests.Demo;

/// <summary>
/// The AI demo PDFs tell the story they promise against the seeded database, read through the real pipeline
/// (visible text only, masking, evidence check, rules, matcher). Here the model is the oracle that answers with the
/// true values: if a demo fails here, the file or the seed is wrong, not a model. Runs in CI. Rolled back.
/// </summary>
[Trait("Category", "Database")]
[Collection(SqlServerCollection.Name)]
public sealed class DemoAiInvoiceTests(SqlServerFixture fixture) : IAsyncLifetime
{
    private EInvoiceDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _db = fixture.CreateContext();
        await _db.Database.BeginTransactionAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.Database.RollbackTransactionAsync();
        await _db.DisposeAsync();
    }

    public static TheoryData<string> Files() => new(DemoAiInvoices.All.Select(d => d.FileName));

    [Theory]
    [MemberData(nameof(Files))]
    public async Task With_a_perfect_reader_each_ai_demo_file_gets_its_promised_outcome(string fileName)
    {
        var demo = DemoAiInvoices.All.Single(d => d.FileName == fileName);

        var failure = await DemoAiCheck.SubmitAsync(_db, demo, new OracleModel(demo.Case));

        Assert.True(failure is null, failure);
    }
}

/// <summary>
/// The same files with the real local model (qwen2.5:7b in Ollama), the way the demo runs them. Local only, never in
/// CI, and not a measurement (that is testdata/ai-eval): it checks that the demo works on this machine before showing it.
/// Needs EINVOICE_OLLAMA_URL and EINVOICE_OLLAMA_MODEL, Docker for the database, and takes a minute or two.
/// </summary>
[Trait("Category", "Ollama")]
[Collection(SqlServerCollection.Name)]
public sealed class DemoAiOllamaTests(SqlServerFixture fixture) : IAsyncLifetime
{
    private EInvoiceDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _db = fixture.CreateContext();
        await _db.Database.BeginTransactionAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.Database.RollbackTransactionAsync();
        await _db.DisposeAsync();
    }

    public static TheoryData<string> Files() => new(DemoAiInvoices.All.Select(d => d.FileName));

    [Theory]
    [MemberData(nameof(Files))]
    public async Task The_real_model_gives_each_ai_demo_file_its_promised_outcome(string fileName)
    {
        var url = Environment.GetEnvironmentVariable("EINVOICE_OLLAMA_URL") ?? throw new InvalidOperationException("Set EINVOICE_OLLAMA_URL.");
        var modelName = Environment.GetEnvironmentVariable("EINVOICE_OLLAMA_MODEL") ?? throw new InvalidOperationException("Set EINVOICE_OLLAMA_MODEL.");
        using var model = new OllamaApiClient(new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromMinutes(5) }, modelName);
        var demo = DemoAiInvoices.All.Single(d => d.FileName == fileName);

        var failure = await DemoAiCheck.SubmitAsync(_db, demo, model);

        Assert.True(failure is null, failure);
    }
}

internal static class DemoAiCheck
{
    private sealed class AcceptingConformance : IConformanceValidator
    {
        public Task<ConformanceResult> CheckAsync(ReadOnlyMemory<byte> xml, CancellationToken cancellationToken) =>
            Task.FromResult(ConformanceResult.Accepted);
    }

    /// <summary>Uploads the file as a clerk would, with AI on; null when the outcome is the promised one, else what happened.</summary>
    public static async Task<string?> SubmitAsync(EInvoiceDbContext db, DemoAiInvoice demo, IChatClient model)
    {
        var data = new EfValidationDataSource(db);
        var intake = new InvoiceIntake(
            new XRechnungParser(),
            new InvoiceValidator(data, new AcceptingConformance()),
            data,
            new SqlInvoiceMatcher(db),
            new EfIntakeStore(db),
            MatchingPolicy.Default,
            IntakeOptions.Default,
            TimeProvider.System,
            new LlmInvoiceExtractor(new PdfTextReader(PdfLimits.Default), model),
            new PdfEmbeddedInvoiceReader(PdfLimits.Default, IntakeOptions.Default.MaxDocumentBytes));

        var result = await intake.SubmitAsync(new UploadedFile(demo.FileName, demo.Content), "clerk@example.com", CancellationToken.None);
        if (result is not { Outcome: IntakeOutcome.Created, InvoiceId: { } id })
        {
            return $"{result.Outcome} {result.Detail}";
        }

        var invoice = await db.Invoices.AsNoTracking().SingleAsync(i => i.Id == id);
        var outcomes = await db.MatchResults.Where(m => m.InvoiceId == id).Select(m => m.Outcome).ToListAsync();
        var findings = await db.InvoiceFindings.Where(f => f.InvoiceId == id).Select(f => f.RuleCode).ToListAsync();
        var truth = demo.Case.Truth;

        var matchOk = demo.Expect.Match switch
        {
            null => outcomes.Count == 0,
            MatchOutcome.FullMatch => outcomes.Count > 0 && outcomes.All(o => o == MatchOutcome.FullMatch),
            { } expected => outcomes.Contains(expected),
        };
        var findingsOk = demo.Expect.Finding is { } code ? findings.SequenceEqual([code]) : findings.Count == 0;
        // Whatever the model did, the amounts the app holds are the document's: an injection cannot change them.
        var valuesOk = invoice.SourceFormat == SourceFormat.Pdf && invoice.GrossTotal?.Amount == truth.GrossTotal;

        return result.Status == demo.Expect.Status && matchOk && findingsOk && valuesOk
            ? null
            : $"{demo.FileName}: {result.Status}, gross {invoice.GrossTotal?.Amount} (document {truth.GrossTotal}), match [{string.Join(", ", outcomes)}], findings [{string.Join(", ", findings)}]";
    }
}
