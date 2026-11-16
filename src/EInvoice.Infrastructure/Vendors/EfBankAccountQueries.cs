using EInvoice.Application.Vendors;
using EInvoice.Domain.Vendors;
using EInvoice.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EInvoice.Infrastructure.Vendors;

/// <summary>Vendors and their bank accounts for the admin pages; earlier versions come from the temporal history.</summary>
public sealed class EfBankAccountQueries(EInvoiceDbContext db) : IBankAccountQueries
{
    // SQL Server marks the current version of a row with the largest datetime2 as its end.
    private static readonly DateTime OpenEnd = new(9999, 12, 31, 0, 0, 0, DateTimeKind.Utc);

    public async Task<IReadOnlyList<VendorOverview>> VendorsAsync(CancellationToken cancellationToken) =>
        await db.Vendors.AsNoTracking()
            .OrderBy(v => v.Number)
            .Select(v => new VendorOverview(
                v.Id,
                v.Number,
                v.Name,
                db.VendorBankAccounts.Count(a => a.VendorId == v.Id && a.Status == BankAccountStatus.Active),
                db.VendorBankAccounts.Count(a => a.VendorId == v.Id && a.Status == BankAccountStatus.Pending)))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<VendorBankDetails?> VendorAsync(Guid vendorId, CancellationToken cancellationToken)
    {
        var vendor = await db.Vendors.AsNoTracking()
            .Where(v => v.Id == vendorId)
            .Select(v => new { v.Id, v.Number, v.Name })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (vendor is null)
        {
            return null;
        }

        var accounts = (await db.VendorBankAccounts.AsNoTracking()
                .Where(a => a.VendorId == vendorId)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .OrderBy(a => a.Iban.Value, StringComparer.Ordinal)
            .Select(a => new BankAccountView(a.Id, a.Iban.Value, a.Iban.ToString(), a.Status, a.ProposedBy, a.ProposedAt, a.ConfirmedBy, a.ConfirmedAt))
            .ToList();

        var versions = await db.VendorBankAccounts.TemporalAll().AsNoTracking()
            .Where(a => a.VendorId == vendorId && EF.Property<DateTime>(a, "ValidTo") < OpenEnd)
            .Select(a => new
            {
                a.Id, a.Iban, a.Status, a.ProposedBy, a.ConfirmedBy,
                From = EF.Property<DateTime>(a, "ValidFrom"),
                To = EF.Property<DateTime>(a, "ValidTo"),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var history = versions
            .OrderBy(v => v.From).ThenBy(v => v.Id)
            .Select(v => new BankAccountHistoryEntry(v.Id, v.Iban.ToString(), v.Status, v.ProposedBy, v.ConfirmedBy, Utc(v.From), Utc(v.To)))
            .ToList();

        return new VendorBankDetails(vendor.Id, vendor.Number, vendor.Name, accounts, history);
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
