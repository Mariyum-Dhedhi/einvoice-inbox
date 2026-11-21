using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using EInvoice.Application.Posting;

namespace EInvoice.Infrastructure.Posting;

/// <summary>
/// Posts approved invoices to the ERP's vendor-invoice endpoint (POST data/VendorInvoices) with the invoice id as
/// Idempotency-Key. Sorts every answer into posted, throttled (429: later, no try used), temporary (try again) or
/// rejected (final), and passes on how long the ERP asked us to wait (Retry-After on 429 and 503, as seconds or an
/// HTTP date). Reasons are short and never repeat a whole ERP answer.
/// </summary>
public sealed class HttpErpConnector(HttpClient http, TimeProvider? time = null) : IErpConnector
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private const int MaxReason = 200;

    /// <summary>
    /// What we send: camelCase. Reading with these options is strict (no field more, none missing): the contract test
    /// reads the committed example with them, so a contract that grows or shrinks fails there first.
    /// </summary>
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
    };

    private static readonly JsonSerializerOptions Reply = new(JsonSerializerDefaults.Web);

    public async Task<ErpResult> PostAsync(Guid idempotencyKey, ErpVendorInvoice invoice, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        using var request = new HttpRequestMessage(HttpMethod.Post, "data/VendorInvoices")
        {
            Content = JsonContent.Create(invoice, options: Json),
        };
        request.Headers.Add("Idempotency-Key", idempotencyKey.ToString());

        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (response.IsSuccessStatusCode)
            {
                // Lenient on purpose: a real ERP answers with many more fields; only the document number is needed.
                // An answer that cannot be read may still mean it was booked: the next try (same key) tells.
                PostedAnswer? answer;
                try
                {
                    answer = await response.Content.ReadFromJsonAsync<PostedAnswer>(Reply, cancellationToken).ConfigureAwait(false);
                }
                catch (JsonException)
                {
                    return new ErpResult(ErpOutcome.Transient, Reason: $"HTTP {status} with an answer that could not be read.");
                }

                return string.IsNullOrWhiteSpace(answer?.DocumentNumber)
                    ? new ErpResult(ErpOutcome.Transient, Reason: $"HTTP {status} without a document number.")
                    : new ErpResult(ErpOutcome.Posted, answer.DocumentNumber);
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return new ErpResult(ErpOutcome.Throttled, Reason: "HTTP 429 from the ERP.", RetryAfter: RetryAfter(response));
            }

            if (response.StatusCode == HttpStatusCode.RequestTimeout || status >= 500)
            {
                var wait = response.StatusCode == HttpStatusCode.ServiceUnavailable ? RetryAfter(response) : null;
                return new ErpResult(ErpOutcome.Transient, Reason: $"HTTP {status} from the ERP.", RetryAfter: wait);
            }

            return new ErpResult(ErpOutcome.Rejected, Reason: Cut($"HTTP {status}: {await TitleAsync(response, cancellationToken).ConfigureAwait(false)}"));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ErpResult(ErpOutcome.Transient, Reason: "No answer from the ERP in time.");
        }
        catch (HttpRequestException)
        {
            return new ErpResult(ErpOutcome.Transient, Reason: "The ERP could not be reached.");
        }
    }

    /// <summary>Retry-After as a wait from now; null when missing, unreadable or already past (the policy decides).</summary>
    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter; // null when missing or not a valid value
        if (header?.Delta is { } delta)
        {
            return delta;
        }

        return header?.Date is { } date && date - _time.GetUtcNow() is { } untilThen && untilThen > TimeSpan.Zero ? untilThen : null;
    }

    /// <summary>The problem's title (what the ERP says went wrong), or a plain description when there is none.</summary>
    private static async Task<string> TitleAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return json.RootElement.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String
                ? title.GetString()!
                : "the ERP refused the invoice.";
        }
        catch (JsonException)
        {
            return "the ERP refused the invoice.";
        }
    }

    private static string Cut(string text) => text.Length <= MaxReason ? text : text[..MaxReason];

    private sealed record PostedAnswer(string DocumentNumber);
}
