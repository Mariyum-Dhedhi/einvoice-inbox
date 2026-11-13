/*
  Synthetic seed data. No real companies, people, VAT numbers or bank accounts.
  Set-based: every table is filled with one INSERT ... SELECT over a number sequence, no row-by-row loops.
  Deterministic: the same ids and values on every run, so tests can refer to them.
  Idempotent: skipped when vendors already exist.

  Ids follow 00000000-0000-0000-KKKK-NNNNNNNNNNNN, where KKKK is the table and N the row number:
  0001 Vendor, 0002 PurchaseOrder, 0003 GoodsReceipt, 0004 Invoice, 0006 VendorBankAccount.

  Scenario for the match procedure (package 6):
    - Every 10th purchase order (PO-0010, PO-0020, ...) has no goods receipt.
    - Six invoices in status Parsed cover all five match outcomes (see the #Scenario table below).
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF EXISTS (SELECT 1 FROM dbo.Vendor)
BEGIN
    PRINT 'Seed data already present; skipping.';
    SET NOEXEC ON;
END;

BEGIN TRANSACTION;

-- 1..200
DROP TABLE IF EXISTS #N;
SELECT TOP (200) CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS int) AS n
INTO #N
FROM sys.all_objects;

-- Vendors: 20 fictional suppliers; V-1020 is inactive (for the validation rules in package 5).
INSERT INTO dbo.Vendor (Id, Number, Name, VatId, IsActive)
SELECT
    CONVERT(uniqueidentifier, CONCAT('00000000-0000-0000-0001-', RIGHT(CONCAT('000000000000', v.n), 12))),
    CONCAT('V-', 1000 + v.n),
    v.Name,
    CONCAT('DE000000', RIGHT(CONCAT('000', v.n), 3)), -- deliberately not a valid USt-IdNr.
    CASE WHEN v.n = 20 THEN 0 ELSE 1 END
FROM (VALUES
    (1, N'Beispiel Bürobedarf GmbH'), (2, N'Muster Möbelwerk AG'), (3, N'Fiktiv Elektro KG'),
    (4, N'Demo Druck & Papier GmbH'), (5, N'Test Technik Handel GmbH'), (6, N'Platzhalter Logistik GmbH'),
    (7, N'Probe Reinigungsdienst e.K.'), (8, N'Exempel IT-Service GmbH'), (9, N'Sample Office Supplies Ltd'),
    (10, N'Musterfrau Catering GmbH'), (11, N'Beispielhaft Werkzeuge OHG'), (12, N'Demo Kabel & Co. KG'),
    (13, N'Fiktiv Fachbuch Verlag GmbH'), (14, N'Testlauf Sicherheitstechnik GmbH'), (15, N'Probe Getränke GmbH'),
    (16, N'Vorlage Büromaschinen AG'), (17, N'Entwurf Gebäudeservice GmbH'), (18, N'Beispiel Energie GmbH'),
    (19, N'Muster Verpackung GmbH'), (20, N'Ehemals Lieferant GmbH')
) AS v(n, Name);

-- Approved bank accounts: one per vendor. Bank code 00000000 does not exist, so these can never be real accounts;
-- the check digits are computed so that each passes the IBAN mod-97 check.
INSERT INTO dbo.VendorBankAccount (Id, VendorId, Iban)
SELECT
    CONVERT(uniqueidentifier, CONCAT('00000000-0000-0000-0006-', RIGHT(CONCAT('000000000000', a.n), 12))),
    CONVERT(uniqueidentifier, CONCAT('00000000-0000-0000-0001-', RIGHT(CONCAT('000000000000', a.n), 12))),
    a.Iban
FROM (VALUES
    (1, 'DE72000000000000001001'), (2, 'DE45000000000000001002'), (3, 'DE18000000000000001003'), (4, 'DE88000000000000001004'),
    (5, 'DE61000000000000001005'), (6, 'DE34000000000000001006'), (7, 'DE07000000000000001007'), (8, 'DE77000000000000001008'),
    (9, 'DE50000000000000001009'), (10, 'DE23000000000000001010'), (11, 'DE93000000000000001011'), (12, 'DE66000000000000001012'),
    (13, 'DE39000000000000001013'), (14, 'DE12000000000000001014'), (15, 'DE82000000000000001015'), (16, 'DE55000000000000001016'),
    (17, 'DE28000000000000001017'), (18, 'DE98000000000000001018'), (19, 'DE71000000000000001019'), (20, 'DE44000000000000001020')
) AS a(n, Iban);

-- Item catalogue used for order lines. Prices with more than two decimals are intentional.
DROP TABLE IF EXISTS #Item;
CREATE TABLE #Item (ItemNo int PRIMARY KEY, ItemNumber nvarchar(50), Description nvarchar(500), UnitPrice decimal(18,6), TaxRatePercent decimal(5,2));
INSERT INTO #Item VALUES
    (1, N'PAPER-A4', N'Copy paper A4, 500 sheets', 4.89, 19),
    (2, N'TONER-K', N'Toner cartridge, black', 62.50, 19),
    (3, N'CHAIR-01', N'Office chair', 189.00, 19),
    (4, N'DESK-160', N'Desk 160 x 80 cm', 349.00, 19),
    (5, N'CABLE-HDMI', N'HDMI cable 2 m', 7.4125, 19),
    (6, N'BOOK-HGB', N'Handbook German commercial code', 39.90, 7),
    (7, N'MOUSE-W', N'Wireless mouse', 24.99, 19),
    (8, N'SCREW-M4', N'Screw M4 x 20, per piece', 0.0125, 19);

-- Purchase orders: 200, spread over vendors 1..19 (none for the inactive vendor).
INSERT INTO dbo.PurchaseOrder (Id, Number, VendorId, OrderDate, Currency)
SELECT
    CONVERT(uniqueidentifier, CONCAT('00000000-0000-0000-0002-', RIGHT(CONCAT('000000000000', n), 12))),
    CONCAT('PO-', RIGHT(CONCAT('0000', n), 4)),
    CONVERT(uniqueidentifier, CONCAT('00000000-0000-0000-0001-', RIGHT(CONCAT('000000000000', ((n - 1) % 19) + 1), 12))),
    DATEADD(DAY, -(30 + n % 60), CAST('2026-11-01' AS date)),
    'EUR'
FROM #N;

-- Order lines: 1 to 3 per order.
INSERT INTO dbo.PurchaseOrderLine (PurchaseOrderId, LineNumber, ItemNumber, Description, Quantity, UnitPrice, TaxRatePercent)
SELECT
    CONVERT(uniqueidentifier, CONCAT('00000000-0000-0000-0002-', RIGHT(CONCAT('000000000000', o.n), 12))),
    l.LineNumber,
    i.ItemNumber,
    i.Description,
    CASE WHEN i.ItemNo = 8 THEN 100 ELSE 1 END * (1 + (o.n * 7 + l.LineNumber * 5) % 20),
    i.UnitPrice,
    i.TaxRatePercent
FROM #N AS o
CROSS JOIN (VALUES (1), (2), (3)) AS l(LineNumber)
JOIN #Item AS i ON i.ItemNo = ((o.n + l.LineNumber * 3) % 8) + 1
WHERE l.LineNumber <= (o.n % 3) + 1;

-- Goods receipts: one per order, delivered in full five days after ordering, except every 10th order.
INSERT INTO dbo.GoodsReceipt (Id, Number, PurchaseOrderId, ReceivedDate)
SELECT
    CONVERT(uniqueidentifier, CONCAT('00000000-0000-0000-0003-', RIGHT(CONCAT('000000000000', po.n), 12))),
    CONCAT('GR-', RIGHT(CONCAT('0000', po.n), 4)),
    p.Id,
    DATEADD(DAY, 5, p.OrderDate)
FROM #N AS po
JOIN dbo.PurchaseOrder AS p ON p.Number = CONCAT('PO-', RIGHT(CONCAT('0000', po.n), 4))
WHERE po.n % 10 <> 0;

INSERT INTO dbo.GoodsReceiptLine (GoodsReceiptId, PurchaseOrderLineNumber, QuantityReceived)
SELECT g.Id, pl.LineNumber, pl.Quantity
FROM dbo.GoodsReceipt AS g
JOIN dbo.PurchaseOrderLine AS pl ON pl.PurchaseOrderId = g.PurchaseOrderId;

-- Invoices: one per match outcome. Lines are derived from the order lines with a price factor and extra quantity.
DROP TABLE IF EXISTS #Scenario;
CREATE TABLE #Scenario (n int PRIMARY KEY, InvoiceNumber nvarchar(50), SourcePo nvarchar(20), StatedPo nvarchar(20), PriceFactor decimal(9,4), ExtraQuantity decimal(18,4), Expected nvarchar(100));
INSERT INTO #Scenario VALUES
    (1, N'RE-2026-0001', N'PO-0001', N'PO-0001', 1.0000, 0, N'FullMatch'),
    (2, N'RE-2026-0002', N'PO-0002', N'PO-0002', 1.0150, 0, N'FullMatch (+1.5%, within tolerance)'),
    (3, N'RE-2026-0003', N'PO-0003', N'PO-0003', 1.0500, 0, N'PriceVariance (+5%)'),
    (4, N'RE-2026-0004', N'PO-0004', N'PO-0004', 1.0000, 2, N'QuantityVariance (2 more than received)'),
    (5, N'RE-2026-0005', N'PO-0010', N'PO-0010', 1.0000, 0, N'NoGoodsReceipt'),
    (6, N'RE-2026-0006', N'PO-0006', N'PO-9999', 1.0000, 0, N'NoPurchaseOrder (unknown order number)');

DROP TABLE IF EXISTS #InvoiceLine;
SELECT
    s.n,
    CONVERT(uniqueidentifier, CONCAT('00000000-0000-0000-0004-', RIGHT(CONCAT('000000000000', s.n), 12))) AS InvoiceId,
    p.VendorId,
    pl.LineNumber,
    pl.Description,
    pl.Quantity + s.ExtraQuantity AS Quantity,
    ROUND(pl.UnitPrice * s.PriceFactor, 6) AS UnitPrice,
    pl.TaxRatePercent
INTO #InvoiceLine
FROM #Scenario AS s
JOIN dbo.PurchaseOrder AS p ON p.Number = s.SourcePo
JOIN dbo.PurchaseOrderLine AS pl ON pl.PurchaseOrderId = p.Id;

INSERT INTO dbo.Invoice (Id, SourceFormat, SubmittedBy, Status, InvoiceNumber, VendorId, IssueDate, PurchaseOrderNumber,
                         NetTotal_Amount, NetTotal_Currency, TaxTotal_Amount, TaxTotal_Currency, GrossTotal_Amount, GrossTotal_Currency)
SELECT
    t.InvoiceId, 1, N'clerk@demo.local', 2, s.InvoiceNumber, t.VendorId, CAST('2026-11-02' AS date), s.StatedPo,
    t.Net, 'EUR', t.Tax, 'EUR', t.Net + t.Tax, 'EUR'
FROM #Scenario AS s
JOIN (
    SELECT n, InvoiceId, VendorId,
           SUM(ROUND(Quantity * UnitPrice, 2)) AS Net,
           SUM(ROUND(ROUND(Quantity * UnitPrice, 2) * TaxRatePercent / 100, 2)) AS Tax
    FROM #InvoiceLine
    GROUP BY n, InvoiceId, VendorId
) AS t ON t.n = s.n;

INSERT INTO dbo.InvoiceLine (InvoiceId, LineNumber, Description, Quantity, UnitPrice, NetAmount_Amount, NetAmount_Currency, TaxRatePercent, PurchaseOrderLineNumber)
SELECT InvoiceId, LineNumber, Description, Quantity, UnitPrice, ROUND(Quantity * UnitPrice, 2), 'EUR', TaxRatePercent, LineNumber
FROM #InvoiceLine;

-- Status history: Received by the clerk, then Parsed by the system.
INSERT INTO dbo.InvoiceStatusChange (InvoiceId, Sequence, FromStatus, ToStatus, Actor, At, Reason)
SELECT
    CONVERT(uniqueidentifier, CONCAT('00000000-0000-0000-0004-', RIGHT(CONCAT('000000000000', s.n), 12))),
    h.Step,
    h.FromStatus, h.ToStatus, h.Actor,
    DATEADD(MINUTE, h.Step, CAST('2026-11-02T08:00:00+00:00' AS datetimeoffset)),
    NULL
FROM #Scenario AS s
CROSS JOIN (VALUES (1, NULL, 1, N'clerk@demo.local'), (2, 1, 2, N'system')) AS h(Step, FromStatus, ToStatus, Actor);

COMMIT TRANSACTION;

SELECT 'Vendor' AS [Table], COUNT(*) AS [Rows] FROM dbo.Vendor
UNION ALL SELECT 'VendorBankAccount', COUNT(*) FROM dbo.VendorBankAccount
UNION ALL SELECT 'PurchaseOrder', COUNT(*) FROM dbo.PurchaseOrder
UNION ALL SELECT 'PurchaseOrderLine', COUNT(*) FROM dbo.PurchaseOrderLine
UNION ALL SELECT 'GoodsReceipt', COUNT(*) FROM dbo.GoodsReceipt
UNION ALL SELECT 'GoodsReceiptLine', COUNT(*) FROM dbo.GoodsReceiptLine
UNION ALL SELECT 'Invoice', COUNT(*) FROM dbo.Invoice
UNION ALL SELECT 'InvoiceLine', COUNT(*) FROM dbo.InvoiceLine;

SET NOEXEC OFF;
