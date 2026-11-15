using System.Diagnostics;
using System.Globalization;
using EInvoice.Application.Extraction;
using EInvoice.Application.Intake;
using EInvoice.Application.Validation;
using EInvoice.Domain.Invoices;
using EInvoice.Domain.Matching;
using EInvoice.Domain.Purchasing;
using EInvoice.Domain.Shared;
using EInvoice.Infrastructure.Extraction;
using EInvoice.Infrastructure.Intake;
using EInvoice.Infrastructure.Matching;
using EInvoice.Infrastructure.Parsing;
using EInvoice.Infrastructure.Persistence;
using EInvoice.Infrastructure.Validation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;

namespace EInvoice.AiEvaluation;

/// <summary>
/// Runs every case through the real upload pipeline, exactly as an upload would go: extraction with the chosen model,
/// the validation rules, the matching procedure, the database. Cases run in file order (E22 and E23 rely on E01/E02),
/// each with its own order, receipts and earlier invoices set up first. Data is committed; use a fresh database.
/// </summary>
public sealed class EvaluationRunner(string connectionString)
{
    private const string Submitter = "ai-evaluation";

    public async Task<IReadOnlyList<CaseResult>> RunAsync(
        IReadOnlyList<EvalCase> cases,
        Func<EvalCase, byte[]> pdfFor,
        Func<EvalCase, IChatClient> modelFor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cases);
        ArgumentNullException.ThrowIfNull(pdfFor);
        ArgumentNullException.ThrowIfNull(modelFor);

        var results = new List<CaseResult>();
        foreach (var c in cases)
        {
            await SetUpAsync(c, cancellationToken).ConfigureAwait(false);

            await using var db = Context();
            using var model = modelFor(c);
            var extractor = new RecordingExtractor(new LlmInvoiceExtractor(new PdfTextReader(PdfLimits.Default), model));
            var data = new EfValidationDataSource(db);
            var intake = new InvoiceIntake(
                new XRechnungParser(),
                new InvoiceValidator(data, new NoXmlHere()),
                data,
                new SqlInvoiceMatcher(db),
                new EfIntakeStore(db),
                MatchingPolicy.Default,
                IntakeOptions.Default,
                TimeProvider.System,
                extractor);

            var clock = Stopwatch.StartNew();
            var result = await intake.SubmitAsync(new UploadedFile($"{c.Id}.pdf", pdfFor(c)), Submitter, cancellationToken).ConfigureAwait(false);
            clock.Stop();

            results.Add(new CaseResult(c, extractor.Last, await OutcomeAsync(db, result, cancellationToken).ConfigureAwait(false), clock.Elapsed));
        }

        return results;
    }

    private EInvoiceDbContext Context() => new(new DbContextOptionsBuilder<EInvoiceDbContext>().UseSqlServer(connectionString).Options);

    /// <summary>Where the invoice ended, with every reason the pipeline gave: finding rule codes and match outcomes.</summary>
    private static async Task<CaseOutcome> OutcomeAsync(EInvoiceDbContext db, IntakeResult result, CancellationToken cancellationToken)
    {
        if (result.Outcome != IntakeOutcome.Created)
        {
            return new CaseOutcome("Refused", [], result.Outcome.ToString());
        }

        db.ChangeTracker.Clear();
        var invoice = await db.Invoices.AsNoTracking().SingleAsync(i => i.Id == result.InvoiceId, cancellationToken).ConfigureAwait(false);
        var findings = await db.InvoiceFindings.AsNoTracking().Where(f => f.InvoiceId == invoice.Id).Select(f => f.RuleCode).ToListAsync(cancellationToken).ConfigureAwait(false);
        var matches = await db.MatchResults.AsNoTracking().Where(r => r.InvoiceId == invoice.Id).Select(r => r.Outcome).ToListAsync(cancellationToken).ConfigureAwait(false);
        var historyReason = invoice.StatusHistory[^1].Reason?.Split(", ", StringSplitOptions.RemoveEmptyEntries) ?? [];

        var reasons = findings
            .Concat(matches.Where(o => o != MatchOutcome.FullMatch).Select(o => o.ToString()))
            .Concat(historyReason.Select(r => r.Split(':')[0].Trim()))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return new CaseOutcome(invoice.Status.ToString(), reasons, null);
    }

    /// <summary>The case's purchase order, goods receipts and earlier invoices, committed before the upload.</summary>
    private async Task SetUpAsync(EvalCase c, CancellationToken cancellationToken)
    {
        if (c.Order is null)
        {
            return;
        }

        var vendor = SeedVendors.Get(c.Vendor ?? throw new InvalidDataException($"{c.Id}: an order needs a vendor.")).Id;
        var number = c.PurchaseOrder ?? throw new InvalidDataException($"{c.Id}: an order needs an order number on the invoice.");
        await using var db = Context();

        var order = new PurchaseOrder(number, vendor, c.IssueDate.AddDays(-30), "EUR", c.Order.Lines
            .Select(l => new PurchaseOrderLine(l.Line, string.Create(CultureInfo.InvariantCulture, $"EV-{l.Line:00}"), Describe(c, l.Line), l.Quantity, l.UnitPrice, Rate(c, l.Line)))
            .ToList());
        db.PurchaseOrders.Add(order);

        if (c.Order.Received.Count > 0)
        {
            db.GoodsReceipts.Add(new GoodsReceipt($"GR-{c.Id}", order.Id, c.IssueDate.AddDays(-5),
                c.Order.Received.Select(r => new GoodsReceiptLine(r.Line, r.Quantity)).ToList()));
        }

        foreach (var (earlier, i) in (c.Order.AlreadyInvoiced ?? []).Select((e, i) => (e, i + 1)))
        {
            // An earlier invoice for part of the order (already parsed and stored), so this one finds less left to bill.
            var price = c.Order.Lines.Single(l => l.Line == earlier.Line).UnitPrice;
            var net = Money.Euro(decimal.Round(earlier.Quantity * price, 2, MidpointRounding.AwayFromZero));
            var tax = net.Multiply(Rate(c, earlier.Line) / 100m);
            var invoice = Invoice.Receive(SourceFormat.XRechnung, Submitter, DateTimeOffset.UtcNow);
            invoice.MarkParsed(
                new ParsedInvoice(string.Create(CultureInfo.InvariantCulture, $"{c.InvoiceNumber}-PRE{i}"), vendor, c.IssueDate.AddDays(-10), number, net, tax, net + tax,
                    [new InvoiceLine(1, Describe(c, earlier.Line), earlier.Quantity, price, net, Rate(c, earlier.Line), earlier.Line)]),
                Submitter,
                DateTimeOffset.UtcNow);
            db.Invoices.Add(invoice);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Describe(EvalCase c, int orderLine) =>
        c.Lines.FirstOrDefault(l => l.OrderLine == orderLine.ToString(CultureInfo.InvariantCulture))?.Description ?? $"Item {orderLine}";

    private static decimal Rate(EvalCase c, int orderLine) =>
        c.Lines.FirstOrDefault(l => l.OrderLine == orderLine.ToString(CultureInfo.InvariantCulture))?.TaxRate ?? 19m;

    /// <summary>Keeps what the extractor returned, so fields can be scored without asking the model twice.</summary>
    private sealed class RecordingExtractor(IInvoiceExtractor inner) : IInvoiceExtractor
    {
        public ExtractionResult? Last { get; private set; }

        public async Task<ExtractionResult> ExtractAsync(ReadOnlyMemory<byte> pdf, CancellationToken cancellationToken)
        {
            Last = await inner.ExtractAsync(pdf, cancellationToken).ConfigureAwait(false);
            return Last;
        }
    }

    /// <summary>Every evaluation invoice is a PDF; the KoSIT check (XML only) must never be asked.</summary>
    private sealed class NoXmlHere : IConformanceValidator
    {
        public Task<ConformanceResult> CheckAsync(ReadOnlyMemory<byte> xml, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The conformance check was asked about a PDF.");
    }
}
