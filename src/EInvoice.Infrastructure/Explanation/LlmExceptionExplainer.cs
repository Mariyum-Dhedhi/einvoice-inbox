using System.Text.Json;
using EInvoice.Application.Explanation;
using EInvoice.Application.Queries;
using Microsoft.Extensions.AI;

namespace EInvoice.Infrastructure.Explanation;

/// <summary>
/// Explains an exception in plain language with a language model, as a suggestion for the person who decides.
/// The model sees only the structured result (codes, numbers, the rules' own messages), has no tools, and must answer
/// in a fixed shape. Every number in its text must come from the result; otherwise, or when anything else is off,
/// the rule-based explanation is shown instead, with the reason.
/// </summary>
public sealed class LlmExceptionExplainer(IChatClient model) : IExceptionExplainer
{
    private const int MaxLength = 2000;

    private const string Prompt = """
        You help an accounts-payable clerk understand why an invoice was stopped for review.
        You get the result of the automatic checks as JSON: findings (rule code, severity, message) and the three-way
        match per invoice line (prices, quantities, the price tolerance).

        Write a short explanation in plain English and suggest one sensible next step. Your text is a suggestion:
        a person decides. You cannot change, approve, reject or pay anything.

        Rules:
        - Use only facts and numbers from the JSON. Never invent or calculate new numbers, dates or deadlines.
        - Write amounts with a dot as decimal separator, as in the JSON.
        - No lists with numbers, no greetings, no more than five sentences in the explanation.
        - Reply with exactly one JSON object: {"explanation": "...", "suggestedStep": "..."}
        """;

    private static readonly JsonElement Schema = JsonDocument.Parse("""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["explanation", "suggestedStep"],
          "properties": { "explanation": { "type": "string" }, "suggestedStep": { "type": "string" } }
        }
        """).RootElement.Clone();

    public bool UsesAi => true;

    public async Task<Application.Explanation.Explanation> ExplainAsync(InvoiceDetail invoice, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        var rules = RuleBasedExplainer.Explain(invoice);
        if (RuleBasedExplainer.Issues(invoice).Count == 0)
        {
            return rules; // nothing to explain: no reason to ask a model
        }

        var facts = ExplanationFacts.From(invoice);
        string reply;
        try
        {
            var options = new ChatOptions
            {
                Temperature = 0f,
                MaxOutputTokens = 1024,
                ResponseFormat = ChatResponseFormat.ForJsonSchema(Schema, "explanation", "An explanation of an invoice exception and a suggested next step."),
            };
            var response = await model.GetResponseAsync(
                [new ChatMessage(ChatRole.System, Prompt), new ChatMessage(ChatRole.User, facts.ToJson())],
                options,
                cancellationToken).ConfigureAwait(false);
            reply = response.Text;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or TimeoutException or IOException)
        {
            return rules with { FallbackReason = "The AI explanation is not available right now." };
        }

        if (!TryRead(reply, out var text, out var step))
        {
            return rules with { FallbackReason = "The AI reply was not in the expected format." };
        }

        if (ExplanationNumbers.Unknown($"{text}\n{step}", facts).Count > 0)
        {
            return rules with { FallbackReason = "The AI explanation contained numbers that are not in the match result." };
        }

        return new Application.Explanation.Explanation(text, step, ExplanationSource.Ai);
    }

    /// <summary>Exactly {"explanation": "...", "suggestedStep": "..."}: two non-empty strings of reasonable length, nothing else.</summary>
    private static bool TryRead(string? reply, out string text, out string step)
    {
        text = step = string.Empty;
        if (string.IsNullOrWhiteSpace(reply) || reply.Length > 3 * MaxLength)
        {
            return false;
        }

        try
        {
            using var json = JsonDocument.Parse(reply, new JsonDocumentOptions { MaxDepth = 4 });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2
                || !root.TryGetProperty("explanation", out var e) || !root.TryGetProperty("suggestedStep", out var s)
                || e.ValueKind != JsonValueKind.String || s.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            text = e.GetString()!.Trim();
            step = s.GetString()!.Trim();
            return Acceptable(text) && Acceptable(step);
        }
        catch (JsonException)
        {
            return false;
        }

        static bool Acceptable(string value) => value.Length is > 0 and <= MaxLength && !value.Any(c => char.IsControl(c) && c != '\n');
    }
}
