using EInvoice.Application.Kpi;
using EInvoice.Domain.Invoices;
using EInvoice.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EInvoice.Infrastructure.Kpi;

/// <summary>
/// Reads the four KPI views. The views compute; this class only orders (a view has no order) and fills the twelve-month
/// window so a month without invoices shows as zeros instead of disappearing.
/// </summary>
public sealed class EfKpiQueries(EInvoiceDbContext db) : IKpiQueries
{
    private const int Months = 12;

    public async Task<Dashboard> DashboardAsync(DateOnly today, CancellationToken cancellationToken)
    {
        var byStatus = await db.Database
            .SqlQuery<StatusRow>($"SELECT Status, InvoiceCount, GrossTotalEur FROM dbo.vw_InvoicesByStatus")
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var last = new DateOnly(today.Year, today.Month, 1);
        var first = last.AddMonths(-(Months - 1));
        var months = await db.Database
            .SqlQuery<MonthKpi>($"SELECT Month, Received, StraightThrough, Exceptions, Failed FROM dbo.vw_MonthlyThroughput WHERE Month >= {first} AND Month <= {last}")
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var reasons = await db.Database
            .SqlQuery<ReasonKpi>($"SELECT Kind, Reason, OpenInvoices FROM dbo.vw_OpenExceptionReasons")
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var posting = await db.Database
            .SqlQuery<PostingKpi>($"SELECT WaitingToPost, Retrying, Failed, Posted, OldestWaitingSince FROM dbo.vw_PostingHealth")
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var byMonth = months.ToDictionary(m => m.Month);
        return new Dashboard(
            byStatus.OrderBy(s => s.Status).Select(s => new StatusKpi((InvoiceStatus)s.Status, s.InvoiceCount, s.GrossTotalEur)).ToList(),
            Enumerable.Range(0, Months).Select(i => first.AddMonths(i))
                .Select(m => byMonth.GetValueOrDefault(m) ?? new MonthKpi(m, 0, 0, 0, 0)).ToList(),
            reasons.OrderByDescending(r => r.OpenInvoices).ThenBy(r => r.Reason, StringComparer.Ordinal).ThenBy(r => r.Kind, StringComparer.Ordinal).ToList(),
            posting.Single());
    }

    private sealed record StatusRow(int Status, int InvoiceCount, decimal GrossTotalEur);
}
