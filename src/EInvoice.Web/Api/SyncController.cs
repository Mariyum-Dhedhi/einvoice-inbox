using System.Text.Json;
using System.Text.Json.Serialization;
using EInvoice.Application.Purchasing;
using EInvoice.Application.Sync;
using EInvoice.Domain.Purchasing;
using EInvoice.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace EInvoice.Web.Api;

/// <summary>
/// The ERP sync's way in (package 16c; contract: contracts/sync/README.md). Upserts by the ERP's id, the sync's
/// watermarks and its open failures. Only the sync's API key may call it (<see cref="Policies.SyncMasterData"/>).
/// Bodies are read strictly: a field more or one missing is 400, and errors never repeat what was sent.
/// </summary>
[ApiController]
[Route("api/sync")]
[Authorize(Policy = Policies.SyncMasterData)]
public sealed class SyncController(ErpSync sync, IErpSyncStore store, TimeProvider time) : ControllerBase
{
    private static readonly JsonSerializerOptions Strict = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
    };

    private static readonly Dictionary<string, SyncEntity> Entities = new(StringComparer.Ordinal)
    {
        ["vendors"] = SyncEntity.Vendors,
        ["purchase-orders"] = SyncEntity.PurchaseOrders,
        ["goods-receipts"] = SyncEntity.GoodsReceipts,
    };

    public sealed record VendorBody(string Number, string Name, string? VatId, string Status);

    public sealed record OrderLineBody(int LineNumber, string ItemNumber, string Description, decimal Quantity, decimal UnitPrice, decimal TaxRatePercent);

    public sealed record OrderBody(string Number, string VendorErpId, DateOnly OrderDate, string Currency, string Status, IReadOnlyList<OrderLineBody> Lines);

    public sealed record ReceiptLineBody(int PurchaseOrderLineNumber, decimal Quantity);

    public sealed record ReceiptBody(string Number, string PurchaseOrderErpId, DateOnly ReceivedDate, IReadOnlyList<ReceiptLineBody> Lines);

    public sealed record UpsertResponse(string Outcome, Guid Id);

    public sealed record WatermarkBody(DateTimeOffset Value);

    public sealed record WatermarkResponse(string Entity, DateTimeOffset? Value);

    public sealed record FailureBody(string Reason);

    // No [Consumes] on any action: it is checked before authorization (415 instead of 401/403). Each action checks itself.

    [HttpPut("vendors/{erpId}")]
    public async Task<IActionResult> PutVendor(string erpId, CancellationToken cancellationToken)
    {
        var (body, problem) = await ReadAsync<VendorBody>(erpId, cancellationToken).ConfigureAwait(false);
        if (problem is not null)
        {
            return problem;
        }

        if (body!.Status is not ("Active" or "Blocked"))
        {
            return Problem400("A vendor's status is Active or Blocked.");
        }

        return Answer(await sync.UpsertVendorAsync(erpId, new ErpVendorRecord(body.Number, body.Name, body.VatId, body.Status == "Blocked"), cancellationToken).ConfigureAwait(false));
    }

    [HttpPut("purchase-orders/{erpId}")]
    public async Task<IActionResult> PutOrder(string erpId, CancellationToken cancellationToken)
    {
        var (body, problem) = await ReadAsync<OrderBody>(erpId, cancellationToken).ConfigureAwait(false);
        if (problem is not null)
        {
            return problem;
        }

        if (!Enum.TryParse<PurchaseOrderStatus>(body!.Status, ignoreCase: false, out var status) || !Enum.IsDefined(status) || int.TryParse(body.Status, out _))
        {
            return Problem400("An order's status is Open or Closed.");
        }

        var lines = body.Lines.Select(l => new OrderLineDraft(l.LineNumber, l.ItemNumber, l.Description, l.Quantity, l.UnitPrice, l.TaxRatePercent)).ToList();
        return Answer(await sync.UpsertOrderAsync(erpId, new ErpOrderRecord(body.Number, body.VendorErpId, body.OrderDate, body.Currency, status, lines), cancellationToken).ConfigureAwait(false));
    }

    [HttpPut("goods-receipts/{erpId}")]
    public async Task<IActionResult> PutReceipt(string erpId, CancellationToken cancellationToken)
    {
        var (body, problem) = await ReadAsync<ReceiptBody>(erpId, cancellationToken).ConfigureAwait(false);
        if (problem is not null)
        {
            return problem;
        }

        var lines = body!.Lines.Select(l => new ReceiptLineDraft(l.PurchaseOrderLineNumber, l.Quantity)).ToList();
        return Answer(await sync.UpsertReceiptAsync(erpId, new ErpReceiptRecord(body.Number, body.PurchaseOrderErpId, body.ReceivedDate, lines), cancellationToken).ConfigureAwait(false));
    }

    // ---- Watermarks -------------------------------------------------------------------------------------------------

    [HttpGet("watermarks/{entity}")]
    public async Task<IActionResult> GetWatermark(string entity, CancellationToken cancellationToken)
    {
        if (!Entities.TryGetValue(entity, out var which))
        {
            return NotFound404();
        }

        return Ok(new WatermarkResponse(entity, await store.WatermarkAsync(which, cancellationToken).ConfigureAwait(false)));
    }

    [HttpPut("watermarks/{entity}")]
    public async Task<IActionResult> PutWatermark(string entity, CancellationToken cancellationToken)
    {
        if (!Entities.TryGetValue(entity, out var which))
        {
            return NotFound404();
        }

        var (body, problem) = await ReadAsync<WatermarkBody>(null, cancellationToken).ConfigureAwait(false);
        if (problem is not null)
        {
            return problem;
        }

        await store.SetWatermarkAsync(which, body!.Value, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    // ---- Failures ---------------------------------------------------------------------------------------------------

    [HttpGet("failures/{entity}")]
    public async Task<IActionResult> GetFailures(string entity, CancellationToken cancellationToken) =>
        Entities.TryGetValue(entity, out var which)
            ? Ok(await store.FailuresAsync(which, cancellationToken).ConfigureAwait(false))
            : NotFound404();

    [HttpPut("failures/{entity}/{erpId}")]
    public async Task<IActionResult> PutFailure(string entity, string erpId, CancellationToken cancellationToken)
    {
        if (!Entities.TryGetValue(entity, out var which))
        {
            return NotFound404();
        }

        var (body, problem) = await ReadAsync<FailureBody>(erpId, cancellationToken).ConfigureAwait(false);
        if (problem is not null)
        {
            return problem;
        }

        if (!ErpSync.IsReason(body!.Reason))
        {
            return Problem400($"A reason has 1 to {ErpSync.MaxReasonLength} characters on one line.");
        }

        await store.RecordFailureAsync(which, erpId, body.Reason, time.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    [HttpDelete("failures/{entity}/{erpId}")]
    public async Task<IActionResult> DeleteFailure(string entity, string erpId, CancellationToken cancellationToken)
    {
        if (!Entities.TryGetValue(entity, out var which))
        {
            return NotFound404();
        }

        if (!ErpSync.IsErpId(erpId))
        {
            return Problem400(ErpIdRule);
        }

        await store.ClearFailureAsync(which, erpId, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------------

    private const string ErpIdRule = "An ERP id has 1 to 50 letters, digits or . _ : - and starts with a letter or digit.";

    /// <summary>The body as <typeparamref name="T"/>, strictly; or the problem to answer instead.</summary>
    private async Task<(T? Body, IActionResult? Problem)> ReadAsync<T>(string? erpId, CancellationToken cancellationToken)
        where T : class
    {
        if (erpId is not null && !ErpSync.IsErpId(erpId))
        {
            return (null, Problem400(ErpIdRule));
        }

        if (Request.ContentType is null || !Request.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        {
            return (null, Refusal(StatusCodes.Status415UnsupportedMediaType, null, "Send the record as application/json."));
        }

        try
        {
            var body = await JsonSerializer.DeserializeAsync<T>(Request.Body, Strict, cancellationToken).ConfigureAwait(false);
            return body is null ? (null, Problem400("The body is empty.")) : (body, null);
        }
        catch (JsonException)
        {
            return (null, Problem400($"The body is not a {typeof(T).Name.Replace("Body", string.Empty, StringComparison.Ordinal)} of the agreed format (contracts/sync/README.md)."));
        }
    }

    private IActionResult Answer(SyncResult result)
    {
        if (result.Succeeded)
        {
            var response = new UpsertResponse(result.Outcome.ToString(), result.Id!.Value);
            return result.Outcome == SyncOutcome.Created ? StatusCode(StatusCodes.Status201Created, response) : Ok(response);
        }

        // 409: waits for another record, try again next run. 422: the record itself is refused, skip it.
        return Refusal(result.Waits ? StatusCodes.Status409Conflict : StatusCodes.Status422UnprocessableEntity, result.Outcome.ToString(), result.Detail);
    }

    private ObjectResult Problem400(string detail) => Refusal(StatusCodes.Status400BadRequest, null, detail);

    private ObjectResult NotFound404() => Refusal(StatusCodes.Status404NotFound, null, "The sync knows vendors, purchase-orders and goods-receipts.");

    private ObjectResult Refusal(int status, string? outcome, string? detail)
    {
        var problem = ProblemDetailsFactory.CreateProblemDetails(HttpContext, status, detail: detail);
        if (outcome is not null)
        {
            problem.Extensions["outcome"] = outcome;
        }

        return new ObjectResult(problem) { StatusCode = status, ContentTypes = { "application/problem+json" } };
    }
}
