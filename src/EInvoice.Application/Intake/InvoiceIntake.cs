using System.Security.Cryptography;
using EInvoice.Application.Matching;
using EInvoice.Application.Parsing;
using EInvoice.Application.Validation;
using EInvoice.Domain.Invoices;
using EInvoice.Domain.Matching;
using EInvoice.Domain.Shared;

namespace EInvoice.Application.Intake;

/// <summary>
/// The upload pipeline: check the file, refuse duplicates, parse, validate, store the original with the invoice,
/// then three-way match. Every accepted upload becomes an invoice whose status says how far it got:
/// Matched (clean), Exception (a human decides, with reasons) or Failed (unreadable, kept for audit).
/// </summary>
public sealed class InvoiceIntake(
    IInvoiceDocumentParser parser,
    InvoiceValidator validator,
    IValidationDataSource vendors,
    IInvoiceMatcher matcher,
    IIntakeStore store,
    MatchingPolicy policy,
    IntakeOptions options,
    TimeProvider time) : IInvoiceIntake
{
    private const string System = "system";

    // Column sizes in the database; longer values are refused with a reason instead of failing on save.
    private const int MaxInvoiceNumber = 50;
    private const int MaxPurchaseOrderReference = 20;
    private const int MaxDescription = 500;
    private const int MaxReason = 200;

    public async Task<IntakeResult> SubmitAsync(UploadedFile file, string submittedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(submittedBy);
        var content = file.Content ?? [];

        // 1. Cheap checks first. The type comes from the bytes, never from the name.
        if (content.Length == 0)
        {
            return new IntakeResult(IntakeOutcome.Empty, Detail: "The file is empty.");
        }

        if (content.Length > options.MaxDocumentBytes)
        {
            return new IntakeResult(IntakeOutcome.TooLarge, Detail: $"The file is larger than {options.MaxDocumentBytes} bytes.");
        }

        switch (ContentSniffer.Detect(content))
        {
            case DetectedContent.Pdf:
                return new IntakeResult(IntakeOutcome.UnsupportedType, Detail: "PDF invoices (ZUGFeRD and plain PDF) are not supported yet; upload XRechnung XML.");
            case DetectedContent.Unknown:
                return new IntakeResult(IntakeOutcome.UnsupportedType, Detail: "Only XRechnung XML files are accepted.");
        }

        // 2. The same file again: point at the invoice it already created.
        var hash = SHA256.HashData(content);
        if (await store.FindInvoiceIdByDocumentHashAsync(hash, cancellationToken).ConfigureAwait(false) is { } existing)
        {
            return new IntakeResult(IntakeOutcome.DuplicateFile, existing, Detail: "This file was uploaded before.");
        }

        var received = time.GetUtcNow();
        var invoice = Invoice.Receive(SourceFormat.XRechnung, submittedBy, received);
        var document = new InvoiceDocument(invoice.Id, UploadNames.Sanitize(file.FileName), "application/xml", content, hash, received);

        // 3. Parse. An unreadable document is kept as Failed, with the reason, so nothing disappears silently.
        var parsed = parser.Parse(new MemoryStream(content, writable: false));
        if (!parsed.Succeeded)
        {
            invoice.TransitionTo(InvoiceStatus.Failed, System, time.GetUtcNow(), Truncate($"{parsed.Error!.Code}: {parsed.Error.Message}"));
            return await StoreAsync(invoice, document, [], hash, cancellationToken).ConfigureAwait(false);
        }

        var source = parsed.Document!;
        if (TooLongField(source) is { } tooLong)
        {
            invoice.TransitionTo(InvoiceStatus.Failed, System, time.GetUtcNow(), tooLong);
            return await StoreAsync(invoice, document, [], hash, cancellationToken).ConfigureAwait(false);
        }

        var vendor = source.SellerVatId is { } vatId
            ? await vendors.FindVendorByVatIdAsync(vatId, cancellationToken).ConfigureAwait(false)
            : null;
        try
        {
            invoice.MarkParsed(ToDomain(source, vendor?.Id), System, time.GetUtcNow());
        }
        catch (Exception ex) when (ex is ArgumentException or DomainException)
        {
            // The document parsed, but a value is outside what an invoice may contain (for example an invalid currency code).
            invoice.TransitionTo(InvoiceStatus.Failed, System, time.GetUtcNow(), "InvalidValue: a document value is outside the accepted range.");
            return await StoreAsync(invoice, document, [], hash, cancellationToken).ConfigureAwait(false);
        }

        // 4. Validate. A duplicate invoice number is refused outright: nothing is stored.
        var validation = await validator.ValidateAsync(source, content, cancellationToken).ConfigureAwait(false);
        if (validation.Findings.Any(f => f.RuleCode == RuleCodes.DuplicateNumber))
        {
            return new IntakeResult(IntakeOutcome.DuplicateInvoiceNumber, Detail: "The vendor already has an invoice with this number.");
        }

        if (validation.IsValid)
        {
            invoice.TransitionTo(InvoiceStatus.Validated, System, time.GetUtcNow());
        }
        else
        {
            invoice.TransitionTo(InvoiceStatus.Exception, System, time.GetUtcNow(),
                Truncate(string.Join(", ", validation.Findings.Select(f => f.RuleCode).Distinct())));
        }

        var stored = await StoreAsync(invoice, document, validation.Findings, hash, cancellationToken).ConfigureAwait(false);
        if (stored.Outcome != IntakeOutcome.Created || invoice.Status != InvoiceStatus.Validated)
        {
            return stored;
        }

        // 5. Match (needs the stored invoice: the set-based procedure reads it from the database).
        var match = await matcher.MatchAsync(invoice.Id, policy, cancellationToken).ConfigureAwait(false);
        if (match.IsFullMatch)
        {
            invoice.TransitionTo(InvoiceStatus.Matched, System, time.GetUtcNow());
        }
        else
        {
            invoice.TransitionTo(InvoiceStatus.Exception, System, time.GetUtcNow(), Truncate(string.Join(", ", match.ExceptionReasons)));
        }

        await store.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new IntakeResult(IntakeOutcome.Created, invoice.Id, invoice.Status);
    }

    /// <summary>The unique indexes catch what the checks above cannot see: a parallel upload that is not committed yet.</summary>
    private async Task<IntakeResult> StoreAsync(Invoice invoice, InvoiceDocument document, IReadOnlyList<ValidationFinding> findings, byte[] hash, CancellationToken cancellationToken)
    {
        try
        {
            await store.AddAsync(invoice, document, findings, cancellationToken).ConfigureAwait(false);
            return new IntakeResult(IntakeOutcome.Created, invoice.Id, invoice.Status);
        }
        catch (DuplicateDocumentException)
        {
            var existing = await store.FindInvoiceIdByDocumentHashAsync(hash, cancellationToken).ConfigureAwait(false);
            return new IntakeResult(IntakeOutcome.DuplicateFile, existing, Detail: "This file was uploaded before.");
        }
        catch (DuplicateInvoiceNumberException)
        {
            return new IntakeResult(IntakeOutcome.DuplicateInvoiceNumber, Detail: "The vendor already has an invoice with this number.");
        }
    }

    private static string? TooLongField(ParsedDocument source) =>
        source.InvoiceNumber.Length > MaxInvoiceNumber
            ? $"InvalidValue: BT-1 invoice number is longer than {MaxInvoiceNumber} characters."
            : source.PurchaseOrderReference?.Length > MaxPurchaseOrderReference
                ? $"InvalidValue: BT-13 purchase order reference is longer than {MaxPurchaseOrderReference} characters."
                : null;

    /// <summary>The parser's document, as stated by the supplier, into the domain's invoice content.</summary>
    private static ParsedInvoice ToDomain(ParsedDocument source, Guid? vendorId) => new(
        InvoiceNumber: source.InvoiceNumber,
        VendorId: vendorId,
        IssueDate: source.IssueDate,
        PurchaseOrderNumber: source.PurchaseOrderReference,
        NetTotal: new Money(source.NetTotal, source.Currency),
        TaxTotal: new Money(source.TaxTotal, source.Currency),
        GrossTotal: new Money(source.GrossTotal, source.Currency),
        Lines: source.Lines.Select(l => new InvoiceLine(
            l.Position,
            l.ItemName.Length > MaxDescription ? l.ItemName[..MaxDescription] : l.ItemName,
            l.Quantity,
            l.UnitPrice,
            new Money(l.NetAmount, source.Currency),
            l.TaxRatePercent,
            OrderLineReferences.ToLineNumber(l.PurchaseOrderLineReference))).ToList());

    private static string Truncate(string reason) =>
        reason.Length <= MaxReason ? reason : string.Concat(reason.AsSpan(0, MaxReason - 1), "…");
}
