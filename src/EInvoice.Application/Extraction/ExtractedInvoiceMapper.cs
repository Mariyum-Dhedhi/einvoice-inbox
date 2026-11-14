using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using EInvoice.Application.Parsing;

namespace EInvoice.Application.Extraction;

/// <summary>
/// Turns a checked extraction into the <see cref="ParsedDocument"/> the XML parsers produce, so one set of
/// validation rules and one matcher handle every invoice. Stated totals stay as stated (the rules compare them);
/// the VAT breakdown is computed here from the lines, never taken from the model.
/// </summary>
public static class ExtractedInvoiceMapper
{
    /// <summary>UNTDID 1001 code for a commercial invoice; a plain PDF does not state one.</summary>
    private const string CommercialInvoice = "380";

    /// <param name="invoice">The extraction, with IBAN placeholders already turned back into accounts.</param>
    public static bool TryMap(ExtractedInvoice invoice, [NotNullWhen(true)] out ParsedDocument? document, [NotNullWhen(false)] out ExtractionError? error)
    {
        ArgumentNullException.ThrowIfNull(invoice);
        document = null;

        if (MissingTerm(invoice) is { } missing)
        {
            error = new ExtractionError(ExtractionErrorCode.MissingField, $"{missing} was not found in the document.");
            return false;
        }

        if (!DateOnly.TryParseExact(invoice.IssueDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var issueDate))
        {
            error = new ExtractionError(ExtractionErrorCode.InvalidModelOutput, "The issue date (BT-2) is not a valid date.");
            return false;
        }

        var currency = invoice.Currency!.Trim().ToUpperInvariant();
        if (currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper))
        {
            error = new ExtractionError(ExtractionErrorCode.InvalidModelOutput, "The currency (BT-5) is not a three-letter code.");
            return false;
        }

        var lines = invoice.Lines!
            .Select((l, i) => new ParsedDocumentLine(
                Position: i + 1,
                LineId: (i + 1).ToString(CultureInfo.InvariantCulture),
                ItemName: l.Description!.Trim(),
                Quantity: l.Quantity!.Value,
                UnitPrice: l.UnitPrice!.Value,
                NetAmount: l.NetAmount!.Value,
                TaxRatePercent: l.TaxRatePercent ?? 0m,
                PurchaseOrderLineReference: string.IsNullOrWhiteSpace(l.OrderLineReference) ? null : l.OrderLineReference.Trim(),
                LineAllowances: 0m,
                LineCharges: 0m))
            .ToList();

        // One entry per VAT rate, in the order the rates first appear: base = sum of the lines, VAT = base x rate.
        var breakdown = lines
            .GroupBy(l => l.TaxRatePercent)
            .Select(g =>
            {
                var taxable = g.Sum(l => l.NetAmount);
                return new VatBreakdownEntry(g.Key > 0 ? "S" : "Z", g.Key, taxable, decimal.Round(taxable * g.Key / 100m, 2, MidpointRounding.AwayFromZero));
            })
            .ToList();

        document = new ParsedDocument(
            Syntax: DocumentSyntax.Pdf,
            InvoiceTypeCode: CommercialInvoice,
            InvoiceNumber: invoice.InvoiceNumber!.Trim(),
            IssueDate: issueDate,
            Currency: currency,
            BuyerReference: null,
            PurchaseOrderReference: string.IsNullOrWhiteSpace(invoice.PurchaseOrderReference) ? null : invoice.PurchaseOrderReference.Trim(),
            SellerName: invoice.SellerName!.Trim(),
            SellerVatId: string.IsNullOrWhiteSpace(invoice.SellerVatId) ? null : string.Concat(invoice.SellerVatId.Where(c => !char.IsWhiteSpace(c))),
            PaymentAccountIbans: invoice.Ibans ?? [],
            SumOfLineNetAmounts: invoice.NetTotal!.Value, // a plain PDF states one net total; LINE_SUM compares the lines with it
            NetTotal: invoice.NetTotal.Value,
            TaxTotal: invoice.TaxTotal!.Value,
            GrossTotal: invoice.GrossTotal!.Value,
            Lines: lines,
            VatBreakdown: breakdown);
        error = null;
        return true;
    }

    /// <summary>The first value an invoice cannot do without, named by its business term; null when all are there.</summary>
    private static string? MissingTerm(ExtractedInvoice invoice)
    {
        if (string.IsNullOrWhiteSpace(invoice.InvoiceNumber))
        {
            return "The invoice number (BT-1)";
        }

        if (string.IsNullOrWhiteSpace(invoice.IssueDate))
        {
            return "The issue date (BT-2)";
        }

        if (string.IsNullOrWhiteSpace(invoice.Currency))
        {
            return "The currency (BT-5)";
        }

        if (string.IsNullOrWhiteSpace(invoice.SellerName))
        {
            return "The seller name (BT-27)";
        }

        if (invoice.NetTotal is null)
        {
            return "The net total (BT-109)";
        }

        if (invoice.TaxTotal is null)
        {
            return "The VAT total (BT-110)";
        }

        if (invoice.GrossTotal is null)
        {
            return "The gross total (BT-112)";
        }

        if (invoice.Lines is not { Count: > 0 })
        {
            return "An invoice line (BG-25, BT-126)";
        }

        foreach (var (line, position) in invoice.Lines.Select((l, i) => (l, i + 1)))
        {
            var term = string.IsNullOrWhiteSpace(line.Description) ? "item name (BT-153)"
                : line.Quantity is null ? "quantity (BT-129)"
                : line.UnitPrice is null ? "unit price (BT-146)"
                : line.NetAmount is null ? "net amount (BT-131)"
                : null;
            if (term is not null)
            {
                return string.Create(CultureInfo.InvariantCulture, $"Line {position}: the {term}");
            }
        }

        return null;
    }
}
