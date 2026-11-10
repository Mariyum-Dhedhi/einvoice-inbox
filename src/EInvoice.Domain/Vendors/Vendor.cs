namespace EInvoice.Domain.Vendors;

/// <summary>
/// Supplier master data. Bank accounts are deliberately not here yet: they get their own
/// history and a two-person approval rule in package 11.
/// </summary>
public sealed class Vendor
{
    public Vendor(string number, string name, string? vatId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(number);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Number = number;
        Name = name;
        VatId = vatId;
    }

    public Guid Id { get; } = Guid.NewGuid();

    public string Number { get; }

    public string Name { get; }

    /// <summary>EU VAT identification number (USt-IdNr.), for example DE123456789.</summary>
    public string? VatId { get; }

    public bool IsActive { get; private set; } = true;

    public void Deactivate() => IsActive = false;

    public void Activate() => IsActive = true;
}
