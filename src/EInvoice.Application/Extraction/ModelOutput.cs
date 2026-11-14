using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace EInvoice.Application.Extraction;

/// <summary>What the model is asked to return: values as written in the document, null when not there.</summary>
public sealed record ExtractedInvoice(
    string? InvoiceNumber,
    string? IssueDate,
    string? Currency,
    string? PurchaseOrderReference,
    string? SellerName,
    string? SellerVatId,
    IReadOnlyList<string>? Ibans,
    decimal? NetTotal,
    decimal? TaxTotal,
    decimal? GrossTotal,
    IReadOnlyList<ExtractedLine>? Lines);

public sealed record ExtractedLine(
    string? Description,
    decimal? Quantity,
    decimal? UnitPrice,
    decimal? NetAmount,
    decimal? TaxRatePercent,
    string? OrderLineReference);

/// <summary>
/// Reads the model's reply as untrusted input. The reply must be exactly one JSON object with the properties below
/// and nothing else; one extra property, wrong type or oversized value rejects all of it. Nothing is repaired:
/// a model that does not follow the format is not trusted with part of the answer either.
/// </summary>
public static class ModelOutput
{
    public const int MaxReplyLength = 200_000;
    public const int MaxTextLength = 500;
    public const int MaxLines = 500;
    public const int MaxIbans = 10;
    private const decimal MaxNumber = 1_000_000_000_000m;

    /// <summary>The same shape as a JSON schema, given to the model as its required output format.</summary>
    public const string JsonSchema = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["invoiceNumber", "issueDate", "currency", "purchaseOrderReference", "sellerName", "sellerVatId", "ibans", "netTotal", "taxTotal", "grossTotal", "lines"],
          "properties": {
            "invoiceNumber": { "type": ["string", "null"] },
            "issueDate": { "type": ["string", "null"], "description": "yyyy-MM-dd" },
            "currency": { "type": ["string", "null"], "description": "ISO 4217 code, for example EUR" },
            "purchaseOrderReference": { "type": ["string", "null"] },
            "sellerName": { "type": ["string", "null"] },
            "sellerVatId": { "type": ["string", "null"] },
            "ibans": { "type": ["array", "null"], "items": { "type": "string" } },
            "netTotal": { "type": ["number", "null"] },
            "taxTotal": { "type": ["number", "null"] },
            "grossTotal": { "type": ["number", "null"] },
            "lines": {
              "type": ["array", "null"],
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["description", "quantity", "unitPrice", "netAmount", "taxRatePercent", "orderLineReference"],
                "properties": {
                  "description": { "type": ["string", "null"] },
                  "quantity": { "type": ["number", "null"] },
                  "unitPrice": { "type": ["number", "null"] },
                  "netAmount": { "type": ["number", "null"] },
                  "taxRatePercent": { "type": ["number", "null"] },
                  "orderLineReference": { "type": ["string", "null"] }
                }
              }
            }
          }
        }
        """;

    private static readonly string[] InvoiceProperties =
        ["invoiceNumber", "issueDate", "currency", "purchaseOrderReference", "sellerName", "sellerVatId", "ibans", "netTotal", "taxTotal", "grossTotal", "lines"];

    private static readonly string[] LineProperties = ["description", "quantity", "unitPrice", "netAmount", "taxRatePercent", "orderLineReference"];

    private static readonly JsonDocumentOptions Strict = new() { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 8 };

    /// <param name="error">A fixed, safe reason. It never contains any part of the reply.</param>
    public static bool TryParse(string? reply, [NotNullWhen(true)] out ExtractedInvoice? invoice, [NotNullWhen(false)] out string? error)
    {
        invoice = null;
        if (string.IsNullOrWhiteSpace(reply))
        {
            error = "The model returned no reply.";
            return false;
        }

        if (reply.Length > MaxReplyLength)
        {
            error = "The model's reply is too long.";
            return false;
        }

        try
        {
            using var json = JsonDocument.Parse(reply, Strict);
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !OnlyKnownProperties(root, InvoiceProperties))
            {
                error = "The model's reply is not the expected JSON object.";
                return false;
            }

            if (!Text(root, "invoiceNumber", out var number) || !Text(root, "issueDate", out var date) || !Text(root, "currency", out var currency)
                || !Text(root, "purchaseOrderReference", out var order) || !Text(root, "sellerName", out var seller) || !Text(root, "sellerVatId", out var vatId)
                || !Number(root, "netTotal", out var net) || !Number(root, "taxTotal", out var tax) || !Number(root, "grossTotal", out var gross)
                || !Ibans(root, out var ibans) || !Lines(root, out var lines))
            {
                error = "The model's reply holds a value of the wrong type or size.";
                return false;
            }

            invoice = new ExtractedInvoice(number, date, currency, order, seller, vatId, ibans, net, tax, gross, lines);
            error = null;
            return true;
        }
        catch (JsonException)
        {
            error = "The model's reply is not valid JSON.";
            return false;
        }
    }

    private static bool OnlyKnownProperties(JsonElement element, string[] known)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!known.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A missing property and an explicit null both mean "not in the document".</summary>
    private static bool Text(JsonElement parent, string name, out string? value)
    {
        value = null;
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString();
        return value is not null && value.Length <= MaxTextLength && !value.Any(char.IsControl);
    }

    private static bool Number(JsonElement parent, string name, out decimal? value)
    {
        value = null;
        if (!parent.TryGetProperty(name, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Number || !element.TryGetDecimal(out var number) || number < 0 || number > MaxNumber)
        {
            return false;
        }

        value = number;
        return true;
    }

    private static bool Ibans(JsonElement root, out IReadOnlyList<string>? ibans)
    {
        ibans = null;
        if (!root.TryGetProperty("ibans", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > MaxIbans)
        {
            return false;
        }

        var list = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: <= MaxTextLength } text || text.Any(char.IsControl))
            {
                return false;
            }

            list.Add(text);
        }

        ibans = list;
        return true;
    }

    private static bool Lines(JsonElement root, out IReadOnlyList<ExtractedLine>? lines)
    {
        lines = null;
        if (!root.TryGetProperty("lines", out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > MaxLines)
        {
            return false;
        }

        var list = new List<ExtractedLine>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !OnlyKnownProperties(item, LineProperties)
                || !Text(item, "description", out var description) || !Number(item, "quantity", out var quantity)
                || !Number(item, "unitPrice", out var unitPrice) || !Number(item, "netAmount", out var netAmount)
                || !Number(item, "taxRatePercent", out var rate) || !Text(item, "orderLineReference", out var orderLine))
            {
                return false;
            }

            list.Add(new ExtractedLine(description, quantity, unitPrice, netAmount, rate, orderLine));
        }

        lines = list;
        return true;
    }
}
