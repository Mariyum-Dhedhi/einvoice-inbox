using EInvoice.Application.Posting;
using EInvoice.Application.Validation;
using EInvoice.Domain.Invoices;
using EInvoice.Domain.Matching;
using EInvoice.Domain.Shared;
using EInvoice.Infrastructure.Persistence;
using EInvoice.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace EInvoice.IntegrationTests.Kpi;

/// <summary>
/// The four KPI views in db/views, specified by their results (the views themselves are written by hand in SQL).
/// Other tests share the database, so each test works in a rolled-back transaction and compares the view before and
/// after adding its own invoices; the month test uses months (2031) no other test touches and checks exact numbers.
/// </summary>
[Trait("Category", "Database")]
[Collection(SqlServerCollection.Name)]
public sealed class KpiViewTests(SqlServerFixture fixture) : IAsyncLifetime
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

    // ---- Rows as the views return them --------------------------------------------------------------------------------

    private sealed record StatusRow(int Status, int InvoiceCount, decimal GrossTotalEur);

    private sealed record MonthRow(DateOnly Month, int Received, int StraightThrough, int Exceptions, int Failed);

    private sealed record ReasonRow(string Kind, string Reason, int OpenInvoices);

    private sealed record PostingRow(int WaitingToPost, int Retrying, int Failed, int Posted, DateTimeOffset? OldestWaitingSince);

    private Task<List<StatusRow>> ByStatusAsync() =>
        _db.Database.SqlQuery<StatusRow>($"SELECT Status, InvoiceCount, GrossTotalEur FROM dbo.vw_InvoicesByStatus").ToListAsync();

    private Task<List<MonthRow>> MonthsAsync() =>
        _db.Database.SqlQuery<MonthRow>($"SELECT Month, Received, StraightThrough, Exceptions, Failed FROM dbo.vw_MonthlyThroughput").ToListAsync();

    private Task<List<ReasonRow>> ReasonsAsync() =>
        _db.Database.SqlQuery<ReasonRow>($"SELECT Kind, Reason, OpenInvoices FROM dbo.vw_OpenExceptionReasons").ToListAsync();

    private Task<List<PostingRow>> PostingAsync() =>
        _db.Database.SqlQuery<PostingRow>($"SELECT WaitingToPost, Retrying, Failed, Posted, OldestWaitingSince FROM dbo.vw_PostingHealth").ToListAsync();

    // ---- Invoices with a chosen history ----------------------------------------------------------------------------

    /// <summary>
    /// An invoice received at <paramref name="received"/> that walks <paramref name="path"/> (Parsed via MarkParsed),
    /// one minute per step, with the given gross amount (net + 19 % VAT, as an invoice would state it).
    /// </summary>
    private async Task<Invoice> InvoiceAsync(DateTimeOffset received, decimal gross, string currency, params InvoiceStatus[] path)
    {
        var invoice = Invoice.Receive(SourceFormat.XRechnung, "clerk@test", received);
        var at = received;
        foreach (var step in path)
        {
            at = at.AddMinutes(1);
            if (step == InvoiceStatus.Parsed)
            {
                var net = decimal.Round(gross / 1.19m, 2);
                var line = new InvoiceLine(1, "Item", 1m, net, new Money(net, currency), 19m, purchaseOrderLineNumber: 1);
                invoice.MarkParsed(new ParsedInvoice($"K{Guid.NewGuid():N}"[..20], null, new DateOnly(2031, 1, 2), null,
                    new Money(net, currency), new Money(gross - net, currency), new Money(gross, currency), [line]), "system", at);
            }
            else
            {
                invoice.TransitionTo(step, "system", at, step == InvoiceStatus.Approved ? "reason" : null);
            }
        }

        _db.Invoices.Add(invoice);
        await _db.SaveChangesAsync();
        return invoice;
    }

    private Task<Invoice> InvoiceAsync(DateTimeOffset received, params InvoiceStatus[] path) => InvoiceAsync(received, 119m, "EUR", path);

    private static readonly InvoiceStatus[] ToException = [InvoiceStatus.Parsed, InvoiceStatus.Exception];
    private static readonly InvoiceStatus[] ToMatched = [InvoiceStatus.Parsed, InvoiceStatus.Validated, InvoiceStatus.Matched];
    private static readonly DateTimeOffset Now2026 = new(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);

    // ---- 1. dbo.vw_InvoicesByStatus ---------------------------------------------------------------------------------

    [Fact]
    public async Task By_status_counts_invoices_and_sums_their_gross_amounts_in_euro()
    {
        var before = await ByStatusAsync();

        await InvoiceAsync(Now2026, 100.00m, "EUR", ToException);
        await InvoiceAsync(Now2026, 50.50m, "EUR", ToException);
        await InvoiceAsync(Now2026, 30.00m, "USD", ToException);      // counted, but not added to a euro sum
        await InvoiceAsync(Now2026, InvoiceStatus.Failed);            // no amount at all

        var after = await ByStatusAsync();
        StatusRow Row(List<StatusRow> rows, InvoiceStatus s) => rows.SingleOrDefault(r => r.Status == (int)s) ?? new StatusRow((int)s, 0, 0m);
        Assert.Equal((3, 150.50m), (Row(after, InvoiceStatus.Exception).InvoiceCount - Row(before, InvoiceStatus.Exception).InvoiceCount,
            Row(after, InvoiceStatus.Exception).GrossTotalEur - Row(before, InvoiceStatus.Exception).GrossTotalEur));
        Assert.Equal((1, 0m), (Row(after, InvoiceStatus.Failed).InvoiceCount - Row(before, InvoiceStatus.Failed).InvoiceCount,
            Row(after, InvoiceStatus.Failed).GrossTotalEur - Row(before, InvoiceStatus.Failed).GrossTotalEur));
    }

    [Fact]
    public async Task By_status_has_one_row_per_status_that_has_invoices_and_never_a_null_sum()
    {
        await InvoiceAsync(Now2026, InvoiceStatus.Failed);

        var rows = await ByStatusAsync();

        Assert.Equal(rows.Count, rows.Select(r => r.Status).Distinct().Count());
        Assert.All(rows, r => Assert.True(r.InvoiceCount > 0, $"status {r.Status} has a row without invoices"));
        Assert.Contains(rows, r => r.Status == (int)InvoiceStatus.Failed && r.GrossTotalEur == 0m); // 0, not NULL
    }

    // ---- 2. dbo.vw_MonthlyThroughput --------------------------------------------------------------------------------

    [Fact]
    public async Task Monthly_throughput_counts_by_the_berlin_month_of_receipt()
    {
        // 31 Oct 23:30 UTC is already 1 November in Berlin (winter time, UTC+1).
        await InvoiceAsync(new DateTimeOffset(2031, 10, 31, 23, 30, 0, TimeSpan.Zero), [.. ToMatched, InvoiceStatus.Approved]); // straight through
        await InvoiceAsync(new DateTimeOffset(2031, 11, 5, 8, 0, 0, TimeSpan.Zero), [.. ToException, InvoiceStatus.Approved]);   // needed a person
        await InvoiceAsync(new DateTimeOffset(2031, 11, 6, 8, 0, 0, TimeSpan.Zero), InvoiceStatus.Failed);                       // unreadable
        await InvoiceAsync(new DateTimeOffset(2031, 10, 15, 8, 0, 0, TimeSpan.Zero), ToMatched);                                  // not decided yet
        // 31 Mar 22:30 UTC is already 1 April in Berlin (summer time, UTC+2).
        await InvoiceAsync(new DateTimeOffset(2031, 3, 31, 22, 30, 0, TimeSpan.Zero), [.. ToMatched, InvoiceStatus.Approved, InvoiceStatus.Posted]);

        var months = (await MonthsAsync()).Where(m => m.Month.Year == 2031).OrderBy(m => m.Month).ToList();

        Assert.Equal(
            new[]
            {
                new MonthRow(new DateOnly(2031, 4, 1), 1, 1, 0, 0),
                new MonthRow(new DateOnly(2031, 10, 1), 1, 0, 0, 0),
                new MonthRow(new DateOnly(2031, 11, 1), 3, 1, 1, 1),
            },
            months);
    }

    [Fact]
    public async Task An_invoice_that_was_an_exception_once_is_never_straight_through()
    {
        // Exception, then matched again after a late goods receipt, then approved: a person was still needed once.
        await InvoiceAsync(new DateTimeOffset(2031, 12, 1, 8, 0, 0, TimeSpan.Zero),
            InvoiceStatus.Parsed, InvoiceStatus.Exception, InvoiceStatus.Matched, InvoiceStatus.Approved);

        var december = Assert.Single(await MonthsAsync(), m => m.Month == new DateOnly(2031, 12, 1));

        Assert.Equal(new MonthRow(new DateOnly(2031, 12, 1), 1, 0, 1, 0), december);
    }

    // ---- 3. dbo.vw_OpenExceptionReasons -----------------------------------------------------------------------------

    private void Finding(Invoice invoice, int sequence, string code) => _db.InvoiceFindings.Add(new InvoiceFindingRecord
    {
        InvoiceId = invoice.Id, Sequence = sequence, RuleCode = code, Severity = FindingSeverity.Review, Message = "m", LinePosition = sequence,
    });

    private void Match(Invoice invoice, int line, MatchOutcome outcome) => _db.MatchResults.Add(new MatchResultRecord
    {
        InvoiceId = invoice.Id, InvoiceLineNumber = line, Outcome = outcome, InvoiceUnitPrice = 1m, Currency = "EUR",
        QuantityInvoiced = 1m, PriceTolerancePercent = 2m, MatchedAt = Now2026,
    });

    [Fact]
    public async Task Open_exception_reasons_count_each_invoice_once_per_reason()
    {
        var before = await ReasonsAsync();
        var first = await InvoiceAsync(Now2026, ToException);
        Finding(first, 1, RuleCodes.IbanNotApproved);
        Finding(first, 2, RuleCodes.LineSum);
        Match(first, 1, MatchOutcome.PriceVariance);
        Match(first, 2, MatchOutcome.FullMatch); // not a reason
        var second = await InvoiceAsync(Now2026, ToException);
        Finding(second, 1, RuleCodes.IbanNotApproved);
        Finding(second, 2, RuleCodes.IbanNotApproved); // the same reason twice on one invoice: still one invoice
        Match(second, 1, MatchOutcome.NoGoodsReceipt);
        var decided = await InvoiceAsync(Now2026, [.. ToException, InvoiceStatus.Approved]); // no longer open
        Finding(decided, 1, RuleCodes.IbanNotApproved);
        await _db.SaveChangesAsync();

        var after = await ReasonsAsync();

        int Delta(string kind, string reason) =>
            (after.SingleOrDefault(r => r.Kind == kind && r.Reason == reason)?.OpenInvoices ?? 0)
            - (before.SingleOrDefault(r => r.Kind == kind && r.Reason == reason)?.OpenInvoices ?? 0);
        Assert.Equal(2, Delta("Finding", RuleCodes.IbanNotApproved));
        Assert.Equal(1, Delta("Finding", RuleCodes.LineSum));
        Assert.Equal(1, Delta("Match", nameof(MatchOutcome.PriceVariance)));
        Assert.Equal(1, Delta("Match", nameof(MatchOutcome.NoGoodsReceipt)));
        Assert.DoesNotContain(after, r => r.Reason == nameof(MatchOutcome.FullMatch));
        Assert.Equal(after.Count, after.Select(r => (r.Kind, r.Reason)).Distinct().Count());
    }

    [Fact]
    public async Task Every_match_outcome_is_named_like_the_application_names_it()
    {
        var invoice = await InvoiceAsync(Now2026, ToException);
        var line = 1;
        foreach (var outcome in Enum.GetValues<MatchOutcome>().Where(o => o != MatchOutcome.FullMatch))
        {
            Match(invoice, line++, outcome);
        }

        await _db.SaveChangesAsync();

        var names = (await ReasonsAsync()).Where(r => r.Kind == "Match").Select(r => r.Reason).ToHashSet();
        Assert.All(Enum.GetNames<MatchOutcome>().Where(n => n != nameof(MatchOutcome.FullMatch)), n => Assert.Contains(n, names));
    }

    // ---- 4. dbo.vw_PostingHealth ------------------------------------------------------------------------------------

    private void Job(Invoice invoice, PostingJobStatus status, int attempts, DateTimeOffset created) => _db.PostingJobs.Add(new PostingJobRecord
    {
        InvoiceId = invoice.Id, Status = status, Attempts = attempts, CreatedAt = created, NextAttemptAt = created,
    });

    [Fact]
    public async Task Posting_health_is_one_row_of_counts_and_the_oldest_waiting_job()
    {
        var before = Assert.Single(await PostingAsync());
        var invoice = await InvoiceAsync(Now2026, ToMatched);
        Job(invoice, PostingJobStatus.Pending, attempts: 0, new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Job(invoice, PostingJobStatus.Pending, attempts: 2, new DateTimeOffset(1999, 6, 1, 0, 0, 0, TimeSpan.Zero)); // retrying, and older
        Job(invoice, PostingJobStatus.Dead, attempts: 6, Now2026);
        Job(invoice, PostingJobStatus.Done, attempts: 1, Now2026);
        Job(invoice, PostingJobStatus.Done, attempts: 3, Now2026);
        await _db.SaveChangesAsync();

        var after = Assert.Single(await PostingAsync());

        Assert.Equal((1, 1, 1, 2), (after.WaitingToPost - before.WaitingToPost, after.Retrying - before.Retrying, after.Failed - before.Failed, after.Posted - before.Posted));
        Assert.Equal(new DateTimeOffset(1999, 6, 1, 0, 0, 0, TimeSpan.Zero), after.OldestWaitingSince); // waiting includes retrying
    }

    [Fact]
    public async Task Posting_health_reads_zero_not_null_when_nothing_waits()
    {
        var rows = await PostingAsync();

        var row = Assert.Single(rows); // always exactly one row, even with no jobs at all
        Assert.True(row.WaitingToPost >= 0 && row.Retrying >= 0 && row.Failed >= 0 && row.Posted >= 0);
    }
}
