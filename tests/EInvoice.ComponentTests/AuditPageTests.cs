using EInvoice.Application.Audit;
using EInvoice.Application.Security;
using EInvoice.Web.Components.Pages;
using EInvoice.Web.Security;

namespace EInvoice.ComponentTests;

/// <summary>The auditor's page: the trail newest first, and a verification that says in words whether it is intact.</summary>
public sealed class AuditPageTests : UiTest
{
    private static readonly DateTimeOffset At = new(2026, 11, 16, 9, 30, 0, TimeSpan.Zero); // 10:30 in Berlin

    public AuditPageTests()
    {
        Auth.SetAuthorized("auditor@test");
        Auth.SetRoles(Roles.Auditor);
        Auth.SetPolicies(Policies.ReadInvoices, Policies.ReadAudit);
    }

    private IRenderedComponent<AuditPage> RenderAt(string uri = "/audit")
    {
        NavigateTo(uri);
        return Render<AuditPage>();
    }

    private void AddEvents(int count)
    {
        for (var i = count; i >= 1; i--)
        {
            Audit.Events.Add(new AuditEventView(i, At, $"user{i}@test", AuditActions.InvoiceStatusChanged, $"Invoice/{i}", """{"from":"Matched","to":"Approved","reason":null}""", new string('a', 64)));
        }
    }

    [Fact]
    public void Lists_events_newest_first_in_berlin_time()
    {
        AddEvents(3);

        var rows = RenderAt().FindAll("[data-testid=audit-row]");

        Assert.Equal(3, rows.Count);
        Assert.StartsWith("3", rows[0].QuerySelector("td")!.TextContent.Trim(), StringComparison.Ordinal);
        Assert.Contains("16.11.2026 10:30", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("user3@test", rows[0].TextContent, StringComparison.Ordinal);
        Assert.Contains("Invoice/3", rows[0].TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Verifying_an_intact_chain_says_so_with_what_to_note_down()
    {
        Audit.Verification = new AuditVerification(true, 42, 42, new string('b', 64), null, null);
        var cut = RenderAt();

        cut.Find("[data-testid=verify]").Click();

        var result = cut.Find("[data-testid=verify-result]");
        Assert.Equal("intact", result.GetAttribute("data-state"));
        Assert.Contains("42 events", result.TextContent, StringComparison.Ordinal);
        Assert.Contains(new string('b', 64), result.TextContent, StringComparison.Ordinal);
        Assert.NotNull(result.QuerySelector("svg")); // never colour alone
    }

    [Theory]
    [InlineData(AuditProblem.HashMismatch, "changed after it was written")]
    [InlineData(AuditProblem.ChainBroken, "does not follow the event before it")]
    [InlineData(AuditProblem.SequenceGap, "missing")]
    public void Verifying_a_broken_chain_names_the_event_and_the_problem(AuditProblem problem, string words)
    {
        Audit.Verification = new AuditVerification(false, 6, 6, new string('c', 64), 7, problem);
        var cut = RenderAt();

        cut.Find("[data-testid=verify]").Click();

        var result = cut.Find("[data-testid=verify-result]");
        Assert.Equal("broken", result.GetAttribute("data-state"));
        Assert.Contains("event 7", result.TextContent, StringComparison.Ordinal);
        Assert.Contains(words, result.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Says_what_the_chain_cannot_prove()
    {
        // Honest about the limit: cutting off the newest events leaves a shorter but intact chain.
        Assert.Contains("note down", RenderAt().Find("[data-testid=audit-limit]").TextContent, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("/audit?page=2", 2)]
    [InlineData("/audit?page=0", 1)]
    [InlineData("/audit?page=x", 1)]
    public void The_page_comes_from_the_address_and_is_checked(string uri, int expected)
    {
        RenderAt(uri);

        Assert.Equal(expected, Audit.ListCalls[^1].Page);
    }

    [Fact]
    public void Event_text_is_shown_as_text_not_markup()
    {
        Audit.Events.Add(new AuditEventView(1, At, "<img src=x onerror=alert(1)>", AuditActions.SignInFailed, "User/<img>", "{}", new string('a', 64)));

        var row = RenderAt().Find("[data-testid=audit-row]");

        Assert.Empty(row.QuerySelectorAll("img"));
        Assert.Contains("<img src=x", row.TextContent, StringComparison.Ordinal);
    }
}
