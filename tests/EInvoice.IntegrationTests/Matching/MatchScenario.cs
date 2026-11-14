using EInvoice.Domain.Invoices;
using EInvoice.Domain.Matching;
using EInvoice.Domain.Purchasing;
using EInvoice.Domain.Shared;
using EInvoice.Infrastructure.Matching;
using EInvoice.Infrastructure.Persistence;

namespace EInvoice.IntegrationTests.Matching;

/// <summary>
/// Builds a fresh purchase order with receipts and invoices in the database (unique numbers, so tests never
/// see each other's or the seed's quantities) and runs the same data through the SQL procedure and the C# matcher.
/// </summary>
internal sealed class MatchScenario(EInvoiceDbContext db)
{
    private static readonly Guid SeededVendor = Guid.Parse("00000000-0000-0000-0001-000000000001");
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    private readonly List<PurchaseOrderLine> _orderLines = [];
    private readonly List<(int PoLine, decimal Quantity)> _receipts = [];
    private bool _hasOrder = true;

    public string PurchaseOrderNumber { get; } = $"T{Guid.NewGuid():N}"[..16];

    public PurchaseOrder? Order { get; private set; }

    public List<GoodsReceipt> Receipts { get; } = [];

    public MatchScenario OrderLine(int lineNumber, decimal quantity, decimal unitPrice)
    {
        _orderLines.Add(new PurchaseOrderLine(lineNumber, $"ITEM-{lineNumber:00}", $"Item {lineNumber}", quantity, unitPrice, 19m));
        return this;
    }

    public MatchScenario Received(int poLine, decimal quantity)
    {
        _receipts.Add((poLine, quantity));
        return this;
    }

    public MatchScenario NoOrder()
    {
        _hasOrder = false;
        return this;
    }

    public async Task<MatchScenario> SaveOrderAsync()
    {
        if (_hasOrder)
        {
            Order = new PurchaseOrder(PurchaseOrderNumber, SeededVendor, new DateOnly(2026, 10, 1), "EUR", _orderLines);
            db.PurchaseOrders.Add(Order);
            foreach (var (poLine, quantity) in _receipts)
            {
                var receipt = new GoodsReceipt($"G{Guid.NewGuid():N}"[..16], Order.Id, new DateOnly(2026, 10, 5), [new GoodsReceiptLine(poLine, quantity)]);
                Receipts.Add(receipt);
                db.GoodsReceipts.Add(receipt);
            }

            await db.SaveChangesAsync();
        }

        return this;
    }

    /// <summary>Adds a parsed invoice that states this scenario's purchase order number (which may not exist).</summary>
    public async Task<Invoice> AddInvoiceAsync(params (int Line, decimal Quantity, decimal UnitPrice, int? PoLine)[] lines)
    {
        var invoice = Invoice.Receive(SourceFormat.XRechnung, "clerk@test", Now);
        var invoiceLines = lines
            .Select(l => new InvoiceLine(l.Line, $"Item {l.Line}", l.Quantity, l.UnitPrice, Money.Euro(l.Quantity * l.UnitPrice), 19m, l.PoLine))
            .ToList();
        var net = invoiceLines.Aggregate(Money.Zero("EUR"), (sum, l) => sum + l.NetAmount);
        invoice.MarkParsed(
            new ParsedInvoice($"I{Guid.NewGuid():N}", SeededVendor, new DateOnly(2026, 11, 2), PurchaseOrderNumber,
                net, net.Multiply(0.19m), net + net.Multiply(0.19m), invoiceLines),
            "system", Now.AddMinutes(1));
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();
        return invoice;
    }

    public Task<InvoiceMatchResult> MatchInSqlAsync(Invoice invoice, MatchingPolicy? policy = null) =>
        new SqlInvoiceMatcher(db).MatchAsync(invoice.Id, policy ?? MatchingPolicy.Default, CancellationToken.None);

    public InvoiceMatchResult MatchInCSharp(Invoice invoice, IReadOnlyDictionary<int, decimal>? alreadyInvoiced = null, MatchingPolicy? policy = null) =>
        ThreeWayMatcher.Match(invoice.Lines, Order, Receipts, alreadyInvoiced ?? new Dictionary<int, decimal>(), policy ?? MatchingPolicy.Default);

    /// <summary>The SQL result must equal the C# result line by line, field by field.</summary>
    public static void AssertSame(InvoiceMatchResult csharp, InvoiceMatchResult sql)
    {
        Assert.Equal(csharp.Lines.Count, sql.Lines.Count);
        foreach (var (c, s) in csharp.Lines.Zip(sql.Lines))
        {
            var line = $"line {c.InvoiceLineNumber}";
            Assert.True(c.InvoiceLineNumber == s.InvoiceLineNumber, $"{line}: line number {s.InvoiceLineNumber}");
            Assert.True(c.Outcome == s.Outcome, $"{line}: outcome C# {c.Outcome} vs SQL {s.Outcome}");
            Assert.True(c.PurchaseOrderLineNumber == s.PurchaseOrderLineNumber, $"{line}: order line C# {c.PurchaseOrderLineNumber} vs SQL {s.PurchaseOrderLineNumber}");
            Assert.True(c.InvoiceUnitPrice == s.InvoiceUnitPrice, $"{line}: invoice price C# {c.InvoiceUnitPrice} vs SQL {s.InvoiceUnitPrice}");
            Assert.True(c.OrderUnitPrice == s.OrderUnitPrice, $"{line}: order price C# {c.OrderUnitPrice} vs SQL {s.OrderUnitPrice}");
            Assert.True(c.PriceVarianceAmount?.Amount == s.PriceVarianceAmount?.Amount, $"{line}: variance amount C# {c.PriceVarianceAmount} vs SQL {s.PriceVarianceAmount}");
            Assert.True(c.QuantityInvoiced == s.QuantityInvoiced, $"{line}: quantity C# {c.QuantityInvoiced} vs SQL {s.QuantityInvoiced}");
            Assert.True(c.QuantityAvailable == s.QuantityAvailable, $"{line}: available C# {c.QuantityAvailable} vs SQL {s.QuantityAvailable}");
            Assert.True(c.QuantityVariance == s.QuantityVariance, $"{line}: quantity variance C# {c.QuantityVariance} vs SQL {s.QuantityVariance}");

            // The stored percentage is a display value with six decimals; the decision itself is made without division.
            var expectedPercent = c.PriceVariancePercent is { } p ? decimal.Round(p, 6, MidpointRounding.AwayFromZero) : (decimal?)null;
            Assert.True(expectedPercent == s.PriceVariancePercent, $"{line}: percent C# {expectedPercent} vs SQL {s.PriceVariancePercent}");
        }

        Assert.Equal(csharp.IsFullMatch, sql.IsFullMatch);
        Assert.Equal(csharp.ExceptionReasons, sql.ExceptionReasons);
    }
}
