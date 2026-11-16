/*
  KPI: what blocks the exception queue. For invoices now in Exception (5): each finding's rule code and each match
  outcome other than FullMatch (1), with the number of invoices that have it (an invoice counts once per reason, even
  when the reason appears on several of its lines).
  Match outcomes are named as the application names them (EInvoice.Domain.Matching.MatchOutcome); an outcome this view
  does not know yet shows as "Outcome<n>" instead of disappearing, and a test notices the gap.
*/
CREATE OR ALTER VIEW dbo.vw_OpenExceptionReasons
AS
SELECT
    CAST('Finding' AS varchar(10))   AS Kind,
    CAST(f.RuleCode AS varchar(40))  AS Reason,
    COUNT(DISTINCT f.InvoiceId)      AS OpenInvoices
FROM dbo.InvoiceFinding AS f
JOIN dbo.Invoice AS i ON i.Id = f.InvoiceId
WHERE i.Status = 5
GROUP BY f.RuleCode

UNION ALL

SELECT
    CAST('Match' AS varchar(10)),
    CAST(CASE m.Outcome
             WHEN 2 THEN 'PriceVariance'
             WHEN 3 THEN 'QuantityVariance'
             WHEN 4 THEN 'NoPurchaseOrder'
             WHEN 5 THEN 'NoGoodsReceipt'
             ELSE CONCAT('Outcome', m.Outcome)
         END AS varchar(40)),
    COUNT(DISTINCT m.InvoiceId)
FROM dbo.MatchResult AS m
JOIN dbo.Invoice AS i ON i.Id = m.InvoiceId
WHERE i.Status = 5
  AND m.Outcome <> 1
GROUP BY m.Outcome;
