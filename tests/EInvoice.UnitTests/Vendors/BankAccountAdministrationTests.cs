using EInvoice.Application.Audit;
using EInvoice.Application.Matching;
using EInvoice.Application.Vendors;
using EInvoice.Domain.Shared;
using EInvoice.Domain.Vendors;

namespace EInvoice.UnitTests.Vendors;

/// <summary>
/// Proposing, confirming and removing bank accounts: every change is audited (with the IBAN masked), a refusal changes
/// and audits nothing, and only a second person can make an account count.
/// </summary>
public sealed class BankAccountAdministrationTests
{
    private const string Admin = "admin@test";
    private const string Admin2 = "admin2@test";
    private const string ValidIban = "DE72 0000 0000 0000 0010 01";
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeStore _store = new();

    private BankAccountAdministration Admin_() => new(_store, new FixedTime(Now));

    private async Task<VendorBankAccount> ProposedAsync()
    {
        var result = await Admin_().ProposeAsync(_store.VendorId, ValidIban, Admin, CancellationToken.None);
        _store.Audited.Clear();
        _store.Saves = 0;
        return _store.Accounts.Single(a => a.Id == result.AccountId);
    }

    // ---- Propose --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_proposal_is_stored_pending_and_audited_with_the_iban_masked()
    {
        var result = await Admin_().ProposeAsync(_store.VendorId, ValidIban, Admin, CancellationToken.None);

        Assert.Equal(BankAccountOutcome.Proposed, result.Outcome);
        var account = Assert.Single(_store.Accounts);
        Assert.Equal((BankAccountStatus.Pending, "DE72000000000000001001"), (account.Status, account.Iban.Value));
        var audit = Assert.Single(_store.Audited);
        Assert.Equal((AuditActions.BankAccountProposed, Admin, Now, $"VendorBankAccount/{account.Id}"), (audit.Action, audit.Actor, audit.At, audit.Subject));
        Assert.DoesNotContain("000000000000001001", audit.Details, StringComparison.Ordinal); // never the full IBAN in the trail
        Assert.Contains(_store.VendorId.ToString(), audit.Details, StringComparison.Ordinal);
        Assert.Equal(1, _store.Saves); // account and its event in one save
    }

    [Theory]
    [InlineData("DE72000000000000001002")] // wrong check digits
    [InlineData("not an iban")]
    [InlineData("")]
    public async Task An_invalid_iban_is_refused_and_nothing_is_stored(string iban)
    {
        var result = await Admin_().ProposeAsync(_store.VendorId, iban, Admin, CancellationToken.None);

        Assert.Equal(BankAccountOutcome.InvalidIban, result.Outcome);
        Assert.Empty(_store.Accounts);
        Assert.Empty(_store.Audited);
        Assert.Equal(0, _store.Saves);
    }

    [Fact]
    public async Task The_same_account_twice_for_a_vendor_is_refused()
    {
        await ProposedAsync();

        var result = await Admin_().ProposeAsync(_store.VendorId, "DE72000000000000001001", Admin2, CancellationToken.None);

        Assert.Equal(BankAccountOutcome.AlreadyExists, result.Outcome);
        Assert.Single(_store.Accounts);
    }

    [Fact]
    public async Task An_unknown_vendor_is_refused()
    {
        var result = await Admin_().ProposeAsync(Guid.NewGuid(), ValidIban, Admin, CancellationToken.None);

        Assert.Equal(BankAccountOutcome.VendorNotFound, result.Outcome);
    }

    // ---- Confirm --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_second_admin_confirms_and_it_is_audited()
    {
        var account = await ProposedAsync();

        var result = await Admin_().ConfirmAsync(account.Id, Admin2, CancellationToken.None);

        Assert.Equal(BankAccountOutcome.Confirmed, result.Outcome);
        Assert.True(account.IsActive);
        var audit = Assert.Single(_store.Audited);
        Assert.Equal((AuditActions.BankAccountConfirmed, Admin2), (audit.Action, audit.Actor));
        Assert.Equal(1, _store.Saves);
    }

    [Fact]
    public async Task The_proposer_cannot_confirm_and_nothing_is_saved()
    {
        var account = await ProposedAsync();

        var result = await Admin_().ConfirmAsync(account.Id, "Admin@Test", CancellationToken.None);

        Assert.Equal(BankAccountOutcome.OwnProposal, result.Outcome);
        Assert.False(account.IsActive);
        Assert.Empty(_store.Audited);
        Assert.Equal(0, _store.Saves);
    }

    [Fact]
    public async Task An_active_account_cannot_be_confirmed_again()
    {
        var account = await ProposedAsync();
        await Admin_().ConfirmAsync(account.Id, Admin2, CancellationToken.None);

        var result = await Admin_().ConfirmAsync(account.Id, "admin3@test", CancellationToken.None);

        Assert.Equal(BankAccountOutcome.NotPending, result.Outcome);
    }

    [Fact]
    public async Task Someone_else_changing_it_at_the_same_time_is_a_conflict()
    {
        var account = await ProposedAsync();
        _store.ConflictOnSave = true;

        var result = await Admin_().ConfirmAsync(account.Id, Admin2, CancellationToken.None);

        Assert.Equal(BankAccountOutcome.Conflict, result.Outcome);
    }

    // ---- Remove ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task One_admin_removes_an_account_with_a_reason_and_it_is_audited()
    {
        var account = await ProposedAsync();
        await Admin_().ConfirmAsync(account.Id, Admin2, CancellationToken.None);
        _store.Audited.Clear();

        var result = await Admin_().RemoveAsync(account.Id, Admin, "Supplier reported the account as closed.", CancellationToken.None);

        Assert.Equal(BankAccountOutcome.Removed, result.Outcome);
        Assert.Empty(_store.Accounts);
        var audit = Assert.Single(_store.Audited);
        Assert.Equal((AuditActions.BankAccountRemoved, Admin), (audit.Action, audit.Actor));
        Assert.Contains("Supplier reported the account as closed.", audit.Details, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task Removing_needs_a_reason(string? reason)
    {
        var account = await ProposedAsync();

        var result = await Admin_().RemoveAsync(account.Id, Admin, reason, CancellationToken.None);

        Assert.Equal(BankAccountOutcome.ReasonRequired, result.Outcome);
        Assert.Single(_store.Accounts);
    }

    [Fact]
    public async Task A_reason_longer_than_200_characters_is_refused()
    {
        var account = await ProposedAsync();

        var result = await Admin_().RemoveAsync(account.Id, Admin, new string('x', 201), CancellationToken.None);

        Assert.Equal(BankAccountOutcome.ReasonTooLong, result.Outcome);
    }

    [Fact]
    public async Task Unknown_accounts_are_not_found()
    {
        Assert.Equal(BankAccountOutcome.NotFound, (await Admin_().ConfirmAsync(Guid.NewGuid(), Admin2, CancellationToken.None)).Outcome);
        Assert.Equal(BankAccountOutcome.NotFound, (await Admin_().RemoveAsync(Guid.NewGuid(), Admin, "reason", CancellationToken.None)).Outcome);
    }

    private sealed class FakeStore : IBankAccountStore
    {
        public Guid VendorId { get; } = Guid.NewGuid();

        public List<VendorBankAccount> Accounts { get; } = [];

        public List<AuditDraft> Audited { get; } = [];

        private readonly List<AuditDraft> _queued = [];
        private readonly List<VendorBankAccount> _added = [];
        private readonly List<VendorBankAccount> _removed = [];

        public int Saves { get; set; }

        public bool ConflictOnSave { get; set; }

        public Task<bool> VendorExistsAsync(Guid vendorId, CancellationToken cancellationToken) => Task.FromResult(vendorId == VendorId);

        public Task<bool> ExistsAsync(Guid vendorId, Iban iban, CancellationToken cancellationToken) =>
            Task.FromResult(Accounts.Any(a => a.VendorId == vendorId && a.Iban == iban));

        public Task<VendorBankAccount?> FindAsync(Guid accountId, CancellationToken cancellationToken) =>
            Task.FromResult(Accounts.SingleOrDefault(a => a.Id == accountId));

        public void Add(VendorBankAccount account) => _added.Add(account);

        public void Remove(VendorBankAccount account) => _removed.Add(account);

        public void Audit(AuditDraft draft) => _queued.Add(draft);

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            if (ConflictOnSave)
            {
                throw new ConcurrentStatusChangeException();
            }

            Saves++;
            Accounts.AddRange(_added);
            Accounts.RemoveAll(_removed.Contains);
            Audited.AddRange(_queued);
            _added.Clear();
            _removed.Clear();
            _queued.Clear();
            return Task.CompletedTask;
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
