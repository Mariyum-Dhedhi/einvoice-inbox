using EInvoice.Application.Queries;
using EInvoice.Application.Validation;
using EInvoice.Domain.Invoices;
using EInvoice.Infrastructure.Persistence;
using EInvoice.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace EInvoice.Infrastructure.Queries;

/// <summary>
/// Read side as EF projections: untracked, only the columns a view needs, every collection in a defined order.
/// The document's bytes are read only by <see cref="GetDocumentAsync"/>.
/// </summary>
public sealed class EfInvoiceQueries(EInvoiceDbContext db) : IInvoiceQueries
{
    public async Task<InvoicePage> ListAsync(InvoiceListQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.Page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.PageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(query.PageSize, InvoiceListQuery.MaxPageSize);

        var invoices = db.Invoices.AsNoTracking().IgnoreAutoIncludes();
        if (query.Status is { } status)
        {
            invoices = invoices.Where(i => i.Status == status);
        }

        var total = await invoices.CountAsync(cancellationToken).ConfigureAwait(false);
        var items = await invoices
            .OrderByDescending(i => EF.Property<long>(i, MappingConventions.RowId)) // insert order: newest first, never ambiguous
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .Select(i => new InvoiceSummary(
                i.Id,
                i.Status,
                i.SourceFormat,
                i.InvoiceNumber,
                db.Vendors.Where(v => v.Id == i.VendorId).Select(v => v.Name).FirstOrDefault(),
                i.IssueDate,
                i.GrossTotal == null ? null : i.GrossTotal.Amount,
                i.GrossTotal == null ? null : i.GrossTotal.Currency,
                i.StatusHistory.Where(h => h.Sequence == 1).Select(h => h.At).First()))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new InvoicePage(items, query.Page, query.PageSize, total);
    }

    public async Task<InvoiceDetail?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        // The invoice and its version in one read, so the version belongs to exactly what is shown.
        var row = await db.Invoices.AsNoTracking()
            .Where(i => i.Id == id)
            .Select(i => new { Invoice = i, Version = EF.Property<byte[]>(i, MappingConventions.Version) })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var invoice = row.Invoice;

        var vendorName = invoice.VendorId is { } vendorId
            ? await db.Vendors.Where(v => v.Id == vendorId).Select(v => v.Name).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            : null;
        var findings = await db.InvoiceFindings.AsNoTracking()
            .Where(f => f.InvoiceId == id)
            .OrderBy(f => f.Sequence)
            .Select(f => new FindingView(f.RuleCode, f.Severity, f.Message, f.LinePosition))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var matches = await db.MatchResults.AsNoTracking()
            .Where(r => r.InvoiceId == id)
            .OrderBy(r => r.InvoiceLineNumber)
            .Select(r => new MatchLineView(
                r.InvoiceLineNumber, r.PurchaseOrderLineNumber, r.Outcome, r.InvoiceUnitPrice, r.OrderUnitPrice,
                r.PriceVariancePercent, r.PriceVarianceAmount, r.QuantityInvoiced, r.QuantityAvailable, r.QuantityVariance,
                r.PriceTolerancePercent, r.MatchedAt))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var document = await db.InvoiceDocuments.AsNoTracking()
            .Where(d => d.InvoiceId == id)
            .Select(d => new DocumentInfo(d.FileName, d.ContentType, d.SizeBytes, d.ReceivedAt)) // not the bytes
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return new InvoiceDetail(
            invoice.Id,
            invoice.Status,
            invoice.SourceFormat,
            invoice.SubmittedBy,
            invoice.InvoiceNumber,
            invoice.VendorId,
            vendorName,
            invoice.IssueDate,
            invoice.PurchaseOrderNumber,
            invoice.GrossTotal?.Currency,
            invoice.NetTotal?.Amount,
            invoice.TaxTotal?.Amount,
            invoice.GrossTotal?.Amount,
            invoice.Lines.Select(ToView).ToList(),
            invoice.StatusHistory.Select(h => new StatusChangeView(h.Sequence, h.From, h.To, h.Actor, h.At, h.Reason)).ToList(),
            findings,
            matches,
            document,
            MappingConventions.VersionText(row.Version));
    }

    public async Task<ApprovalQueuePage> ApprovalQueueAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSize, InvoiceListQuery.MaxPageSize);

        var waiting = db.Invoices.AsNoTracking().IgnoreAutoIncludes()
            .Where(i => i.Status == InvoiceStatus.Matched || i.Status == InvoiceStatus.Exception);
        var total = await waiting.CountAsync(cancellationToken).ConfigureAwait(false);
        var items = await waiting
            .OrderBy(i => EF.Property<long>(i, MappingConventions.RowId)) // insert order: oldest first, never ambiguous
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(i => new ApprovalQueueItem(
                i.Id,
                i.Status,
                i.InvoiceNumber,
                db.Vendors.Where(v => v.Id == i.VendorId).Select(v => v.Name).FirstOrDefault(),
                i.GrossTotal == null ? null : i.GrossTotal.Amount,
                i.GrossTotal == null ? null : i.GrossTotal.Currency,
                i.SubmittedBy,
                i.StatusHistory.Where(h => h.Sequence == 1).Select(h => h.At).First(),
                db.InvoiceFindings.Any(f => f.InvoiceId == i.Id && f.Severity == FindingSeverity.Blocking)))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new ApprovalQueuePage(items, page, pageSize, total);
    }

    public async Task<StoredDocument?> GetDocumentAsync(Guid id, CancellationToken cancellationToken) =>
        await db.InvoiceDocuments.AsNoTracking()
            .Where(d => d.InvoiceId == id)
            .Select(d => new StoredDocument(d.FileName, d.ContentType, d.Content))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

    private static InvoiceLineView ToView(InvoiceLine l) =>
        new(l.LineNumber, l.Description, l.Quantity, l.UnitPrice, l.NetAmount.Amount, l.TaxRatePercent, l.PurchaseOrderLineNumber);
}
