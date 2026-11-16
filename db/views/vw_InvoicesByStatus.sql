/*
  KPI: the work in each state. One row per status that has invoices.
  GrossTotalEur adds only euro amounts (limits and the books are in EUR); other currencies are counted, not summed.
  An invoice without amounts (for example Failed: unreadable) adds 0.
*/
CREATE OR ALTER VIEW dbo.vw_InvoicesByStatus
AS
SELECT
    i.Status                                          AS Status,
    COUNT(*)                                          AS InvoiceCount,
    CAST(COALESCE(SUM(CASE WHEN i.GrossTotal_Currency = 'EUR' THEN i.GrossTotal_Amount END), 0)
         AS decimal(18, 2))                           AS GrossTotalEur
FROM dbo.Invoice AS i
GROUP BY i.Status;
