using EInvoice.Application.Validation;
using EInvoice.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EInvoice.Infrastructure.Validation;

/// <summary>Master-data lookups for validation. Read-only, no tracking, parameterised by EF Core.</summary>
public sealed class EfValidationDataSource(EInvoiceDbContext db) : IValidationDataSource
{
    public async Task<VendorSnapshot?> FindVendorByVatIdAsync(string vatId, CancellationToken cancellationToken)
    {
        var vendor = await db.Vendors.AsNoTracking()
            .Where(v => v.VatId == vatId)
            .Select(v => new { v.Id, v.Number, v.IsActive })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (vendor is null)
        {
            return null;
        }

        var ibans = await db.VendorBankAccounts.AsNoTracking()
            .Where(a => a.VendorId == vendor.Id)
            .Select(a => a.Iban)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new VendorSnapshot(vendor.Id, vendor.Number, vendor.IsActive, ibans);
    }

    public Task<bool> InvoiceNumberExistsAsync(Guid vendorId, string invoiceNumber, CancellationToken cancellationToken) =>
        db.Invoices.AsNoTracking().AnyAsync(i => i.VendorId == vendorId && i.InvoiceNumber == invoiceNumber, cancellationToken);

    public Task<int> CountPossibleDuplicatesAsync(Guid vendorId, decimal grossTotal, DateOnly issueDate, string invoiceNumber, CancellationToken cancellationToken) =>
        db.Invoices.AsNoTracking().CountAsync(
            i => i.VendorId == vendorId
                 && i.IssueDate == issueDate
                 && i.GrossTotal!.Amount == grossTotal
                 && i.InvoiceNumber != invoiceNumber,
            cancellationToken);
}
