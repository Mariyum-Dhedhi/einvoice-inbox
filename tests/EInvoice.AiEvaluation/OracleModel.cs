using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace EInvoice.AiEvaluation;

/// <summary>
/// A "model" that knows the answer: it replies with the case's true values, exactly as the schema asks (the IBAN as
/// the placeholder the masking produced). Used to check the evaluation itself: with perfect extraction, every case
/// must reach its expected answer. Its numbers are never reported as a model result.
/// </summary>
public sealed class OracleModel(EvalCase evalCase) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var t = evalCase.Truth;
        var reply = new
        {
            invoiceNumber = evalCase.InvoiceNumber,
            issueDate = evalCase.IssueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            currency = "EUR",
            purchaseOrderReference = evalCase.PurchaseOrder,
            sellerName = t.SellerName,
            sellerVatId = t.SellerVatId,
            ibans = new[] { "[IBAN_1]" },
            netTotal = t.NetTotal,
            taxTotal = t.TaxTotal,
            grossTotal = t.GrossTotal,
            lines = t.Lines.Select(l => new
            {
                description = l.Description,
                quantity = l.Quantity,
                unitPrice = l.UnitPrice,
                netAmount = l.NetAmount,
                taxRatePercent = l.TaxRate,
                orderLineReference = l.OrderLine,
            }),
        };
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(reply))));
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
