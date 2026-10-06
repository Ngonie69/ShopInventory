-- Why the credit-memo fiscalisation sweep's attempts failed, and where each credited invoice's receipt lives.
--
-- Read-only. No INSERT, UPDATE, DELETE or DDL. Run with PGOPTIONS='-c default_transaction_read_only=on'
-- and psql -f.
--
-- The first pass (2026-10-06 04:05 CAT) sent 25 memos dated 23 Sep to the platform, and every one came
-- back ValidationFailed. That route is taken only when REVMax answers "not found" for both the memo and
-- the invoice it credits under their SAP DocNums. So the question is what each credited invoice is:
--
--   van_online   a StockReservations row (the van app's online sale). Its receipt may be under the sale
--                reference, not the DocNum.
--   desk_sale    a DesktopSales row (till, vending, offline van) - fiscalised before SAP under its own
--                reference. FiscalizationStatus is an int: 0 Pending, 1 Success, 2 Failed, 3 Skipped.
--   logged       a DesktopFiscalTransactions 'Invoice' row for the DocNum, i.e. this app filed it by DocNum.
--   none         nothing here knows the invoice - keyed in SAP and filed (if at all) by REVMax's add-on.

\echo '1. the sweep''s attempts'
SELECT t."DocNum"                     AS memo,
       t."OriginalInvoiceNumber"      AS credits_invoice,
       t."Status",
       left(t."Message", 300)         AS message
FROM "DesktopFiscalTransactions" t
WHERE t."SourceSystem" = 'CreditNoteSweep'
ORDER BY t."DocNum";

\echo '2. what each credited invoice is'
WITH originals AS (
    SELECT DISTINCT t."OriginalInvoiceNumber" AS invoice_no
    FROM "DesktopFiscalTransactions" t
    WHERE t."SourceSystem" = 'CreditNoteSweep' AND t."OriginalInvoiceNumber" ~ '^[0-9]+$'
)
SELECT o.invoice_no,
       r."SourceSystem"               AS reservation_source,
       r."ExternalReferenceId"        AS reservation_ref,
       s."SourceSystem"               AS sale_source,
       s."ExternalReferenceId"        AS sale_ref,
       s."FiscalizationStatus"        AS sale_fiscal_status,
       s."ReceiptGlobalNo"            AS sale_receipt,
       s."FiscalDeviceId"             AS sale_device,
       (SELECT string_agg(DISTINCT l."Status" || ':' || COALESCE(l."ReceiptGlobalNo"::text, '-') || ':' || l."SourceSystem", ', ')
          FROM "DesktopFiscalTransactions" l
         WHERE l."DocumentType" = 'Invoice' AND l."DocNum" = o.invoice_no::int) AS logged,
       CASE
           WHEN r."Id" IS NOT NULL THEN 'van_online'
           WHEN s."Id" IS NOT NULL THEN 'desk_sale'
           WHEN EXISTS (SELECT 1 FROM "DesktopFiscalTransactions" l
                         WHERE l."DocumentType" = 'Invoice' AND l."DocNum" = o.invoice_no::int) THEN 'logged'
           ELSE 'none'
       END                            AS kind
FROM originals o
LEFT JOIN "StockReservations" r ON r."SAPDocNum" = o.invoice_no::int
LEFT JOIN "DesktopSales" s      ON s."SapDocNum" = o.invoice_no::int
ORDER BY o.invoice_no;
