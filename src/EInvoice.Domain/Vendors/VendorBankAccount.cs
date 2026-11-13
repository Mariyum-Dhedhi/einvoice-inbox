using EInvoice.Domain.Shared;

namespace EInvoice.Domain.Vendors;

/// <summary>
/// An approved bank account of a vendor. Invoices asking for payment to any other account need review
/// (bank-detail fraud control). Package 11 adds change history and a second approver for changes.
/// </summary>
public sealed class VendorBankAccount
{
    public VendorBankAccount(Guid vendorId, Iban iban)
    {
        ArgumentNullException.ThrowIfNull(iban);
        VendorId = vendorId;
        Iban = iban;
    }

    /// <summary>For EF Core when loading from the database.</summary>
    private VendorBankAccount() => Iban = null!;

    public Guid Id { get; } = Guid.NewGuid();

    public Guid VendorId { get; }

    public Iban Iban { get; }
}
