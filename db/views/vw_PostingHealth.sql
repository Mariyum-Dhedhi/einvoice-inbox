/*
  KPI: is the ERP connection healthy? Always exactly one row: an aggregate without GROUP BY returns one row even when
  dbo.PostingJob is empty, and COUNT gives 0, never NULL. Status: 1 Pending, 2 Done, 3 Dead.
  OldestWaitingSince is the oldest job still waiting (first try or retrying); NULL when nothing waits.
*/
CREATE OR ALTER VIEW dbo.vw_PostingHealth
AS
SELECT
    COUNT(CASE WHEN j.Status = 1 AND j.Attempts = 0 THEN 1 END)  AS WaitingToPost,
    COUNT(CASE WHEN j.Status = 1 AND j.Attempts > 0 THEN 1 END)  AS Retrying,
    COUNT(CASE WHEN j.Status = 3 THEN 1 END)                     AS Failed,
    COUNT(CASE WHEN j.Status = 2 THEN 1 END)                     AS Posted,
    MIN(CASE WHEN j.Status = 1 THEN j.CreatedAt END)             AS OldestWaitingSince
FROM dbo.PostingJob AS j;
