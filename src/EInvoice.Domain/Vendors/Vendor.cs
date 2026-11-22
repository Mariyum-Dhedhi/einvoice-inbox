using EInvoice.Domain.Shared;

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

    public string Name { get; private set; }

    /// <summary>EU VAT identification number (USt-IdNr.), for example DE123456789.</summary>
    public string? VatId { get; private set; }

    public bool IsActive { get; private set; } = true;

    public RecordSource Source { get; private set; } = RecordSource.Local;

    /// <summary>The ERP's id for this vendor; null for one made in the app.</summary>
    public string? ErpId { get; private set; }

    /// <summary>A vendor read from the ERP. A blocked one is inactive: it gets no new orders made in the app.</summary>
    public static Vendor FromErp(string erpId, string number, string name, string? vatId, bool blocked)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(erpId);
        return new Vendor(number, name, vatId) { Source = RecordSource.Erp, ErpId = erpId, IsActive = !blocked };
    }

    /// <summary>Follows the ERP: name, VAT id and block. The number stays (orders and postings refer to it).</summary>
    /// <returns>False when nothing changed: an unchanged record writes no audit event.</returns>
    public bool ApplyFromErp(string name, string? vatId, bool blocked)
    {
        ErpOnly(Source);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (Name == name && VatId == vatId && IsActive == !blocked)
        {
            return false;
        }

        Name = name;
        VatId = vatId;
        IsActive = !blocked;
        return true;
    }

    public void Deactivate() => IsActive = false;

    public void Activate() => IsActive = true;

    internal static void ErpOnly(RecordSource source)
    {
        if (source != RecordSource.Erp)
        {
            throw new InvalidOperationException("Only records from the ERP follow the ERP; records made in the app stay as they are.");
        }
    }
}
