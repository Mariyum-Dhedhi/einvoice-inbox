/*
  Puts the load database into one of the states docs/performance.md compares (-v State=...):
    before     as before package 15a: no purchase-order index, no invoice-line index, reads wait for writers
    po-index   only IX_Invoice_PurchaseOrderNumber (the first fix; the load test then found the deadlock)
    after      as the migrations and db/database.sql build it
  Only in EInvoiceLoad. Changing READ_COMMITTED_SNAPSHOT needs the database to itself: stop the app first.
*/
SET NOCOUNT ON;

IF DB_NAME() <> N'EInvoiceLoad' OR '$(State)' NOT IN ('before', 'po-index', 'after')
BEGIN
    RAISERROR(N'state.sql runs only in EInvoiceLoad, with State before, po-index or after.', 16, 1);
    SET NOEXEC ON;
END;

IF '$(State)' = 'before' AND EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Invoice_PurchaseOrderNumber')
    DROP INDEX IX_Invoice_PurchaseOrderNumber ON dbo.Invoice;
IF '$(State)' <> 'before' AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Invoice_PurchaseOrderNumber')
    CREATE INDEX IX_Invoice_PurchaseOrderNumber ON dbo.Invoice (PurchaseOrderNumber) INCLUDE (Id, Status);

IF '$(State)' <> 'after' AND EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_InvoiceLine_Quantities')
    DROP INDEX IX_InvoiceLine_Quantities ON dbo.InvoiceLine;
IF '$(State)' = 'after' AND NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_InvoiceLine_Quantities')
    CREATE INDEX IX_InvoiceLine_Quantities ON dbo.InvoiceLine (InvoiceId, LineNumber) INCLUDE (Quantity, PurchaseOrderLineNumber);

IF '$(State)' = 'after'
    ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;
ELSE
    ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT OFF WITH ROLLBACK IMMEDIATE;

SELECT
    (SELECT COUNT(*) FROM sys.indexes WHERE name = N'IX_Invoice_PurchaseOrderNumber') AS PurchaseOrderIndex,
    (SELECT COUNT(*) FROM sys.indexes WHERE name = N'IX_InvoiceLine_Quantities') AS InvoiceLineIndex,
    (SELECT is_read_committed_snapshot_on FROM sys.databases WHERE database_id = DB_ID()) AS SnapshotReads,
    (SELECT COUNT(*) FROM dbo.Invoice) AS Invoices,
    (SELECT COUNT(*) FROM dbo.Invoice WHERE PurchaseOrderNumber = N'PO-0101') AS OnPO0101;

SET NOEXEC OFF;
