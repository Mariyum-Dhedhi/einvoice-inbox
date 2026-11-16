/*
  What usp_MatchInvoice cost per run during a load test, from SQL Server's own counters (sys.dm_exec_procedure_stats).
  Run with -v Step=clear before the load test (empties this database's plan cache, so the counters start at zero)
  and with -v Step=report after it. See tests/load/README.md.
*/
SET NOCOUNT ON;

IF DB_NAME() <> N'EInvoiceLoad'
BEGIN
    RAISERROR(N'match-stats.sql runs only in the database EInvoiceLoad.', 16, 1);
    SET NOEXEC ON;
END;

IF '$(Step)' = 'clear'
    ALTER DATABASE SCOPED CONFIGURATION CLEAR PROCEDURE_CACHE;

IF '$(Step)' = 'report'
    SELECT
        s.execution_count AS Matches,
        CAST(s.total_logical_reads * 1.0 / s.execution_count AS decimal(12,1)) AS PagesPerMatch,
        CAST(s.total_worker_time / 1000.0 / s.execution_count AS decimal(12,2)) AS CpuMsPerMatch,
        CAST(s.total_elapsed_time / 1000.0 / s.execution_count AS decimal(12,2)) AS ElapsedMsPerMatch,
        (SELECT COUNT(*) FROM dbo.Invoice) AS Invoices,
        CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Invoice_PurchaseOrderNumber') THEN 'yes' ELSE 'no' END AS PurchaseOrderIndex
    FROM sys.dm_exec_procedure_stats AS s
    WHERE s.database_id = DB_ID() AND s.object_id = OBJECT_ID(N'dbo.usp_MatchInvoice');

SET NOEXEC OFF;
