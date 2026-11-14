using EInvoice.Application.Intake;
using EInvoice.Application.Matching;
using EInvoice.Application.Queries;
using EInvoice.Domain.Invoices;
using EInvoice.Domain.Matching;
using Microsoft.AspNetCore.Mvc;

namespace EInvoice.Web.Api;

/// <summary>
/// Upload, list, detail, re-match and download of supplier invoices. Every error is RFC 9457 problem details
/// (application/problem+json) with a safe explanation; refusals carry the outcome name for clients to branch on.
/// </summary>
[ApiController]
[Route("api/invoices")]
public sealed class InvoicesController(ICurrentUser user) : ControllerBase
{
    private const string FileField = "file";

    public sealed record UploadResponse(Guid InvoiceId, InvoiceStatus Status);

    public sealed record RematchResponse(Guid InvoiceId, RematchOutcome Outcome, InvoiceStatus Status, IReadOnlyList<MatchOutcome> Reasons);

    /// <summary>Upload one XRechnung XML file as multipart/form-data field "file".</summary>
    /// <response code="201">Stored; the status says how far it got (Matched, Exception or Failed).</response>
    [HttpPost]
    [Consumes("multipart/form-data")]
    [DisableFormValueModelBinding]
    [ProducesResponseType<UploadResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status415UnsupportedMediaType, "application/problem+json")]
    public async Task<IActionResult> Upload([FromServices] InvoiceIntake intake, [FromServices] IntakeOptions options, CancellationToken cancellationToken)
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

        var result = await intake.SubmitAsync(new UploadedFile(file.FileName, content), user.Name, cancellationToken).ConfigureAwait(false);
        return result.Outcome switch
        {
            IntakeOutcome.Created => Created($"/api/invoices/{result.InvoiceId}", new UploadResponse(result.InvoiceId!.Value, result.Status!.Value)),
            IntakeOutcome.Empty => Refusal(StatusCodes.Status400BadRequest, result),
            IntakeOutcome.TooLarge => Refusal(StatusCodes.Status413PayloadTooLarge, result),
            IntakeOutcome.UnsupportedType => Refusal(StatusCodes.Status415UnsupportedMediaType, result),
            IntakeOutcome.DuplicateFile or IntakeOutcome.DuplicateInvoiceNumber => Refusal(StatusCodes.Status409Conflict, result),
            _ => throw new InvalidOperationException($"Unhandled intake outcome {result.Outcome}."),
        };
    }

    /// <summary>Invoices, newest first. Filter by status name; pages of at most 100.</summary>
    [HttpGet]
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
    [ProducesResponseType<InvoiceDetail>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<IActionResult> Get([FromServices] IInvoiceQueries queries, Guid id, CancellationToken cancellationToken) =>
        await queries.GetAsync(id, cancellationToken).ConfigureAwait(false) is { } detail ? Ok(detail) : NotFoundProblem();

    /// <summary>The original file, byte for byte, always as a download.</summary>
    [HttpGet("{id:guid}/document")]
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
    [ProducesResponseType<RematchResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    public async Task<IActionResult> Rematch([FromServices] InvoiceRematch rematch, Guid id, CancellationToken cancellationToken)
    {
        var result = await rematch.RematchAsync(id, user.Name, cancellationToken).ConfigureAwait(false);
        return result.Outcome switch
        {
            RematchOutcome.Matched or RematchOutcome.StillException => Ok(new RematchResponse(id, result.Outcome, result.Status!.Value, result.Reasons)),
            RematchOutcome.NotFound => NotFoundProblem(),
            RematchOutcome.NotAllowed => Problem(statusCode: StatusCodes.Status409Conflict, detail: result.Detail),
            _ => throw new InvalidOperationException($"Unhandled re-match outcome {result.Outcome}."),
        };
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
