using EInvoice.Application.Security;
using EInvoice.Application.Vendors;
using EInvoice.Domain.Vendors;
using EInvoice.Web.Components.Admin;
using EInvoice.Web.Security;

namespace EInvoice.ComponentTests;

/// <summary>Plays an admin: signed in as admin2@test with the admin's policies.</summary>
public abstract class AdminTest : UiTest
{
    public const string Me = "admin2@test";

    protected AdminTest()
    {
        Auth.SetAuthorized(Me);
        Auth.SetRoles(Roles.Admin);
        Auth.SetPolicies(Policies.ManageUsers, Policies.ManageVendors);
    }
}

/// <summary>The vendor list: where bank accounts wait for a second admin.</summary>
public sealed class VendorsPageTests : AdminTest
{
    [Fact]
    public void Lists_vendors_with_their_accounts_and_what_waits_for_confirmation()
    {
        var id = Guid.NewGuid();
        BankAccounts.Vendors.Add(new VendorOverview(id, "V-0001", "Beispiel Bürobedarf GmbH", ActiveAccounts: 1, PendingAccounts: 2));
        BankAccounts.Vendors.Add(new VendorOverview(Guid.NewGuid(), "V-0002", "Muster AG", 1, 0));

        var rows = Render<VendorsPage>().FindAll("[data-testid=vendor-row]");

        Assert.Equal(2, rows.Count);
        Assert.Equal($"/admin/vendors/{id}", rows[0].QuerySelector("a")!.GetAttribute("href"));
        Assert.Contains("2 waiting for a second admin", rows[0].TextContent, StringComparison.Ordinal);
        Assert.NotNull(rows[0].QuerySelector("[data-testid=pending] svg")); // never colour alone
        Assert.Empty(rows[1].QuerySelectorAll("[data-testid=pending]"));
    }
}

/// <summary>One vendor's bank accounts: propose, confirm (only someone else's proposal), remove with a reason, history.</summary>
public sealed class VendorBankAccountsPageTests : AdminTest
{
    private static readonly DateTimeOffset At = new(2026, 11, 16, 8, 0, 0, TimeSpan.Zero); // 09:00 in Berlin
    private readonly Guid _vendor = Guid.NewGuid();
    private readonly Guid _activeId = Guid.NewGuid();
    private readonly Guid _othersProposal = Guid.NewGuid();
    private readonly Guid _myProposal = Guid.NewGuid();

    public VendorBankAccountsPageTests()
    {
        BankAccounts.Details[_vendor] = new VendorBankDetails(_vendor, "V-0001", "Beispiel Bürobedarf GmbH",
        [
            new BankAccountView(_activeId, "DE72000000000000001001", "DE72 **** **** **** **10 01", BankAccountStatus.Active, "seed", At, "admin@test", At),
            new BankAccountView(_othersProposal, "DE45000000000000001002", "DE45 **** **** **** **10 02", BankAccountStatus.Pending, "admin@test", At, null, null),
            new BankAccountView(_myProposal, "DE18000000000000001003", "DE18 **** **** **** **10 03", BankAccountStatus.Pending, Me.ToUpperInvariant(), At, null, null),
        ],
        [
            new BankAccountHistoryEntry(_activeId, "DE72 **** **** **** **10 01", BankAccountStatus.Pending, "seed", null, At, At.AddHours(1)),
        ]);
    }

    private IRenderedComponent<VendorBankAccountsPage> Show() => Render<VendorBankAccountsPage>(p => p.Add(c => c.Id, _vendor));

    private static AngleSharp.Dom.IElement Row(IRenderedComponent<VendorBankAccountsPage> cut, Guid id) =>
        cut.FindAll("[data-testid=account-row]").Single(r => r.GetAttribute("data-account") == id.ToString());

    [Fact]
    public void Shows_the_full_iban_grouped_for_checking_against_the_suppliers_letter()
    {
        Assert.Contains("DE72 0000 0000 0000 0010 01", Row(Show(), _activeId).TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Says_in_words_with_an_icon_whether_an_account_counts()
    {
        var cut = Show();

        var active = Row(cut, _activeId).QuerySelector("[data-testid=account-status]")!;
        var pending = Row(cut, _othersProposal).QuerySelector("[data-testid=account-status]")!;
        Assert.Equal(("active", "pending"), (active.GetAttribute("data-status"), pending.GetAttribute("data-status")));
        Assert.Contains("confirmed by admin@test", active.TextContent, StringComparison.Ordinal);
        Assert.Contains("Waiting for a second admin", pending.TextContent, StringComparison.Ordinal);
        Assert.NotNull(active.QuerySelector("svg"));
        Assert.NotNull(pending.QuerySelector("svg"));
    }

    [Fact]
    public void Someone_elses_proposal_can_be_confirmed()
    {
        var cut = Show();

        Row(cut, _othersProposal).QuerySelector("[data-testid=confirm]")!.Click();

        Assert.Equal((_othersProposal, Me), Assert.Single(BankAccountAdmin.Confirmed));
    }

    [Fact]
    public void Ones_own_proposal_offers_no_confirm_and_says_why()
    {
        var row = Row(Show(), _myProposal);

        Assert.Null(row.QuerySelector("[data-testid=confirm]"));
        Assert.Contains("another admin confirms", row.TextContent, StringComparison.Ordinal);
        Assert.Null(Row(Show(), _activeId).QuerySelector("[data-testid=confirm]"));
    }

    [Fact]
    public void Proposing_sends_the_typed_iban_and_shows_the_result()
    {
        var cut = Show();
        cut.Find("[data-testid=new-iban] input").Input("DE89 3704 0044 0532 0130 00");

        cut.Find("[data-testid=propose]").Click();

        Assert.Equal((_vendor, "DE89 3704 0044 0532 0130 00", Me), Assert.Single(BankAccountAdmin.Proposed));
        Assert.Contains("waits for a second admin", cut.Find("[data-testid=account-result]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void A_refused_proposal_shows_the_reason()
    {
        BankAccountAdmin.Result = new BankAccountResult(BankAccountOutcome.InvalidIban, Detail: "This is not a valid IBAN.");
        var cut = Show();
        cut.Find("[data-testid=new-iban] input").Input("DE00");

        cut.Find("[data-testid=propose]").Click();

        Assert.Contains("not a valid IBAN", cut.Find("[data-testid=account-result]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Removing_needs_a_reason_and_sends_it()
    {
        var cut = Show();
        Row(cut, _activeId).QuerySelector("[data-testid=remove]")!.Click();

        Assert.True(cut.Find("[data-testid=remove-confirm]").HasAttribute("disabled"));
        cut.Find("[data-testid=remove-reason] textarea").Input("Supplier closed this account.");
        cut.Find("[data-testid=remove-confirm]").Click();

        Assert.Equal((_activeId, Me, "Supplier closed this account."), Assert.Single(BankAccountAdmin.Removed));
    }

    [Fact]
    public void History_shows_earlier_versions_with_the_iban_masked()
    {
        var row = Assert.Single(Show().FindAll("[data-testid=history-row]"));

        Assert.Contains("DE72 **** **** **** **10 01", row.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("0000 0000 0000 0010", row.TextContent, StringComparison.Ordinal);
        Assert.Contains("16.11.2026 09:00", row.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void A_confirm_after_the_role_was_withdrawn_does_nothing()
    {
        var cut = Show();
        var confirm = Row(cut, _othersProposal).QuerySelector("[data-testid=confirm]")!;

        Auth.SetPolicies(Policies.ManageUsers);
        confirm.Click();

        Assert.Empty(BankAccountAdmin.Confirmed);
    }

    [Fact]
    public void An_unknown_vendor_says_so()
    {
        var cut = Render<VendorBankAccountsPage>(p => p.Add(c => c.Id, Guid.NewGuid()));

        Assert.NotNull(cut.Find("[data-testid=vendor-not-found]"));
    }
}
