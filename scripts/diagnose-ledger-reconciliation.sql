-- Why did the hourly SAP comparison take stock off a shop with no document behind it?
--
-- The "Adjustment / No document / Corrected during the day" rows on the stock movement view are
-- Reconciliation movements written by StockLedgerDivergenceJob: the ledger was moved to
-- SAP issuable - unposted till sales. A drop means one of two things, and this separates them:
--   (a) SAP really lost the units to something keyed in SAP (goods issue, SAP-client invoice,
--       inventory posting, production issue). Sections 1-4 show the drop; the SAP query in
--       diagnose-ledger-reconciliation-sap.sql names the document.
--   (b) The correction itself was wrong: a till sale counted as unposted (section 5) while SAP had
--       already taken it, so the same units were subtracted twice. Signature: a sale in section 5 with
--       a SapDocNum, or a Reconciliation drop followed by a put-back an hour later (section 2).
--
--   psql -h localhost -U postgres -d ShopInventory -P pager=off \
--        -v day="'2026-10-14'" -v items="'ICV006','RMA001'" -v wh=KEFSHOP -f diagnose-ledger-reconciliation.sql
--
-- Nothing here writes. Set PGOPTIONS=-c default_transaction_read_only=on as well.

\set ON_ERROR_STOP on
\if :{?day} \else \set day '''2026-10-14''' \endif
\if :{?items} \else \set items '''ICV006'',''RMA001''' \endif
\if :{?wh} \else \set wh '' \endif

\echo '== 1. The Reconciliation movements themselves: which warehouse, when, how much =='
SELECT  m."WarehouseCode",
        m."ItemCode",
        m."OccurredAt" AT TIME ZONE 'Africa/Harare' AS at_cat,
        m."Quantity",
        m."BalanceAfter",
        m."Reference"
FROM    "StockMovements" m
WHERE   m."LedgerDay" = :day::date
  AND   m."Kind" = 'Reconciliation'
  AND   m."ItemCode" IN (:items)
ORDER BY m."WarehouseCode", m."ItemCode", m."OccurredAt";

\echo ''
\echo '== 2. Every movement of those items in those warehouses that day, in time order =='
-- Commit/Settle/Release are this system's own documents; transfer:* are TransferEventListener's.
-- A Reconciliation drop with no till Commit near it, and no put-back later, is (a). A drop that is
-- put back by the next hourly pass is (b) in its transient form: a sale posting between the job's
-- unposted-sales read and its SAP read, counted in both.
WITH pairs AS (
    -- Every warehouse that held the items that morning, not only those a correction touched: a
    -- gap the correction left alone (because it was netting the same sales) is the case to find.
    SELECT DISTINCT r."WarehouseCode", r."ItemCode"
    FROM   "DailyStockSnapshotItems" r
    JOIN   "DailyStockSnapshots" s ON s."Id" = r."SnapshotId"
    WHERE  s."SnapshotDate" = :day::date AND r."ItemCode" IN (:items)
      AND  (:'wh' = '' OR r."WarehouseCode" = :'wh')
)
SELECT  m."OccurredAt" AT TIME ZONE 'Africa/Harare' AS at_cat,
        m."WarehouseCode",
        m."ItemCode",
        'ledger:' || m."Kind"                      AS source,
        m."Quantity",
        m."BalanceAfter",
        m."DocumentKey",
        m."Reference"                              AS detail
FROM    "StockMovements" m
JOIN    pairs p ON p."WarehouseCode" = m."WarehouseCode" AND p."ItemCode" = m."ItemCode"
WHERE   m."LedgerDay" = :day::date
UNION ALL
SELECT  a."DetectedAt" AT TIME ZONE 'Africa/Harare',
        a."WarehouseCode",
        a."ItemCode",
        'transfer:' || a."Direction",
        a."AdjustmentQuantity",
        NULL,
        a."TransferDocNum"::text,
        a."SourceWarehouse" || ' -> ' || a."DestinationWarehouse"
FROM    "StockTransferAdjustments" a
JOIN    pairs p ON p."WarehouseCode" = a."WarehouseCode" AND p."ItemCode" = a."ItemCode"
WHERE   (a."DetectedAt" AT TIME ZONE 'Africa/Harare')::date = :day::date
ORDER BY 1, 2, 3;

\echo ''
\echo '== 3. What each hourly comparison saw: ledger against SAP issuable =='
-- Difference = ledger - SAP. The first row where SAP falls below the ledger brackets the hour the
-- SAP-side change landed. A Difference that recurs every hour at the same size after a correction
-- means the correction did not hold, which points at (b).
SELECT  d."CheckedAt" AT TIME ZONE 'Africa/Harare' AS checked_cat,
        d."WarehouseCode",
        d."ItemCode",
        d."Source",
        d."Reference",
        d."LedgerQuantity",
        d."SapIssuableQuantity",
        d."Difference"
FROM    "StockLedgerDivergences" d
WHERE   d."LedgerDay" = :day::date
  AND   d."ItemCode" IN (:items)
ORDER BY d."WarehouseCode", d."ItemCode", d."CheckedAt";

\echo ''
\echo '== 4. The morning rows: what the day opened with, and where it stands =='
SELECT  r."WarehouseCode",
        r."ItemCode",
        r."ItemDescription",
        r."BatchNumber",
        r."OriginalQuantity",
        r."AvailableQuantity",
        r."ExpiryDate"
FROM    "DailyStockSnapshotItems" r
JOIN    "DailyStockSnapshots" s ON s."Id" = r."SnapshotId"
WHERE   s."SnapshotDate" = :day::date
  AND   r."ItemCode" IN (:items)
ORDER BY r."WarehouseCode", r."ItemCode", r."ExpiryDate" NULLS LAST;

\echo ''
\echo '== 5. Till sales the correction subtracted as "not yet in SAP" =='
-- UnpostedTillSales counts every sale of the item in the warehouse whose ConsolidationStatus is not
-- 1 (Consolidated): today's of any source, earlier days' (30-day lookback) from till sources.
-- ConsolidationStatus: 0 Pending, 1 Consolidated, 2 Failed, 3 Excluded.
-- A row here that HAS a SapDocNum / SapDocEntry is in SAP already and was subtracted twice -- that
-- is (b), and it repeats every hour until the status is put right. Status 3 (Excluded) is also
-- suspect: such a sale may never reach SAP, or may be in SAP by another route.
WITH pairs AS (
    -- Every warehouse that held the items that morning, not only those a correction touched: a
    -- gap the correction left alone (because it was netting the same sales) is the case to find.
    SELECT DISTINCT r."WarehouseCode", r."ItemCode"
    FROM   "DailyStockSnapshotItems" r
    JOIN   "DailyStockSnapshots" s ON s."Id" = r."SnapshotId"
    WHERE  s."SnapshotDate" = :day::date AND r."ItemCode" IN (:items)
      AND  (:'wh' = '' OR r."WarehouseCode" = :'wh')
)
SELECT  s."Id",
        s."ExternalReferenceId",
        s."SourceSystem",
        s."CreatedAt" AT TIME ZONE 'Africa/Harare' AS created_cat,
        l."WarehouseCode",
        l."ItemCode",
        l."Quantity",
        s."FiscalizationStatus",
        s."ConsolidationStatus",
        s."PostingAttempts",
        s."PostedAt" AT TIME ZONE 'Africa/Harare'  AS posted_cat,
        s."SapDocEntry",
        s."SapDocNum",
        s."LastPostingError"
FROM    "DesktopSaleLines" l
JOIN    "DesktopSales" s ON s."Id" = l."SaleId"
JOIN    pairs p ON p."WarehouseCode" = l."WarehouseCode" AND p."ItemCode" = l."ItemCode"
WHERE   s."ConsolidationStatus" <> 1
  AND   s."CreatedAt" >= (:day::date - 30)::timestamp AT TIME ZONE 'Africa/Harare'
  AND   s."CreatedAt" <  (:day::date + 1)::timestamp AT TIME ZONE 'Africa/Harare'
ORDER BY s."CreatedAt";

\echo ''
\echo '== 6. That day''s till sales of the items, posted or not: when each reached SAP =='
-- A sale whose posted_cat falls just before a Reconciliation drop in section 2 is the transient
-- form of (b). Total sold here against the drop size: if they match, the drop is the sales.
WITH pairs AS (
    -- Every warehouse that held the items that morning, not only those a correction touched: a
    -- gap the correction left alone (because it was netting the same sales) is the case to find.
    SELECT DISTINCT r."WarehouseCode", r."ItemCode"
    FROM   "DailyStockSnapshotItems" r
    JOIN   "DailyStockSnapshots" s ON s."Id" = r."SnapshotId"
    WHERE  s."SnapshotDate" = :day::date AND r."ItemCode" IN (:items)
      AND  (:'wh' = '' OR r."WarehouseCode" = :'wh')
)
SELECT  s."ExternalReferenceId",
        s."SourceSystem",
        s."CreatedAt" AT TIME ZONE 'Africa/Harare' AS created_cat,
        s."PostedAt" AT TIME ZONE 'Africa/Harare'  AS posted_cat,
        l."WarehouseCode",
        l."ItemCode",
        l."Quantity",
        s."ConsolidationStatus",
        s."SapDocNum"
FROM    "DesktopSaleLines" l
JOIN    "DesktopSales" s ON s."Id" = l."SaleId"
JOIN    pairs p ON p."WarehouseCode" = l."WarehouseCode" AND p."ItemCode" = l."ItemCode"
WHERE   (s."CreatedAt" AT TIME ZONE 'Africa/Harare')::date = :day::date
ORDER BY s."CreatedAt";

\echo ''
\echo '== 7. Credit notes that put units back on the shelf for those items =='
-- These are ADDED to the correction's target while SAP has not had the memo. ManualInSap is
-- included by the code, though SAP already holds those units; a ManualInSap row here inflates the
-- target, it cannot explain a drop.
SELECT  c."Number",
        c."SapStatus",
        c."SapDocNum",
        c."UnitsReturnedToLedger",
        s."ExternalReferenceId" AS sale,
        s."WarehouseCode"
FROM    "DesktopCreditNotes" c
JOIN    "DesktopSales" s ON s."Id" = c."SaleId"
WHERE   c."UnitsReturnedToLedger"
  AND   s."CreatedAt" >= (:day::date - 30)::timestamp AT TIME ZONE 'Africa/Harare'
  AND   EXISTS (SELECT 1 FROM "DesktopSaleLines" l
                WHERE l."SaleId" = s."Id" AND l."ItemCode" IN (:items))
ORDER BY s."CreatedAt";
