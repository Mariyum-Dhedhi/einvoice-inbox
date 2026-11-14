using EInvoice.Application.Intake;
using EInvoice.Application.Validation;
using EInvoice.Domain.Matching;
using EInvoice.Infrastructure.Intake;
using EInvoice.Infrastructure.Matching;
using EInvoice.Infrastructure.Parsing;
using EInvoice.Infrastructure.Validation;
using EInvoice.IntegrationTests.Infrastructure;
using EInvoice.IntegrationTests.TestData;

namespace EInvoice.IntegrationTests.Intake;

/// <summary>
/// Two uploads arriving at the same moment. The validation check ("does this number exist?") cannot see an upload
/// that is not committed yet, so the unique indexes in the database are the backstop: exactly one upload wins.
/// Data is committed under unique numbers, so it does not affect other tests.
/// </summary>
[Trait("Category", "Database")]
[Collection(SqlServerCollection.Name)]
public sealed class InvoiceIntakeRaceTests(SqlServerFixture fixture)
{
    private sealed class Accepting : IConformanceValidator
    {
        public Task<ConformanceResult> CheckAsync(ReadOnlyMemory<byte> xml, CancellationToken cancellationToken) =>
            Task.FromResult(ConformanceResult.Accepted);
    }

    private Task<IntakeResult> SubmitOnOwnConnectionAsync(byte[] content)
    {
        var db = fixture.CreateContext();
        var data = new EfValidationDataSource(db);
        var intake = new InvoiceIntake(new XRechnungParser(), new InvoiceValidator(data, new Accepting()), data,
            new SqlInvoiceMatcher(db), new EfIntakeStore(db), MatchingPolicy.Default, IntakeOptions.Default, TimeProvider.System);
        return intake.SubmitAsync(new UploadedFile("invoice.xml", content), "clerk@test", CancellationToken.None)
            .ContinueWith(t => { db.Dispose(); return t.Result; }, TaskScheduler.Default);
    }

    [Fact]
    public async Task Same_vendor_and_number_uploaded_in_parallel_is_stored_once()
    {
        for (var round = 0; round < 5; round++)
        {
            var builder = new XRechnungBuilder { PurchaseOrderNumber = "PO-9999" }.Line("Item", 1m, 10m);
            var first = builder.Build();
            builder.IssueDate = builder.IssueDate.AddDays(1); // different file, same vendor and number
            var second = builder.Build();

            var results = await Task.WhenAll(Task.Run(() => SubmitOnOwnConnectionAsync(first)), Task.Run(() => SubmitOnOwnConnectionAsync(second)));

            Assert.Single(results, r => r.Outcome == IntakeOutcome.Created);
            Assert.Single(results, r => r.Outcome == IntakeOutcome.DuplicateInvoiceNumber);
        }
    }

    [Fact]
    public async Task Same_file_uploaded_in_parallel_is_stored_once()
    {
        for (var round = 0; round < 5; round++)
        {
            var file = new XRechnungBuilder { PurchaseOrderNumber = "PO-9999" }.Line("Item", 1m, 10m).Build();

            var results = await Task.WhenAll(Task.Run(() => SubmitOnOwnConnectionAsync(file)), Task.Run(() => SubmitOnOwnConnectionAsync(file)));

            Assert.Single(results, r => r.Outcome == IntakeOutcome.Created);
            var loser = Assert.Single(results, r => r.Outcome != IntakeOutcome.Created);
            Assert.Contains(loser.Outcome, new[] { IntakeOutcome.DuplicateFile, IntakeOutcome.DuplicateInvoiceNumber });
        }
    }
}
