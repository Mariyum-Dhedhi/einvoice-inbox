/*
  Run as the application user (einvoice_app) after db-init. Fails (THROW) on the first broken expectation.
  Checks the seed counts and that the security controls hold: the app user cannot change the schema
  or the migration history, and the database itself rejects a duplicate invoice number.
*/
SET NOCOUNT ON;
SET XACT_ABORT OFF;

-- Seed counts
IF (SELECT COUNT(*) FROM dbo.Vendor) <> 20 THROW 50001, 'Expected 20 vendors.', 1;
IF (SELECT COUNT(*) FROM dbo.PurchaseOrder) <> 200 THROW 50002, 'Expected 200 purchase orders.', 1;
IF (SELECT COUNT(*) FROM dbo.VendorBankAccount) <> 20 THROW 50006, 'Expected 20 vendor bank accounts.', 1;
IF (SELECT COUNT(*) FROM dbo.GoodsReceipt) <> 180 THROW 50003, 'Expected 180 goods receipts (every 10th order has none).', 1;
IF (SELECT COUNT(*) FROM dbo.Invoice) <> 6 THROW 50004, 'Expected 6 seeded invoices.', 1;
IF EXISTS (SELECT 1 FROM dbo.Invoice WHERE GrossTotal_Amount <> NetTotal_Amount + TaxTotal_Amount)
    THROW 50005, 'Seeded invoice totals do not add up.', 1;
PRINT 'OK: seed counts and totals';

-- The app user cannot create tables
BEGIN TRY
    EXEC (N'CREATE TABLE dbo.ShouldNotExist (Id int);');
    THROW 50101, 'SECURITY: app user was able to create a table.', 1;
END TRY
BEGIN CATCH
    IF ERROR_NUMBER() = 50101 THROW;
    PRINT 'OK: app user cannot create tables';
END CATCH;

-- The app user cannot drop tables
BEGIN TRY
    EXEC (N'DROP TABLE dbo.Vendor;');
    THROW 50102, 'SECURITY: app user was able to drop a table.', 1;
END TRY
BEGIN CATCH
    IF ERROR_NUMBER() = 50102 THROW;
    PRINT 'OK: app user cannot drop tables';
END CATCH;

-- The app user cannot rewrite migration history
BEGIN TRY
    DELETE FROM dbo.__EFMigrationsHistory;
    THROW 50103, 'SECURITY: app user was able to delete migration history.', 1;
END TRY
BEGIN CATCH
    IF ERROR_NUMBER() = 50103 THROW;
    PRINT 'OK: app user cannot change migration history';
END CATCH;

-- The database rejects a second invoice with the same vendor and number (unique index), even when rejected
BEGIN TRY
    BEGIN TRANSACTION;
    INSERT INTO dbo.Invoice (Id, SourceFormat, SubmittedBy, Status, InvoiceNumber, VendorId)
    SELECT NEWID(), 1, N'verify', 7, InvoiceNumber, VendorId FROM dbo.Invoice WHERE InvoiceNumber = N'RE-2026-0001';
    ROLLBACK TRANSACTION;
    THROW 50104, 'Duplicate vendor + invoice number was accepted.', 1;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    IF ERROR_NUMBER() = 50104 THROW;
    IF ERROR_NUMBER() <> 2601 THROW; -- 2601 = duplicate key in unique index
    PRINT 'OK: duplicate invoice number rejected by UX_Invoice_Vendor_InvoiceNumber';
END CATCH;

-- CHECK constraint rejects an unknown status
BEGIN TRY
    BEGIN TRANSACTION;
    INSERT INTO dbo.Invoice (Id, SourceFormat, SubmittedBy, Status) VALUES (NEWID(), 1, N'verify', 42);
    ROLLBACK TRANSACTION;
    THROW 50105, 'Unknown status 42 was accepted.', 1;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    IF ERROR_NUMBER() = 50105 THROW;
    IF ERROR_NUMBER() <> 547 THROW; -- 547 = constraint violation
    PRINT 'OK: unknown status rejected by CK_Invoice_Status';
END CATCH;

-- The app user can run the match procedure ...
DECLARE @Seeded uniqueidentifier = (SELECT Id FROM dbo.Invoice WHERE InvoiceNumber = N'RE-2026-0003');
BEGIN TRANSACTION;
DECLARE @Results TABLE (InvoiceLineNumber int, PurchaseOrderLineNumber int, Outcome int, InvoiceUnitPrice decimal(18,6), OrderUnitPrice decimal(18,6),
    PriceVariancePercent decimal(19,6), PriceVarianceAmount decimal(18,2), Currency char(3), QuantityInvoiced decimal(18,4),
    QuantityAvailable decimal(18,4), QuantityVariance decimal(18,4));
INSERT INTO @Results EXEC dbo.usp_MatchInvoice @InvoiceId = @Seeded, @PriceTolerancePercent = 2.00;
ROLLBACK TRANSACTION;
IF NOT EXISTS (SELECT 1 FROM @Results) OR EXISTS (SELECT 1 FROM @Results WHERE Outcome <> 2)
    THROW 50106, 'usp_MatchInvoice did not report the seeded price variance.', 1;
PRINT 'OK: app user can execute usp_MatchInvoice (RE-2026-0003 -> PriceVariance)';

-- ... but cannot change it
BEGIN TRY
    EXEC (N'CREATE OR ALTER PROCEDURE dbo.usp_MatchInvoice AS SELECT 1;');
    THROW 50107, 'SECURITY: app user was able to alter usp_MatchInvoice.', 1;
END TRY
BEGIN CATCH
    IF ERROR_NUMBER() = 50107 THROW;
    PRINT 'OK: app user cannot alter stored procedures';
END CATCH;

PRINT 'All database checks passed.';
