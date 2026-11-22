using EInvoice.Application.Vendors;
using EInvoice.Domain.Shared;
using EInvoice.Domain.Vendors;
using EInvoice.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace EInvoice.Infrastructure.Vendors;

/// <summary>Vendors and their bank accounts for the admin pages; earlier versions come from the temporal history.</summary>
public sealed class EfBankAccountQueries(EInvoiceDbContext db) : IBankAccountQueries
{
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

        // The history table itself, not FOR SYSTEM_TIME ALL (TemporalAll): the transaction time moves in steps of a few
        // milliseconds, so a version can begin and end in the same instant, and FOR SYSTEM_TIME leaves those out
        // (issue #75). Versions of the same instant are put in the order they were written by their rowversion.
        var versions = await db.Database.SqlQuery<HistoryRow>($"""
                SELECT Id, Iban, Status, ProposedBy, ConfirmedBy, ValidFrom, ValidTo
                FROM dbo.VendorBankAccountHistory
                WHERE VendorId = {vendorId}
                ORDER BY ValidFrom, Version
                """)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var history = versions
            .Select(v => new BankAccountHistoryEntry(v.Id, Iban.Parse(v.Iban).ToString(), v.Status, v.ProposedBy, v.ConfirmedBy, Utc(v.ValidFrom), Utc(v.ValidTo)))
            .ToList();

        return new VendorBankDetails(vendor.Id, vendor.Number, vendor.Name, accounts, history);
    }

    private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

    private sealed class HistoryRow
    {
        public Guid Id { get; init; }
        public string Iban { get; init; } = "";
        public BankAccountStatus Status { get; init; }
        public string ProposedBy { get; init; } = "";
        public string? ConfirmedBy { get; init; }
        public DateTime ValidFrom { get; init; }
        public DateTime ValidTo { get; init; }
    }
}
