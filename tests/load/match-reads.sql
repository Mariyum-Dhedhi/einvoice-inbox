/*
  Pages read by one three-way match: runs dbo.usp_MatchInvoice for the seeded invoice RE-2026-0001 (its own order,
  PO-0001) with STATISTICS IO and TIME, inside a transaction that is rolled back. Run it three times and quote the
  last (warm cache). See tests/load/README.md.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> N'EInvoiceLoad'
BEGIN
    RAISERROR(N'match-reads.sql runs only in the database EInvoiceLoad.', 16, 1);
    SET NOEXEC ON;
END;

SELECT COUNT(*) AS Invoices,
       CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Invoice_PurchaseOrderNumber') THEN 'yes' ELSE 'no' END AS PurchaseOrderIndex
FROM dbo.Invoice;

BEGIN TRANSACTION;
SET STATISTICS IO, TIME ON;
EXEC dbo.usp_MatchInvoice @InvoiceId = '00000000-0000-0000-0004-000000000001', @PriceTolerancePercent = 2.00;
SET STATISTICS IO, TIME OFF;
ROLLBACK TRANSACTION;

SET NOEXEC OFF;
