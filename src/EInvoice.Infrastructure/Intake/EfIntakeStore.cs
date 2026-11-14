using EInvoice.Application.Intake;
using EInvoice.Application.Validation;
using EInvoice.Domain.Invoices;
using EInvoice.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace EInvoice.Infrastructure.Intake;

/// <summary>
/// Stores new invoices for the upload pipeline. A unique-index violation (SQL Server errors 2601/2627) on the file
/// hash or on vendor + invoice number means a parallel upload won the race; it becomes a typed exception.
/// </summary>
public sealed class EfIntakeStore(EInvoiceDbContext db) : IIntakeStore
{
    private const int MaxMessage = 500;

    public Task<Guid?> FindInvoiceIdByDocumentHashAsync(byte[] sha256, CancellationToken cancellationToken) =>
        db.InvoiceDocuments.AsNoTracking()
            .Where(d => d.Sha256 == sha256)
            .Select(d => (Guid?)d.InvoiceId)
            .FirstOrDefaultAsync(cancellationToken);

    public async Task AddAsync(Invoice invoice, InvoiceDocument document, IReadOnlyList<ValidationFinding> findings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(findings);

        db.Invoices.Add(invoice);
        db.InvoiceDocuments.Add(document);
        db.InvoiceFindings.AddRange(findings.Select((f, i) => new InvoiceFindingRecord
        {
            InvoiceId = invoice.Id,
            Sequence = i + 1,
            RuleCode = f.RuleCode,
            Severity = f.Severity,
            Message = f.Message.Length <= MaxMessage ? f.Message : f.Message[..MaxMessage],
            LinePosition = f.LinePosition,
        }));

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 } sql)
        {
            // Forget the rows that were not stored, so this context can still be used.
            foreach (var entry in db.ChangeTracker.Entries().Where(e => e.State == EntityState.Added).ToList())
            {
                entry.State = EntityState.Detached;
            }

            if (sql.Message.Contains("UX_InvoiceDocument_Sha256", StringComparison.Ordinal))
            {
                throw new DuplicateDocumentException();
            }

            if (sql.Message.Contains("UX_Invoice_Vendor_InvoiceNumber", StringComparison.Ordinal))
            {
                throw new DuplicateInvoiceNumberException();
            }

            throw;
        }
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
