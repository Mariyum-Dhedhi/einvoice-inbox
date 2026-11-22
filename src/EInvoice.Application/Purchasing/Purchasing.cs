using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using EInvoice.Application.Audit;
using EInvoice.Domain.Purchasing;
using EInvoice.Domain.Shared;

namespace EInvoice.Application.Purchasing;

public enum PurchasingOutcome
{
    OrderCreated = 1,
    ReceiptRecorded = 2,

    /// <summary>An order or receipt number that cannot be stored as typed (empty, too long, characters).</summary>
    InvalidNumber = 3,

    /// <summary>The number exists already (also when someone else saved it at the same moment).</summary>
    NumberTaken = 4,

    VendorNotFound = 5,

    /// <summary>A deactivated vendor gets no new orders.</summary>
    VendorInactive = 6,

    /// <summary>A date in the future, or a receipt before its order.</summary>
    InvalidDate = 7,

    /// <summary>A line that cannot be stored as typed, or does not fit the order; <see cref="PurchasingResult.Position"/> says which.</summary>
    InvalidLine = 8,

    OrderNotFound = 9,

    /// <summary>A receipt with no quantity in it.</summary>
    NothingReceived = 10,

    /// <summary>More received than was ordered, counting earlier receipts.</summary>
    OverReceived = 11,

    /// <summary>The order comes from the ERP: its receipts come from the ERP too (package 16c).</summary>
    ErpManaged = 12,
}

/// <param name="Id">The new order or receipt; null for a refusal.</param>
/// <param name="Detail">A safe reason, in English, for refusals (logs, API).</param>
/// <param name="Position">For a line problem: the line's position on the form (1-based), so the page can name it.</param>
public sealed record PurchasingResult(PurchasingOutcome Outcome, Guid? Id = null, string? Detail = null, int? Position = null);

public sealed record OrderLineDraft(int LineNumber, string ItemNumber, string Description, decimal Quantity, decimal UnitPrice, decimal TaxRatePercent);

public sealed record OrderDraft(string Number, Guid VendorId, DateOnly OrderDate, IReadOnlyList<OrderLineDraft> Lines);

public sealed record ReceiptLineDraft(int OrderLineNumber, decimal Quantity);

public sealed record ReceiptDraft(string Number, DateOnly ReceivedDate, IReadOnlyList<ReceiptLineDraft> Lines);

/// <summary>What the database columns hold (MappingConventions): longer or more precise values are refused, never cut.</summary>
public static class PurchasingLimits
{
    public const int MaxNumberLength = 20;
    public const int MaxItemNumberLength = 50;
    public const int MaxDescriptionLength = 500;
    public const int MaxLines = 100;
    public const int QuantityDecimals = 4;
    public const int UnitPriceDecimals = 6;
    public const int TaxRateDecimals = 2;

    /// <summary>decimal(18,4): 14 digits before the point.</summary>
    public const decimal MaxQuantity = 99_999_999_999_999.9999m;

    /// <summary>decimal(18,6): 12 digits before the point.</summary>
    public const decimal MaxUnitPrice = 999_999_999_999.999999m;

    /// <summary>Orders are in euros, like the approval limits and the KPIs.</summary>
    public const string Currency = "EUR";
}

public interface IPurchasing
{
    Task<PurchasingResult> CreateOrderAsync(OrderDraft draft, string buyer, CancellationToken cancellationToken);

    Task<PurchasingResult> RecordReceiptAsync(Guid orderId, ReceiptDraft draft, string buyer, CancellationToken cancellationToken);
}

public interface IPurchasingStore
{
    /// <summary>True for an active vendor, false for a deactivated one, null for none.</summary>
    Task<bool?> VendorIsActiveAsync(Guid vendorId, CancellationToken cancellationToken);

    Task<bool> OrderNumberExistsAsync(string number, CancellationToken cancellationToken);

    Task<bool> ReceiptNumberExistsAsync(string number, CancellationToken cancellationToken);

    Task<PurchaseOrder?> FindOrderAsync(Guid orderId, CancellationToken cancellationToken);

    /// <summary>Received so far per order line, over every receipt of the order.</summary>
    Task<IReadOnlyDictionary<int, decimal>> ReceivedAsync(Guid orderId, CancellationToken cancellationToken);

    void Add(PurchaseOrder order);

    void Add(GoodsReceipt receipt);

    /// <summary>Queues an audit event that is written by the next save, in the same transaction.</summary>
    void Audit(AuditDraft draft);

    /// <exception cref="DuplicateNumberException">The order or receipt number exists (unique index); nothing was saved.</exception>
    Task SaveChangesAsync(CancellationToken cancellationToken);
}

public sealed class DuplicateNumberException : Exception
{
    public DuplicateNumberException()
        : base("An order or goods receipt with this number exists.")
    {
    }
}

/// <summary>
/// Records what was ordered and what arrived: the data invoices are matched against. Add only: nothing recorded is
/// changed afterwards, so a matched invoice never loses its basis. Every addition is saved with its audit event;
/// a refusal stores and audits nothing.
/// </summary>
public sealed partial class PurchasingAdministration(IPurchasingStore store, TimeProvider time) : IPurchasing
{
    private static readonly TimeZoneInfo Berlin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");

    public async Task<PurchasingResult> CreateOrderAsync(OrderDraft draft, string buyer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(buyer);

        var number = draft.Number?.Trim() ?? string.Empty;
        if (!IsNumber(number))
        {
            return Refused(PurchasingOutcome.InvalidNumber, $"An order number has 1 to {PurchasingLimits.MaxNumberLength} letters, digits or . _ / - and starts with a letter or digit.");
        }

        if (draft.OrderDate > Today())
        {
            return Refused(PurchasingOutcome.InvalidDate, "The order date is in the future.");
        }

        if (CheckLines(draft.Lines) is { } lineProblem)
        {
            return lineProblem;
        }

        switch (await store.VendorIsActiveAsync(draft.VendorId, cancellationToken).ConfigureAwait(false))
        {
            case null:
                return Refused(PurchasingOutcome.VendorNotFound, "There is no such vendor.");
            case false:
                return Refused(PurchasingOutcome.VendorInactive, "The vendor is deactivated and gets no new orders.");
        }

        if (await store.OrderNumberExistsAsync(number, cancellationToken).ConfigureAwait(false))
        {
            return Taken("order");
        }

        var order = new PurchaseOrder(number, draft.VendorId, draft.OrderDate, PurchasingLimits.Currency,
            draft.Lines.Select(l => new PurchaseOrderLine(l.LineNumber, l.ItemNumber.Trim(), l.Description.Trim(), l.Quantity, l.UnitPrice, l.TaxRatePercent)).ToList());
        store.Add(order);
        store.Audit(new AuditDraft(time.GetUtcNow(), buyer, AuditActions.PurchaseOrderCreated, Subject(order.Id),
            JsonSerializer.Serialize(new
            {
                number,
                vendorId = draft.VendorId,
                orderDate = draft.OrderDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                lines = order.Lines.Select(l => new { line = l.LineNumber, quantity = l.Quantity, unitPrice = l.UnitPrice, taxRate = l.TaxRatePercent }),
            })));
        return await SaveAsync(new PurchasingResult(PurchasingOutcome.OrderCreated, order.Id), "order", cancellationToken).ConfigureAwait(false);
    }

    public async Task<PurchasingResult> RecordReceiptAsync(Guid orderId, ReceiptDraft draft, string buyer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(buyer);

        var number = draft.Number?.Trim() ?? string.Empty;
        if (!IsNumber(number))
        {
            return Refused(PurchasingOutcome.InvalidNumber, $"A goods receipt number has 1 to {PurchasingLimits.MaxNumberLength} letters, digits or . _ / - and starts with a letter or digit.");
        }

        var order = await store.FindOrderAsync(orderId, cancellationToken).ConfigureAwait(false);
        if (order is null)
        {
            return Refused(PurchasingOutcome.OrderNotFound, "There is no such purchase order.");
        }

        if (order.Source == RecordSource.Erp)
        {
            return Refused(PurchasingOutcome.ErpManaged, "This order comes from the ERP; its goods receipts come from the ERP too.");
        }

        if (draft.ReceivedDate > Today() || draft.ReceivedDate < order.OrderDate)
        {
            return Refused(PurchasingOutcome.InvalidDate, "A goods receipt is dated between its order date and today.");
        }

        var ordered = order.Lines.ToDictionary(l => l.LineNumber, l => l.Quantity);
        var seen = new HashSet<int>();
        for (var i = 0; i < draft.Lines.Count; i++)
        {
            var line = draft.Lines[i];
            if (!ordered.ContainsKey(line.OrderLineNumber) || !seen.Add(line.OrderLineNumber)
                || line.Quantity < 0 || Decimals(line.Quantity) > PurchasingLimits.QuantityDecimals)
            {
                return Refused(PurchasingOutcome.InvalidLine, $"Receipt line {i + 1} does not fit the order (unknown or repeated order line, or not a quantity).", line.OrderLineNumber);
            }
        }

        var received = draft.Lines.Where(l => l.Quantity > 0).ToList();
        if (received.Count == 0)
        {
            return Refused(PurchasingOutcome.NothingReceived, "Enter at least one received quantity.");
        }

        var before = await store.ReceivedAsync(orderId, cancellationToken).ConfigureAwait(false);
        if (received.FirstOrDefault(l => before.GetValueOrDefault(l.OrderLineNumber) + l.Quantity > ordered[l.OrderLineNumber]) is { } over)
        {
            return Refused(PurchasingOutcome.OverReceived, $"More would be received on order line {over.OrderLineNumber} than was ordered.", over.OrderLineNumber);
        }

        if (await store.ReceiptNumberExistsAsync(number, cancellationToken).ConfigureAwait(false))
        {
            return Taken("goods receipt");
        }

        var receipt = new GoodsReceipt(number, orderId, draft.ReceivedDate, received.Select(l => new GoodsReceiptLine(l.OrderLineNumber, l.Quantity)).ToList());
        store.Add(receipt);
        store.Audit(new AuditDraft(time.GetUtcNow(), buyer, AuditActions.GoodsReceiptRecorded, Subject(orderId),
            JsonSerializer.Serialize(new
            {
                number,
                receiptId = receipt.Id,
                receivedDate = draft.ReceivedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                lines = receipt.Lines.Select(l => new { line = l.PurchaseOrderLineNumber, quantity = l.QuantityReceived }),
            })));
        return await SaveAsync(new PurchasingResult(PurchasingOutcome.ReceiptRecorded, receipt.Id), "goods receipt", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The first line that cannot be stored as typed, by its position on the form.</summary>
    internal static PurchasingResult? CheckLines(IReadOnlyList<OrderLineDraft> lines)
    {
        if (lines is null || lines.Count == 0 || lines.Count > PurchasingLimits.MaxLines)
        {
            return Refused(PurchasingOutcome.InvalidLine, $"An order has 1 to {PurchasingLimits.MaxLines} lines.");
        }

        var numbers = new HashSet<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            var item = l.ItemNumber?.Trim() ?? string.Empty;
            var description = l.Description?.Trim() ?? string.Empty;
            var problem =
                l.LineNumber < 1 || !numbers.Add(l.LineNumber) ? "line numbers start at 1 and are unique"
                : !IsText(item, PurchasingLimits.MaxItemNumberLength) ? $"the item number has 1 to {PurchasingLimits.MaxItemNumberLength} characters"
                : !IsText(description, PurchasingLimits.MaxDescriptionLength) ? $"the description has 1 to {PurchasingLimits.MaxDescriptionLength} characters"
                : l.Quantity <= 0 || l.Quantity > PurchasingLimits.MaxQuantity || Decimals(l.Quantity) > PurchasingLimits.QuantityDecimals ? "the quantity is more than 0 with at most 4 decimals"
                : l.UnitPrice < 0 || l.UnitPrice > PurchasingLimits.MaxUnitPrice || Decimals(l.UnitPrice) > PurchasingLimits.UnitPriceDecimals ? "the unit price is 0 or more with at most 6 decimals"
                : l.TaxRatePercent < 0 || l.TaxRatePercent > 100 || Decimals(l.TaxRatePercent) > PurchasingLimits.TaxRateDecimals ? "the tax rate is between 0 and 100 % with at most 2 decimals"
                : null;
            if (problem is not null)
            {
                return Refused(PurchasingOutcome.InvalidLine, $"Line {i + 1}: {problem}.", i + 1);
            }
        }

        return null;
    }

    private async Task<PurchasingResult> SaveAsync(PurchasingResult success, string what, CancellationToken cancellationToken)
    {
        try
        {
            await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return success;
        }
        catch (DuplicateNumberException)
        {
            return Taken(what); // saved by someone else at the same moment: the unique index decided
        }
    }

    internal static DateOnly Today(TimeProvider time) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), Berlin).DateTime);

    private DateOnly Today() => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), Berlin).DateTime);

    private static string Subject(Guid orderId) => $"PurchaseOrder/{orderId}";

    internal static bool IsNumber(string number) => number.Length <= PurchasingLimits.MaxNumberLength && NumberPattern().IsMatch(number);

    internal static bool IsText(string text, int maxLength) => text.Length > 0 && text.Length <= maxLength && !text.Any(char.IsControl);

    /// <summary>Decimal places that carry a value (4.50 has one).</summary>
    internal static int Decimals(decimal value) => BitConverter.GetBytes(decimal.GetBits(value / 1.000000000000000000000000000000000m)[3])[2];

    private static PurchasingResult Refused(PurchasingOutcome outcome, string detail, int? position = null) => new(outcome, Detail: detail, Position: position);

    private static PurchasingResult Taken(string what) => Refused(PurchasingOutcome.NumberTaken, $"A {what} with this number exists.");

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._/-]*$")]
    private static partial Regex NumberPattern();
}
