-- What this database knows about the invoices that refused wash bay credit notes were raised against.
--
-- Read-only. No INSERT, UPDATE, DELETE or DDL; safe to run against the live database. Run it through
-- Check-RevmaxHeldCreditNotes.ps1 -SqlFile Check-CreditedOriginals.sql.
--
-- WHY THIS EXISTS
--
-- On 2026-11-09 the platform refused ~80 credit notes added from wash bay approvals: "invoice N has
-- not been fiscalised". REVMax answered "Invoice not Found" for every one of those invoices under its
-- DocNum. Before deciding to fiscalise the originals late, check whether a receipt for them was ever
-- recorded here under another device, another number, or a sale reference.
--
-- HOW TO READ THE ANSWER
--
--   1. refused_credits   each refused credit note, the invoice it credits, and its customer and total.
--   2. invoice_log       every fiscal transaction row ever recorded for those invoice numbers. A row
--                        with a receipt number or device names where it was filed; none means this
--                        app never saw it fiscalised.
--   3. pre_sap_sales     till, van or vending sales that became those invoices; if any, the receipt is
--                        under the sale's reference rather than the DocNum.

\echo '1. refused_credits (platform said the original is not fiscalised, last 30 days)'
WITH refused AS (
    SELECT DISTINCT ON (t."DocNum")
           t."DocNum", t."OriginalInvoiceNumber", t."TimestampUtc", t."Message"
    FROM "DesktopFiscalTransactions" t
    WHERE t."DocumentType" = 'CreditNote'
      AND t."TimestampUtc" >= now() - interval '30 days'
      AND t."Message" ILIKE '%which has not been fiscalised%'
    ORDER BY t."DocNum", t."TimestampUtc" DESC
)
SELECT r."DocNum"                 AS credit_note,
       r."OriginalInvoiceNumber"  AS original_invoice,
       m."DocDate"::date          AS credit_date,
       m."CardCode",
       m."CardName",
       m."DocTotal",
       m."DocCurrency"
FROM refused r
LEFT JOIN "SapCreditNoteSnapshots" m ON m."SapDocNum" = r."DocNum"
ORDER BY r."OriginalInvoiceNumber";

\echo '2. invoice_log (any fiscal row ever recorded for those original invoices)'
WITH originals AS (
    SELECT DISTINCT t."OriginalInvoiceNumber" AS n
    FROM "DesktopFiscalTransactions" t
    WHERE t."DocumentType" = 'CreditNote'
      AND t."TimestampUtc" >= now() - interval '30 days'
      AND t."Message" ILIKE '%which has not been fiscalised%'
      AND t."OriginalInvoiceNumber" ~ '^[0-9]+$'
)
SELECT i."DocNum",
       i."DocumentType",
       i."Status",
       i."SourceSystem",
       i."DeviceId",
       i."DeviceSerialNumber",
       i."ReceiptGlobalNo",
       i."FiscalDay",
       i."TimestampUtc"::date AS logged_on,
       i."CardCode",
       i."DocTotal",
       left(i."Message", 120) AS message
FROM "DesktopFiscalTransactions" i
JOIN originals o ON o.n = i."DocNum"::text
WHERE i."DocumentType" <> 'CreditNote'
ORDER BY i."DocNum", i."TimestampUtc";

\echo '3. pre_sap_sales (till, van or vending sales posted as those invoices)'
WITH originals AS (
    SELECT DISTINCT t."OriginalInvoiceNumber"::int AS n
    FROM "DesktopFiscalTransactions" t
    WHERE t."DocumentType" = 'CreditNote'
      AND t."TimestampUtc" >= now() - interval '30 days'
      AND t."Message" ILIKE '%which has not been fiscalised%'
      AND t."OriginalInvoiceNumber" ~ '^[0-9]+$'
)
SELECT s."SapDocNum", s."ExternalReferenceId", s."SourceSystem", s."CardCode", s."TotalAmount",
       s."CreatedAt"::date AS sold_on
FROM "DesktopSales" s
JOIN originals o ON o.n = s."SapDocNum"
ORDER BY s."SapDocNum";
