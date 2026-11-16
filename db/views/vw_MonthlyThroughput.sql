/*
  KPI: how much runs without a person. One row per month of receipt, in Berlin time.

  Received is the invoice's first status change (Sequence 1). AT TIME ZONE 'W. Europe Standard Time' turns that moment
  into German local time, summer time included, so 31 Oct 23:30 UTC counts in November.
  "Ever" looks at the whole status history, not the status now:
    StraightThrough  reached Approved (6) and was never in Exception (5)
    Exceptions       was in Exception (5) at least once (also when it was matched again later)
    Failed           was Failed (9) at least once
*/
CREATE OR ALTER VIEW dbo.vw_MonthlyThroughput
AS
WITH Received AS
(
    SELECT
        c.InvoiceId,
        DATEFROMPARTS(YEAR(local.At), MONTH(local.At), 1) AS Month
    FROM dbo.InvoiceStatusChange AS c
    CROSS APPLY (SELECT c.At AT TIME ZONE 'W. Europe Standard Time' AS At) AS local
    WHERE c.Sequence = 1
),
History AS
(
    SELECT
        r.Month,
        r.InvoiceId,
        MAX(CASE WHEN s.ToStatus = 6 THEN 1 ELSE 0 END) AS EverApproved,
        MAX(CASE WHEN s.ToStatus = 5 THEN 1 ELSE 0 END) AS EverException,
        MAX(CASE WHEN s.ToStatus = 9 THEN 1 ELSE 0 END) AS EverFailed
    FROM Received AS r
    JOIN dbo.InvoiceStatusChange AS s ON s.InvoiceId = r.InvoiceId
    GROUP BY r.Month, r.InvoiceId
)
SELECT
    h.Month                                                             AS Month,
    COUNT(*)                                                            AS Received,
    SUM(CASE WHEN h.EverApproved = 1 AND h.EverException = 0 THEN 1 ELSE 0 END) AS StraightThrough,
    SUM(h.EverException)                                                AS Exceptions,
    SUM(h.EverFailed)                                                   AS Failed
FROM History AS h
GROUP BY h.Month;
