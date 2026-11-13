using EInvoice.Domain.Matching;
using EInvoice.IntegrationTests.Infrastructure;

namespace EInvoice.IntegrationTests.Matching;

/// <summary>
/// Two invoices that together bill more than was delivered, matched at the same moment on two connections.
/// Neither may slip through. Data is committed (two connections cannot share a transaction) under unique
/// numbers, so it does not affect other tests.
/// </summary>
[Trait("Category", "Database")]
[Collection(SqlServerCollection.Name)]
public sealed class MatchConcurrencyTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task Overbilling_invoices_matched_in_parallel_are_both_flagged()
    {
        for (var round = 0; round < 5; round++)
        {
            await using var setup = fixture.CreateContext();
            var scenario = await new MatchScenario(setup).OrderLine(1, 10, 10m).Received(1, 10).SaveOrderAsync();
            var a = await scenario.AddInvoiceAsync((1, 6, 10m, null));
            var b = await scenario.AddInvoiceAsync((1, 6, 10m, null));

            await using var first = fixture.CreateContext();
            await using var second = fixture.CreateContext();
            var results = await Task.WhenAll(
                Task.Run(() => new MatchScenario(first).MatchInSqlAsync(a)),
                Task.Run(() => new MatchScenario(second).MatchInSqlAsync(b)));

            Assert.All(results, r => Assert.Equal(MatchOutcome.QuantityVariance, r.Lines[0].Outcome));
        }
    }
}
