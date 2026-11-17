using EInvoice.Application.Approval;
using EInvoice.Application.Intake;
using EInvoice.Application.Matching;
using EInvoice.Application.Queries;
using EInvoice.Domain.Invoices;
using EInvoice.Domain.Matching;
using EInvoice.Web.Configuration;
using EInvoice.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace EInvoice.Web.Api;

/// <summary>
/// Upload, list, detail, re-match and download of supplier invoices. Every error is RFC 9457 problem details
/// (application/problem+json) with a safe explanation; refusals carry the outcome name for clients to branch on.
/// </summary>
/// <remarks>Each action names its policy (see <see cref="Policies"/>); there is no class-wide default.</remarks>
[ApiController]
[Route("api/invoices")]
public sealed class InvoicesController : ControllerBase
{
    private const string FileField = "file";

    public sealed record UploadResponse(Guid InvoiceId, InvoiceStatus Status);

    public sealed record RematchResponse(Guid InvoiceId, RematchOutcome Outcome, InvoiceStatus Status, IReadOnlyList<MatchOutcome> Reasons);

    /// <param name="Version">The "version" from the invoice detail.</param>
    public sealed record DecisionBody(string? Version, string? Reason);

    public sealed record DecisionResponse(Guid InvoiceId, InvoiceStatus Status);

    /// <summary>Upload one XRechnung XML file, or a plain PDF invoice (read by AI when it is switched on), as multipart/form-data field "file".</summary>
    /// <response code="201">Stored; the status says how far it got (Matched, Exception or Failed).</response>
    [HttpPost]
    [Authorize(Policy = Policies.UploadInvoices)]
    // No [Consumes]: it is checked while the endpoint is chosen, before authorization, and would tell a caller who
    // may not upload what the endpoint expects (415 instead of 403). The action checks the content type itself.
    [DisableFormValueModelBinding]
    [ProducesResponseType<UploadResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status415UnsupportedMediaType, "application/problem+json")]
    public async Task<IActionResult> Upload([FromServices] IInvoiceIntake intake, [FromServices] IntakeOptions options, CancellationToken cancellationToken)
    {
        if (!Request.HasFormContentType)
        {
            return Refusal(StatusCodes.Status415UnsupportedMediaType, null, $"Send the invoice as multipart/form-data field '{FileField}'.");
        }

        IFormCollection form;
        try
        {
            form = await Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            // The multipart body limit (set from the document limit) stopped the read.
            return TooLarge(options);
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return TooLarge(options);
        }

        if (form.Files.GetFile(FileField) is not { } file)
        {
            return Refusal(StatusCodes.Status400BadRequest, null, $"Send the invoice as multipart/form-data field '{FileField}'.");
        }

        if (file.Length > options.MaxDocumentBytes)
        {
            return TooLarge(options);
        }

        var content = new byte[file.Length];
        await using (var stream = file.OpenReadStream())
        {
            await stream.ReadExactlyAsync(content, cancellationToken).ConfigureAwait(false);
        }

        var result = await intake.SubmitAsync(new UploadedFile(file.FileName, content), SignedInUser.Name(User), cancellationToken).ConfigureAwait(false);
        return result.Outcome switch
        {
            IntakeOutcome.Created => Created($"/api/invoices/{result.InvoiceId}", new UploadResponse(result.InvoiceId!.Value, result.Status!.Value)),
            IntakeOutcome.Empty => Refusal(StatusCodes.Status400BadRequest, result),
            IntakeOutcome.TooLarge => Refusal(StatusCodes.Status413PayloadTooLarge, result),
            IntakeOutcome.UnsupportedType => Refusal(StatusCodes.Status415UnsupportedMediaType, result),
            IntakeOutcome.DuplicateFile or IntakeOutcome.DuplicateInvoiceNumber => Refusal(StatusCodes.Status409Conflict, result),
            IntakeOutcome.ExtractionUnavailable => Unavailable(result),
            _ => throw new InvalidOperationException($"Unhandled intake outcome {result.Outcome}."),
        };
    }

    /// <summary>Invoices, newest first. Filter by status name; pages of at most 100.</summary>
    [HttpGet]
    [Authorize(Policy = Policies.ReadInvoices)]
    [ProducesResponseType<InvoicePage>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    public async Task<IActionResult> List(
        [FromServices] IInvoiceQueries queries,
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = InvoiceListQuery.DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        InvoiceStatus? filter = null;
        if (status is not null)
        {
            // Names only: Enum.TryParse would also take "99", a number that is no status.
            var name = Enum.GetNames<InvoiceStatus>().FirstOrDefault(n => string.Equals(n, status, StringComparison.OrdinalIgnoreCase));
            if (name is null)
            {
                ModelState.AddModelError(nameof(status), $"Unknown status. Use one of: {string.Join(", ", Enum.GetNames<InvoiceStatus>())}.");
            }
            else
            {
                filter = Enum.Parse<InvoiceStatus>(name);
            }
        }

        if (page < 1)
        {
            ModelState.AddModelError(nameof(page), "Page starts at 1.");
        }

        if (pageSize is < 1 or > InvoiceListQuery.MaxPageSize)
        {
            ModelState.AddModelError(nameof(pageSize), $"Page size is 1 to {InvoiceListQuery.MaxPageSize}.");
        }

        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        return Ok(await queries.ListAsync(new InvoiceListQuery(filter, page, pageSize), cancellationToken).ConfigureAwait(false));
    }

    /// <summary>One invoice with its lines, status history, findings, match results and file information.</summary>
    [HttpGet("{id:guid}")]
    [Authorize(Policy = Policies.ReadInvoices)]
    [ProducesResponseType<InvoiceDetail>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> Get([FromServices] IInvoiceQueries queries, Guid id, CancellationToken cancellationToken) =>
        await queries.GetAsync(id, cancellationToken).ConfigureAwait(false) is { } detail ? Ok(detail) : NotFoundProblem();

    /// <summary>The original file, byte for byte, always as a download.</summary>
    [HttpGet("{id:guid}/document")]
    [Authorize(Policy = Policies.ReadInvoices)]
    [ProducesResponseType<byte[]>(StatusCodes.Status200OK, "application/xml")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> Document([FromServices] IInvoiceQueries queries, Guid id, CancellationToken cancellationToken)
    {
        if (await queries.GetDocumentAsync(id, cancellationToken).ConfigureAwait(false) is not { } document)
        {
            return NotFoundProblem();
        }

        // Attachment + nosniff (set for every response): the browser saves the file, never renders or runs it.
        return File(document.Content, document.ContentType, document.FileName);
    }

    /// <summary>Match again, for an invoice in Exception because of its match (for example after a late goods receipt).</summary>
    [HttpPost("{id:guid}/match")]
    [Authorize(Policy = Policies.RematchInvoices)]
    [ProducesResponseType<RematchResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> Rematch([FromServices] IInvoiceRematch rematch, Guid id, CancellationToken cancellationToken)
    {
        var result = await rematch.RematchAsync(id, SignedInUser.Name(User), cancellationToken).ConfigureAwait(false);
        return result.Outcome switch
        {
            RematchOutcome.Matched or RematchOutcome.StillException => Ok(new RematchResponse(id, result.Outcome, result.Status!.Value, result.Reasons)),
            RematchOutcome.NotFound => NotFoundProblem(),
            RematchOutcome.NotAllowed or RematchOutcome.Conflict => Problem(statusCode: StatusCodes.Status409Conflict, detail: result.Detail),
            _ => throw new InvalidOperationException($"Unhandled re-match outcome {result.Outcome}."),
        };
    }

    /// <summary>
    /// Approve an invoice waiting for a decision (Matched or Exception). Send the "version" from the detail: the decision
    /// applies only to that version. Approving an Exception needs a "reason".
    /// </summary>
    [HttpPost("{id:guid}/approve")]
    [Authorize(Policy = Policies.DecideInvoices)]
    [ProducesResponseType<DecisionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public Task<IActionResult> Approve(
        Guid id, [FromBody] DecisionBody? body, [FromServices] IInvoiceApproval approval, [FromServices] IOptions<ApprovalSettings> settings, CancellationToken cancellationToken) =>
        DecideAsync(id, Decision.Approve, body, approval, settings.Value, cancellationToken);

    /// <summary>Reject an invoice waiting for a decision. Send the "version" from the detail and a "reason".</summary>
    [HttpPost("{id:guid}/reject")]
    [Authorize(Policy = Policies.DecideInvoices)]
    [ProducesResponseType<DecisionResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public Task<IActionResult> Reject(
        Guid id, [FromBody] DecisionBody? body, [FromServices] IInvoiceApproval approval, [FromServices] IOptions<ApprovalSettings> settings, CancellationToken cancellationToken) =>
        DecideAsync(id, Decision.Reject, body, approval, settings.Value, cancellationToken);

    private async Task<IActionResult> DecideAsync(
        Guid id, Decision decision, DecisionBody? body, IInvoiceApproval approval, ApprovalSettings settings, CancellationToken cancellationToken)
    {
        var approver = ApproverOf.User(User, settings.DefaultLimitEur);
        var result = await approval.DecideAsync(new DecisionRequest(id, decision, body?.Version ?? string.Empty, body?.Reason), approver, cancellationToken)
            .ConfigureAwait(false);
        var status = result.Outcome switch
        {
            DecisionOutcome.Approved or DecisionOutcome.Rejected => StatusCodes.Status200OK,
            DecisionOutcome.NotFound => StatusCodes.Status404NotFound,
            DecisionOutcome.ReasonRequired or DecisionOutcome.ReasonTooLong or DecisionOutcome.VersionRequired => StatusCodes.Status400BadRequest,
            DecisionOutcome.OwnInvoice or DecisionOutcome.OverLimit or DecisionOutcome.CurrencyNotSupported => StatusCodes.Status403Forbidden,
            DecisionOutcome.Conflict or DecisionOutcome.NotAllowed or DecisionOutcome.BlockingFindings => StatusCodes.Status409Conflict,
            _ => throw new InvalidOperationException($"Unhandled decision outcome {result.Outcome}."),
        };
        if (status == StatusCodes.Status200OK)
        {
            return Ok(new DecisionResponse(id, result.Status!.Value));
        }

        var problem = ProblemDetailsFactory.CreateProblemDetails(HttpContext, status, detail: result.Detail);
        problem.Extensions["outcome"] = result.Outcome.ToString();
        return new ObjectResult(problem) { StatusCode = status, ContentTypes = { "application/problem+json" } };
    }

    /// <summary>The extraction model is down: nothing was stored, so the client can simply send the same file again later.</summary>
    private ObjectResult Unavailable(IntakeResult result)
    {
        Response.Headers.RetryAfter = "60";
        return Refusal(StatusCodes.Status503ServiceUnavailable, result);
    }

    private ObjectResult NotFoundProblem() => Problem(statusCode: StatusCodes.Status404NotFound, detail: "There is no invoice with this id.");

    private ObjectResult TooLarge(IntakeOptions options) =>
        Refusal(StatusCodes.Status413PayloadTooLarge, null, $"The file is larger than {options.MaxDocumentBytes} bytes.");

    /// <summary>A refused upload: problem details plus the outcome name and, for a duplicate file, the existing invoice.</summary>
    private ObjectResult Refusal(int status, IntakeResult? result, string? detail = null)
    {
        var problem = ProblemDetailsFactory.CreateProblemDetails(HttpContext, status, detail: detail ?? result?.Detail);
        if (result is not null)
        {
            problem.Extensions["outcome"] = result.Outcome.ToString();
            if (result.InvoiceId is { } existing)
            {
                problem.Extensions["invoiceId"] = existing;
            }
        }

        return new ObjectResult(problem) { StatusCode = status, ContentTypes = { "application/problem+json" } };
    }
}
