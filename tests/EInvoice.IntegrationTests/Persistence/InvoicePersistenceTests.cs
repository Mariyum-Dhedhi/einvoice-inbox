using EInvoice.Domain.Invoices;
using EInvoice.Domain.Shared;
using EInvoice.Infrastructure.Persistence;
using EInvoice.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace EInvoice.IntegrationTests.Persistence;

/// <summary>
/// Round-trip tests against a real SQL Server, connected as the low-privilege application user.
/// Each test runs in a transaction that is rolled back, so the seeded data is never changed.
/// Runs against the shared Testcontainers SQL Server (SqlServerFixture).
/// </summary>
[Trait("Category", "Database")]
[Collection(SqlServerCollection.Name)]
public sealed class InvoicePersistenceTests(SqlServerFixture fixture) : IAsyncLifetime
{
    // Seeded vendor V-1001 (see db/seed/seed.sql).
    private static readonly Guid SeededVendorId = Guid.Parse("00000000-0000-0000-0001-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    private EInvoiceDbContext _context = null!;

    public async Task InitializeAsync()
    {
        _context = fixture.CreateContext();
        await _context.Database.BeginTransactionAsync();
    }

    public async Task DisposeAsync()
    {
        await _context.Database.RollbackTransactionAsync();
        await _context.DisposeAsync();
    }

    private static Invoice ParsedInvoice(string number)
    {
        var invoice = Invoice.Receive(SourceFormat.XRechnung, "clerk@test", Now);
        invoice.MarkParsed(
            new ParsedInvoice(number, SeededVendorId, new DateOnly(2026, 11, 2), "PO-0001",
                Money.Euro(100.00m), Money.Euro(19.00m), Money.Euro(119.00m),
                [new InvoiceLine(1, "Office chair", 2m, 50.0125m, Money.Euro(100.03m), 19m, purchaseOrderLineNumber: 1)]),
            "system",
            Now.AddMinutes(1));
        return invoice;
    }

    [Fact]
    public async Task Invoice_round_trips_with_lines_money_and_history()
    {
        var invoice = ParsedInvoice("IT-ROUNDTRIP-1");
        _context.Invoices.Add(invoice);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var loaded = await _context.Invoices.SingleAsync(i => i.Id == invoice.Id);

        Assert.Equal(InvoiceStatus.Parsed, loaded.Status);
        Assert.Equal("clerk@test", loaded.SubmittedBy);
        Assert.Equal(Money.Euro(119.00m), loaded.GrossTotal);
        var line = Assert.Single(loaded.Lines);
        Assert.Equal(50.0125m, line.UnitPrice); // more than two decimals survive
        Assert.Equal(Money.Euro(100.03m), line.NetAmount);
        Assert.Equal([InvoiceStatus.Received, InvoiceStatus.Parsed], loaded.StatusHistory.Select(h => h.To));
        Assert.Null(loaded.StatusHistory[0].From);
    }

    [Fact]
    public async Task Database_rejects_a_second_invoice_with_the_same_vendor_and_number()
    {
        _context.Invoices.Add(ParsedInvoice("IT-DUPLICATE-1"));
        await _context.SaveChangesAsync();

        _context.Invoices.Add(ParsedInvoice("IT-DUPLICATE-1"));

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => _context.SaveChangesAsync());
        Assert.Contains("UX_Invoice_Vendor_InvoiceNumber", ex.InnerException?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Several_just_received_invoices_without_number_are_allowed()
    {
        _context.Invoices.Add(Invoice.Receive(SourceFormat.XRechnung, "clerk@test", Now));
        _context.Invoices.Add(Invoice.Receive(SourceFormat.Pdf, "clerk@test", Now));

        await _context.SaveChangesAsync(); // the filtered index ignores rows with no vendor or number
    }

    [Fact]
    public async Task Seed_data_is_present()
    {
        Assert.Equal(20, await _context.Vendors.CountAsync());
        // Count the seeded orders by their numbers: other tests may commit their own orders (unique T... numbers).
        Assert.Equal(200, await _context.PurchaseOrders.CountAsync(p => p.Number.StartsWith("PO-")));
        Assert.True(await _context.GoodsReceipts.AnyAsync());
    }
}
