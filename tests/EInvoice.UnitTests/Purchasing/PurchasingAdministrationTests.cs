using EInvoice.Application.Audit;
using EInvoice.Application.Purchasing;
using EInvoice.Domain.Purchasing;

namespace EInvoice.UnitTests.Purchasing;

/// <summary>
/// Recording what was ordered and what arrived: the data invoices are matched against. Add only: nothing recorded is
/// changed afterwards, so a matched invoice never loses its basis. Every addition is saved together with its audit
/// event; a refusal stores and audits nothing, and says which line is wrong. Values too long or too precise for the
/// database are refused, never cut.
/// </summary>
public sealed class PurchasingAdministrationTests
{
    private const string Buyer = "buyer@test";
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero); // 10:00 in Berlin
    private static readonly DateOnly Today = new(2026, 11, 17);

    private readonly FakeStore _store = new();

    private PurchasingAdministration Purchasing() => new(_store, new FixedTime(Now));

    private OrderDraft Order(string number = "PO-7001", IReadOnlyList<OrderLineDraft>? lines = null, DateOnly? date = null, Guid? vendor = null) =>
        new(number, vendor ?? _store.ActiveVendor, date ?? Today,
            lines ?? [new OrderLineDraft(1, "DESK-160", "Desk 160 x 80 cm", 4m, 349.00m, 19m), new OrderLineDraft(2, "BOOK-HGB", "Handbook", 10m, 39.90m, 7m)]);

    private async Task<PurchaseOrder> CreatedAsync()
    {
        var result = await Purchasing().CreateOrderAsync(Order(), Buyer, CancellationToken.None);
        _store.Audited.Clear();
        _store.Saves = 0;
        return _store.Orders.Single(o => o.Id == result.Id);
    }

    // ---- Orders ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_new_order_is_stored_with_its_lines_and_audited_in_the_same_save()
    {
        var result = await Purchasing().CreateOrderAsync(Order(), Buyer, CancellationToken.None);

        Assert.Equal(PurchasingOutcome.OrderCreated, result.Outcome);
        var order = Assert.Single(_store.Orders);
        Assert.Equal((result.Id, "PO-7001", _store.ActiveVendor, Today, "EUR"), (order.Id, order.Number, order.VendorId, order.OrderDate, order.Currency));
        Assert.Equal([(1, 4m, 349.00m), (2, 10m, 39.90m)], order.Lines.Select(l => (l.LineNumber, l.Quantity, l.UnitPrice)));
        var audit = Assert.Single(_store.Audited);
        Assert.Equal((AuditActions.PurchaseOrderCreated, Buyer, Now, $"PurchaseOrder/{order.Id}"), (audit.Action, audit.Actor, audit.At, audit.Subject));
        Assert.Contains("PO-7001", audit.Details, StringComparison.Ordinal);
        Assert.Equal(1, _store.Saves);
    }

    [Fact]
    public async Task Text_is_trimmed_before_it_is_checked_and_stored()
    {
        var result = await Purchasing().CreateOrderAsync(
            Order(number: "  PO-7002 ", lines: [new OrderLineDraft(1, " DESK-160 ", "  Desk  ", 1m, 349m, 19m)]), Buyer, CancellationToken.None);

        Assert.Equal(PurchasingOutcome.OrderCreated, result.Outcome);
        var order = Assert.Single(_store.Orders);
        Assert.Equal(("PO-7002", "DESK-160", "Desk"), (order.Number, order.Lines[0].ItemNumber, order.Lines[0].Description));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("PO 7001")]                 // a space would not match the reference an invoice quotes
    [InlineData("PO-7001;DROP")]
    [InlineData("-PO")]                      // starts with a letter or digit
    [InlineData("PO-123456789012345678")]    // 21 characters: refused, never cut to 20
    public async Task An_order_number_that_cannot_be_stored_as_typed_is_refused(string number)
    {
        var result = await Purchasing().CreateOrderAsync(Order(number), Buyer, CancellationToken.None);

        AssertRefused(result, PurchasingOutcome.InvalidNumber);
    }

    [Fact]
    public async Task Twenty_characters_are_accepted()
    {
        var result = await Purchasing().CreateOrderAsync(Order("PO-12345678901234567"), Buyer, CancellationToken.None);

        Assert.Equal(PurchasingOutcome.OrderCreated, result.Outcome);
    }

    [Fact]
    public async Task An_order_number_that_exists_is_refused()
    {
        await CreatedAsync();

        var result = await Purchasing().CreateOrderAsync(Order("po-7001"), Buyer, CancellationToken.None); // ignoring case, as SQL Server compares

        AssertRefused(result, PurchasingOutcome.NumberTaken);
    }

    [Fact]
    public async Task The_same_number_saved_by_someone_else_at_the_same_moment_is_refused_without_an_event()
    {
        _store.DuplicateOnSave = true; // the unique index wins the race

        var result = await Purchasing().CreateOrderAsync(Order(), Buyer, CancellationToken.None);

        AssertRefused(result, PurchasingOutcome.NumberTaken);
    }

    [Fact]
    public async Task An_unknown_vendor_is_refused()
    {
        var result = await Purchasing().CreateOrderAsync(Order(vendor: Guid.NewGuid()), Buyer, CancellationToken.None);

        AssertRefused(result, PurchasingOutcome.VendorNotFound);
    }

    [Fact]
    public async Task A_deactivated_vendor_gets_no_new_orders()
    {
        var result = await Purchasing().CreateOrderAsync(Order(vendor: _store.InactiveVendor), Buyer, CancellationToken.None);

        AssertRefused(result, PurchasingOutcome.VendorInactive);
    }

    [Fact]
    public async Task An_order_dated_in_the_future_is_refused()
    {
        var result = await Purchasing().CreateOrderAsync(Order(date: Today.AddDays(1)), Buyer, CancellationToken.None);

        AssertRefused(result, PurchasingOutcome.InvalidDate);
    }

    [Fact]
    public async Task An_order_without_lines_is_refused()
    {
        var result = await Purchasing().CreateOrderAsync(Order(lines: []), Buyer, CancellationToken.None);

        AssertRefused(result, PurchasingOutcome.InvalidLine);
    }

    public static TheoryData<OrderLineDraft> BadLines() => new()
    {
        new OrderLineDraft(2, "X", "Desk", 0m, 1m, 19m),                         // nothing ordered
        new OrderLineDraft(2, "X", "Desk", -1m, 1m, 19m),
        new OrderLineDraft(2, "X", "Desk", 1.00001m, 1m, 19m),                   // 5 decimals: quantity holds 4
        new OrderLineDraft(2, "X", "Desk", 100_000_000_000_000m, 1m, 19m),       // more than decimal(18,4) holds
        new OrderLineDraft(2, "X", "Desk", 1m, -0.01m, 19m),
        new OrderLineDraft(2, "X", "Desk", 1m, 1.0000001m, 19m),                 // 7 decimals: unit price holds 6
        new OrderLineDraft(2, "X", "Desk", 1m, 1_000_000_000_000m, 19m),         // more than decimal(18,6) holds
        new OrderLineDraft(2, "X", "Desk", 1m, 1m, -1m),
        new OrderLineDraft(2, "X", "Desk", 1m, 1m, 100.01m),
        new OrderLineDraft(2, "X", "Desk", 1m, 1m, 19.005m),                     // tax rate holds 2 decimals
        new OrderLineDraft(2, "", "Desk", 1m, 1m, 19m),
        new OrderLineDraft(2, new string('I', 51), "Desk", 1m, 1m, 19m),         // item number holds 50
        new OrderLineDraft(2, "X", " ", 1m, 1m, 19m),
        new OrderLineDraft(2, "X", new string('D', 501), 1m, 1m, 19m),           // description holds 500
        new OrderLineDraft(2, "X", "Desk\u0000", 1m, 1m, 19m),                   // control characters
        new OrderLineDraft(0, "X", "Desk", 1m, 1m, 19m),                         // line numbers start at 1
        new OrderLineDraft(1, "X", "Desk", 1m, 1m, 19m),                         // line 1 twice
    };

    [Theory]
    [MemberData(nameof(BadLines))]
    public async Task A_line_that_cannot_be_stored_as_typed_is_refused_and_named(OrderLineDraft bad)
    {
        var good = new OrderLineDraft(1, "DESK-160", "Desk 160 x 80 cm", 4m, 349.00m, 19m);

        var result = await Purchasing().CreateOrderAsync(Order(lines: [good, bad]), Buyer, CancellationToken.None);

        AssertRefused(result, PurchasingOutcome.InvalidLine);
        Assert.Equal(2, result.Position); // the second line on the form
    }

    [Fact]
    public async Task The_limits_themselves_are_accepted()
    {
        var line = new OrderLineDraft(1, new string('I', 50), new string('D', 500), 99_999_999_999_999.9999m, 999_999_999_999.999999m, 100m);

        var result = await Purchasing().CreateOrderAsync(Order(lines: [line, new OrderLineDraft(2, "X", "Y", 0.0001m, 0m, 0m)]), Buyer, CancellationToken.None);

        Assert.Equal(PurchasingOutcome.OrderCreated, result.Outcome);
    }

    [Fact]
    public async Task At_most_one_hundred_lines()
    {
        var lines = Enumerable.Range(1, PurchasingLimits.MaxLines + 1).Select(n => new OrderLineDraft(n, "X", "Item", 1m, 1m, 19m)).ToList();

        var tooMany = await Purchasing().CreateOrderAsync(Order(lines: lines), Buyer, CancellationToken.None);
        var enough = await Purchasing().CreateOrderAsync(Order("PO-7003", lines: lines[..PurchasingLimits.MaxLines]), Buyer, CancellationToken.None);

        Assert.Equal(PurchasingOutcome.InvalidLine, tooMany.Outcome);
        Assert.Equal(PurchasingOutcome.OrderCreated, enough.Outcome);
    }

    // ---- Goods receipts -------------------------------------------------------------------------------------------

    private ReceiptDraft Receipt(string number = "GR-7001", DateOnly? date = null, params ReceiptLineDraft[] lines) =>
        new(number, date ?? Today, lines.Length > 0 ? lines : [new ReceiptLineDraft(1, 4m), new ReceiptLineDraft(2, 10m)]);

    [Fact]
    public async Task A_receipt_is_stored_for_its_order_and_audited_in_the_same_save()
    {
        var order = await CreatedAsync();

        var result = await Purchasing().RecordReceiptAsync(order.Id, Receipt(), Buyer, CancellationToken.None);

        Assert.Equal(PurchasingOutcome.ReceiptRecorded, result.Outcome);
        var receipt = Assert.Single(_store.Receipts);
        Assert.Equal((result.Id, "GR-7001", order.Id, Today), (receipt.Id, receipt.Number, receipt.PurchaseOrderId, receipt.ReceivedDate));
        Assert.Equal([(1, 4m), (2, 10m)], receipt.Lines.Select(l => (l.PurchaseOrderLineNumber, l.QuantityReceived)));
        var audit = Assert.Single(_store.Audited);
        Assert.Equal((AuditActions.GoodsReceiptRecorded, Buyer, $"PurchaseOrder/{order.Id}"), (audit.Action, audit.Actor, audit.Subject));
        Assert.Contains("GR-7001", audit.Details, StringComparison.Ordinal);
        Assert.Equal(1, _store.Saves);
    }

    [Fact]
    public async Task Lines_with_nothing_received_are_left_out_of_a_partial_delivery()
    {
        var order = await CreatedAsync();

        var result = await Purchasing().RecordReceiptAsync(order.Id, Receipt(lines: [new ReceiptLineDraft(1, 2m), new ReceiptLineDraft(2, 0m)]), Buyer, CancellationToken.None);

        Assert.Equal(PurchasingOutcome.ReceiptRecorded, result.Outcome);
        Assert.Equal([(1, 2m)], Assert.Single(_store.Receipts).Lines.Select(l => (l.PurchaseOrderLineNumber, l.QuantityReceived)));
    }

    [Fact]
    public async Task A_receipt_with_nothing_in_it_is_refused()
    {
        var order = await CreatedAsync();

        var result = await Purchasing().RecordReceiptAsync(order.Id, Receipt(lines: [new ReceiptLineDraft(1, 0m)]), Buyer, CancellationToken.None);

        AssertRefused(result, PurchasingOutcome.NothingReceived);
    }

    [Fact]
    public async Task More_than_was_ordered_is_refused_counting_earlier_receipts()
    {
        var order = await CreatedAsync();
        await Purchasing().RecordReceiptAsync(order.Id, Receipt("GR-7001", null, new ReceiptLineDraft(1, 3m)), Buyer, CancellationToken.None);
        _store.Audited.Clear();
        _store.Saves = 0;

        var tooMuch = await Purchasing().RecordReceiptAsync(order.Id, Receipt("GR-7002", null, new ReceiptLineDraft(1, 1.0001m)), Buyer, CancellationToken.None);
        var rest = await Purchasing().RecordReceiptAsync(order.Id, Receipt("GR-7003", null, new ReceiptLineDraft(1, 1m)), Buyer, CancellationToken.None);

        Assert.Equal((PurchasingOutcome.OverReceived, 1), (tooMuch.Outcome, tooMuch.Position));
        Assert.Equal(PurchasingOutcome.ReceiptRecorded, rest.Outcome); // exactly the 4 ordered
    }

    [Theory]
    [InlineData(3, 1)]          // the order has lines 1 and 2
    [InlineData(1, -1)]
    [InlineData(1, 0.00001)]    // 5 decimals
    public async Task A_receipt_line_that_does_not_fit_the_order_is_refused(int orderLine, double quantity)
    {
        var order = await CreatedAsync();

        var result = await Purchasing().RecordReceiptAsync(order.Id, Receipt(lines: [new ReceiptLineDraft(orderLine, (decimal)quantity)]), Buyer, CancellationToken.None);

        AssertRefused(result, PurchasingOutcome.InvalidLine);
    }

    [Fact]
    public async Task The_same_order_line_twice_in_one_receipt_is_refused()
    {
        var order = await CreatedAsync();

        var result = await Purchasing().RecordReceiptAsync(order.Id, Receipt(lines: [new ReceiptLineDraft(1, 1m), new ReceiptLineDraft(1, 1m)]), Buyer, CancellationToken.None);

        AssertRefused(result, PurchasingOutcome.InvalidLine);
    }

    [Fact]
    public async Task A_receipt_before_the_order_or_in_the_future_is_refused()
    {
        var order = await CreatedAsync();

        var before = await Purchasing().RecordReceiptAsync(order.Id, Receipt(date: order.OrderDate.AddDays(-1)), Buyer, CancellationToken.None);
        var future = await Purchasing().RecordReceiptAsync(order.Id, Receipt(date: Today.AddDays(1)), Buyer, CancellationToken.None);

        AssertRefused(before, PurchasingOutcome.InvalidDate);
        AssertRefused(future, PurchasingOutcome.InvalidDate);
    }

    [Fact]
    public async Task A_receipt_for_an_unknown_order_is_refused()
    {
        var result = await Purchasing().RecordReceiptAsync(Guid.NewGuid(), Receipt(), Buyer, CancellationToken.None);

        AssertRefused(result, PurchasingOutcome.OrderNotFound);
    }

    [Theory]
    [InlineData("")]
    [InlineData("GR 1")]
    [InlineData("GR-123456789012345678")]
    public async Task A_receipt_number_that_cannot_be_stored_as_typed_is_refused(string number)
    {
        var order = await CreatedAsync();

        var result = await Purchasing().RecordReceiptAsync(order.Id, Receipt(number), Buyer, CancellationToken.None);

        AssertRefused(result, PurchasingOutcome.InvalidNumber);
    }

    [Fact]
    public async Task A_receipt_number_that_exists_is_refused_also_in_a_race()
    {
        var order = await CreatedAsync();
        await Purchasing().RecordReceiptAsync(order.Id, Receipt("GR-7001", null, new ReceiptLineDraft(1, 1m)), Buyer, CancellationToken.None);
        _store.Audited.Clear();
        _store.Saves = 0;

        var known = await Purchasing().RecordReceiptAsync(order.Id, Receipt("gr-7001", null, new ReceiptLineDraft(1, 1m)), Buyer, CancellationToken.None);
        _store.DuplicateOnSave = true;
        var race = await Purchasing().RecordReceiptAsync(order.Id, Receipt("GR-7009", null, new ReceiptLineDraft(1, 1m)), Buyer, CancellationToken.None);

        AssertRefused(known, PurchasingOutcome.NumberTaken);
        AssertRefused(race, PurchasingOutcome.NumberTaken);
    }

    private void AssertRefused(PurchasingResult result, PurchasingOutcome outcome)
    {
        Assert.Equal(outcome, result.Outcome);
        Assert.Null(result.Id);
        Assert.False(string.IsNullOrWhiteSpace(result.Detail));
        Assert.Empty(_store.Audited);
        Assert.Equal(0, _store.Saves);
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeStore : IPurchasingStore
    {
        public Guid ActiveVendor { get; } = Guid.NewGuid();

        public Guid InactiveVendor { get; } = Guid.NewGuid();

        public List<PurchaseOrder> Orders { get; } = [];

        public List<GoodsReceipt> Receipts { get; } = [];

        public List<AuditDraft> Audited { get; } = [];

        public int Saves { get; set; }

        public bool DuplicateOnSave { get; set; }

        private readonly List<AuditDraft> _queued = [];
        private readonly List<PurchaseOrder> _orders = [];
        private readonly List<GoodsReceipt> _receipts = [];

        public Task<bool?> VendorIsActiveAsync(Guid vendorId, CancellationToken cancellationToken) =>
            Task.FromResult<bool?>(vendorId == ActiveVendor ? true : vendorId == InactiveVendor ? false : null);

        public Task<bool> OrderNumberExistsAsync(string number, CancellationToken cancellationToken) =>
            Task.FromResult(Orders.Any(o => string.Equals(o.Number, number, StringComparison.OrdinalIgnoreCase)));

        public Task<bool> ReceiptNumberExistsAsync(string number, CancellationToken cancellationToken) =>
            Task.FromResult(Receipts.Any(r => string.Equals(r.Number, number, StringComparison.OrdinalIgnoreCase)));

        public Task<PurchaseOrder?> FindOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
            Task.FromResult(Orders.SingleOrDefault(o => o.Id == orderId));

        public Task<IReadOnlyDictionary<int, decimal>> ReceivedAsync(Guid orderId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<int, decimal>>(Receipts.Where(r => r.PurchaseOrderId == orderId)
                .SelectMany(r => r.Lines).GroupBy(l => l.PurchaseOrderLineNumber).ToDictionary(g => g.Key, g => g.Sum(l => l.QuantityReceived)));

        public void Add(PurchaseOrder order) => _orders.Add(order);

        public void Add(GoodsReceipt receipt) => _receipts.Add(receipt);

        public void Audit(AuditDraft draft) => _queued.Add(draft);

        public Task SaveChangesAsync(CancellationToken cancellationToken)
        {
            if (DuplicateOnSave)
            {
                _queued.Clear();
                _orders.Clear();
                _receipts.Clear();
                throw new DuplicateNumberException();
            }

            Saves++;
            Orders.AddRange(_orders);
            Receipts.AddRange(_receipts);
            Audited.AddRange(_queued);
            _orders.Clear();
            _receipts.Clear();
            _queued.Clear();
            return Task.CompletedTask;
        }
    }
}
