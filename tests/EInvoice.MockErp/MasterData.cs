using System.Buffers.Text;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace EInvoice.MockErp;

// The mock ERP's master data (package 16c), what the Go sync reads: vendors, purchase orders with lines and product
// receipts (goods receipts) with lines, OData style. Records are never deleted: a vendor is Blocked, an order Closed.
// Contract: contracts/d365/README.md, "Master data contract".

public sealed record ErpVendor(string Id, string VendorAccountNumber, string Name, string? VatNumber, string Status, DateTimeOffset ModifiedDateTime);

public sealed record ErpOrderLine(int LineNumber, string ItemNumber, string Description, decimal Quantity, decimal UnitPrice, decimal TaxRatePercent);

public sealed record ErpPurchaseOrder(
    string Id, string PurchaseOrderNumber, string VendorId, DateOnly OrderDate, string CurrencyCode, string Status,
    DateTimeOffset ModifiedDateTime, IReadOnlyList<ErpOrderLine> Lines);

public sealed record ErpReceiptLine(int PurchaseOrderLineNumber, decimal Quantity);

public sealed record ErpProductReceipt(
    string Id, string ProductReceiptNumber, string PurchaseOrderId, DateOnly ReceivedDate, DateTimeOffset ModifiedDateTime,
    IReadOnlyList<ErpReceiptLine> Lines);

/// <summary>An OData page: the records and, while more follow, the absolute link to the next page.</summary>
public sealed record ODataPage<T>(
    [property: JsonPropertyName("value")] IReadOnlyList<T> Value,
    [property: JsonPropertyName("@odata.nextLink"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? NextLink = null);

/// <summary>
/// The three sets, each kept in change order (modifiedDateTime, then id). Built once from a fixed seed, so two mocks
/// with the same settings hold the same records (times are relative to the start).
/// </summary>
public sealed class MasterData
{
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web);

    private static readonly (string Item, string Description, decimal Price, decimal Tax)[] Catalog =
    [
        ("PAPER-A4", "Kopierpapier A4, 500 Blatt", 4.50m, 19m),
        ("PAPER-A3", "Kopierpapier A3, 500 Blatt", 9.80m, 19m),
        ("TONER-BK", "Toner schwarz", 89.90m, 19m),
        ("CHAIR-01", "Buerostuhl ergonomisch", 249.00m, 19m),
        ("DESK-160", "Schreibtisch 160 x 80 cm", 389.00m, 19m),
        ("COFFEE-1", "Kaffeebohnen 1 kg", 18.75m, 7m),
        ("WATER-12", "Mineralwasser, Kasten 12 x 1 l", 6.49m, 19m),
        ("BOOK-HGB", "Handelsgesetzbuch, aktuelle Auflage", 24.90m, 7m),
        ("CABLE-C2", "USB-C Kabel 2 m", 12.99m, 19m),
        ("SCREW-M6", "Schrauben M6 x 30, 100 Stueck", 7.35m, 19m),
        ("PALLET-E", "Europalette", 21.00m, 19m),
        ("SERVICE-H", "Wartung, je Stunde", 95.00m, 19m),
    ];

    private static readonly string[] NameParts = ["Nordlicht", "Rheinblick", "Alpenrand", "Elbtal", "Hanse", "Spree", "Isar", "Weser", "Main", "Moselgrund"];

    private static readonly string[] Trades = ["Papier", "Buerobedarf", "Logistik", "Technik", "Moebel", "Handel", "Service", "Verpackung"];

    private static readonly string[] Forms = ["GmbH", "AG", "KG", "GmbH & Co. KG", "e.K."];

    private MasterData(List<ErpVendor> vendors, List<ErpPurchaseOrder> orders, List<ErpProductReceipt> receipts)
    {
        Vendors = Sorted(vendors, v => v.ModifiedDateTime, v => v.Id);
        Orders = Sorted(orders, o => o.ModifiedDateTime, o => o.Id);
        Receipts = Sorted(receipts, r => r.ModifiedDateTime, r => r.Id);
    }

    public IReadOnlyList<ErpVendor> Vendors { get; }

    public IReadOnlyList<ErpPurchaseOrder> Orders { get; }

    public IReadOnlyList<ErpProductReceipt> Receipts { get; }

    public static MasterData FromConfiguration(IConfigurationSection section, DateTimeOffset now)
    {
        var vendors = section.GetValue("Vendors", 250);
        var orders = section.GetValue("PurchaseOrders", 10_000);
        var seed = section.GetValue("Seed", 16);
        if (vendors is < 0 or > 99_999 || orders is < 0 or > 99_999)
        {
            throw new InvalidOperationException("MasterData:Vendors and MasterData:PurchaseOrders must be 0 to 99999.");
        }

        if (orders > 0 && vendors == 0)
        {
            throw new InvalidOperationException("MasterData: purchase orders need at least one vendor.");
        }

        return Generate(seed, vendors, orders, now);
    }

    /// <summary>
    /// Synthetic records within the app's rules: numbers of letters, digits and dashes; VAT ids DE + 9 digits (or none);
    /// EUR orders dated no later than their change; 1 to 5 lines; receipts never more than ordered. About 5 % of
    /// vendors are Blocked, 10 % of orders Closed, 70 % of orders have a receipt and some a second one.
    /// </summary>
    public static MasterData Generate(int seed, int vendorCount, int orderCount, DateTimeOffset now)
    {
        var random = new Random(seed);
        now = new DateTimeOffset(now.UtcTicks - (now.UtcTicks % TimeSpan.TicksPerSecond), TimeSpan.Zero);
        DateTimeOffset Before(int fromDays, int toDays) => now.AddSeconds(-random.Next(toDays * 86_400, fromDays * 86_400));

        var vendors = new List<ErpVendor>(vendorCount);
        for (var i = 1; i <= vendorCount; i++)
        {
            var name = $"{NameParts[random.Next(NameParts.Length)]} {Trades[random.Next(Trades.Length)]} {i:0000} {Forms[random.Next(Forms.Length)]}";
            var vatId = random.NextDouble() < 0.8 ? $"DE{random.Next(100_000_000, 999_999_999)}" : null;
            var status = random.NextDouble() < 0.05 ? "Blocked" : "Active";
            vendors.Add(new ErpVendor($"V-{i:00000}", $"K-{i:00000}", name, vatId, status, Before(120, 91)));
        }

        var orders = new List<ErpPurchaseOrder>(orderCount);
        var receipts = new List<ErpProductReceipt>();
        for (var i = 1; i <= orderCount; i++)
        {
            var modified = Before(90, 6);
            var lines = new List<ErpOrderLine>();
            var lineCount = random.Next(1, 6);
            for (var n = 1; n <= lineCount; n++)
            {
                var (item, description, price, tax) = Catalog[random.Next(Catalog.Length)];
                var quantity = random.NextDouble() < 0.1 ? random.Next(1, 40) + 0.5m : random.Next(1, 200);
                lines.Add(new ErpOrderLine(n, item, description, quantity, price, tax));
            }

            var order = new ErpPurchaseOrder(
                $"PO-{i:00000}", $"PO-26-{i:000000}", vendors[random.Next(vendors.Count)].Id,
                DateOnly.FromDateTime(modified.AddDays(-random.Next(0, 10)).UtcDateTime), "EUR",
                random.NextDouble() < 0.1 ? "Closed" : "Open", modified, lines);
            orders.Add(order);

            if (random.NextDouble() < 0.7)
            {
                var second = random.NextDouble() < 0.15;
                var first = new List<ErpReceiptLine>();
                var rest = new List<ErpReceiptLine>();
                foreach (var line in lines)
                {
                    var part = second ? Math.Floor(line.Quantity / 2) : line.Quantity;
                    if (part > 0)
                    {
                        first.Add(new ErpReceiptLine(line.LineNumber, part));
                    }

                    if (second && line.Quantity - part > 0)
                    {
                        rest.Add(new ErpReceiptLine(line.LineNumber, line.Quantity - part));
                    }
                }

                var when = modified.AddSeconds(random.Next(3_600, 5 * 86_400));
                AddReceipt(receipts, order.Id, when, first);
                if (rest.Count > 0)
                {
                    AddReceipt(receipts, order.Id, when.AddSeconds(random.Next(3_600, 86_400)), rest);
                }
            }
        }

        return new MasterData(vendors, orders, receipts);
    }

    private static void AddReceipt(List<ErpProductReceipt> receipts, string orderId, DateTimeOffset when, List<ErpReceiptLine> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        var n = receipts.Count + 1;
        receipts.Add(new ErpProductReceipt($"PR-{n:00000}", $"GR-26-{n:000000}", orderId, DateOnly.FromDateTime(when.UtcDateTime), when, lines));
    }

    private static List<T> Sorted<T>(List<T> records, Func<T, DateTimeOffset> modified, Func<T, string> id) =>
        [.. records.OrderBy(modified).ThenBy(id, StringComparer.Ordinal)];
}

/// <summary>
/// The OData reading rules of the mock: $filter=modifiedDateTime ge &lt;ISO time&gt;, $orderby=modifiedDateTime,id,
/// $top 1 to 1000 (default 1000), $skiptoken from a nextLink. Anything else is a 400: an ERP that silently ignored a
/// filter would make the sync believe it read only changes.
/// </summary>
public static partial class ODataQuery
{
    public const int MaxTop = 1000;
    public const string OrderBy = "modifiedDateTime,id";

    private static readonly string[] Known = ["$filter", "$orderby", "$top", "$skiptoken"];

    public sealed record Parsed(DateTimeOffset? Since, int Top, (DateTimeOffset Modified, string Id)? After, string? Filter, bool TopGiven);

    /// <summary>The query, or why it is refused.</summary>
    public static (Parsed? Query, string? Problem) Parse(IQueryCollection query)
    {
        if (query.Keys.FirstOrDefault(k => !Known.Contains(k, StringComparer.Ordinal)) is { } unknown)
        {
            return (null, $"The query option {Safe(unknown)} is not supported: only $filter, $orderby, $top and $skiptoken.");
        }

        if (query.Any(q => q.Value.Count != 1))
        {
            return (null, "Each query option may appear once.");
        }

        DateTimeOffset? since = null;
        string? filter = null;
        if (query.TryGetValue("$filter", out var f))
        {
            var match = FilterPattern().Match(f.ToString());
            if (!match.Success || !DateTimeOffset.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return (null, "The only filter is: modifiedDateTime ge <ISO 8601 time with offset>.");
            }

            since = parsed;
            filter = f.ToString();
        }

        if (query.TryGetValue("$orderby", out var o) && o.ToString() != OrderBy)
        {
            return (null, $"The only order is {OrderBy}.");
        }

        var top = MaxTop;
        var topGiven = query.TryGetValue("$top", out var t);
        if (topGiven && (!int.TryParse(t.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out top) || top is < 1 or > MaxTop))
        {
            return (null, $"$top is a whole number from 1 to {MaxTop}.");
        }

        (DateTimeOffset, string)? after = null;
        if (query.TryGetValue("$skiptoken", out var s))
        {
            after = ReadToken(s.ToString());
            if (after is null)
            {
                return (null, "The $skiptoken is not one this ERP gave.");
            }
        }

        return (new Parsed(since, top, after, filter, topGiven), null);
    }

    /// <summary>One page of <paramref name="records"/> (already in change order) and the link to the next, if any.</summary>
    public static ODataPage<T> Page<T>(IReadOnlyList<T> records, Func<T, DateTimeOffset> modified, Func<T, string> id, Parsed query, HttpRequest request)
    {
        var taken = records
            .Where(r => query.Since is not { } since || modified(r) >= since)
            .Where(r => query.After is not { } after || modified(r) > after.Modified || (modified(r) == after.Modified && string.CompareOrdinal(id(r), after.Id) > 0))
            .Take(query.Top + 1)
            .ToList();
        if (taken.Count <= query.Top)
        {
            return new ODataPage<T>(taken, null);
        }

        taken.RemoveAt(taken.Count - 1);
        var last = taken[^1];
        var parts = new List<string>();
        if (query.Filter is not null)
        {
            parts.Add("$filter=" + Uri.EscapeDataString(query.Filter));
        }

        parts.Add("$orderby=" + Uri.EscapeDataString(OrderBy));
        parts.Add("$top=" + query.Top.ToString(CultureInfo.InvariantCulture));
        parts.Add("$skiptoken=" + WriteToken(modified(last), id(last)));
        var next = $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path}?{string.Join('&', parts)}";
        return new ODataPage<T>(taken, next);
    }

    /// <summary>"set" or "set('key')" (OData key: a quote inside is doubled).</summary>
    public static (string Set, string? Key)? Segment(string segment)
    {
        var match = SegmentPattern().Match(segment);
        return match.Success ? (match.Groups[1].Value, match.Groups[2].Success ? match.Groups[2].Value.Replace("''", "'", StringComparison.Ordinal) : null) : null;
    }

    private static string WriteToken(DateTimeOffset modified, string id) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{modified.UtcTicks.ToString(CultureInfo.InvariantCulture)}|{id}"));

    private static (DateTimeOffset, string)? ReadToken(string token)
    {
        try
        {
            var text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(token));
            var bar = text.IndexOf('|', StringComparison.Ordinal);
            return bar > 0 && long.TryParse(text.AsSpan(0, bar), NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                && ticks <= DateTimeOffset.MaxValue.UtcTicks && bar < text.Length - 1
                ? (new DateTimeOffset(ticks, TimeSpan.Zero), text[(bar + 1)..])
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>A query option's name in an error: short and plain, never what the caller typed beyond that.</summary>
    private static string Safe(string name) => name.Length <= 20 && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '$') ? name : "given";

    [GeneratedRegex(@"^modifiedDateTime ge (\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2}))$")]
    private static partial Regex FilterPattern();

    [GeneratedRegex(@"^([A-Za-z]+)(?:\('((?:[^']|'')*)'\))?$")]
    private static partial Regex SegmentPattern();
}
