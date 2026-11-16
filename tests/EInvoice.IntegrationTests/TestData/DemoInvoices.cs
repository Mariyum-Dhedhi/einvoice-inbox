using System.Text;
using EInvoice.Application.Validation;
using EInvoice.Domain.Invoices;
using EInvoice.Domain.Matching;

namespace EInvoice.IntegrationTests.TestData;

/// <summary>What uploading a demo file must give (checked by DemoInvoiceTests against the seeded database).</summary>
internal sealed record DemoExpectation(InvoiceStatus Status, MatchOutcome? Match = null, string? Finding = null);

internal sealed record DemoInvoice(string FileName, string Story, DemoExpectation Expect, byte[] Content);

/// <summary>
/// The sample invoices in demo/invoices: synthetic, built from the seeded vendors and orders (db/seed/seed.sql), one
/// per story the demo tells. Each uses its own order, so they do not change each other's outcome. Fixed numbers and
/// dates: after a demo the database is reset (demo/README.md), and the same files work again.
/// </summary>
internal static class DemoInvoices
{
    private static readonly DateOnly IssueDate = new(2026, 11, 13);

    /// <summary>A bank account no vendor has: valid check digits, not approved for anyone.</summary>
    public static readonly string UnknownIban = TestIbans.ForAccount("0000666666");

    public static IReadOnlyList<DemoInvoice> All { get; } =
    [
        new("01-full-match.xml",
            "XRechnung for order PO-0103 (Exempel IT-Service GmbH): prices and quantities agree with order and goods receipt, two VAT rates.",
            new DemoExpectation(InvoiceStatus.Matched, MatchOutcome.FullMatch),
            Xml(new XRechnungBuilder
                {
                    InvoiceNumber = "DEMO-2026-0001", IssueDate = IssueDate, PurchaseOrderNumber = "PO-0103",
                    SellerName = "Exempel IT-Service GmbH", SellerVatId = "DE000000008", Iban = "DE77000000000000001008",
                }
                .Line("Office chair", 7m, 189.00m, orderLine: "1")
                .Line("Handbook German commercial code", 12m, 39.90m, taxRate: 7m, orderLine: "2")
                .BuildXml())),

        new("02-zugferd-full-match.pdf",
            "ZUGFeRD PDF (XRechnung profile) for order PO-0101 (Platzhalter Logistik GmbH): the embedded XML is the invoice, no AI needed.",
            new DemoExpectation(InvoiceStatus.Matched, MatchOutcome.FullMatch),
            ZugferdPdf.Invoice(Xml(new CiiInvoiceBuilder
                {
                    Profile = CiiInvoiceBuilder.Profiles.XRechnung,
                    InvoiceNumber = "DEMO-2026-0002", IssueDate = IssueDate, PurchaseOrderNumber = "PO-0101",
                    SellerName = "Platzhalter Logistik GmbH", SellerVatId = "DE000000006", Iban = "DE34000000000000001006",
                }
                .Line("Copy paper A4, 500 sheets", 13m, 4.89m, orderLine: "1")
                .Line("Desk 160 x 80 cm", 18m, 349.00m, orderLine: "2")
                .Line("Wireless mouse", 3m, 24.99m, orderLine: "3")
                .BuildXml()))),

        new("03-price-variance.xml",
            "Order PO-0104 (Sample Office Supplies Ltd): the desk is billed at 389.00 instead of the ordered 349.00 (+11 %, tolerance 2 %).",
            new DemoExpectation(InvoiceStatus.Exception, MatchOutcome.PriceVariance),
            Xml(new XRechnungBuilder
                {
                    InvoiceNumber = "DEMO-2026-0003", IssueDate = IssueDate, PurchaseOrderNumber = "PO-0104",
                    SellerName = "Sample Office Supplies Ltd", SellerVatId = "DE000000009", Iban = "DE50000000000000001009",
                }
                .Line("Desk 160 x 80 cm", 14m, 389.00m, orderLine: "1")
                .Line("Wireless mouse", 19m, 24.99m, orderLine: "2")
                .Line("Toner cartridge, black", 4m, 62.50m, orderLine: "3")
                .BuildXml())),

        new("04-quantity-variance.xml",
            "Order PO-0102 (Probe Reinigungsdienst e.K.): 25 toner cartridges billed, only 20 were delivered.",
            new DemoExpectation(InvoiceStatus.Exception, MatchOutcome.QuantityVariance),
            Xml(new XRechnungBuilder
                {
                    InvoiceNumber = "DEMO-2026-0004", IssueDate = IssueDate, PurchaseOrderNumber = "PO-0102",
                    SellerName = "Probe Reinigungsdienst e.K.", SellerVatId = "DE000000007", Iban = "DE07000000000000001007",
                }
                .Line("Toner cartridge, black", 25m, 62.50m, orderLine: "1")
                .BuildXml())),

        new("05-no-goods-receipt.xml",
            "Order PO-0110 (Probe Getränke GmbH): ordered, but nothing has been received yet.",
            new DemoExpectation(InvoiceStatus.Exception, MatchOutcome.NoGoodsReceipt),
            Xml(new XRechnungBuilder
                {
                    InvoiceNumber = "DEMO-2026-0005", IssueDate = IssueDate, PurchaseOrderNumber = "PO-0110",
                    SellerName = "Probe Getränke GmbH", SellerVatId = "DE000000015", Iban = "DE82000000000000001015",
                }
                .Line("Toner cartridge, black", 16m, 62.50m, orderLine: "1")
                .Line("HDMI cable 2 m", 1m, 7.4125m, orderLine: "2")
                .Line("Screw M4 x 20, per piece", 600m, 0.0125m, orderLine: "3")
                .BuildXml())),

        new("06-no-purchase-order.xml",
            "Test Technik Handel GmbH refers to order PO-9998, which does not exist.",
            new DemoExpectation(InvoiceStatus.Exception, MatchOutcome.NoPurchaseOrder),
            Xml(new XRechnungBuilder
                {
                    InvoiceNumber = "DEMO-2026-0006", IssueDate = IssueDate, PurchaseOrderNumber = "PO-9998",
                    SellerName = "Test Technik Handel GmbH", SellerVatId = "DE000000005", Iban = "DE61000000000000001005",
                }
                .Line("HDMI cable 2 m", 40m, 7.4125m, orderLine: "1")
                .BuildXml())),

        new("07-unknown-bank-account.xml",
            "Order PO-0106 (Beispielhaft Werkzeuge OHG): the invoice asks for payment to a bank account nobody approved (possible fraud).",
            new DemoExpectation(InvoiceStatus.Exception, Finding: RuleCodes.IbanNotApproved), // a finding stops it before matching
            Xml(new XRechnungBuilder
                {
                    InvoiceNumber = "DEMO-2026-0007", IssueDate = IssueDate, PurchaseOrderNumber = "PO-0106",
                    SellerName = "Beispielhaft Werkzeuge OHG", SellerVatId = "DE000000011", Iban = UnknownIban,
                }
                .Line("Handbook German commercial code", 8m, 39.90m, taxRate: 7m, orderLine: "1")
                .Line("Copy paper A4, 500 sheets", 13m, 4.89m, orderLine: "2")
                .BuildXml())),

        new("08-unreadable.xml",
            "Looks like an invoice but breaks off in the middle: stored as Failed, the original is kept.",
            new DemoExpectation(InvoiceStatus.Failed),
            Unreadable()),
    ];

    /// <summary>Line ends are always "\n", so the files are the same bytes on Windows and on Linux (CI).</summary>
    private static byte[] Xml(string xml) => Encoding.UTF8.GetBytes(xml.Replace("\r\n", "\n", StringComparison.Ordinal));

    private static byte[] Unreadable()
    {
        var xml = Encoding.UTF8.GetString(Xml(new XRechnungBuilder { InvoiceNumber = "DEMO-2026-0008", IssueDate = IssueDate }
            .Line("Wireless mouse", 2m, 24.99m).BuildXml()));
        return Encoding.UTF8.GetBytes(xml[..(xml.Length / 2)]);
    }
}
