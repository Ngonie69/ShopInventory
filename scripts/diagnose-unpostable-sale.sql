-- Why was this fiscalised till sale refused by SAP for stock?
--
-- Answers, in order, the questions that separate the possible causes. Run against the production
-- ShopInventory database. Set the reference once, below: either the external reference
-- (KEF-FAC-...) or the fiscal receipt number.
--
--   psql -d ShopInventory -v ref="'KEF-FAC-20260916-XXXXXXXX'" -f diagnose-unpostable-sale.sql
--
-- Nothing here writes.

\set ON_ERROR_STOP on
\if :{?ref} \else \set ref '''KEF-FAC-20260915-8F904ECE7543''' \endif

\echo '== 1. The sale: when it was taken, and what SAP said =='
-- The counter SAP check went live at 2026-09-16 01:42 CAT (= 2026-09-15 23:42 UTC). A sale taken
-- before that was never checked against SAP at all, and the answer is simply "the guard did not
-- exist yet". After it, the guard ran and something got past it.
SELECT  s."Id",
        s."ExternalReferenceId",
        s."FiscalReceiptNumber",
        s."WarehouseCode",
        s."CreatedAt"                                  AS created_utc,
        s."CreatedAt" + interval '2 hours'             AS created_cat,
        s."CreatedAt" >= timestamp '2026-09-15 23:42'  AS counter_check_was_live,
        s."FiscalizationStatus",
        s."ConsolidationStatus",
        s."PostingAttempts",
        s."PostIssuedAtUtc",
        s."PostedAt",
        s."SapDocNum",
        s."LastPostingError"
FROM    "DesktopSales" s
WHERE   s."ExternalReferenceId" = :ref
   OR   s."FiscalReceiptNumber" = :ref;

\echo ''
\echo '== 2. Its lines: which item is short, and how much was sold =='
SELECT  l."LineNum", l."ItemCode", l."ItemDescription",
        l."Quantity", l."UoMCode", l."WarehouseCode"
FROM    "DesktopSaleLines" l
JOIN    "DesktopSales" s ON s."Id" = l."SaleId"
WHERE   s."ExternalReferenceId" = :ref OR s."FiscalReceiptNumber" = :ref
ORDER BY l."LineNum";

\echo ''
\echo '== 3. What else was holding the same stock when this sale was checked =='
-- Every till sale of the same items, in the same warehouse, that had not reached SAP at the moment
-- this one was taken. These are what CounterSapStockCheck nets off SAP's figure (claimedAhead).
-- ConsolidationStatus: 0 Pending, 1 Consolidated, 2 Failed, 3 Excluded. A sale listed here as 1 was
-- NOT netted off SAP's figure: if it had not in fact reached SAP, the check was told stock was free
-- that was already spoken for. A sale older than DailyStock:UnpostedSaleLookbackDays (30) was not
-- netted either, whatever its status -- see days_before_sale.
WITH target AS (
    SELECT s."Id", s."CreatedAt", s."WarehouseCode"
    FROM   "DesktopSales" s
    WHERE  s."ExternalReferenceId" = :ref OR s."FiscalReceiptNumber" = :ref
)
SELECT  o."ExternalReferenceId",
        o."CreatedAt" + interval '2 hours' AS created_cat,
        o."ConsolidationStatus",
        o."PostingAttempts",
        o."SapDocNum",
        ol."ItemCode",
        ol."Quantity",
        EXTRACT(day FROM (SELECT "CreatedAt" FROM target) - o."CreatedAt") AS days_before_sale,
        o."LastPostingError"
FROM    "DesktopSales" o
JOIN    "DesktopSaleLines" ol ON ol."SaleId" = o."Id"
JOIN    target t ON TRUE
WHERE   o."Id" <> t."Id"
  AND   ol."WarehouseCode" = t."WarehouseCode"
  AND   ol."ItemCode" IN (
            SELECT l."ItemCode" FROM "DesktopSaleLines" l WHERE l."SaleId" = t."Id")
  AND   o."CreatedAt" < t."CreatedAt"
  AND   (o."PostedAt" IS NULL OR o."PostedAt" > t."CreatedAt")
ORDER BY o."CreatedAt" DESC;

\echo ''
\echo '== 4. Was the ledger known to disagree with SAP on these items that day? =='
SELECT  d."CheckedAt" + interval '2 hours' AS checked_cat,
        d."ItemCode", d."WarehouseCode", d."Source",
        d."LedgerQuantity", d."SapIssuableQuantity", d."Difference"
FROM    "StockLedgerDivergences" d
JOIN    "DesktopSales" s ON (s."ExternalReferenceId" = :ref OR s."FiscalReceiptNumber" = :ref)
WHERE   d."WarehouseCode" = s."WarehouseCode"
  AND   d."ItemCode" IN (
            SELECT l."ItemCode" FROM "DesktopSaleLines" l WHERE l."SaleId" = s."Id")
  AND   d."CheckedAt" BETWEEN s."CreatedAt" - interval '1 day' AND s."CreatedAt" + interval '1 day'
ORDER BY d."CheckedAt";

\echo ''
\echo '== 5. Is the dead local batch table actually empty? =='
-- ReadWarehouseBatchesAsync reads ProductBatches FIRST and only asks SAP when it finds no rows --
-- with no staleness test of any kind. Nothing in the codebase writes this table, so it should be
-- empty and SAP should always be read. A non-zero count here means the counter check has been
-- allocating against local rows instead of SAP, and is the whole answer.
SELECT  COUNT(*) AS product_batch_rows,
        COUNT(*) FILTER (WHERE "Quantity" > 0 AND "IsActive") AS rows_that_would_shadow_sap
FROM    "ProductBatches";

\echo ''
\echo '== 6. Was the guard switched off? =='
-- CheckSapStockAtCounter defaults true and is not in appsettings.json, but an environment variable
-- DailyStock__CheckSapStockAtCounter=false would silence it. This cannot be read from the database;
-- check the API host environment. The API logs a warning per sale when it is off:
--   "Sale {Reference} was not checked against SAP stock: DailyStock:CheckSapStockAtCounter is off"
SELECT 'Check the API log for the warning quoted above, around ' ||
       to_char(s."CreatedAt" + interval '2 hours', 'YYYY-MM-DD HH24:MI') || ' CAT' AS next_step
FROM   "DesktopSales" s
WHERE  s."ExternalReferenceId" = :ref OR s."FiscalReceiptNumber" = :ref;
