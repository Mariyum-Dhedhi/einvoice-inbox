using EInvoice.Domain.Invoices;
using EInvoice.Domain.Matching;
using EInvoice.Infrastructure.Matching;
using EInvoice.Infrastructure.Persistence;
using EInvoice.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace EInvoice.IntegrationTests.Matching;

/// <summary>
/// dbo.usp_MatchInvoice against a real SQL Server, called as the application user.
/// Each test runs in a transaction that is rolled back.
/// </summary>
[Trait("Category", "Database")]
[Collection(SqlServerCollection.Name)]
public sealed class MatchProcedureTests(SqlServerFixture fixture) : IAsyncLifetime
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

    // ---- Done when: all five outcomes are produced from the seed data --------------------------------------

    [Theory]
    [InlineData("RE-2026-0001", MatchOutcome.FullMatch)]
    [InlineData("RE-2026-0002", MatchOutcome.FullMatch)]        // +1.5%, inside the 2% tolerance
    [InlineData("RE-2026-0003", MatchOutcome.PriceVariance)]    // +5%
    [InlineData("RE-2026-0004", MatchOutcome.QuantityVariance)] // 2 more than received on every line
    [InlineData("RE-2026-0005", MatchOutcome.NoGoodsReceipt)]   // order PO-0010 has no receipt
    [InlineData("RE-2026-0006", MatchOutcome.NoPurchaseOrder)]  // states order PO-9999, which does not exist
    public async Task Seeded_invoice_gets_its_expected_outcome_on_every_line(string invoiceNumber, MatchOutcome expected)
    {
        var invoiceId = await _db.Invoices.Where(i => i.InvoiceNumber == invoiceNumber).Select(i => i.Id).SingleAsync();

        var result = await new SqlInvoiceMatcher(_db).MatchAsync(invoiceId, MatchingPolicy.Default, CancellationToken.None);

        Assert.NotEmpty(result.Lines);
        Assert.All(result.Lines, l => Assert.Equal(expected, l.Outcome));
    }

    [Fact]
    public async Task All_five_outcomes_are_produced_from_the_seed_data()
    {
        var matcher = new SqlInvoiceMatcher(_db);
        var outcomes = new HashSet<MatchOutcome>();
        foreach (var id in await _db.Invoices.Where(i => i.InvoiceNumber!.StartsWith("RE-2026-")).Select(i => i.Id).ToListAsync())
        {
            outcomes.UnionWith((await matcher.MatchAsync(id, MatchingPolicy.Default, CancellationToken.None)).Lines.Select(l => l.Outcome));
        }

        Assert.Equal(Enum.GetValues<MatchOutcome>().Order(), outcomes.Order());
    }

    // ---- SQL and C# cannot drift: the same data through both ---------------------------------------------

    public static TheoryData<bool, bool, bool, bool> DecisionTable()
    {
        var data = new TheoryData<bool, bool, bool, bool>();
        foreach (var order in new[] { false, true })
        {
            foreach (var receipt in new[] { false, true })
            {
                foreach (var priceOk in new[] { false, true })
                {
                    foreach (var quantityOk in new[] { false, true })
                    {
                        data.Add(order, receipt, priceOk, quantityOk);
                    }
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(DecisionTable))]
    public async Task Decision_table_row_gives_the_same_result_in_sql_and_csharp(bool order, bool receipt, bool priceOk, bool quantityOk)
    {
        var scenario = new MatchScenario(_db);
        if (order)
        {
            scenario.OrderLine(1, quantity: 10, unitPrice: 100m);
            if (receipt)
            {
                scenario.Received(1, 10);
            }
        }
        else
        {
            scenario.NoOrder();
        }

        await scenario.SaveOrderAsync();
        var invoice = await scenario.AddInvoiceAsync((1, quantityOk ? 10 : 12, priceOk ? 101m : 105m, null));

        MatchScenario.AssertSame(scenario.MatchInCSharp(invoice), await scenario.MatchInSqlAsync(invoice));
    }

    [Theory]
    [InlineData("100.00", MatchOutcome.FullMatch)]
    [InlineData("101.99", MatchOutcome.FullMatch)]
    [InlineData("102.00", MatchOutcome.FullMatch)]       // exactly on the boundary
    [InlineData("102.001", MatchOutcome.PriceVariance)]  // 2.001%: must not be rounded down
    [InlineData("102.01", MatchOutcome.PriceVariance)]
    [InlineData("95.00", MatchOutcome.FullMatch)]        // lower prices are accepted
    public async Task Price_boundaries_agree_with_csharp(string invoicePrice, MatchOutcome expected)
    {
        var price = decimal.Parse(invoicePrice, System.Globalization.CultureInfo.InvariantCulture);
        var scenario = await new MatchScenario(_db).OrderLine(1, 10, 100.00m).Received(1, 10).SaveOrderAsync();
        var invoice = await scenario.AddInvoiceAsync((1, 1, price, null));

        var sql = await scenario.MatchInSqlAsync(invoice);

        Assert.Equal(expected, sql.Lines[0].Outcome);
        MatchScenario.AssertSame(scenario.MatchInCSharp(invoice), sql);
    }

    [Theory]
    [InlineData("0", MatchOutcome.FullMatch)]
    [InlineData("0.01", MatchOutcome.PriceVariance)]
    public async Task Free_order_line_flags_any_charge_without_dividing_by_zero(string invoicePrice, MatchOutcome expected)
    {
        var price = decimal.Parse(invoicePrice, System.Globalization.CultureInfo.InvariantCulture);
        var scenario = await new MatchScenario(_db).OrderLine(1, 10, 0m).Received(1, 10).SaveOrderAsync();
        var invoice = await scenario.AddInvoiceAsync((1, 1, price, null));

        var sql = await scenario.MatchInSqlAsync(invoice);

        Assert.Equal(expected, sql.Lines[0].Outcome);
        Assert.Null(sql.Lines[0].PriceVariancePercent);
        MatchScenario.AssertSame(scenario.MatchInCSharp(invoice), sql);
    }

    [Fact]
    public async Task Tolerance_is_a_parameter()
    {
        var scenario = await new MatchScenario(_db).OrderLine(1, 10, 100.00m).Received(1, 10).SaveOrderAsync();
        var invoice = await scenario.AddInvoiceAsync((1, 1, 100.01m, null));
        var strict = new MatchingPolicy(PriceTolerancePercent: 0m);

        var sql = await scenario.MatchInSqlAsync(invoice, strict);

        Assert.Equal(MatchOutcome.PriceVariance, sql.Lines[0].Outcome);
        MatchScenario.AssertSame(scenario.MatchInCSharp(invoice, policy: strict), sql);
    }

    [Fact]
    public async Task Two_invoice_lines_on_the_same_order_line_share_the_received_quantity()
    {
        // 10 received; lines of 6 and 6. The window function gives the second line only the remaining 4.
        var scenario = await new MatchScenario(_db).OrderLine(1, 10, 10m).Received(1, 10).SaveOrderAsync();
        var invoice = await scenario.AddInvoiceAsync((1, 6, 10m, 1), (2, 6, 10m, 1));

        var sql = await scenario.MatchInSqlAsync(invoice);

        Assert.Equal([MatchOutcome.FullMatch, MatchOutcome.QuantityVariance], sql.Lines.Select(l => l.Outcome));
        Assert.Equal(4m, sql.Lines[1].QuantityAvailable);
        MatchScenario.AssertSame(scenario.MatchInCSharp(invoice), sql);
    }

    [Fact]
    public async Task Partial_deliveries_are_summed_and_several_order_lines_are_matched_at_once()
    {
        var scenario = await new MatchScenario(_db)
            .OrderLine(1, 10, 10m).OrderLine(2, 5, 0.0125m).OrderLine(3, 4, 50m)
            .Received(1, 4).Received(1, 6).Received(2, 5)
            .SaveOrderAsync();
        var invoice = await scenario.AddInvoiceAsync((1, 10, 10m, null), (2, 5, 0.0127m, null), (3, 4, 50m, null), (4, 1, 1m, 9));

        var sql = await scenario.MatchInSqlAsync(invoice);

        Assert.Equal(
            [MatchOutcome.FullMatch, MatchOutcome.FullMatch, MatchOutcome.NoGoodsReceipt, MatchOutcome.NoPurchaseOrder],
            sql.Lines.Select(l => l.Outcome)); // line 2: 0.0127 vs 0.0125 is +1.6%
        MatchScenario.AssertSame(scenario.MatchInCSharp(invoice), sql);
    }

    // ---- Already invoiced: every other invoice except rejected ones --------------------------------------

    [Fact]
    public async Task Two_invoices_that_together_overbill_a_delivery_are_both_flagged()
    {
        var scenario = await new MatchScenario(_db).OrderLine(1, 10, 10m).Received(1, 10).SaveOrderAsync();
        var a = await scenario.AddInvoiceAsync((1, 6, 10m, null));
        var b = await scenario.AddInvoiceAsync((1, 6, 10m, null));

        var sqlA = await scenario.MatchInSqlAsync(a);
        var sqlB = await scenario.MatchInSqlAsync(b);

        Assert.Equal(MatchOutcome.QuantityVariance, sqlA.Lines[0].Outcome);
        Assert.Equal(MatchOutcome.QuantityVariance, sqlB.Lines[0].Outcome);
        Assert.Equal(4m, sqlA.Lines[0].QuantityAvailable);
        MatchScenario.AssertSame(scenario.MatchInCSharp(a, new Dictionary<int, decimal> { [1] = 6m }), sqlA);
    }

    [Fact]
    public async Task Two_partial_invoices_that_fit_the_delivery_both_match()
    {
        var scenario = await new MatchScenario(_db).OrderLine(1, 10, 10m).Received(1, 10).SaveOrderAsync();
        var a = await scenario.AddInvoiceAsync((1, 4, 10m, null));
        var b = await scenario.AddInvoiceAsync((1, 6, 10m, null));

        Assert.True((await scenario.MatchInSqlAsync(a)).IsFullMatch);
        Assert.True((await scenario.MatchInSqlAsync(b)).IsFullMatch);
    }

    [Fact]
    public async Task Rejected_invoice_releases_its_quantity()
    {
        var scenario = await new MatchScenario(_db).OrderLine(1, 10, 10m).Received(1, 10).SaveOrderAsync();
        var rejected = await scenario.AddInvoiceAsync((1, 6, 10m, null));
        rejected.TransitionTo(InvoiceStatus.Exception, "system", DateTimeOffset.UtcNow, "QuantityVariance");
        rejected.TransitionTo(InvoiceStatus.Rejected, "approver@test", DateTimeOffset.UtcNow);
        await _db.SaveChangesAsync();
        var other = await scenario.AddInvoiceAsync((1, 10, 10m, null));

        var sql = await scenario.MatchInSqlAsync(other);

        Assert.True(sql.IsFullMatch);
        Assert.Equal(10m, sql.Lines[0].QuantityAvailable);
    }

    // ---- Storage ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Results_are_stored_and_running_again_replaces_them()
    {
        var scenario = await new MatchScenario(_db).OrderLine(1, 10, 100m).Received(1, 10).SaveOrderAsync();
        var invoice = await scenario.AddInvoiceAsync((1, 10, 105m, null), (2, 1, 1m, null));

        var first = await scenario.MatchInSqlAsync(invoice);
        var second = await scenario.MatchInSqlAsync(invoice);

        MatchScenario.AssertSame(first, second);
        var stored = await _db.MatchResults.AsNoTracking().Where(r => r.InvoiceId == invoice.Id).OrderBy(r => r.InvoiceLineNumber).ToListAsync();
        Assert.Equal(2, stored.Count);
        Assert.Equal(MatchOutcome.PriceVariance, stored[0].Outcome);
        Assert.Equal(2.00m, stored[0].PriceTolerancePercent); // the tolerance used is recorded with the result
        Assert.Equal(MatchOutcome.NoPurchaseOrder, stored[1].Outcome);
    }

    [Fact]
    public async Task Unknown_invoice_is_an_error_not_an_empty_result()
    {
        await Assert.ThrowsAnyAsync<Exception>(() =>
            new SqlInvoiceMatcher(_db).MatchAsync(Guid.NewGuid(), MatchingPolicy.Default, CancellationToken.None));
    }
}
