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

namespace EInvoice.IntegrationTests.Demo;

/// <summary>
/// The demo files tell the story they promise: uploaded one after the other into the seeded database, as in a demo,
/// each gets its stated status, match outcome and nothing else. Rolled back afterwards. The KoSIT check is a stand-in
/// here; the files' conformance is the builders' (XRechnungBuilderKositTests).
/// </summary>
[Trait("Category", "Database")]
[Collection(SqlServerCollection.Name)]
public sealed class DemoInvoiceTests(SqlServerFixture fixture) : IAsyncLifetime
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

    private sealed class AcceptingConformance : IConformanceValidator
    {
        public Task<ConformanceResult> CheckAsync(ReadOnlyMemory<byte> xml, CancellationToken cancellationToken) =>
            Task.FromResult(ConformanceResult.Accepted);
    }

    private InvoiceIntake Intake()
    {
        var data = new EfValidationDataSource(_db);
        return new InvoiceIntake(
            new XRechnungParser(),
            new InvoiceValidator(data, new AcceptingConformance()),
            data,
            new SqlInvoiceMatcher(_db),
            new EfIntakeStore(_db),
            MatchingPolicy.Default,
            IntakeOptions.Default,
            TimeProvider.System,
            new DisabledInvoiceExtractor(), // the demo runs with AI off
            new PdfEmbeddedInvoiceReader(PdfLimits.Default, IntakeOptions.Default.MaxDocumentBytes));
    }

    [Fact]
    public async Task Every_demo_file_gets_the_outcome_its_story_promises()
    {
        var intake = Intake();
        var failures = new List<string>();

        foreach (var demo in DemoInvoices.All)
        {
            var result = await intake.SubmitAsync(new UploadedFile(demo.FileName, demo.Content), "clerk@example.com", CancellationToken.None);
            if (result is not { Outcome: IntakeOutcome.Created, InvoiceId: { } id })
            {
                failures.Add($"{demo.FileName}: {result.Outcome} {result.Detail}");
                continue;
            }

            var outcomes = await _db.MatchResults.Where(m => m.InvoiceId == id).Select(m => m.Outcome).ToListAsync();
            var findings = await _db.InvoiceFindings.Where(f => f.InvoiceId == id).Select(f => f.RuleCode).ToListAsync();
            var matchOk = demo.Expect.Match switch
            {
                null => outcomes.Count == 0,
                MatchOutcome.FullMatch => outcomes.Count > 0 && outcomes.All(o => o == MatchOutcome.FullMatch),
                { } expected => outcomes.Contains(expected),
            };
            var findingsOk = demo.Expect.Finding is { } code ? findings.SequenceEqual([code]) : findings.Count == 0;
            if (result.Status != demo.Expect.Status || !matchOk || (demo.Expect.Status != InvoiceStatus.Failed && !findingsOk))
            {
                failures.Add($"{demo.FileName}: {result.Status}, match [{string.Join(", ", outcomes)}], findings [{string.Join(", ", findings)}]");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public async Task Uploading_the_first_demo_file_again_is_refused_as_a_duplicate()
    {
        var intake = Intake();
        var first = DemoInvoices.All[0];

        await intake.SubmitAsync(new UploadedFile(first.FileName, first.Content), "clerk@example.com", CancellationToken.None);
        var again = await intake.SubmitAsync(new UploadedFile("copy.xml", first.Content), "clerk@example.com", CancellationToken.None);

        Assert.Equal(IntakeOutcome.DuplicateFile, again.Outcome);
    }
}
