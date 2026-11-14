using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;

namespace EInvoice.Infrastructure.Extraction;

/// <summary>
/// A stand-in for a language model, for tests and CI: it reads the labelled layout of the synthetic test invoices
/// ("Invoice number: ...", a line table with | separators, "Gross total: ...") with fixed rules and answers in the
/// same JSON a real model is asked for. It is deterministic and needs nothing installed. It says nothing about how
/// well a real model extracts: its results are for checking the plumbing and are never reported as accuracy.
/// </summary>
public sealed partial class FakeInvoiceModel : IChatClient
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        cancellationToken.ThrowIfCancellationRequested();

        var user = messages.Last(m => m.Role == ChatRole.User).Text;
        var lines = Fence().Replace(user, string.Empty).Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Answer(lines))));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The fake model does not stream.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    private static string Answer(string[] lines)
    {
        string? Labelled(string label) =>
            lines.Select(l => l.StartsWith(label, StringComparison.OrdinalIgnoreCase) ? l[label.Length..].Trim() : null).FirstOrDefault(v => !string.IsNullOrEmpty(v));

        var gross = Labelled("Gross total:");
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            Text(json, "invoiceNumber", Labelled("Invoice number:"));
            Text(json, "issueDate", DateOnly.TryParseExact(Labelled("Invoice date:"), "dd.MM.yyyy", German, DateTimeStyles.None, out var date) ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null);
            Text(json, "currency", gross is not null && Currency().Match(gross) is { Success: true } currency ? currency.Value : null);
            Text(json, "purchaseOrderReference", Labelled("Purchase order:"));
            Text(json, "sellerName", lines.FirstOrDefault());
            Text(json, "sellerVatId", Labelled("USt-IdNr.:"));

            json.WriteStartArray("ibans");
            foreach (Match placeholder in IbanPlaceholder().Matches(Labelled("IBAN:") ?? string.Empty))
            {
                json.WriteStringValue(placeholder.Value);
            }

            json.WriteEndArray();
            Number(json, "netTotal", Labelled("Net total:"));
            Number(json, "taxTotal", Labelled("VAT:"));
            Number(json, "grossTotal", gross);

            json.WriteStartArray("lines");
            foreach (var row in lines.Select(l => TableRow().Match(l)).Where(m => m.Success))
            {
                json.WriteStartObject();
                Text(json, "description", row.Groups["description"].Value);
                Number(json, "quantity", row.Groups["quantity"].Value);
                Number(json, "unitPrice", row.Groups["price"].Value);
                Number(json, "netAmount", row.Groups["net"].Value);
                Number(json, "taxRatePercent", row.Groups["rate"].Value);
                Text(json, "orderLineReference", row.Groups["order"].Value is "-" or "" ? null : row.Groups["order"].Value);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void Text(Utf8JsonWriter json, string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            json.WriteNull(name);
        }
        else
        {
            json.WriteString(name, value);
        }
    }

    /// <summary>The first German-format number in the text ("1.234,56 EUR" is 1234.56).</summary>
    private static void Number(Utf8JsonWriter json, string name, string? text)
    {
        if (text is not null && GermanNumber().Match(text) is { Success: true } match
            && decimal.TryParse(match.Value, NumberStyles.Number, German, out var value))
        {
            json.WriteNumber(name, value);
        }
        else
        {
            json.WriteNull(name);
        }
    }

    [GeneratedRegex(@"</?document-[A-Za-z0-9]+>")]
    private static partial Regex Fence();

    [GeneratedRegex(@"^\d+ \| (?<description>.+?) \| (?<quantity>[\d.,]+) \| (?<price>[\d.,]+) \| (?<net>[\d.,]+) \| (?<rate>[\d.,]+) % \| (?<order>\S*)$")]
    private static partial Regex TableRow();

    [GeneratedRegex(@"\d[\d.]*(?:,\d+)?")]
    private static partial Regex GermanNumber();

    [GeneratedRegex(@"\b[A-Z]{3}\b")]
    private static partial Regex Currency();

    [GeneratedRegex(@"\[IBAN_\d+\]")]
    private static partial Regex IbanPlaceholder();
}
