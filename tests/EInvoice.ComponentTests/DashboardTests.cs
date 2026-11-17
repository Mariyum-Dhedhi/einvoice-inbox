using EInvoice.Application.Kpi;
using EInvoice.Web.Components.Pages;

namespace EInvoice.ComponentTests;

/// <summary>The KPI dashboard: four panels, numbers in German format, states in words with an icon.</summary>
public sealed class DashboardTests : UiTest
{
    [Fact]
    public void Shows_invoices_by_status_in_words_with_german_amounts()
    {
        var panel = Render<DashboardPage>().Find("[data-testid=kpi-status]");

        Assert.Equal("Exception", panel.QuerySelector("[data-testid=status]")!.GetAttribute("data-status"));
        Assert.Contains("1.234,50", panel.TextContent, StringComparison.Ordinal);
        Assert.Contains("98.765,43", panel.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Shows_the_straight_through_rate_per_month_and_a_dash_for_an_empty_month()
    {
        var rows = Render<DashboardPage>().FindAll("[data-testid=kpi-month]");

        Assert.Contains("Oktober 2026", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("70,0", rows[0].TextContent, StringComparison.Ordinal); // 7 of 10
        Assert.Contains("–", rows[1].TextContent, StringComparison.Ordinal);    // nothing received: no rate, not 0 %
    }

    [Fact]
    public void Shows_open_exception_reasons_in_plain_words()
    {
        var panel = Render<DashboardPage>().Find("[data-testid=kpi-reasons]");

        Assert.Contains(T["Rule.IBAN_NOT_APPROVED.Title"], panel.TextContent, StringComparison.Ordinal); // a rule code gets its name
        Assert.Contains("Nothing received", panel.TextContent, StringComparison.Ordinal); // the same label as on the invoice page
    }

    [Theory]
    [InlineData(0, 0, "healthy")]
    [InlineData(2, 0, "retrying")]
    [InlineData(0, 1, "failed")]
    public void Posting_health_says_its_state_in_words_with_an_icon(int retrying, int failed, string state)
    {
        Kpis.Data = Kpis.Data with { Posting = new PostingKpi(0, retrying, failed, 5, null) };

        var health = Render<DashboardPage>().Find("[data-testid=kpi-posting]");

        Assert.Equal(state, health.GetAttribute("data-state"));
        Assert.NotNull(health.QuerySelector("svg"));
    }

    [Fact]
    public void Asks_for_today_in_berlin()
    {
        Render<DashboardPage>();

        Assert.NotNull(Kpis.AskedFor);
    }
}
