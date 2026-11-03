-- Shop & Vending sales for two months, exported as CSV for the month-on-month workbook.
-- Read-only: runs inside BEGIN READ ONLY ... ROLLBACK on a default_transaction_read_only session.
-- Each \o writes to a full path the .ps1 passes as f_<name> (forward slashes, unquoted: psql keeps
-- the quotes of :'name' in the file name). Counters only: both van sources are left out, as on /reports/desktop-sales.
--
-- Variables (from the .ps1): from = first day, to_excl = the day after the last day, f_* = output files.

BEGIN READ ONLY;

-- 1. Shops: the warehouse -> shop name map.
\o :f_shops
COPY (
  SELECT "Code", "Name", "WarehouseCode", "BusinessPartnerCode", "IsActive"
  FROM "Shops"
  ORDER BY "WarehouseCode", "IsActive" DESC, "Id"
) TO STDOUT WITH CSV HEADER;

-- 2. Sales by month, counter, warehouse, tender and currency (sale headers).
\o :f_sales_by_tender
COPY (
  SELECT to_char(s."DocDate", 'YYYY-MM') AS "Month",
         coalesce(s."SourceSystem", '') AS "SourceSystem",
         s."WarehouseCode",
         coalesce(s."PaymentMethod", '') AS "PaymentMethod",
         s."Currency",
         count(*) AS "Sales",
         sum(s."TotalAmount") AS "Gross",
         sum(s."VatAmount") AS "Vat"
  FROM "DesktopSales" s
  WHERE s."DocDate" >= :'from'::date AND s."DocDate" < :'to_excl'::date
    AND (s."SourceSystem" IS NULL OR s."SourceSystem" NOT IN ('KefalosVanSales', 'KefalosVanSalesOnline'))
  GROUP BY 1, 2, 3, 4, 5
  ORDER BY 1, 2, 3, 4, 5
) TO STDOUT WITH CSV HEADER;

-- 3. Item lines by month, counter, warehouse, item and currency. LineTotal is before VAT.
\o :f_lines_by_item
COPY (
  SELECT to_char(s."DocDate", 'YYYY-MM') AS "Month",
         coalesce(s."SourceSystem", '') AS "SourceSystem",
         s."WarehouseCode",
         l."ItemCode",
         max(l."ItemDescription") AS "ItemDescription",
         s."Currency",
         count(DISTINCT s."Id") AS "Sales",
         sum(l."Quantity") AS "Quantity",
         sum(l."LineTotal") AS "NetValue"
  FROM "DesktopSales" s
  JOIN "DesktopSaleLines" l ON l."SaleId" = s."Id"
  WHERE s."DocDate" >= :'from'::date AND s."DocDate" < :'to_excl'::date
    AND (s."SourceSystem" IS NULL OR s."SourceSystem" NOT IN ('KefalosVanSales', 'KefalosVanSalesOnline'))
  GROUP BY 1, 2, 3, 4, 6
  ORDER BY 1, 2, 3, 4, 6
) TO STDOUT WITH CSV HEADER;

-- 4. Fiscalised till credits by the month of the sale they reverse.
\o :f_credits
COPY (
  SELECT to_char(s."DocDate", 'YYYY-MM') AS "Month",
         coalesce(s."SourceSystem", '') AS "SourceSystem",
         s."WarehouseCode",
         coalesce(s."PaymentMethod", '') AS "PaymentMethod",
         c."Currency",
         count(*) AS "Credits",
         sum(c."Amount") AS "Amount"
  FROM "DesktopCreditNotes" c
  JOIN "DesktopSales" s ON s."Id" = c."SaleId"
  WHERE c."Status" = 'Fiscalised'
    AND s."DocDate" >= :'from'::date AND s."DocDate" < :'to_excl'::date
    AND (s."SourceSystem" IS NULL OR s."SourceSystem" NOT IN ('KefalosVanSales', 'KefalosVanSalesOnline'))
  GROUP BY 1, 2, 3, 4, 5
  ORDER BY 1, 2, 3, 4, 5
) TO STDOUT WITH CSV HEADER;

-- 5. Days traded per warehouse, so a shop that opened or closed mid-month is visible.
\o :f_trading_days
COPY (
  SELECT to_char(s."DocDate", 'YYYY-MM') AS "Month",
         coalesce(s."SourceSystem", '') AS "SourceSystem",
         s."WarehouseCode",
         count(DISTINCT s."DocDate") AS "TradingDays",
         min(s."DocDate") AS "FirstDay",
         max(s."DocDate") AS "LastDay"
  FROM "DesktopSales" s
  WHERE s."DocDate" >= :'from'::date AND s."DocDate" < :'to_excl'::date
    AND (s."SourceSystem" IS NULL OR s."SourceSystem" NOT IN ('KefalosVanSales', 'KefalosVanSalesOnline'))
  GROUP BY 1, 2, 3
  ORDER BY 1, 2, 3
) TO STDOUT WITH CSV HEADER;

\o
ROLLBACK;
