using System.Text.Json;
using EInvoice.Application.Audit;
using EInvoice.Application.Matching;
using EInvoice.Domain.Shared;
using EInvoice.Domain.Vendors;

namespace EInvoice.Application.Vendors;

public enum BankAccountOutcome
{
    Proposed = 1,
    Confirmed = 2,
    Removed = 3,
    NotFound = 4,
    VendorNotFound = 5,
    InvalidIban = 6,
    AlreadyExists = 7,

    /// <summary>The proposer tried to confirm their own proposal (two-person rule).</summary>
    OwnProposal = 8,

    NotPending = 9,

    /// <summary>Someone else changed the account at the same time; nothing was saved.</summary>
    Conflict = 10,

    ReasonRequired = 11,
    ReasonTooLong = 12,
}

public sealed record BankAccountResult(BankAccountOutcome Outcome, Guid? AccountId = null, string? Detail = null);

/// <param name="Iban">The full IBAN: shown only to admins, who must compare it with an independent source.</param>
/// <param name="MaskedIban">For everything else.</param>
public sealed record BankAccountView(
    Guid Id, string Iban, string MaskedIban, BankAccountStatus Status, string ProposedBy, DateTimeOffset ProposedAt, string? ConfirmedBy, DateTimeOffset? ConfirmedAt);

/// <summary>One earlier version of an account, as SQL Server kept it (temporal table); the IBAN masked.</summary>
public sealed record BankAccountHistoryEntry(
    Guid AccountId, string MaskedIban, BankAccountStatus Status, string ProposedBy, string? ConfirmedBy, DateTimeOffset ValidFrom, DateTimeOffset? ValidTo);

public sealed record VendorBankDetails(
    Guid VendorId, string Number, string Name, IReadOnlyList<BankAccountView> Accounts, IReadOnlyList<BankAccountHistoryEntry> History);

public sealed record VendorOverview(Guid Id, string Number, string Name, int ActiveAccounts, int PendingAccounts);

public interface IBankAccountQueries
{
    /// <summary>Every vendor by number, with how many accounts count and how many wait for a second admin.</summary>
    Task<IReadOnlyList<VendorOverview>> VendorsAsync(CancellationToken cancellationToken);

    /// <summary>Today's accounts (by IBAN) and every earlier version (oldest first); null for an unknown vendor.</summary>
    Task<VendorBankDetails?> VendorAsync(Guid vendorId, CancellationToken cancellationToken);
}

public interface IBankAccountStore
{
    Task<bool> VendorExistsAsync(Guid vendorId, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(Guid vendorId, Iban iban, CancellationToken cancellationToken);

    Task<VendorBankAccount?> FindAsync(Guid accountId, CancellationToken cancellationToken);

    void Add(VendorBankAccount account);

    void Remove(VendorBankAccount account);

    /// <summary>Queues an audit event that is written by the next save, in the same transaction.</summary>
    void Audit(AuditDraft draft);

    /// <exception cref="ConcurrentStatusChangeException">Changed by someone else at the same time; nothing was saved.</exception>
    /// <exception cref="BankAccountExistsException">The vendor already has this account (unique index).</exception>
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public sealed class BankAccountExistsException : Exception
{
    public BankAccountExistsException()
        : base("The vendor already has this bank account.")
    {
    }
}

public interface IBankAccountAdministration
{
    Task<BankAccountResult> ProposeAsync(Guid vendorId, string iban, string admin, CancellationToken cancellationToken);

    Task<BankAccountResult> ConfirmAsync(Guid accountId, string admin, CancellationToken cancellationToken);

    Task<BankAccountResult> RemoveAsync(Guid accountId, string admin, string? reason, CancellationToken cancellationToken);
}

/// <summary>
/// Bank accounts under the two-person rule. Every change is saved together with its audit event (IBAN masked);
/// a refusal saves and audits nothing. Removing needs only one admin, because it makes payments stricter, not looser.
/// </summary>
public sealed class BankAccountAdministration(IBankAccountStore store, TimeProvider time) : IBankAccountAdministration
{
    private const int MaxReasonLength = 200;

    public async Task<BankAccountResult> ProposeAsync(Guid vendorId, string iban, string admin, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(admin);
        if (!Iban.TryParse(iban, out var parsed))
        {
            return new(BankAccountOutcome.InvalidIban, Detail: "This is not a valid IBAN (format or check digits).");
        }

        if (!await store.VendorExistsAsync(vendorId, cancellationToken).ConfigureAwait(false))
        {
            return new(BankAccountOutcome.VendorNotFound, Detail: "There is no such vendor.");
        }

        if (await store.ExistsAsync(vendorId, parsed, cancellationToken).ConfigureAwait(false))
        {
            return AlreadyExists();
        }

        var account = VendorBankAccount.Propose(vendorId, parsed, admin, time.GetUtcNow());
        store.Add(account);
        store.Audit(Event(account, AuditActions.BankAccountProposed, admin, reason: null));
        try
        {
            await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (BankAccountExistsException)
        {
            return AlreadyExists(); // proposed by someone else at the same moment
        }

        return new(BankAccountOutcome.Proposed, account.Id, "Proposed. It counts once a second admin has confirmed it.");
    }

    public async Task<BankAccountResult> ConfirmAsync(Guid accountId, string admin, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(admin);
        var account = await store.FindAsync(accountId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return NotFound();
        }

        try
        {
            account.Confirm(admin, time.GetUtcNow());
        }
        catch (TwoPersonRuleException)
        {
            return new(BankAccountOutcome.OwnProposal, accountId, "You proposed this account; another admin has to confirm it.");
        }
        catch (BankAccountNotPendingException)
        {
            return new(BankAccountOutcome.NotPending, accountId, "This account is already confirmed.");
        }

        store.Audit(Event(account, AuditActions.BankAccountConfirmed, admin, reason: null));
        return await SaveAsync(new(BankAccountOutcome.Confirmed, accountId, "Confirmed. Invoices may now ask for payment to this account."), cancellationToken).ConfigureAwait(false);
    }

    public async Task<BankAccountResult> RemoveAsync(Guid accountId, string admin, string? reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(admin);
        var trimmed = reason?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Any(char.IsControl))
        {
            return new(BankAccountOutcome.ReasonRequired, accountId, "Give a reason for removing the account.");
        }

        if (trimmed.Length > MaxReasonLength)
        {
            return new(BankAccountOutcome.ReasonTooLong, accountId, $"The reason can have at most {MaxReasonLength} characters.");
        }

        var account = await store.FindAsync(accountId, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return NotFound();
        }

        store.Remove(account);
        store.Audit(Event(account, AuditActions.BankAccountRemoved, admin, trimmed));
        return await SaveAsync(new(BankAccountOutcome.Removed, accountId, "Removed. Invoices asking for payment to it now need review."), cancellationToken).ConfigureAwait(false);
    }

    private async Task<BankAccountResult> SaveAsync(BankAccountResult success, CancellationToken cancellationToken)
    {
        try
        {
            await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return success;
        }
        catch (ConcurrentStatusChangeException)
        {
            return new(BankAccountOutcome.Conflict, success.AccountId, "Someone else changed this account at the same time. Reload the page.");
        }
    }

    // The IBAN only masked: the audit trail is read by more people than the bank data.
    private AuditDraft Event(VendorBankAccount account, string action, string admin, string? reason) =>
        new(time.GetUtcNow(), admin, action, $"VendorBankAccount/{account.Id}",
            JsonSerializer.Serialize(new { vendorId = account.VendorId, iban = account.Iban.ToString(), reason }));

    private static BankAccountResult NotFound() => new(BankAccountOutcome.NotFound, Detail: "There is no such bank account.");

    private static BankAccountResult AlreadyExists() => new(BankAccountOutcome.AlreadyExists, Detail: "The vendor already has this bank account.");
}
