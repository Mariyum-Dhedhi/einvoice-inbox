using EInvoice.Application.Kpi;
using EInvoice.Domain.Invoices;
using EInvoice.Domain.Shared;
using EInvoice.Infrastructure.Kpi;
using EInvoice.Infrastructure.Persistence;
using EInvoice.IntegrationTests.Infrastructure;

namespace EInvoice.IntegrationTests.Kpi;

/// <summary>
/// The dashboard's data as the application reads it from the KPI views: in a defined order (the database guarantees
/// none), the last twelve months, the biggest exception reasons first. Rolled back after each test.
/// </summary>
[Trait("Category", "Database")]
[Collection(SqlServerCollection.Name)]
public sealed class KpiQueriesTests(SqlServerFixture fixture) : IAsyncLifetime
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

    private async Task ReceivedAsync(DateTimeOffset at)
    {
        var invoice = Invoice.Receive(SourceFormat.XRechnung, "clerk@test", at);
        invoice.TransitionTo(InvoiceStatus.Failed, "system", at.AddMinutes(1), "test");
        _db.Invoices.Add(invoice);
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task Reads_all_four_kpis_in_a_defined_order()
    {
        var today = new DateOnly(2031, 6, 15);

        var dashboard = await new EfKpiQueries(_db).DashboardAsync(today, CancellationToken.None);

        Assert.Equal(dashboard.ByStatus.OrderBy(s => s.Status).Select(s => s.Status), dashboard.ByStatus.Select(s => s.Status));
        Assert.Equal(dashboard.OpenReasons.OrderByDescending(r => r.OpenInvoices).ThenBy(r => r.Reason, StringComparer.Ordinal).Select(r => r.Reason),
            dashboard.OpenReasons.Select(r => r.Reason));
        Assert.NotNull(dashboard.Posting);
    }

    [Fact]
    public async Task Shows_the_last_twelve_months_oldest_first_with_empty_months_as_zero()
    {
        await ReceivedAsync(new DateTimeOffset(2031, 6, 1, 8, 0, 0, TimeSpan.Zero));
        await ReceivedAsync(new DateTimeOffset(2031, 6, 2, 8, 0, 0, TimeSpan.Zero));
        await ReceivedAsync(new DateTimeOffset(2030, 7, 10, 8, 0, 0, TimeSpan.Zero));   // twelve months back: shown
        await ReceivedAsync(new DateTimeOffset(2030, 6, 10, 8, 0, 0, TimeSpan.Zero));   // thirteen: not shown

        var months = (await new EfKpiQueries(_db).DashboardAsync(new DateOnly(2031, 6, 15), CancellationToken.None)).Months;

        Assert.Equal(12, months.Count);
        Assert.Equal((new DateOnly(2030, 7, 1), 1), (months[0].Month, months[0].Received));
        Assert.Equal((new DateOnly(2031, 6, 1), 2), (months[^1].Month, months[^1].Received));
        Assert.Equal(0, months.Single(m => m.Month == new DateOnly(2031, 1, 1)).Received); // a quiet month is a zero, not a gap
    }

    [Theory]
    [InlineData(0, 0, null)]
    [InlineData(4, 3, 75.0)]
    [InlineData(3, 1, 33.3)]
    public void Straight_through_rate_is_a_share_of_received_or_nothing(int received, int straightThrough, double? expected)
    {
        var month = new MonthKpi(new DateOnly(2031, 1, 1), received, straightThrough, 0, 0);

        Assert.Equal(expected is null ? null : (decimal?)(decimal)expected, month.StraightThroughPercent);
    }
}
