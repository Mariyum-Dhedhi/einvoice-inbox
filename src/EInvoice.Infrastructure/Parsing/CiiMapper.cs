using System.Xml.Linq;
using EInvoice.Application.Parsing;
using static EInvoice.Infrastructure.Parsing.MappingHelpers;

namespace EInvoice.Infrastructure.Parsing;

/// <summary>
/// UN/CEFACT Cross Industry Invoice to EN 16931 business terms (paths per the XRechnung CII syntax binding).
/// ZUGFeRD embeds this same XML, so package 12 reuses this mapper.
/// </summary>
internal static class CiiMapper
{
    private static readonly XNamespace Rsm = XRechnungParser.CiiInvoice;
    private static readonly XNamespace Ram = "urn:un:unece:uncefact:data:standard:ReusableAggregateBusinessInformationEntity:100";
    private static readonly XNamespace Udt = "urn:un:unece:uncefact:data:standard:UnqualifiedDataType:100";

    public static ParsedDocument Map(XElement invoice)
    {
        var header = invoice.Element(Rsm + "ExchangedDocument");
        var transaction = invoice.Element(Rsm + "SupplyChainTradeTransaction");
        var agreement = transaction?.Element(Ram + "ApplicableHeaderTradeAgreement");
        var settlement = transaction?.Element(Ram + "ApplicableHeaderTradeSettlement");
        var totals = settlement?.Element(Ram + "SpecifiedTradeSettlementHeaderMonetarySummation");
        var seller = agreement?.Element(Ram + "SellerTradeParty");

        var currency = Required(settlement?.Element(Ram + "InvoiceCurrencyCode"), "BT-5 invoice currency code");

        var vatId = seller?.Elements(Ram + "SpecifiedTaxRegistration")
            .Select(r => r.Element(Ram + "ID"))
            .FirstOrDefault(id => (string?)id?.Attribute("schemeID") == "VA");

        var ibans = (settlement?.Elements(Ram + "SpecifiedTradeSettlementPaymentMeans") ?? [])
            .Select(m => Optional(m.Element(Ram + "PayeePartyCreditorFinancialAccount")?.Element(Ram + "IBANID")))
            .OfType<string>()
            .Select(i => WithoutSpaces(i)!)
            .ToList();

        var lines = (transaction?.Elements(Ram + "IncludedSupplyChainTradeLineItem") ?? [])
            .Select((line, i) => MapLine(line, i + 1))
            .ToList();
        if (lines.Count == 0)
        {
            throw new DocumentRejectedException(ParseErrorCode.MissingField, "BG-25 invoice line is missing (at least one is required).");
        }

        return new ParsedDocument(
            DocumentSyntax.Cii,
            InvoiceTypeCode: Required(header?.Element(Ram + "TypeCode"), "BT-3 invoice type code"),
            InvoiceNumber: Required(header?.Element(Ram + "ID"), "BT-1 invoice number"),
            IssueDate: Date102(header?.Element(Ram + "IssueDateTime")?.Element(Udt + "DateTimeString"), "BT-2 invoice issue date"),
            Currency: currency,
            BuyerReference: Optional(agreement?.Element(Ram + "BuyerReference")),
            PurchaseOrderReference: Optional(agreement?.Element(Ram + "BuyerOrderReferencedDocument")?.Element(Ram + "IssuerAssignedID")),
            SellerName: Required(seller?.Element(Ram + "Name"), "BT-27 seller name"),
            SellerVatId: WithoutSpaces(Optional(vatId)),
            PaymentAccountIbans: ibans,
            SumOfLineNetAmounts: RequiredDecimal(totals?.Element(Ram + "LineTotalAmount"), "BT-106 sum of invoice line net amount"),
            NetTotal: RequiredDecimal(totals?.Element(Ram + "TaxBasisTotalAmount"), "BT-109 invoice total amount without VAT"),
            TaxTotal: TaxTotalInInvoiceCurrency(totals?.Elements(Ram + "TaxTotalAmount") ?? [], currency),
            GrossTotal: RequiredDecimal(totals?.Element(Ram + "GrandTotalAmount"), "BT-112 invoice total amount with VAT"),
            Lines: lines);
    }

    private static ParsedDocumentLine MapLine(XElement line, int position)
    {
        var lineAgreement = line.Element(Ram + "SpecifiedLineTradeAgreement");
        var netPrice = lineAgreement?.Element(Ram + "NetPriceProductTradePrice");
        var lineSettlement = line.Element(Ram + "SpecifiedLineTradeSettlement");

        return new ParsedDocumentLine(
            Position: position,
            LineId: Required(line.Element(Ram + "AssociatedDocumentLineDocument")?.Element(Ram + "LineID"), $"BT-126 invoice line identifier on line {position}"),
            ItemName: Required(line.Element(Ram + "SpecifiedTradeProduct")?.Element(Ram + "Name"), $"BT-153 item name on line {position}"),
            Quantity: RequiredDecimal(line.Element(Ram + "SpecifiedLineTradeDelivery")?.Element(Ram + "BilledQuantity"), $"BT-129 invoiced quantity on line {position}"),
            UnitPrice: UnitPrice(
                RequiredDecimal(netPrice?.Element(Ram + "ChargeAmount"), $"BT-146 item net price on line {position}"),
                OptionalDecimal(netPrice?.Element(Ram + "BasisQuantity"), $"BT-149 item price base quantity on line {position}"),
                position),
            NetAmount: RequiredDecimal(
                lineSettlement?.Element(Ram + "SpecifiedTradeSettlementLineMonetarySummation")?.Element(Ram + "LineTotalAmount"),
                $"BT-131 invoice line net amount on line {position}"),
            TaxRatePercent: OptionalDecimal(lineSettlement?.Element(Ram + "ApplicableTradeTax")?.Element(Ram + "RateApplicablePercent"), $"BT-152 VAT rate on line {position}") ?? 0m,
            PurchaseOrderLineReference: Optional(lineAgreement?.Element(Ram + "BuyerOrderReferencedDocument")?.Element(Ram + "LineID")));
    }

    /// <summary>CII dates carry a format code; EN 16931 allows only 102 (yyyyMMdd).</summary>
    private static DateOnly Date102(XElement? dateTimeString, string term)
    {
        if (dateTimeString is not null && (string?)dateTimeString.Attribute("format") != "102")
        {
            throw new DocumentRejectedException(ParseErrorCode.InvalidValue, $"{term} must use date format 102 (yyyyMMdd).");
        }

        return RequiredDate(dateTimeString, "yyyyMMdd", term);
    }
}
