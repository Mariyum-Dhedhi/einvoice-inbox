using System.Xml.Linq;
using EInvoice.Application.Parsing;
using static EInvoice.Infrastructure.Parsing.MappingHelpers;

namespace EInvoice.Infrastructure.Parsing;

/// <summary>UBL 2.1 Invoice to EN 16931 business terms (paths per the XRechnung UBL syntax binding).</summary>
internal static class UblMapper
{
    private static readonly XNamespace Cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    private static readonly XNamespace Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";

    public static ParsedDocument Map(XElement invoice)
    {
        var currency = Required(invoice.Element(Cbc + "DocumentCurrencyCode"), "BT-5 invoice currency code");
        var seller = invoice.Element(Cac + "AccountingSupplierParty")?.Element(Cac + "Party");
        var totals = invoice.Element(Cac + "LegalMonetaryTotal");

        var vatId = seller?.Elements(Cac + "PartyTaxScheme")
            .FirstOrDefault(s => Optional(s.Element(Cac + "TaxScheme")?.Element(Cbc + "ID")) == "VAT")
            ?.Element(Cbc + "CompanyID");

        var ibans = invoice.Elements(Cac + "PaymentMeans")
            .Select(m => Optional(m.Element(Cac + "PayeeFinancialAccount")?.Element(Cbc + "ID")))
            .OfType<string>()
            .Select(i => WithoutSpaces(i)!)
            .ToList();

        var lines = invoice.Elements(Cac + "InvoiceLine").Select((line, i) => MapLine(line, i + 1)).ToList();
        if (lines.Count == 0)
        {
            throw new DocumentRejectedException(ParseErrorCode.MissingField, "BG-25 invoice line is missing (at least one is required).");
        }

        return new ParsedDocument(
            DocumentSyntax.Ubl,
            InvoiceTypeCode: Required(invoice.Element(Cbc + "InvoiceTypeCode"), "BT-3 invoice type code"),
            InvoiceNumber: Required(invoice.Element(Cbc + "ID"), "BT-1 invoice number"),
            IssueDate: RequiredDate(invoice.Element(Cbc + "IssueDate"), "yyyy-MM-dd", "BT-2 invoice issue date"),
            Currency: currency,
            BuyerReference: Optional(invoice.Element(Cbc + "BuyerReference")),
            PurchaseOrderReference: PurchaseOrderReference(invoice.Element(Cac + "OrderReference")),
            SellerName: Required(seller?.Element(Cac + "PartyLegalEntity")?.Element(Cbc + "RegistrationName"), "BT-27 seller name"),
            SellerVatId: WithoutSpaces(Optional(vatId)),
            PaymentAccountIbans: ibans,
            SumOfLineNetAmounts: RequiredDecimal(totals?.Element(Cbc + "LineExtensionAmount"), "BT-106 sum of invoice line net amount"),
            NetTotal: RequiredDecimal(totals?.Element(Cbc + "TaxExclusiveAmount"), "BT-109 invoice total amount without VAT"),
            TaxTotal: TaxTotalInInvoiceCurrency(invoice.Elements(Cac + "TaxTotal").Select(t => t.Element(Cbc + "TaxAmount")).OfType<XElement>(), currency),
            GrossTotal: RequiredDecimal(totals?.Element(Cbc + "TaxInclusiveAmount"), "BT-112 invoice total amount with VAT"),
            Lines: lines,
            VatBreakdown: VatBreakdown(invoice));
    }

    /// <summary>BG-23 lives in the cac:TaxTotal that has subtotals; the BT-111 tax-currency total never has any.</summary>
    private static List<VatBreakdownEntry> VatBreakdown(XElement invoice) =>
        invoice.Elements(Cac + "TaxTotal")
            .SelectMany(t => t.Elements(Cac + "TaxSubtotal"))
            .Select((subtotal, i) =>
            {
                var category = subtotal.Element(Cac + "TaxCategory");
                var n = i + 1;
                return new VatBreakdownEntry(
                    CategoryCode: Required(category?.Element(Cbc + "ID"), $"BT-118 VAT category code in breakdown {n}"),
                    RatePercent: OptionalDecimal(category?.Element(Cbc + "Percent"), $"BT-119 VAT rate in breakdown {n}") ?? 0m,
                    TaxableAmount: RequiredDecimal(subtotal.Element(Cbc + "TaxableAmount"), $"BT-116 VAT taxable amount in breakdown {n}"),
                    TaxAmount: RequiredDecimal(subtotal.Element(Cbc + "TaxAmount"), $"BT-117 VAT amount in breakdown {n}"));
            })
            .ToList();

    /// <summary>
    /// Line-level allowances and charges (BG-27/BG-28) are direct children of cac:InvoiceLine.
    /// A cac:AllowanceCharge inside cac:Price is a price discount (BT-147), already included in the net price.
    /// </summary>
    private static (decimal Allowances, decimal Charges) LineAllowancesAndCharges(XElement line, int position)
    {
        decimal allowances = 0m, charges = 0m;
        foreach (var ac in line.Elements(Cac + "AllowanceCharge"))
        {
            var amount = RequiredDecimal(ac.Element(Cbc + "Amount"), $"BT-136/BT-141 allowance or charge amount on line {position}");
            if (IsCharge(ac.Element(Cbc + "ChargeIndicator"), position))
            {
                charges += amount;
            }
            else
            {
                allowances += amount;
            }
        }

        return (allowances, charges);
    }

    /// <summary>
    /// UBL requires cbc:ID whenever cac:OrderReference exists, so an invoice with only a sales order number (BT-14)
    /// fills BT-13 with a placeholder: "NA" per the EN 16931 UBL binding, "Dummywert" in the KoSIT samples.
    /// Only that documented combination is treated as "no purchase order"; anything else is kept as stated.
    /// </summary>
    private static string? PurchaseOrderReference(XElement? orderReference)
    {
        var id = Optional(orderReference?.Element(Cbc + "ID"));
        var hasSalesOrder = Optional(orderReference?.Element(Cbc + "SalesOrderID")) is not null;
        return hasSalesOrder && id is not null && OrderReferencePlaceholders.Contains(id) ? null : id;
    }

    private static readonly HashSet<string> OrderReferencePlaceholders = new(StringComparer.OrdinalIgnoreCase) { "NA", "Dummywert" };

    private static ParsedDocumentLine MapLine(XElement line, int position)
    {
        var item = line.Element(Cac + "Item");
        var price = line.Element(Cac + "Price");
        var (allowances, charges) = LineAllowancesAndCharges(line, position);

        return new ParsedDocumentLine(
            Position: position,
            LineId: Required(line.Element(Cbc + "ID"), $"BT-126 invoice line identifier on line {position}"),
            ItemName: Required(item?.Element(Cbc + "Name"), $"BT-153 item name on line {position}"),
            Quantity: RequiredDecimal(line.Element(Cbc + "InvoicedQuantity"), $"BT-129 invoiced quantity on line {position}"),
            UnitPrice: UnitPrice(
                RequiredDecimal(price?.Element(Cbc + "PriceAmount"), $"BT-146 item net price on line {position}"),
                OptionalDecimal(price?.Element(Cbc + "BaseQuantity"), $"BT-149 item price base quantity on line {position}"),
                position),
            NetAmount: RequiredDecimal(line.Element(Cbc + "LineExtensionAmount"), $"BT-131 invoice line net amount on line {position}"),
            TaxRatePercent: OptionalDecimal(item?.Element(Cac + "ClassifiedTaxCategory")?.Element(Cbc + "Percent"), $"BT-152 VAT rate on line {position}") ?? 0m,
            PurchaseOrderLineReference: Optional(line.Element(Cac + "OrderLineReference")?.Element(Cbc + "LineID")),
            LineAllowances: allowances,
            LineCharges: charges);
    }
}
