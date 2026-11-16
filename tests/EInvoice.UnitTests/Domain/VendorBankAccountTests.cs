using EInvoice.Domain.Shared;
using EInvoice.Domain.Vendors;

namespace EInvoice.UnitTests.Domain;

/// <summary>
/// The two-person rule as a domain rule: a bank account is proposed by one person and counts only after a different
/// person confirmed it. No code path can activate an account by one person alone.
/// </summary>
public sealed class VendorBankAccountTests
{
    private static readonly Guid Vendor = Guid.NewGuid();
    private static readonly Iban Iban = Iban.Parse("DE72000000000000001001");
    private static readonly DateTimeOffset T0 = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_proposed_account_is_pending_and_names_its_proposer()
    {
        var account = VendorBankAccount.Propose(Vendor, Iban, "admin@test", T0);

        Assert.Equal(BankAccountStatus.Pending, account.Status);
        Assert.Equal(("admin@test", T0), (account.ProposedBy, account.ProposedAt));
        Assert.Null(account.ConfirmedBy);
        Assert.False(account.IsActive);
    }

    [Fact]
    public void A_second_person_confirms_it()
    {
        var account = VendorBankAccount.Propose(Vendor, Iban, "admin@test", T0);

        account.Confirm("admin2@test", T0.AddHours(1));

        Assert.True(account.IsActive);
        Assert.Equal(("admin2@test", T0.AddHours(1)), (account.ConfirmedBy, account.ConfirmedAt));
    }

    [Theory]
    [InlineData("admin@test")]
    [InlineData("ADMIN@test")] // the same account written differently is the same person
    public void The_proposer_cannot_confirm_their_own_proposal(string confirmer)
    {
        var account = VendorBankAccount.Propose(Vendor, Iban, "admin@test", T0);

        Assert.Throws<TwoPersonRuleException>(() => account.Confirm(confirmer, T0.AddHours(1)));
        Assert.Equal(BankAccountStatus.Pending, account.Status);
    }

    [Fact]
    public void An_active_account_cannot_be_confirmed_again()
    {
        var account = VendorBankAccount.Propose(Vendor, Iban, "admin@test", T0);
        account.Confirm("admin2@test", T0.AddHours(1));

        Assert.Throws<BankAccountNotPendingException>(() => account.Confirm("admin3@test", T0.AddHours(2)));
        Assert.Equal("admin2@test", account.ConfirmedBy);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void A_proposal_names_a_person(string proposer)
    {
        Assert.ThrowsAny<ArgumentException>(() => VendorBankAccount.Propose(Vendor, Iban, proposer, T0));
    }
}
