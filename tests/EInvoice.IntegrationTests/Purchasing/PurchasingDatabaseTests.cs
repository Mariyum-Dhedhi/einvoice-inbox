using EInvoice.Application.Audit;
using EInvoice.Application.Extraction;
using EInvoice.Application.Intake;
using EInvoice.Application.Purchasing;
using EInvoice.Application.Validation;
using EInvoice.Domain.Invoices;
using EInvoice.Domain.Matching;
using EInvoice.Infrastructure.Extraction;
using EInvoice.Infrastructure.Intake;
using EInvoice.Infrastructure.Matching;
using EInvoice.Infrastructure.Parsing;
using EInvoice.Infrastructure.Persistence;
using EInvoice.Infrastructure.Purchasing;
using EInvoice.Infrastructure.Validation;
using EInvoice.IntegrationTests.Infrastructure;
using EInvoice.IntegrationTests.TestData;
using Microsoft.EntityFrameworkCore;

namespace EInvoice.IntegrationTests.Purchasing;

/// <summary>
/// Orders and goods receipts recorded through the app, in SQL Server: listed and searched, received quantities
/// summed, the invoices against an order shown, audited in the hash chain, and an invoice for a new order matched.
/// Rolled back; numbers are unique per test so other tests sharing the database never see them.
/// </summary>
[Trait("Category", "Database")]
[Collection(SqlServerCollection.Name)]
public sealed class PurchasingDatabaseTests(SqlServerFixture fixture) : IAsyncLifetime
{
    private const string Buyer = "buyer@test";

    /// <summary>Seeded vendor 12 (Demo Kabel &amp; Co. KG, active) and its approved bank account.</summary>
    private static readonly Guid Vendor = Guid.Parse("00000000-0000-0000-0001-000000000012");

    private EInvoiceDbContext _db = null!;
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();

    public async Task InitializeAsync()
    {
        _db = fixture.CreateContext();
        await _db.Database.BeginTransactionAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.Database.RollbackTransactionAsync();
        await _db.DisposeAsync();
    }

    private string Number(string prefix) => $"{prefix}-{_suffix}"; // at most 20 characters

    private PurchasingAdministration Purchasing() => new(new EfPurchasingStore(_db), TimeProvider.System);

    private EfPurchasingQueries Queries() => new(_db);

    private OrderDraft Order(string number) => new(number, Vendor, new DateOnly(2026, 11, 2),
    [
        new OrderLineDraft(1, "CABLE-HDMI", "HDMI cable 2 m", 40m, 7.4125m, 19m),
        new OrderLineDraft(2, "BOOK-HGB", "Handbook German commercial code", 3m, 39.90m, 7m),
    ]);

    private async Task<Guid> OrderAsync(string number)
    {
        var result = await Purchasing().CreateOrderAsync(Order(number), Buyer, CancellationToken.None);
        Assert.Equal(PurchasingOutcome.OrderCreated, result.Outcome);
        return result.Id!.Value;
    }

    [Fact]
    public async Task A_new_order_is_listed_with_its_vendor_total_and_receipt_state_and_found_by_number_or_vendor()
    {
        var number = Number("PO");
        await OrderAsync(number);

        var byNumber = await Queries().ListAsync(new PurchaseOrderListQuery(number, 1, 25), CancellationToken.None);
        var byVendor = await Queries().ListAsync(new PurchaseOrderListQuery("demo kabel", 1, 100), CancellationToken.None);

        var item = Assert.Single(byNumber.Items);
        Assert.Equal((number, "Demo Kabel & Co. KG", "EUR", 416.20m, ReceiptState.NothingReceived), (item.Number, item.VendorName, item.Currency, item.NetTotal, item.Received));
        Assert.Contains(byVendor.Items, i => i.Number == number);
        Assert.All(byVendor.Items, i => Assert.Equal("Demo Kabel & Co. KG", i.VendorName));
    }

    [Theory]
    [InlineData("%")]
    [InlineData("_")]
    [InlineData("[a-z]")]
    public async Task Search_text_is_taken_literally(string search)
    {
        await OrderAsync(Number("PO"));

        var page = await Queries().ListAsync(new PurchaseOrderListQuery(search, 1, 25), CancellationToken.None);

        Assert.Empty(page.Items); // no order number or vendor name contains these characters
    }

    [Fact]
    public async Task Receipts_add_up_per_line_and_the_detail_shows_ordered_received_open_and_invoiced()
    {
        var id = await OrderAsync(Number("PO"));
        Assert.Equal(PurchasingOutcome.ReceiptRecorded, (await Purchasing().RecordReceiptAsync(id,
            new ReceiptDraft(Number("GA"), new DateOnly(2026, 11, 5), [new ReceiptLineDraft(1, 25m)]), Buyer, CancellationToken.None)).Outcome);
        Assert.Equal(PurchasingOutcome.ReceiptRecorded, (await Purchasing().RecordReceiptAsync(id,
            new ReceiptDraft(Number("GB"), new DateOnly(2026, 11, 6), [new ReceiptLineDraft(1, 15m), new ReceiptLineDraft(2, 1m)]), Buyer, CancellationToken.None)).Outcome);

        var detail = await Queries().GetAsync(id, withInvoices: true, CancellationToken.None);

        Assert.NotNull(detail);
        Assert.Equal([(1, 40m, 40m, 0m), (2, 3m, 1m, 2m)], detail.Lines.Select(l => (l.LineNumber, l.Quantity, l.Received, l.Open)));
        Assert.Equal([Number("GA"), Number("GB")], detail.Receipts.Select(r => r.Number));
        Assert.Empty(detail.Invoices!);
        var listed = Assert.Single((await Queries().ListAsync(new PurchaseOrderListQuery(detail.Number, 1, 25), CancellationToken.None)).Items);
        Assert.Equal(ReceiptState.Partly, listed.Received);
    }

    [Fact]
    public async Task An_invoice_for_a_new_order_is_matched_against_it_and_shown_on_the_order()
    {
        var number = Number("PO");
        var id = await OrderAsync(number);
        await Purchasing().RecordReceiptAsync(id, new ReceiptDraft(Number("GR"), new DateOnly(2026, 11, 5), [new ReceiptLineDraft(1, 40m), new ReceiptLineDraft(2, 3m)]), Buyer, CancellationToken.None);
        var xml = new XRechnungBuilder
        {
            InvoiceNumber = Number("RE"), IssueDate = new DateOnly(2026, 11, 9), PurchaseOrderNumber = number,
            SellerName = "Demo Kabel & Co. KG", SellerVatId = "DE000000012", Iban = "DE66000000000000001012",
        }
        .Line("HDMI cable 2 m", 40m, 7.4125m, orderLine: "1")
        .Line("Handbook German commercial code", 3m, 39.90m, taxRate: 7m, orderLine: "2")
        .BuildXml();

        var result = await Intake().SubmitAsync(new UploadedFile("invoice.xml", System.Text.Encoding.UTF8.GetBytes(xml)), "clerk@test", CancellationToken.None);

        Assert.Equal((IntakeOutcome.Created, InvoiceStatus.Matched), (result.Outcome, result.Status));
        var detail = await Queries().GetAsync(id, withInvoices: true, CancellationToken.None);
        var invoice = Assert.Single(detail!.Invoices!);
        Assert.Equal((result.InvoiceId, InvoiceStatus.Matched), (invoice.Id, invoice.Status));
        Assert.Equal([40m, 3m], detail.Lines.Select(l => l.Invoiced));
    }

    [Fact]
    public async Task A_rejected_invoice_no_longer_counts_as_invoiced_but_is_still_listed()
    {
        // Found by a planted bug: "invoiced" counted every invoice quoting the order, rejected ones included.
        var number = Number("PO");
        var id = await OrderAsync(number);
        await Purchasing().RecordReceiptAsync(id, new ReceiptDraft(Number("GR"), new DateOnly(2026, 11, 5), [new ReceiptLineDraft(1, 40m), new ReceiptLineDraft(2, 3m)]), Buyer, CancellationToken.None);
        var xml = new XRechnungBuilder
        {
            InvoiceNumber = Number("RJ"), IssueDate = new DateOnly(2026, 11, 10), PurchaseOrderNumber = number,
            SellerName = "Demo Kabel & Co. KG", SellerVatId = "DE000000012", Iban = "DE66000000000000001012",
        }
        .Line("HDMI cable 2 m", 40m, 7.4125m, orderLine: "1")
        .BuildXml();
        var result = await Intake().SubmitAsync(new UploadedFile("invoice.xml", System.Text.Encoding.UTF8.GetBytes(xml)), "clerk@test", CancellationToken.None);
        var invoice = await _db.Invoices.SingleAsync(i => i.Id == result.InvoiceId);
        invoice.TransitionTo(InvoiceStatus.Rejected, "approver@test", DateTimeOffset.UtcNow, "Not ours");
        await _db.SaveChangesAsync();

        var detail = await Queries().GetAsync(id, withInvoices: true, CancellationToken.None);

        Assert.Equal([0m, 0m], detail!.Lines.Select(l => l.Invoiced));
        Assert.Equal(InvoiceStatus.Rejected, Assert.Single(detail.Invoices!).Status);
    }

    [Fact]
    public async Task Without_invoice_access_the_detail_holds_no_invoices()
    {
        var id = await OrderAsync(Number("PO"));

        var detail = await Queries().GetAsync(id, withInvoices: false, CancellationToken.None);

        Assert.Null(detail!.Invoices); // the buyer reads orders, not invoices
    }

    [Fact]
    public async Task Each_addition_writes_one_audit_event_in_the_chain()
    {
        var id = await OrderAsync(Number("PO"));
        await Purchasing().RecordReceiptAsync(id, new ReceiptDraft(Number("GR"), new DateOnly(2026, 11, 5), [new ReceiptLineDraft(1, 1m)]), Buyer, CancellationToken.None);

        var events = await _db.AuditEvents.Where(e => e.Subject == $"PurchaseOrder/{id}").OrderBy(e => e.Sequence).ToListAsync();

        Assert.Equal([AuditActions.PurchaseOrderCreated, AuditActions.GoodsReceiptRecorded], events.Select(e => e.Action));
        Assert.All(events, e => Assert.Equal(Buyer, e.Actor));
    }

    [Fact]
    public async Task Only_active_vendors_are_offered_for_a_new_order()
    {
        var vendors = await Queries().ActiveVendorsAsync(CancellationToken.None);

        Assert.Contains(vendors, v => v.Id == Vendor);
        Assert.DoesNotContain(vendors, v => v.Name == "Ehemals Lieferant GmbH"); // seeded vendor 20 is inactive
        Assert.Equal(vendors.Select(v => v.Number).Order(StringComparer.Ordinal), vendors.Select(v => v.Number));
    }

    [Fact]
    public async Task Two_buyers_saving_the_same_order_number_at_once_get_one_order()
    {
        var number = Number("RACE"); // committed for a moment: not the seed's PO- pattern
        await using var other = fixture.CreateContext();
        await using var otherTransaction = await other.Database.BeginTransactionAsync();
        var first = await new PurchasingAdministration(new EfPurchasingStore(other), TimeProvider.System).CreateOrderAsync(Order(number), "buyer2@test", CancellationToken.None);

        // The second one's check runs before the first commits: the unique index decides. Snapshot reads do not see the
        // uncommitted order, so the insert waits for the first transaction and then fails.
        var commit = Task.Run(async () =>
        {
            await Task.Delay(500);
            await otherTransaction.CommitAsync();
        });
        var second = await Purchasing().CreateOrderAsync(Order(number), Buyer, CancellationToken.None);
        await commit;

        Assert.Equal(PurchasingOutcome.OrderCreated, first.Outcome);
        Assert.Equal(PurchasingOutcome.NumberTaken, second.Outcome);
        Assert.False(await _db.AuditEvents.AnyAsync(e => e.Actor == Buyer)); // the refused one wrote no event

        // Committed by the other buyer: remove it again as an administrator would (no deletes for the app user).
        await using var admin = fixture.CreateAdminContext();
        await admin.Database.ExecuteSqlInterpolatedAsync($"DELETE l FROM dbo.PurchaseOrderLine l JOIN dbo.PurchaseOrder p ON p.Id = l.PurchaseOrderId WHERE p.Number = {number}; DELETE FROM dbo.PurchaseOrder WHERE Number = {number}");
    }

    private InvoiceIntake Intake()
    {
        var data = new EfValidationDataSource(_db);
        return new InvoiceIntake(
            new XRechnungParser(),
            new InvoiceValidator(data, new AcceptingConformance()),
            data,
            new SqlInvoiceMatcher(_db),
            new EfIntakeStore(_db),
            MatchingPolicy.Default,
            IntakeOptions.Default,
            TimeProvider.System,
            new DisabledInvoiceExtractor(),
            new PdfEmbeddedInvoiceReader(EInvoice.Application.Extraction.PdfLimits.Default, IntakeOptions.Default.MaxDocumentBytes));
    }

    private sealed class AcceptingConformance : IConformanceValidator
    {
        public Task<ConformanceResult> CheckAsync(ReadOnlyMemory<byte> xml, CancellationToken cancellationToken) =>
            Task.FromResult(ConformanceResult.Accepted);
    }
}
