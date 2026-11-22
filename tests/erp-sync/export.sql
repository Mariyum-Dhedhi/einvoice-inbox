-- The app's records from the ERP as canonical lines, the same as the mock ERP's GET _mock/master-data/export:
-- V|erpId|number|name|vatId|Active/Blocked, O|erpId|number|vendorErpId|date|currency|Open/Closed,
-- L|orderErpId|line|item|description|quantity|unitPrice|taxRate, R|erpId|number|orderErpId|date, RL|receiptErpId|line|quantity.
-- Amounts at the columns' scale (4, 6, 2 decimals). Only records from the ERP (Source = 2). No order: the caller sorts.
SET NOCOUNT ON;
SELECT CONCAT('V|', v.ErpId, '|', v.Number, '|', v.Name, '|', ISNULL(v.VatId, ''), '|', CASE WHEN v.IsActive = 1 THEN 'Active' ELSE 'Blocked' END)
FROM dbo.Vendor v WHERE v.Source = 2
UNION ALL
SELECT CONCAT('O|', o.ErpId, '|', o.Number, '|', v.ErpId, '|', CONVERT(char(10), o.OrderDate, 23), '|', o.Currency, '|', CASE o.Status WHEN 1 THEN 'Open' ELSE 'Closed' END)
FROM dbo.PurchaseOrder o JOIN dbo.Vendor v ON v.Id = o.VendorId WHERE o.Source = 2
UNION ALL
SELECT CONCAT('L|', o.ErpId, '|', l.LineNumber, '|', l.ItemNumber, '|', l.Description, '|', CONVERT(varchar(40), l.Quantity), '|', CONVERT(varchar(40), l.UnitPrice), '|', CONVERT(varchar(40), l.TaxRatePercent))
FROM dbo.PurchaseOrderLine l JOIN dbo.PurchaseOrder o ON o.Id = l.PurchaseOrderId WHERE o.Source = 2
UNION ALL
SELECT CONCAT('R|', g.ErpId, '|', g.Number, '|', o.ErpId, '|', CONVERT(char(10), g.ReceivedDate, 23))
FROM dbo.GoodsReceipt g JOIN dbo.PurchaseOrder o ON o.Id = g.PurchaseOrderId WHERE g.Source = 2
UNION ALL
SELECT CONCAT('RL|', g.ErpId, '|', l.PurchaseOrderLineNumber, '|', CONVERT(varchar(40), l.QuantityReceived))
FROM dbo.GoodsReceiptLine l JOIN dbo.GoodsReceipt g ON g.Id = l.GoodsReceiptId WHERE g.Source = 2;
