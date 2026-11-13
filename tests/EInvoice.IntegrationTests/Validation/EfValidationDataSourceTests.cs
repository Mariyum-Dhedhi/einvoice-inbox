using EInvoice.Domain.Shared;
using EInvoice.Infrastructure.Persistence;
using EInvoice.Infrastructure.Validation;
using EInvoice.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace EInvoice.IntegrationTests.Validation;

/// <summary>Master-data queries used by validation, against the seeded database as the app user.</summary>
[Trait("Category", "Database")]
[Collection(SqlServerCollection.Name)]
public sealed class EfValidationDataSourceTests(SqlServerFixture fixture) : IAsyncLifetime
{
    // Seed: vendor n has VAT id DE000000nnn and IBAN DE..0000000000001000+n; vendor 20 is inactive.
    // Invoice RE-2026-0001 belongs to vendor 1 (via PO-0001), issued 2026-11-02.
    private static readonly Guid Vendor1 = Guid.Parse("00000000-0000-0000-0001-000000000001");

    private EInvoiceDbContext _context = null!;
    private EfValidationDataSource _source = null!;

    public Task InitializeAsync()
    {
        _context = fixture.CreateContext();
        _source = new EfValidationDataSource(_context);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _context.DisposeAsync();

    [Fact]
    public async Task Finds_vendor_by_vat_id_with_its_approved_ibans()
    {
        var vendor = await _source.FindVendorByVatIdAsync("DE000000001", CancellationToken.None);

        Assert.NotNull(vendor);
        Assert.Equal("V-1001", vendor.Number);
        Assert.True(vendor.IsActive);
        Assert.Equal([Iban.Parse("DE72000000000000001001")], vendor.ApprovedIbans);
    }

    [Fact]
    public async Task Inactive_vendor_is_returned_as_inactive()
    {
        var vendor = await _source.FindVendorByVatIdAsync("DE000000020", CancellationToken.None);

        Assert.NotNull(vendor);
        Assert.False(vendor.IsActive);
    }

    [Fact]
    public async Task Unknown_vat_id_returns_null()
    {
        Assert.Null(await _source.FindVendorByVatIdAsync("DE999999999", CancellationToken.None));
    }

    [Fact]
    public async Task Existing_invoice_number_is_found_for_its_vendor_only()
    {
        Assert.True(await _source.InvoiceNumberExistsAsync(Vendor1, "RE-2026-0001", CancellationToken.None));
        Assert.False(await _source.InvoiceNumberExistsAsync(Vendor1, "RE-2026-9999", CancellationToken.None));
        Assert.False(await _source.InvoiceNumberExistsAsync(Guid.NewGuid(), "RE-2026-0001", CancellationToken.None));
    }

    [Fact]
    public async Task Possible_duplicate_is_same_vendor_amount_and_date_under_another_number()
    {
        var existing = await _context.Invoices.SingleAsync(i => i.InvoiceNumber == "RE-2026-0001");
        var gross = existing.GrossTotal!.Amount;

        Assert.Equal(1, await _source.CountPossibleDuplicatesAsync(Vendor1, gross, new DateOnly(2026, 11, 2), "RE-2026-7777", CancellationToken.None));
        Assert.Equal(0, await _source.CountPossibleDuplicatesAsync(Vendor1, gross, new DateOnly(2026, 11, 2), "RE-2026-0001", CancellationToken.None));
        Assert.Equal(0, await _source.CountPossibleDuplicatesAsync(Vendor1, gross + 0.01m, new DateOnly(2026, 11, 2), "RE-2026-7777", CancellationToken.None));
        Assert.Equal(0, await _source.CountPossibleDuplicatesAsync(Vendor1, gross, new DateOnly(2026, 11, 3), "RE-2026-7777", CancellationToken.None));
    }
}
