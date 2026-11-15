using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using EInvoice.Application.Queries;

namespace EInvoice.Application.Explanation;

/// <summary>
/// What a model may see to explain an exception: codes, numbers and the rules' own messages. No invoice number, no
/// item names, no seller text: nothing the supplier wrote can reach this prompt, so it cannot carry an injection.
/// Also the list of numbers an explanation is allowed to contain.
/// </summary>
public sealed partial class ExplanationFacts
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly object _facts;

    private ExplanationFacts(object facts, IReadOnlySet<decimal> numbers)
    {
        _facts = facts;
        Numbers = numbers;
    }

    /// <summary>Every number in the facts: amounts, prices, percentages, quantities, line numbers, numbers in rule messages.</summary>
    public IReadOnlySet<decimal> Numbers { get; }

    public static ExplanationFacts From(InvoiceDetail invoice)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        var facts = new
        {
            status = invoice.Status.ToString(),
            currency = invoice.Currency,
            netTotal = invoice.NetTotal,
            vatTotal = invoice.TaxTotal,
            grossTotal = invoice.GrossTotal,
            findings = invoice.Findings.Select(f => new { code = f.RuleCode, severity = f.Severity.ToString(), line = f.LinePosition, message = f.Message }),
            matchLines = invoice.MatchResults.Select(m => new
            {
                line = m.InvoiceLineNumber,
                orderLine = m.PurchaseOrderLineNumber,
                outcome = m.Outcome.ToString(),
                invoiceUnitPrice = m.InvoiceUnitPrice,
                orderUnitPrice = m.OrderUnitPrice,
                priceDifferencePercent = m.PriceVariancePercent,
                priceDifferenceTotal = m.PriceVarianceAmount,
                quantityInvoiced = m.QuantityInvoiced,
                quantityReceivedNotYetInvoiced = m.QuantityAvailable,
                quantityShort = m.QuantityVariance,
                priceTolerancePercent = m.PriceTolerancePercent,
            }),
        };

        var numbers = new HashSet<decimal>();
        void Add(decimal? value)
        {
            if (value is { } v)
            {
                numbers.Add(v);
            }
        }

        Add(invoice.NetTotal);
        Add(invoice.TaxTotal);
        Add(invoice.GrossTotal);
        foreach (var f in invoice.Findings)
        {
            Add(f.LinePosition);
            foreach (var n in ExplanationNumbers.Find(f.Message))
            {
                Add(n.Value);
            }
        }

        foreach (var m in invoice.MatchResults)
        {
            Add(m.InvoiceLineNumber);
            Add(m.PurchaseOrderLineNumber);
            Add(m.InvoiceUnitPrice);
            Add(m.OrderUnitPrice);
            Add(m.PriceVariancePercent);
            Add(m.PriceVarianceAmount);
            Add(m.QuantityInvoiced);
            Add(m.QuantityAvailable);
            Add(m.QuantityVariance);
            Add(m.PriceTolerancePercent);
        }

        return new ExplanationFacts(facts, numbers);
    }

    public string ToJson() => JsonSerializer.Serialize(_facts, Json);
}

/// <summary>Finds the numbers in a text and tells which of them are not in the facts.</summary>
public static partial class ExplanationNumbers
{
    /// <summary>
    /// Numbers in the text that are not in the facts. A number counts as known when it equals a fact, or a fact
    /// rounded to 1 or 2 decimals ("2.5 %" for 2.5000). "820" equals 820.00 as it is. Not rounded to whole numbers:
    /// 2.5 % would then allow "3", and a line 3 the result never mentions would pass.
    /// </summary>
    public static IReadOnlyList<string> Unknown(string text, ExplanationFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        var known = facts.Numbers
            .SelectMany(n => new[] { n, decimal.Round(n, 1, MidpointRounding.AwayFromZero), decimal.Round(n, 2, MidpointRounding.AwayFromZero) })
            .ToHashSet();
        return Find(text).Where(n => !known.Contains(n.Value)).Select(n => n.Text).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Numbers in English notation ("1,904.00", "2.5", "14").</summary>
    public static IEnumerable<(string Text, decimal Value)> Find(string? text)
    {
        foreach (Match match in Number().Matches(text ?? string.Empty))
        {
            if (decimal.TryParse(match.Value.Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
            {
                yield return (match.Value, value);
            }
        }
    }

    [GeneratedRegex(@"(?<![\d.,])\d{1,3}(?:,\d{3})+(?:\.\d+)?(?![\d,])|(?<![\d.,])\d+(?:\.\d+)?(?![\d,])")]
    private static partial Regex Number();
}
