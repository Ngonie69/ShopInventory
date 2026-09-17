-- Why did SAP refuse this fiscalised van sale with "Account currency 805650 not the same as document
-- currency"?
--
-- 805650 is "VAT Control ZiG", the tax account behind VAT groups O8/O011. So the invoice was USD but at
-- least one line was charged a ZiG VAT group. This answers where that came from: the line's own tax
-- code (the fiscal-first poster writes it from SapItemTaxGroups), or SAP's default for the posting
-- account (every VANxxx partner but VAN001/VAN018/VAN019 defaults to O8).
--
--   psql -h localhost -U postgres -d ShopInventory -P pager=off -v ref="'VAN005-INV-20260917-E58A14'" -f diagnose-van-sale-currency-refusal.sql
--
-- Nothing here writes.

\set ON_ERROR_STOP on
\if :{?ref} \else \set ref '''VAN005-INV-20260917-E58A14''' \endif

\echo '== 1. The reservation: account billed, currency, status =='
SELECT  r."Id", r."ReservationId", r."ExternalReferenceId", r."SourceSystem",
        r."CardCode", r."CardName", r."RouteCustomerCode", r."Currency",
        r."Status", r."CreatedAt", r."CreatedBy"
FROM    "StockReservations" r
WHERE   r."ExternalReferenceId" = :ref;

\echo ''
\echo '== 2. Its lines: the tax code each one went to SAP with, beside the item master copy =='
SELECT  l."LineNum", l."ItemCode", l."ItemDescription", l."OriginalQuantity", l."UoMCode",
        l."UnitPrice", l."WarehouseCode", l."CostCentreCode",
        l."TaxCode"         AS sent_tax_code,
        t."VatGroup"        AS item_master_vat_group,
        t."ResolvedAtUtc"   AS item_master_read_at
FROM    "StockReservationLines" l
JOIN    "StockReservations" r ON r."Id" = l."ReservationId"
LEFT JOIN "SapItemTaxGroups" t ON upper(t."ItemCode") = upper(l."ItemCode")
WHERE   r."ExternalReferenceId" = :ref
ORDER BY l."LineNum";

\echo ''
\echo '== 3. The receipt row: what the fiscal receipt declared per line =='
SELECT  s."Id", s."CardCode", s."Currency", s."FiscalizationStatus", s."FiscalReceiptNumber",
        s."PostingAttempts", s."LastPostingError"
FROM    "DesktopSales" s
WHERE   s."ExternalReferenceId" = :ref;

SELECT  dl."LineNum", dl."ItemCode", dl."TaxCode", dl."TaxPercent", dl."LineTotal"
FROM    "DesktopSaleLines" dl
JOIN    "DesktopSales" s ON s."Id" = dl."SaleId"
WHERE   s."ExternalReferenceId" = :ref
ORDER BY dl."LineNum";

\echo ''
\echo '== 4. The rep: which SAP account their sales post against =='
SELECT  u."Id", u."Username", u."FirstName", u."LastName", u."Role",
        u."AssignedBusinessPartnerCode", u."AssignedWarehouseCodes", u."AssignedCostCentreCode"
FROM    "Users" u
WHERE   u."Id"::text = (SELECT r."CreatedBy" FROM "StockReservations" r WHERE r."ExternalReferenceId" = :ref);

\echo ''
\echo '== 5. Other van sales today on the same account: did they post, and with which tax codes? =='
SELECT  r."ExternalReferenceId", r."Status", r."SAPDocNum", r."Currency",
        string_agg(DISTINCT coalesce(l."TaxCode", '<null>'), ',') AS tax_codes
FROM    "StockReservations" r
JOIN    "StockReservationLines" l ON l."ReservationId" = r."Id"
WHERE   r."CardCode" = (SELECT x."CardCode" FROM "StockReservations" x WHERE x."ExternalReferenceId" = :ref)
  AND   r."CreatedAt" >= now() - interval '3 days'
GROUP BY r."Id", r."ExternalReferenceId", r."Status", r."SAPDocNum", r."Currency"
ORDER BY r."Id" DESC
LIMIT 20;
