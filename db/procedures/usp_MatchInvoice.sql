/*
  dbo.usp_MatchInvoice: three-way match of one invoice (invoice line vs order line vs goods received).

  Set-based: one INSERT ... SELECT matches every line of the invoice. No cursor, no loop.
  The rules are the same as the C# ThreeWayMatcher (src/EInvoice.Domain/Matching); integration tests run the
  same data through both and compare every field, so the two cannot drift apart.

  Rules, in order of precedence (first match wins):
    4 NoPurchaseOrder   the stated order, or the referenced order line, does not exist
    5 NoGoodsReceipt    nothing has been received for that order line
    3 QuantityVariance  invoiced more than received minus invoiced elsewhere minus earlier lines of this invoice
    2 PriceVariance     unit price more than @PriceTolerancePercent above the order price (lower is accepted)
    1 FullMatch

  "Invoiced elsewhere" counts every other invoice for the same order line except rejected ones, so the result
  does not depend on the order in which invoices are matched.

  Results replace earlier results for the invoice; running it twice gives the same rows.
*/
CREATE OR ALTER PROCEDURE dbo.usp_MatchInvoice
    @InvoiceId             uniqueidentifier,
    @PriceTolerancePercent decimal(5, 2)       -- from configuration (MatchingPolicy), never hard-coded
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;  -- any error rolls the whole match back

    IF @PriceTolerancePercent IS NULL OR @PriceTolerancePercent < 0 OR @PriceTolerancePercent > 100
        THROW 50201, N'@PriceTolerancePercent must be between 0 and 100.', 1;

    DECLARE @PurchaseOrderNumber nvarchar(20);
    SELECT @PurchaseOrderNumber = PurchaseOrderNumber FROM dbo.Invoice WHERE Id = @InvoiceId;
    IF @@ROWCOUNT = 0
        THROW 50202, N'Invoice not found.', 1;

    BEGIN TRANSACTION;

    -- One match per purchase order at a time: two invoices for the same order are matched one after the other.
    IF @PurchaseOrderNumber IS NOT NULL
    BEGIN
        DECLARE @LockResource nvarchar(255) = CONCAT(N'usp_MatchInvoice:', @PurchaseOrderNumber);
        DECLARE @LockResult int;
        EXEC @LockResult = sys.sp_getapplock
            @Resource = @LockResource, @LockMode = N'Exclusive', @LockOwner = N'Transaction', @LockTimeout = 10000;
        IF @LockResult < 0
            THROW 50203, N'Timed out waiting for another match on the same purchase order.', 1;
    END;

    DELETE FROM dbo.MatchResult WHERE InvoiceId = @InvoiceId;

    WITH OrderLines AS (
        -- The lines of the order the invoice refers to (none if the number is unknown).
        SELECT pol.PurchaseOrderId, pol.LineNumber, pol.UnitPrice
        FROM dbo.PurchaseOrder AS po
        JOIN dbo.PurchaseOrderLine AS pol ON pol.PurchaseOrderId = po.Id
        WHERE po.Number = @PurchaseOrderNumber
    ),
    Received AS (
        -- Total received per order line, over all goods receipts (partial deliveries add up).
        SELECT grl.PurchaseOrderLineNumber, SUM(grl.QuantityReceived) AS Quantity
        FROM dbo.PurchaseOrder AS po
        JOIN dbo.GoodsReceipt AS gr ON gr.PurchaseOrderId = po.Id
        JOIN dbo.GoodsReceiptLine AS grl ON grl.GoodsReceiptId = gr.Id
        WHERE po.Number = @PurchaseOrderNumber
        GROUP BY grl.PurchaseOrderLineNumber
    ),
    InvoicedElsewhere AS (
        -- Quantity billed for each order line on every OTHER invoice, except rejected ones (status 7).
        SELECT COALESCE(il.PurchaseOrderLineNumber, il.LineNumber) AS PurchaseOrderLineNumber, SUM(il.Quantity) AS Quantity
        FROM dbo.Invoice AS i
        JOIN dbo.InvoiceLine AS il ON il.InvoiceId = i.Id
        WHERE i.PurchaseOrderNumber = @PurchaseOrderNumber
          AND i.Id <> @InvoiceId
          AND i.Status <> 7
        GROUP BY COALESCE(il.PurchaseOrderLineNumber, il.LineNumber)
    ),
    InvoiceLines AS (
        -- The order line a line refers to: the one the supplier stated, otherwise the same line number.
        -- ClaimedByEarlierLines is a running total (window function) of what earlier lines of THIS invoice
        -- already billed for the same order line, so two lines cannot bill one delivery twice.
        SELECT il.LineNumber,
               il.Quantity,
               il.UnitPrice,
               il.NetAmount_Currency AS Currency,
               COALESCE(il.PurchaseOrderLineNumber, il.LineNumber) AS ReferencedLine,
               COALESCE(SUM(il.Quantity) OVER (
                   PARTITION BY COALESCE(il.PurchaseOrderLineNumber, il.LineNumber)
                   ORDER BY il.LineNumber
                   ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING), 0) AS ClaimedByEarlierLines
        FROM dbo.InvoiceLine AS il
        WHERE il.InvoiceId = @InvoiceId
    ),
    Facts AS (
        SELECT l.LineNumber,
               l.Quantity,
               l.UnitPrice,
               l.Currency,
               ol.LineNumber                  AS OrderLineNumber,
               ol.UnitPrice                   AS OrderUnitPrice,
               COALESCE(r.Quantity, 0)        AS Received,
               l.UnitPrice - ol.UnitPrice     AS PriceDifference,
               CASE WHEN ol.LineNumber IS NOT NULL
                    THEN GREATEST(0, COALESCE(r.Quantity, 0) - COALESCE(e.Quantity, 0) - l.ClaimedByEarlierLines)
               END                            AS Available
        FROM InvoiceLines AS l
        LEFT JOIN OrderLines AS ol ON ol.LineNumber = l.ReferencedLine
        LEFT JOIN Received AS r ON r.PurchaseOrderLineNumber = ol.LineNumber
        LEFT JOIN InvoicedElsewhere AS e ON e.PurchaseOrderLineNumber = ol.LineNumber
    )
    INSERT INTO dbo.MatchResult
        (InvoiceId, InvoiceLineNumber, PurchaseOrderLineNumber, Outcome, InvoiceUnitPrice, OrderUnitPrice,
         PriceVariancePercent, PriceVarianceAmount, Currency, QuantityInvoiced, QuantityAvailable, QuantityVariance,
         PriceTolerancePercent, MatchedAt)
    SELECT
        @InvoiceId,
        f.LineNumber,
        f.OrderLineNumber,
        CASE
            WHEN f.OrderLineNumber IS NULL THEN 4                                         -- NoPurchaseOrder
            WHEN f.Received = 0 THEN 5                                                    -- NoGoodsReceipt
            WHEN f.Quantity > f.Available THEN 3                                          -- QuantityVariance
            -- difference / order price > tolerance / 100, rewritten without division: no rounding (2.001% fails),
            -- and for a free order line (price 0) it becomes "any charge above 0", with no special case.
            WHEN f.PriceDifference * 100 > @PriceTolerancePercent * f.OrderUnitPrice THEN 2  -- PriceVariance
            ELSE 1                                                                        -- FullMatch
        END,
        f.UnitPrice,
        f.OrderUnitPrice,
        CASE WHEN f.OrderUnitPrice > 0 THEN CAST(f.PriceDifference * 100 / f.OrderUnitPrice AS decimal(19, 6)) END,
        CAST(ROUND(f.PriceDifference * f.Quantity, 2) AS decimal(18, 2)),                -- ROUND: halves away from zero
        f.Currency,
        f.Quantity,
        f.Available,
        CASE WHEN f.OrderLineNumber IS NOT NULL THEN GREATEST(0, f.Quantity - f.Available) END,
        @PriceTolerancePercent,
        SYSDATETIMEOFFSET()
    FROM Facts AS f;

    COMMIT TRANSACTION;

    SELECT InvoiceLineNumber, PurchaseOrderLineNumber, Outcome, InvoiceUnitPrice, OrderUnitPrice, PriceVariancePercent,
           PriceVarianceAmount, Currency, QuantityInvoiced, QuantityAvailable, QuantityVariance
    FROM dbo.MatchResult
    WHERE InvoiceId = @InvoiceId
    ORDER BY InvoiceLineNumber;
END;
