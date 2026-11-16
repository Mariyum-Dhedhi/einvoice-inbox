using EInvoice.Domain.Invoices;

namespace EInvoice.Application.Kpi;

public sealed record StatusKpi(InvoiceStatus Status, int InvoiceCount, decimal GrossTotalEur);

/// <param name="Month">The first day of the month (Berlin time).</param>
public sealed record MonthKpi(DateOnly Month, int Received, int StraightThrough, int Exceptions, int Failed)
{
    /// <summary>Share of received invoices that went straight through, rounded to one decimal; none when nothing came in.</summary>
    public decimal? StraightThroughPercent => Received == 0 ? null : decimal.Round(StraightThrough * 100m / Received, 1, MidpointRounding.AwayFromZero);
}

/// <param name="Kind">"Finding" (a rule code) or "Match" (a match outcome's name).</param>
public sealed record ReasonKpi(string Kind, string Reason, int OpenInvoices);

public sealed record PostingKpi(int WaitingToPost, int Retrying, int Failed, int Posted, DateTimeOffset? OldestWaitingSince);

public sealed record Dashboard(
    IReadOnlyList<StatusKpi> ByStatus,
    IReadOnlyList<MonthKpi> Months,
    IReadOnlyList<ReasonKpi> OpenReasons,
    PostingKpi Posting);

/// <summary>The KPI views (db/views) for the dashboard, in a defined order.</summary>
public interface IKpiQueries
{
    /// <summary>
    /// Statuses in status order; the twelve months up to the month of <paramref name="today"/>, oldest first, a quiet
    /// month as zeros; open exception reasons with the most invoices first; the posting health.
    /// </summary>
    Task<Dashboard> DashboardAsync(DateOnly today, CancellationToken cancellationToken);
}
