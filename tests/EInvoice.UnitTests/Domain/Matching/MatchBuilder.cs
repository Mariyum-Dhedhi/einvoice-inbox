using EInvoice.Domain.Invoices;
using EInvoice.Domain.Matching;
using EInvoice.Domain.Purchasing;
using EInvoice.Domain.Shared;

namespace EInvoice.UnitTests.Domain.Matching;

/// <summary>Small builder for synthetic three-way match scenarios.</summary>
internal sealed class MatchBuilder
{
    private static readonly Guid VendorId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly List<PurchaseOrderLine> _orderLines = [];
    private readonly List<(int PoLine, decimal Quantity)> _receipts = [];
    private readonly List<InvoiceLine> _invoiceLines = [];
    private readonly Dictionary<int, decimal> _alreadyInvoiced = [];
    private bool _hasOrder = true;
    private MatchingPolicy _policy = MatchingPolicy.Default;

    public MatchBuilder OrderLine(int lineNumber, decimal quantity, decimal unitPrice)
    {
        _orderLines.Add(new PurchaseOrderLine(lineNumber, $"ITEM-{lineNumber:00}", $"Item {lineNumber}", quantity, unitPrice, 19m));
        return this;
    }

    public MatchBuilder NoOrder()
    {
        _hasOrder = false;
        return this;
    }

    public MatchBuilder Received(int poLine, decimal quantity)
    {
        _receipts.Add((poLine, quantity));
        return this;
    }

    public MatchBuilder AlreadyInvoiced(int poLine, decimal quantity)
    {
        _alreadyInvoiced[poLine] = quantity;
        return this;
    }

    public MatchBuilder InvoiceLine(int lineNumber, decimal quantity, decimal unitPrice, int? poLine = null)
    {
        _invoiceLines.Add(new InvoiceLine(lineNumber, $"Item {lineNumber}", quantity, unitPrice, Money.Euro(quantity * unitPrice), 19m, poLine));
        return this;
    }

    public MatchBuilder Policy(MatchingPolicy policy)
    {
        _policy = policy;
        return this;
    }

    public InvoiceMatchResult Match()
    {
        PurchaseOrder? order = _hasOrder
            ? new PurchaseOrder("PO-1001", VendorId, new DateOnly(2026, 10, 1), "EUR", _orderLines)
            : null;

        // One receipt document per delivery, so several deliveries of the same line are summed.
        var receipts = order is null
            ? []
            : _receipts
                .Select((r, i) => new GoodsReceipt($"GR-{5000 + i}", order.Id, new DateOnly(2026, 10, 5), [new GoodsReceiptLine(r.PoLine, r.Quantity)]))
                .ToList();

        return ThreeWayMatcher.Match(_invoiceLines, order, receipts, _alreadyInvoiced, _policy);
    }

    public LineMatchResult MatchSingleLine() => Assert.Single(Match().Lines);
}
