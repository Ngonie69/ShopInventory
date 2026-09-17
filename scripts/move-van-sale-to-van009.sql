-- Moves the refused test van sale VAN005-INV-20260917-E58A14 from SAP account VAN008 to VAN009, the
-- account every real VAN005 / FAC055 sale posts to, so a Retry posts it on a combination SAP has taken
-- all day. Touches only this one sale. Run AFTER the Retry fix is deployed, then press Retry on the
-- sale in the Exception Center.
--
--   psql -h localhost -U postgres -d ShopInventory -P pager=off -f move-van-sale-to-van009.sql
--
-- Prints the rows before and after, and rolls back unless every update hit exactly one row.

\set ON_ERROR_STOP on
\set ref '''VAN005-INV-20260917-E58A14'''

BEGIN;

\echo '== Before =='
SELECT 'reservation' AS row, "CardCode", "Status" FROM "StockReservations" WHERE "ExternalReferenceId" = :ref
UNION ALL
SELECT 'receipt row', "CardCode", "FiscalizationStatus"::text FROM "DesktopSales" WHERE "ExternalReferenceId" = :ref
UNION ALL
SELECT 'queue entry', "CustomerCode", "Status"::text FROM "InvoiceQueue" WHERE "ExternalReference" = :ref;

DO $$
DECLARE n int;
BEGIN
  UPDATE "StockReservations" SET "CardCode" = 'VAN009'
   WHERE "ExternalReferenceId" = 'VAN005-INV-20260917-E58A14' AND "CardCode" = 'VAN008' AND "SAPDocNum" IS NULL;
  GET DIAGNOSTICS n = ROW_COUNT; IF n <> 1 THEN RAISE EXCEPTION 'StockReservations: % rows, expected 1', n; END IF;

  UPDATE "DesktopSales" SET "CardCode" = 'VAN009'
   WHERE "ExternalReferenceId" = 'VAN005-INV-20260917-E58A14' AND "CardCode" = 'VAN008' AND "SapDocNum" IS NULL;
  GET DIAGNOSTICS n = ROW_COUNT; IF n <> 1 THEN RAISE EXCEPTION 'DesktopSales: % rows, expected 1', n; END IF;

  UPDATE "InvoiceQueue"
     SET "CustomerCode" = 'VAN009',
         "InvoicePayload" = replace("InvoicePayload", '"cardCode":"VAN008"', '"cardCode":"VAN009"')
   WHERE "ExternalReference" = 'VAN005-INV-20260917-E58A14' AND "CustomerCode" = 'VAN008' AND "SapDocNum" IS NULL;
  GET DIAGNOSTICS n = ROW_COUNT; IF n <> 1 THEN RAISE EXCEPTION 'InvoiceQueue: % rows, expected 1', n; END IF;
END $$;

\echo '== After =='
SELECT 'reservation' AS row, "CardCode", "Status" FROM "StockReservations" WHERE "ExternalReferenceId" = :ref
UNION ALL
SELECT 'receipt row', "CardCode", "FiscalizationStatus"::text FROM "DesktopSales" WHERE "ExternalReferenceId" = :ref
UNION ALL
SELECT 'queue entry', "CustomerCode", "Status"::text FROM "InvoiceQueue" WHERE "ExternalReference" = :ref;
SELECT position('"cardCode":"VAN009"' in "InvoicePayload") > 0 AS payload_moved
FROM "InvoiceQueue" WHERE "ExternalReference" = :ref;

COMMIT;
