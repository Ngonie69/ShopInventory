-- Un-adopts the REVMax receipts the status read-back recorded against our documents although
-- another device filed them (or they were the other kind of receipt).
--
-- GET /api/RevmaxAPI/GetInvoice/{n} is not scoped to our device: the box serves several and answers
-- a number with whichever device's receipt carries it. Until PR #488 the read-back behind the
-- invoice status backfill and the credit-note status sync did not check DeviceID or receiptType,
-- so it could write "Fiscalised" against our invoice on another device's receipt — invoice 776179
-- read Fiscalised on receipt 8406 with no QR or verification code, and its PDF printed no fiscal
-- block. Those rows also hide the Fiscalise button on a document our device never filed.
--
-- Touches only rows those two writers made from a REVMax answer, judged by the answer they kept in
-- RawResponse. Each matching row is rewritten to what the fixed reader now records for the same
-- answer: "Not Fiscalised", with the receipt fields cleared so nothing reads it as evidence.
-- RawResponse is left as it was, so the foreign receipt stays on record.
--
-- DRY RUN BY DEFAULT: lists the rows and rolls back. Pass -v apply=1 to commit (any value commits).
--   psql -h localhost -U postgres -d ShopInventory -P pager=off -f repair-foreign-revmax-receipts.sql
--   psql ... -v apply=1 -f repair-foreign-revmax-receipts.sql
-- -v ourdevice=NNNNN overrides our device (Revmax:DefaultRefDeviceId, 22862).
--
-- Proved by scripts/Test-RepairForeignRevmaxReceipts.sql.

\set ON_ERROR_STOP on
\if :{?ourdevice}
\else
\set ourdevice 22862
\endif

BEGIN;

-- RawResponse is free text written by many paths; one unparseable row must not abort the repair.
CREATE OR REPLACE FUNCTION pg_temp.try_jsonb(value text) RETURNS jsonb
LANGUAGE plpgsql IMMUTABLE AS $$
BEGIN
    RETURN value::jsonb;
EXCEPTION WHEN others THEN
    RETURN NULL;
END $$;

CREATE TEMP TABLE foreign_receipts ON COMMIT DROP AS
SELECT t."Id",
       t."DocumentType",
       t."DocNum",
       t."ReceiptGlobalNo",
       btrim(r ->> 'DeviceID')          AS owning_device,
       r -> 'Data' ->> 'receiptType'    AS held_type,
       r -> 'Data' ->> 'invoiceNo'      AS held_invoice_no,
       t."CardCode",
       t."LastSyncedAtUtc"
FROM "DesktopFiscalTransactions" t
CROSS JOIN LATERAL (SELECT pg_temp.try_jsonb(t."RawResponse") AS r) answer
WHERE t."Status" = 'Fiscalised'
  AND (   (t."DocumentType" = 'Invoice'
           AND t."SourceSystem" = 'InvoiceFiscalisationBackfill'
           AND t."ClientTransactionId" LIKE 'invoice-status-backfill-%')
       OR (t."DocumentType" = 'CreditNote'
           AND t."SourceSystem" = 'CreditNote'
           AND t."ClientTransactionId" LIKE 'credit-note-fiscalisation-%'))
  -- A REVMax GetInvoice answer that found a receipt. The platform's answers, from before the
  -- 2026-09-09 switch back to REVMax, are {"IsFiscalised":…,"Matches":[…]} and carry no "Code".
  AND r ->> 'Code' = '1'
  AND (   btrim(r ->> 'DeviceID') IS DISTINCT FROM :'ourdevice'
       OR lower(r -> 'Data' ->> 'receiptType') IS DISTINCT FROM
              CASE t."DocumentType" WHEN 'Invoice' THEN 'fiscalinvoice' ELSE 'creditnote' END);

\echo '== Rows recorded as fiscalised on a receipt that is not ours =='
SELECT "DocumentType", "DocNum", "ReceiptGlobalNo", owning_device, held_type, held_invoice_no,
       "CardCode", "LastSyncedAtUtc"
FROM foreign_receipts
ORDER BY "DocumentType", "DocNum";

UPDATE "DesktopFiscalTransactions" t
   SET "Status"             = 'Not Fiscalised',
       "ReceiptGlobalNo"    = NULL,
       "QRCode"             = NULL,
       "VerificationCode"   = NULL,
       "DeviceId"           = NULL,
       "DeviceSerialNumber" = NULL,
       "FiscalDay"          = NULL,
       "Message"            = format(
           'Repaired %s: REVMax answered %s %s with receipt %s of device %s (%s), which is not ours '
           || '(device %s). That is no evidence our device filed this document; the answer is kept in RawResponse.',
           to_char(now() AT TIME ZONE 'Africa/Harare', 'YYYY-MM-DD'),
           f."DocumentType", f."DocNum",
           coalesce(f."ReceiptGlobalNo"::text, '?'), coalesce(f.owning_device, '(none)'),
           coalesce(f.held_type, 'no type'), :'ourdevice'),
       "LastSyncedAtUtc"    = now()
  FROM foreign_receipts f
 WHERE t."Id" = f."Id";

SELECT count(*) AS rows_matched FROM foreign_receipts \gset
\echo '== Rows matched:' :rows_matched '=='

\if :{?apply}
COMMIT;
\echo '== Committed =='
\else
ROLLBACK;
\echo '== Dry run: rolled back, nothing was changed. Re-run with -v apply=1 to commit. =='
\endif
