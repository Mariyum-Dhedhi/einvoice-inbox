using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using EInvoice.Application.Audit;
using EInvoice.Application.Purchasing;
using EInvoice.Domain.Purchasing;
using EInvoice.Domain.Vendors;

namespace EInvoice.Application.Sync;

/// <summary>What the sync reads from the ERP. Stored as numbers: never renumber or reuse a value.</summary>
public enum SyncEntity
{
    Vendors = 1,
    PurchaseOrders = 2,
    GoodsReceipts = 3,
}

public enum SyncOutcome
{
    Created = 1,
    Updated = 2,

    /// <summary>The same values again (an overlap re-read): nothing stored, nothing audited.</summary>
    Unchanged = 3,

    /// <summary>The record breaks a rule (a line, a currency, a date, a number that cannot be stored): skip it.</summary>
    InvalidRecord = 4,

    /// <summary>The number belongs to another record, for example one made in the app.</summary>
    NumberTaken = 5,

    /// <summary>A record's number cannot change: orders, invoices and postings refer to it.</summary>
    NumberChanged = 6,

    /// <summary>The order's vendor is not here yet: keep it and try again on the next run.</summary>
    VendorNotFound = 7,

    /// <summary>An order cannot move to another vendor.</summary>
    VendorChanged = 8,

    /// <summary>The receipt's order is not here yet: keep it and try again on the next run.</summary>
    OrderNotFound = 9,

    /// <summary>A receipt cannot move to another order.</summary>
    OrderChanged = 10,

    /// <summary>An order line would be less than what was received, or a line with receipts would go.</summary>
    OrderBelowReceived = 11,

    /// <summary>More received than ordered, counting the order's other receipts.</summary>
    OverReceived = 12,
}

/// <param name="Detail">A safe reason in English (logs, API); never the record's content.</param>
public sealed record SyncResult(SyncOutcome Outcome, Guid? Id = null, string? Detail = null)
{
    /// <summary>Changed or not, the record is as the ERP has it.</summary>
    public bool Succeeded => Outcome is SyncOutcome.Created or SyncOutcome.Updated or SyncOutcome.Unchanged;

    /// <summary>Waits for another record: worth trying again on the next run.</summary>
    public bool Waits => Outcome is SyncOutcome.VendorNotFound or SyncOutcome.OrderNotFound;
}

public sealed record ErpVendorRecord(string Number, string Name, string? VatId, bool Blocked);

public sealed record ErpOrderRecord(string Number, string VendorErpId, DateOnly OrderDate, string Currency, PurchaseOrderStatus Status, IReadOnlyList<OrderLineDraft> Lines);

public sealed record ErpReceiptRecord(string Number, string PurchaseOrderErpId, DateOnly ReceivedDate, IReadOnlyList<ReceiptLineDraft> Lines);

public sealed record SyncFailureView(string ErpId, string Reason, DateTimeOffset FirstSeen, DateTimeOffset LastSeen, int Tries);

public interface IErpSyncStore
{
    /// <summary>Tracked: a change is saved by <see cref="SaveChangesAsync"/>.</summary>
    Task<Vendor?> FindVendorAsync(string erpId, CancellationToken cancellationToken);

    Task<PurchaseOrder?> FindOrderAsync(string erpId, CancellationToken cancellationToken);

    Task<GoodsReceipt?> FindReceiptAsync(string erpId, CancellationToken cancellationToken);

    Task<bool> VendorNumberExistsAsync(string number, CancellationToken cancellationToken);

    Task<bool> OrderNumberExistsAsync(string number, CancellationToken cancellationToken);

    Task<bool> ReceiptNumberExistsAsync(string number, CancellationToken cancellationToken);

    /// <summary>Received per order line over the order's receipts, leaving out <paramref name="exceptReceipt"/>.</summary>
    Task<IReadOnlyDictionary<int, decimal>> ReceivedAsync(Guid orderId, Guid? exceptReceipt, CancellationToken cancellationToken);

    void Add(Vendor vendor);

    void Add(PurchaseOrder order);

    void Add(GoodsReceipt receipt);

    void Audit(AuditDraft draft);

    /// <exception cref="DuplicateNumberException">A number or VAT id exists (unique index); nothing was saved.</exception>
    Task SaveChangesAsync(CancellationToken cancellationToken);

    Task<DateTimeOffset?> WatermarkAsync(SyncEntity entity, CancellationToken cancellationToken);

    Task SetWatermarkAsync(SyncEntity entity, DateTimeOffset value, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Oldest first.</summary>
    Task<IReadOnlyList<SyncFailureView>> FailuresAsync(SyncEntity entity, CancellationToken cancellationToken);

    /// <summary>Adds the failure, or counts one more try of a known one.</summary>
    Task RecordFailureAsync(SyncEntity entity, string erpId, string reason, DateTimeOffset at, CancellationToken cancellationToken);

    Task ClearFailureAsync(SyncEntity entity, string erpId, CancellationToken cancellationToken);
}

/// <summary>
/// Master data from the ERP (package 16c): create or update by the ERP's id, under the same rules as records made in
/// the app. Each real change is saved with its audit event (actor "erp-sync", source "ERP sync"); the same values again
/// change nothing and write nothing, so the sync may read a record twice. A refusal stores nothing.
/// </summary>
public sealed partial class ErpSync(IErpSyncStore store, TimeProvider time)
{
    public const string Actor = "erp-sync";
    public const string Source = "ERP sync";
    public const int MaxErpIdLength = 50;
    public const int MaxVendorNameLength = 200;
    public const int MaxVatIdLength = 20;
    public const int MaxReasonLength = 200;

    /// <summary>Letters, digits and . _ : - ; it travels in a URL path, so no slash.</summary>
    public static bool IsErpId(string? erpId) => erpId is { Length: > 0 and <= MaxErpIdLength } && ErpIdPattern().IsMatch(erpId);

    /// <summary>A reason the sync stores with a failure: 1 to 200 characters, one line.</summary>
    public static bool IsReason(string? reason) => reason is { Length: > 0 and <= MaxReasonLength } && !reason.Any(char.IsControl);

    // ---- Vendors ----------------------------------------------------------------------------------------------------

    public async Task<SyncResult> UpsertVendorAsync(string erpId, ErpVendorRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        var number = record.Number?.Trim() ?? string.Empty;
        var name = record.Name?.Trim() ?? string.Empty;
        var vatId = string.IsNullOrWhiteSpace(record.VatId) ? null : record.VatId.Trim();
        if (!PurchasingAdministration.IsNumber(number)
            || !PurchasingAdministration.IsText(name, MaxVendorNameLength)
            || (vatId is not null && !VatIdPattern().IsMatch(vatId)))
        {
            return Refused(SyncOutcome.InvalidRecord, $"A vendor has a number (1 to {PurchasingLimits.MaxNumberLength} letters, digits or . _ / -), a name of 1 to {MaxVendorNameLength} characters and an optional VAT id of 4 to {MaxVatIdLength} letters and digits.");
        }

        var vendor = await store.FindVendorAsync(erpId, cancellationToken).ConfigureAwait(false);
        if (vendor is null)
        {
            if (await store.VendorNumberExistsAsync(number, cancellationToken).ConfigureAwait(false))
            {
                return Refused(SyncOutcome.NumberTaken, "A vendor with this number exists.");
            }

            vendor = Vendor.FromErp(erpId, number, name, vatId, record.Blocked);
            store.Add(vendor);
            Audit(AuditActions.VendorSyncedFromErp, $"Vendor/{vendor.Id}", erpId, SyncOutcome.Created, VendorState(vendor));
            return await SaveAsync(SyncOutcome.Created, vendor.Id, cancellationToken).ConfigureAwait(false);
        }

        if (!string.Equals(vendor.Number, number, StringComparison.Ordinal))
        {
            return Refused(SyncOutcome.NumberChanged, "A vendor's number cannot change.");
        }

        if (!vendor.ApplyFromErp(name, vatId, record.Blocked))
        {
            return new SyncResult(SyncOutcome.Unchanged, vendor.Id);
        }

        Audit(AuditActions.VendorSyncedFromErp, $"Vendor/{vendor.Id}", erpId, SyncOutcome.Updated, VendorState(vendor));
        return await SaveAsync(SyncOutcome.Updated, vendor.Id, cancellationToken).ConfigureAwait(false);
    }

    // ---- Purchase orders --------------------------------------------------------------------------------------------

    public async Task<SyncResult> UpsertOrderAsync(string erpId, ErpOrderRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        var number = record.Number?.Trim() ?? string.Empty;
        if (!PurchasingAdministration.IsNumber(number))
        {
            return Refused(SyncOutcome.InvalidRecord, $"An order number has 1 to {PurchasingLimits.MaxNumberLength} letters, digits or . _ / -.");
        }

        if (record.Currency != PurchasingLimits.Currency)
        {
            return Refused(SyncOutcome.InvalidRecord, "Orders are in EUR.");
        }

        if (record.OrderDate > PurchasingAdministration.Today(time))
        {
            return Refused(SyncOutcome.InvalidRecord, "The order date is in the future.");
        }

        if (PurchasingAdministration.CheckLines(record.Lines) is { } lineProblem)
        {
            return Refused(SyncOutcome.InvalidRecord, lineProblem.Detail!);
        }

        var vendor = await store.FindVendorAsync(record.VendorErpId ?? string.Empty, cancellationToken).ConfigureAwait(false);
        if (vendor is null)
        {
            return Refused(SyncOutcome.VendorNotFound, "The order's vendor is not here yet.");
        }

        var lines = record.Lines.Select(l => new PurchaseOrderLine(l.LineNumber, l.ItemNumber.Trim(), l.Description.Trim(), l.Quantity, l.UnitPrice, l.TaxRatePercent)).ToList();
        var order = await store.FindOrderAsync(erpId, cancellationToken).ConfigureAwait(false);
        if (order is null)
        {
            if (await store.OrderNumberExistsAsync(number, cancellationToken).ConfigureAwait(false))
            {
                return Refused(SyncOutcome.NumberTaken, "An order with this number exists.");
            }

            order = PurchaseOrder.FromErp(erpId, number, vendor.Id, record.OrderDate, record.Currency, lines, record.Status);
            store.Add(order);
            Audit(AuditActions.PurchaseOrderSyncedFromErp, $"PurchaseOrder/{order.Id}", erpId, SyncOutcome.Created, OrderState(order));
            return await SaveAsync(SyncOutcome.Created, order.Id, cancellationToken).ConfigureAwait(false);
        }

        if (!string.Equals(order.Number, number, StringComparison.Ordinal))
        {
            return Refused(SyncOutcome.NumberChanged, "An order's number cannot change.");
        }

        if (order.VendorId != vendor.Id)
        {
            return Refused(SyncOutcome.VendorChanged, "An order cannot move to another vendor.");
        }

        var received = await store.ReceivedAsync(order.Id, null, cancellationToken).ConfigureAwait(false);
        var wanted = lines.ToDictionary(l => l.LineNumber, l => l.Quantity);
        if (received.FirstOrDefault(r => r.Value > 0 && r.Value > wanted.GetValueOrDefault(r.Key)) is { Value: > 0 } short1)
        {
            return Refused(SyncOutcome.OrderBelowReceived, $"Order line {short1.Key} would be less than what was received.");
        }

        if (!order.ApplyFromErp(record.OrderDate, lines, record.Status))
        {
            return new SyncResult(SyncOutcome.Unchanged, order.Id);
        }

        Audit(AuditActions.PurchaseOrderSyncedFromErp, $"PurchaseOrder/{order.Id}", erpId, SyncOutcome.Updated, OrderState(order));
        return await SaveAsync(SyncOutcome.Updated, order.Id, cancellationToken).ConfigureAwait(false);
    }

    // ---- Goods receipts ---------------------------------------------------------------------------------------------

    public async Task<SyncResult> UpsertReceiptAsync(string erpId, ErpReceiptRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        var number = record.Number?.Trim() ?? string.Empty;
        if (!PurchasingAdministration.IsNumber(number))
        {
            return Refused(SyncOutcome.InvalidRecord, $"A goods receipt number has 1 to {PurchasingLimits.MaxNumberLength} letters, digits or . _ / -.");
        }

        if (record.Lines is not { Count: > 0 and <= PurchasingLimits.MaxLines }
            || record.Lines.Select(l => l.OrderLineNumber).Distinct().Count() != record.Lines.Count
            || record.Lines.Any(l => l.OrderLineNumber < 1 || l.Quantity <= 0 || l.Quantity > PurchasingLimits.MaxQuantity || PurchasingAdministration.Decimals(l.Quantity) > PurchasingLimits.QuantityDecimals))
        {
            return Refused(SyncOutcome.InvalidRecord, $"A goods receipt has 1 to {PurchasingLimits.MaxLines} lines, each for another order line and with a quantity above 0 (at most 4 decimals).");
        }

        var order = await store.FindOrderAsync(record.PurchaseOrderErpId ?? string.Empty, cancellationToken).ConfigureAwait(false);
        if (order is null)
        {
            return Refused(SyncOutcome.OrderNotFound, "The receipt's order is not here yet.");
        }

        if (record.ReceivedDate < order.OrderDate || record.ReceivedDate > PurchasingAdministration.Today(time))
        {
            return Refused(SyncOutcome.InvalidRecord, "A goods receipt is dated between its order date and today.");
        }

        var ordered = order.Lines.ToDictionary(l => l.LineNumber, l => l.Quantity);
        if (record.Lines.FirstOrDefault(l => !ordered.ContainsKey(l.OrderLineNumber)) is { } unknown)
        {
            return Refused(SyncOutcome.InvalidRecord, $"The order has no line {unknown.OrderLineNumber}.");
        }

        var receipt = await store.FindReceiptAsync(erpId, cancellationToken).ConfigureAwait(false);
        if (receipt is not null && receipt.PurchaseOrderId != order.Id)
        {
            return Refused(SyncOutcome.OrderChanged, "A goods receipt cannot move to another order.");
        }

        if (receipt is not null && !string.Equals(receipt.Number, number, StringComparison.Ordinal))
        {
            return Refused(SyncOutcome.NumberChanged, "A goods receipt's number cannot change.");
        }

        var before = await store.ReceivedAsync(order.Id, receipt?.Id, cancellationToken).ConfigureAwait(false);
        if (record.Lines.FirstOrDefault(l => before.GetValueOrDefault(l.OrderLineNumber) + l.Quantity > ordered[l.OrderLineNumber]) is { } over)
        {
            return Refused(SyncOutcome.OverReceived, $"More would be received on order line {over.OrderLineNumber} than was ordered.");
        }

        var lines = record.Lines.Select(l => new GoodsReceiptLine(l.OrderLineNumber, l.Quantity)).ToList();
        if (receipt is null)
        {
            if (await store.ReceiptNumberExistsAsync(number, cancellationToken).ConfigureAwait(false))
            {
                return Refused(SyncOutcome.NumberTaken, "A goods receipt with this number exists.");
            }

            receipt = GoodsReceipt.FromErp(erpId, number, order.Id, record.ReceivedDate, lines);
            store.Add(receipt);
            Audit(AuditActions.GoodsReceiptSyncedFromErp, $"PurchaseOrder/{order.Id}", erpId, SyncOutcome.Created, ReceiptState(receipt));
            return await SaveAsync(SyncOutcome.Created, receipt.Id, cancellationToken).ConfigureAwait(false);
        }

        if (!receipt.ApplyFromErp(record.ReceivedDate, lines))
        {
            return new SyncResult(SyncOutcome.Unchanged, receipt.Id);
        }

        Audit(AuditActions.GoodsReceiptSyncedFromErp, $"PurchaseOrder/{order.Id}", erpId, SyncOutcome.Updated, ReceiptState(receipt));
        return await SaveAsync(SyncOutcome.Updated, receipt.Id, cancellationToken).ConfigureAwait(false);
    }

    // ---- Audit ------------------------------------------------------------------------------------------------------

    private void Audit(string action, string subject, string erpId, SyncOutcome change, object state) =>
        store.Audit(new AuditDraft(time.GetUtcNow(), Actor, action, subject, JsonSerializer.Serialize(new { source = Source, erpId, change = change.ToString(), state })));

    private static object VendorState(Vendor v) => new { number = v.Number, name = v.Name, vatId = v.VatId, active = v.IsActive };

    private static object OrderState(PurchaseOrder o) => new
    {
        number = o.Number,
        vendorId = o.VendorId,
        orderDate = o.OrderDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        status = o.Status.ToString(),
        lines = o.Lines.Select(l => new { line = l.LineNumber, quantity = l.Quantity, unitPrice = l.UnitPrice, taxRate = l.TaxRatePercent }),
    };

    private static object ReceiptState(GoodsReceipt r) => new
    {
        number = r.Number,
        receiptId = r.Id,
        receivedDate = r.ReceivedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        lines = r.Lines.Select(l => new { line = l.PurchaseOrderLineNumber, quantity = l.QuantityReceived }),
    };

    private async Task<SyncResult> SaveAsync(SyncOutcome outcome, Guid id, CancellationToken cancellationToken)
    {
        try
        {
            await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return new SyncResult(outcome, id);
        }
        catch (DuplicateNumberException)
        {
            return Refused(SyncOutcome.NumberTaken, "The number (or the vendor's VAT id) belongs to another record.");
        }
    }

    private static SyncResult Refused(SyncOutcome outcome, string detail) => new(outcome, Detail: detail);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:-]*$")]
    private static partial Regex ErpIdPattern();

    [GeneratedRegex("^[A-Z]{2}[A-Za-z0-9]{2,18}$")]
    private static partial Regex VatIdPattern();
}
