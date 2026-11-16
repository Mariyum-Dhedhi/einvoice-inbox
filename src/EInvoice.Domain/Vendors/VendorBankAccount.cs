using EInvoice.Domain.Shared;

namespace EInvoice.Domain.Vendors;

/// <summary>Stored as numbers: never renumber or reuse a value.</summary>
public enum BankAccountStatus
{
    /// <summary>Proposed by one person; does not count for payments until a second person confirms it.</summary>
    Pending = 1,

    /// <summary>Confirmed by a second person; invoices may ask for payment to it.</summary>
    Active = 2,
}

/// <summary>
/// A vendor's bank account under the two-person rule (bank-detail fraud control): one person proposes it, a different
/// person confirms it, and only then does it count. Invoices asking for payment to any other account need review.
/// Its earlier versions are kept by the database (temporal table).
/// </summary>
public sealed class VendorBankAccount
{
    private VendorBankAccount(Guid vendorId, Iban iban, string proposedBy, DateTimeOffset proposedAt)
    {
        VendorId = vendorId;
        Iban = iban;
        ProposedBy = proposedBy;
        ProposedAt = proposedAt;
        Status = BankAccountStatus.Pending;
    }

    /// <summary>For EF Core when loading from the database.</summary>
    private VendorBankAccount()
    {
        Iban = null!;
        ProposedBy = null!;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public Guid VendorId { get; }

    public Iban Iban { get; }

    public BankAccountStatus Status { get; private set; }

    public string ProposedBy { get; }

    public DateTimeOffset ProposedAt { get; }

    public string? ConfirmedBy { get; private set; }

    public DateTimeOffset? ConfirmedAt { get; private set; }

    public bool IsActive => Status == BankAccountStatus.Active;

    public static VendorBankAccount Propose(Guid vendorId, Iban iban, string proposedBy, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(iban);
        ArgumentException.ThrowIfNullOrWhiteSpace(proposedBy);
        return new VendorBankAccount(vendorId, iban, proposedBy, at);
    }

    /// <summary>The second person's confirmation; the proposer can never confirm their own proposal.</summary>
    public void Confirm(string confirmedBy, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmedBy);
        if (Status != BankAccountStatus.Pending)
        {
            throw new BankAccountNotPendingException();
        }

        if (string.Equals(ProposedBy, confirmedBy, StringComparison.OrdinalIgnoreCase))
        {
            throw new TwoPersonRuleException();
        }

        Status = BankAccountStatus.Active;
        ConfirmedBy = confirmedBy;
        ConfirmedAt = at;
    }
}

public sealed class TwoPersonRuleException() : DomainException("A bank account must be confirmed by someone other than the person who proposed it.");

public sealed class BankAccountNotPendingException() : DomainException("Only a proposed bank account can be confirmed.");
