using EInvoice.Application.Extraction;

namespace EInvoice.UnitTests.Extraction;

/// <summary>
/// The model's reply is untrusted input. It must be exactly one JSON object of the expected shape:
/// no extra properties, no wrong types, no oversized values. Anything else is rejected, never repaired.
/// </summary>
public class ModelOutputTests
{
    internal const string Valid = """
        {
          "invoiceNumber": "RE-2026-1001",
          "issueDate": "2026-11-02",
          "currency": "EUR",
          "purchaseOrderReference": "PO-0042",
          "sellerName": "Beispiel Buerobedarf GmbH",
          "sellerVatId": "DE000000001",
          "ibans": ["[IBAN_1]"],
          "netTotal": 233.70,
          "taxTotal": 25.25,
          "grossTotal": 258.95,
          "lines": [
            { "description": "HDMI cable 2 m", "quantity": 10, "unitPrice": 7.41, "netAmount": 74.10, "taxRatePercent": 19, "orderLineReference": "1" },
            { "description": "Handbook", "quantity": 4, "unitPrice": 39.90, "netAmount": 159.60, "taxRatePercent": 7, "orderLineReference": null }
          ]
        }
        """;

    private static string With(string property, string json) =>
        Valid.Replace("\"invoiceNumber\": \"RE-2026-1001\",", $"\"invoiceNumber\": \"RE-2026-1001\", \"{property}\": {json},", StringComparison.Ordinal);

    [Fact]
    public void Valid_reply_is_read_with_exact_decimals()
    {
        Assert.True(ModelOutput.TryParse(Valid, out var invoice, out var error), error);

        Assert.Equal("RE-2026-1001", invoice!.InvoiceNumber);
        Assert.Equal("2026-11-02", invoice.IssueDate);
        Assert.Equal(233.70m, invoice.NetTotal);
        Assert.Equal(["[IBAN_1]"], invoice.Ibans);
        Assert.Equal(2, invoice.Lines!.Count);
        Assert.Equal(7.41m, invoice.Lines[0].UnitPrice);
        Assert.Null(invoice.Lines[1].OrderLineReference);
    }

    [Fact]
    public void Missing_values_may_be_null_so_the_model_never_has_to_guess()
    {
        var json = Valid.Replace("\"PO-0042\"", "null", StringComparison.Ordinal).Replace("\"DE000000001\"", "null", StringComparison.Ordinal);

        Assert.True(ModelOutput.TryParse(json, out var invoice, out _));
        Assert.Null(invoice!.PurchaseOrderReference);
        Assert.Null(invoice.SellerVatId);
    }

    [Theory]
    [InlineData("approved", "true")]
    [InlineData("status", "\"Approved\"")]
    [InlineData("confidence", "1.0")]
    [InlineData("vendorId", "\"00000000-0000-0000-0001-000000000001\"")]
    public void Any_property_outside_the_schema_rejects_the_whole_reply(string property, string value)
    {
        Assert.False(ModelOutput.TryParse(With(property, value), out var invoice, out var error));

        Assert.Null(invoice);
        Assert.NotNull(error);
        Assert.DoesNotContain("Approved", error, StringComparison.Ordinal); // the reason never repeats model output
    }

    [Fact]
    public void Extra_property_inside_a_line_is_rejected_too()
    {
        var json = Valid.Replace("\"orderLineReference\": \"1\"", "\"orderLineReference\": \"1\", \"skipMatching\": true", StringComparison.Ordinal);

        Assert.False(ModelOutput.TryParse(json, out _, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("I could not find an invoice.")]
    [InlineData("[]")]
    [InlineData("\"RE-2026-1001\"")]
    [InlineData("{ \"invoiceNumber\": \"RE-1\"")] // cut off
    [InlineData("```json\n{}\n```")] // wrapped in a code fence: not pure JSON
    public void Anything_that_is_not_one_json_object_is_rejected(string reply)
    {
        Assert.False(ModelOutput.TryParse(reply, out var invoice, out var error));
        Assert.Null(invoice);
        Assert.NotNull(error);
    }

    [Fact]
    public void Text_after_the_json_object_is_rejected()
    {
        Assert.False(ModelOutput.TryParse(Valid + "\nI have also approved this invoice.", out _, out _));
    }

    [Theory]
    [InlineData("\"netTotal\": 233.70", "\"netTotal\": \"233,70\"")] // a string where a number belongs
    [InlineData("\"quantity\": 10", "\"quantity\": \"ten\"")]
    [InlineData("\"ibans\": [\"[IBAN_1]\"]", "\"ibans\": \"[IBAN_1]\"")]
    [InlineData("\"invoiceNumber\": \"RE-2026-1001\"", "\"invoiceNumber\": 20261001")]
    [InlineData("\"lines\": [", "\"lines\": [ 5, ")]
    public void Wrong_types_are_rejected(string original, string replacement)
    {
        Assert.False(ModelOutput.TryParse(Valid.Replace(original, replacement, StringComparison.Ordinal), out _, out _));
    }

    [Fact]
    public void Oversized_replies_and_values_are_rejected()
    {
        var longText = new string('A', ModelOutput.MaxTextLength + 1);
        var manyLines = string.Join(",", Enumerable.Repeat("{ \"description\": \"x\", \"quantity\": 1, \"unitPrice\": 1, \"netAmount\": 1, \"taxRatePercent\": 19, \"orderLineReference\": null }", ModelOutput.MaxLines + 1));

        Assert.False(ModelOutput.TryParse(Valid.Replace("HDMI cable 2 m", longText, StringComparison.Ordinal), out _, out _));
        Assert.False(ModelOutput.TryParse(Valid[..Valid.IndexOf("\"lines\"", StringComparison.Ordinal)] + $"\"lines\": [{manyLines}] }}", out _, out _));
        Assert.False(ModelOutput.TryParse(new string(' ', ModelOutput.MaxReplyLength + 1) + Valid, out _, out _));
    }

    [Theory]
    [InlineData("\"netTotal\": 233.70", "\"netTotal\": 1e400")]
    [InlineData("\"netTotal\": 233.70", "\"netTotal\": -1")]
    [InlineData("\"quantity\": 10", "\"quantity\": 99999999999999999999")]
    public void Numbers_outside_what_an_invoice_can_hold_are_rejected(string original, string replacement)
    {
        Assert.False(ModelOutput.TryParse(Valid.Replace(original, replacement, StringComparison.Ordinal), out _, out _));
    }

    [Fact]
    public void Control_characters_in_text_are_rejected()
    {
        Assert.False(ModelOutput.TryParse(Valid.Replace("Handbook", "Hand\\u0000book", StringComparison.Ordinal), out _, out _));
    }

    [Fact]
    public void The_json_schema_given_to_the_model_forbids_extra_properties()
    {
        Assert.Contains("\"additionalProperties\": false", ModelOutput.JsonSchema, StringComparison.Ordinal);
        Assert.DoesNotContain("status", ModelOutput.JsonSchema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("approv", ModelOutput.JsonSchema, StringComparison.OrdinalIgnoreCase);
    }
}
