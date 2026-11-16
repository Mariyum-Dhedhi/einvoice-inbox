/*
  Volume for the load test: $(Invoices) synthetic invoices from the past year, as they would pile up in a year of use.
  Only for the separate load database (EInvoiceLoad, see tests/load/README.md); never for development or production.

  Set-based like db/seed/seed.sql; ids 00000000-0000-0000-0009-NNNNNNNNNNNN (0009 = load volume).
  Every invoice has a document, one line, its status history and, after matching, its match result:
    70 % Posted, 5 % Approved, 5 % Matched (waiting), 5 % Rejected, 10 % Exception, 5 % Failed (unreadable, no data).
  Each refers to its own historic order (PO-H000001, ...): those orders are not in the database, the invoices only
  have to be there, as old invoices are, when matching looks for other invoices of the same order.

  Not written: audit events (the hash chain is appended only through the application), document content (a short
  placeholder: no query under test reads it).
  Vendor 6 is left out on purpose: the load test uploads for vendor 6, and its invoices must not meet a volume
  invoice with the same amount and date (POSSIBLE_DUPLICATE would stop them before matching).

  Skipped when volume invoices already exist.
*/
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DB_NAME() <> N'EInvoiceLoad'
BEGIN
    RAISERROR(N'volume.sql runs only in the database EInvoiceLoad.', 16, 1);
    SET NOEXEC ON;
END;

IF EXISTS (SELECT 1 FROM dbo.Invoice WHERE InvoiceNumber LIKE N'VOL-%')
BEGIN
    PRINT 'Volume already present; skipping.';
    SET NOEXEC ON;
END;

DECLARE @Count int = $(Invoices);
DECLARE @Newest datetimeoffset = '2026-11-13T08:00:00+00:00';

DROP TABLE IF EXISTS #V;
SELECT
    n,
    CONVERT(uniqueidentifier, CONCAT('00000000-0000-0000-0009-', RIGHT(CONCAT('000000000000', n), 12))) AS Id,
    -- Path through the statuses, by n % 20: see the header.
    CASE WHEN n % 20 < 14 THEN 8 WHEN n % 20 = 14 THEN 6 WHEN n % 20 = 15 THEN 4 WHEN n % 20 = 16 THEN 7
         WHEN n % 20 < 19 THEN 5 ELSE 9 END AS Status,
    -- Vendors 1..19 without 6 (see the header); 20 is inactive.
    CONVERT(uniqueidentifier, CONCAT('00000000-0000-0000-0001-',
        RIGHT(CONCAT('000000000000', CASE WHEN (n - 1) % 18 + 1 >= 6 THEN (n - 1) % 18 + 2 ELSE (n - 1) % 18 + 1 END), 12))) AS VendorId,
    -- Spread evenly over the year before @Newest, newest last (higher n, later).
    DATEADD(SECOND, -CAST((CAST(@Count - n AS bigint) * 31536000) / @Count AS int), @Newest) AS ReceivedAt,
    CAST(1 + n % 50 AS decimal(18,4)) AS Quantity,
    CAST(4.89 + (n % 97) * 1.25 AS decimal(18,6)) AS UnitPrice
INTO #V
FROM (
    SELECT TOP (@Count) CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS int) AS n
    FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b CROSS JOIN sys.all_objects AS c
) AS numbers;

BEGIN TRANSACTION;

-- In order of n, so RowId (the inbox order: newest first) follows the time of receipt.
INSERT INTO dbo.Invoice (Id, SourceFormat, SubmittedBy, Status, InvoiceNumber, VendorId, IssueDate, PurchaseOrderNumber,
                         NetTotal_Amount, NetTotal_Currency, TaxTotal_Amount, TaxTotal_Currency, GrossTotal_Amount, GrossTotal_Currency)
SELECT
    v.Id, 1, N'clerk@example.com', v.Status,
    CASE WHEN v.Status = 9 THEN NULL ELSE CONCAT(N'VOL-', v.n) END,
    CASE WHEN v.Status = 9 THEN NULL ELSE v.VendorId END,
    CASE WHEN v.Status = 9 THEN NULL ELSE DATEADD(DAY, -2, CAST(v.ReceivedAt AS date)) END,
    CASE WHEN v.Status = 9 THEN NULL ELSE CONCAT(N'PO-H', RIGHT(CONCAT('000000', v.n), 6)) END,
    CASE WHEN v.Status = 9 THEN NULL ELSE t.Net END, CASE WHEN v.Status = 9 THEN NULL ELSE 'EUR' END,
    CASE WHEN v.Status = 9 THEN NULL ELSE t.Tax END, CASE WHEN v.Status = 9 THEN NULL ELSE 'EUR' END,
    CASE WHEN v.Status = 9 THEN NULL ELSE t.Net + t.Tax END, CASE WHEN v.Status = 9 THEN NULL ELSE 'EUR' END
FROM #V AS v
CROSS APPLY (SELECT ROUND(v.Quantity * v.UnitPrice, 2) AS Net, ROUND(ROUND(v.Quantity * v.UnitPrice, 2) * 0.19, 2) AS Tax) AS t
ORDER BY v.n;

INSERT INTO dbo.InvoiceDocument (InvoiceId, FileName, ContentType, Content, Sha256, SizeBytes, ReceivedAt)
SELECT v.Id, CONCAT(N'VOL-', v.n, N'.xml'), 'application/xml', d.Content, HASHBYTES('SHA2_256', d.Content), DATALENGTH(d.Content), v.ReceivedAt
FROM #V AS v
CROSS APPLY (SELECT CAST(CONCAT('<!-- load volume ', v.n, ' -->') AS varbinary(max)) AS Content) AS d
ORDER BY v.n;

INSERT INTO dbo.InvoiceLine (InvoiceId, LineNumber, Description, Quantity, UnitPrice, NetAmount_Amount, NetAmount_Currency, TaxRatePercent, PurchaseOrderLineNumber)
SELECT v.Id, 1, N'Copy paper A4, 500 sheets', v.Quantity, v.UnitPrice, ROUND(v.Quantity * v.UnitPrice, 2), 'EUR', 19, 1
FROM #V AS v
WHERE v.Status <> 9
ORDER BY v.n;

-- Status history: the steps of each path, a few minutes apart (Posted a day later).
INSERT INTO dbo.InvoiceStatusChange (InvoiceId, Sequence, FromStatus, ToStatus, Actor, At, Reason)
SELECT v.Id, s.Sequence, s.FromStatus, s.ToStatus, s.Actor, DATEADD(MINUTE, s.Minutes, v.ReceivedAt), s.Reason
FROM #V AS v
JOIN (VALUES
    -- Status, Sequence, FromStatus, ToStatus, Actor, Minutes, Reason
    (8, 1, NULL, 1, N'clerk@example.com', 0, NULL), (8, 2, 1, 2, N'system', 0, NULL), (8, 3, 2, 3, N'system', 0, NULL),
    (8, 4, 3, 4, N'system', 0, NULL), (8, 5, 4, 6, N'approver@example.com', 30, NULL), (8, 6, 6, 8, N'erp-connector', 1440, NULL),
    (6, 1, NULL, 1, N'clerk@example.com', 0, NULL), (6, 2, 1, 2, N'system', 0, NULL), (6, 3, 2, 3, N'system', 0, NULL),
    (6, 4, 3, 4, N'system', 0, NULL), (6, 5, 4, 6, N'approver@example.com', 30, NULL),
    (4, 1, NULL, 1, N'clerk@example.com', 0, NULL), (4, 2, 1, 2, N'system', 0, NULL), (4, 3, 2, 3, N'system', 0, NULL),
    (4, 4, 3, 4, N'system', 0, NULL),
    (7, 1, NULL, 1, N'clerk@example.com', 0, NULL), (7, 2, 1, 2, N'system', 0, NULL), (7, 3, 2, 3, N'system', 0, NULL),
    (7, 4, 3, 5, N'system', 0, N'PriceVariance'), (7, 5, 5, 7, N'approver@example.com', 60, N'Price not agreed'),
    (5, 1, NULL, 1, N'clerk@example.com', 0, NULL), (5, 2, 1, 2, N'system', 0, NULL), (5, 3, 2, 3, N'system', 0, NULL),
    (5, 4, 3, 5, N'system', 0, N'PriceVariance'),
    (9, 1, NULL, 1, N'clerk@example.com', 0, NULL), (9, 2, 1, 9, N'system', 0, N'NotXml: the document is not well-formed XML.')
) AS s(Status, Sequence, FromStatus, ToStatus, Actor, Minutes, Reason) ON s.Status = v.Status
ORDER BY v.n, s.Sequence;

-- Match results: full match on the approved path, price variance (+5 %) on the exception path.
INSERT INTO dbo.MatchResult (InvoiceId, InvoiceLineNumber, PurchaseOrderLineNumber, Outcome, InvoiceUnitPrice, OrderUnitPrice,
                             PriceVariancePercent, PriceVarianceAmount, Currency, QuantityInvoiced, QuantityAvailable,
                             QuantityVariance, PriceTolerancePercent, MatchedAt)
SELECT v.Id, 1, 1, m.Outcome, v.UnitPrice, ROUND(v.UnitPrice / m.Factor, 6),
       CASE WHEN m.Outcome = 1 THEN 0 ELSE 5 END,
       ROUND((v.UnitPrice - ROUND(v.UnitPrice / m.Factor, 6)) * v.Quantity, 2),
       'EUR', v.Quantity, v.Quantity, 0, 2.00, v.ReceivedAt
FROM #V AS v
CROSS APPLY (SELECT CASE WHEN v.Status IN (5, 7) THEN 2 ELSE 1 END AS Outcome,
                    CASE WHEN v.Status IN (5, 7) THEN 1.05 ELSE 1.00 END AS Factor) AS m
WHERE v.Status IN (4, 5, 6, 7, 8)
ORDER BY v.n;

COMMIT TRANSACTION;

SELECT Status, COUNT(*) AS Invoices FROM dbo.Invoice GROUP BY Status ORDER BY Status;

SET NOEXEC OFF;
