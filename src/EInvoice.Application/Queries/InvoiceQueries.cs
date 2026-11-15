using EInvoice.Application.Validation;
using EInvoice.Domain.Invoices;
using EInvoice.Domain.Matching;

namespace EInvoice.Application.Queries;

/// <summary>Read side for the API and the UI. Returns views, never tracked entities, and never the file bytes except through <see cref="GetDocumentAsync"/>.</summary>
public interface IInvoiceQueries
{
    Task<InvoicePage> ListAsync(InvoiceListQuery query, CancellationToken cancellationToken);

    Task<InvoiceDetail?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Invoices waiting for a decision (Matched, Exception), oldest first: the one waiting longest comes first.</summary>
    Task<ApprovalQueuePage> ApprovalQueueAsync(int page, int pageSize, CancellationToken cancellationToken);

    Task<StoredDocument?> GetDocumentAsync(Guid id, CancellationToken cancellationToken);
}

/// <param name="Page">1-based.</param>
public sealed record InvoiceListQuery(InvoiceStatus? Status, int Page, int PageSize)
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;
}

public sealed record InvoicePage(IReadOnlyList<InvoiceSummary> Items, int Page, int PageSize, int TotalCount);

public sealed record ApprovalQueuePage(IReadOnlyList<ApprovalQueueItem> Items, int Page, int PageSize, int TotalCount);

/// <summary>One invoice in the approval queue, with what the screen needs to say what an approver can do with it.</summary>
public sealed record ApprovalQueueItem(
    Guid Id,
    InvoiceStatus Status,
    string? InvoiceNumber,
    string? VendorName,
    decimal? GrossTotal,
    string? Currency,
    string SubmittedBy,
    DateTimeOffset ReceivedAt,
    bool HasBlockingFindings);

public sealed record InvoiceSummary(
    Guid Id,
    InvoiceStatus Status,
    SourceFormat SourceFormat,
    string? InvoiceNumber,
    string? VendorName,
    DateOnly? IssueDate,
    decimal? GrossTotal,
    string? Currency,
    DateTimeOffset ReceivedAt);

/// <param name="Version">Opaque; changes with every change to the invoice. A decision sends it back (package 10).</param>
public sealed record InvoiceDetail(
    Guid Id,
    InvoiceStatus Status,
    SourceFormat SourceFormat,
    string SubmittedBy,
    string? InvoiceNumber,
    Guid? VendorId,
    string? VendorName,
    DateOnly? IssueDate,
    string? PurchaseOrderNumber,
    string? Currency,
    decimal? NetTotal,
    decimal? TaxTotal,
    decimal? GrossTotal,
    IReadOnlyList<InvoiceLineView> Lines,
    IReadOnlyList<StatusChangeView> History,
    IReadOnlyList<FindingView> Findings,
    IReadOnlyList<MatchLineView> MatchResults,
    DocumentInfo? Document,
    string Version = "");

public sealed record InvoiceLineView(
    int LineNumber,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    decimal NetAmount,
    decimal TaxRatePercent,
    int? PurchaseOrderLineNumber);

public sealed record StatusChangeView(int Sequence, InvoiceStatus? From, InvoiceStatus To, string Actor, DateTimeOffset At, string? Reason);

public sealed record FindingView(string RuleCode, FindingSeverity Severity, string Message, int? LinePosition);

public sealed record MatchLineView(
    int InvoiceLineNumber,
    int? PurchaseOrderLineNumber,
    MatchOutcome Outcome,
    decimal InvoiceUnitPrice,
    decimal? OrderUnitPrice,
    decimal? PriceVariancePercent,
    decimal? PriceVarianceAmount,
    decimal QuantityInvoiced,
    decimal? QuantityAvailable,
    decimal? QuantityVariance,
    decimal PriceTolerancePercent,
    DateTimeOffset MatchedAt);

public sealed record DocumentInfo(string FileName, string ContentType, long SizeBytes, DateTimeOffset ReceivedAt);

public sealed record StoredDocument(string FileName, string ContentType, byte[] Content);
