-- Which SAP credit memos the scheduled credit-note fiscalisation will take on its first passes.
--
-- Read-only. No INSERT, UPDATE, DELETE or DDL; safe to run against the live database. Run it with
-- PGOPTIONS='-c default_transaction_read_only=on' and psql -f, as the other scripts here are run.
--
-- WHY THIS EXISTS
--
-- A credit memo keyed straight into SAP B1 reaches ZIMRA only if a clerk prints it: the platform's B1
-- bridge hooks the print, and nothing polls SAP. Before the 2026-09-30 cut-over REVMax's own SAP add-on
-- filed every memo. SapCreditNoteFiscalisationSweep now files the rest, and filing is irreversible, so
-- this shows what it will take before it runs. It mirrors SapCreditNoteFiscalisationSweep.FindCandidatesAsync
-- with the defaults in appsettings.json (14 days, 15-minute grace, EXP* excluded, 3 attempts).
--
-- HOW TO READ THE ANSWER
--
--   1. backlog            memos the sweep will take, by day. A memo REVMax or the platform already holds
--                         costs one lookup and is adopted, not filed again.
--   2. complete_without   memos the lists call fiscalised that carry no receipt number - the
--      _receipt_number    "Complete" beside "No receipt" rows. source_system and status say which
--                         path wrote the evidence.

\echo '1. backlog'
WITH settings AS (
    SELECT (now() AT TIME ZONE 'Africa/Harare')::date - 13 AS from_day,
           now() - interval '15 minutes'                  AS settled_before
),
memos AS (
    SELECT m."SapDocEntry", m."SapDocNum", m."DocDate", m."CardCode", m."CardName", m."DocTotal"
    FROM "SapCreditNoteSnapshots" m, settings s
    WHERE NOT m."IsCancelled"
      AND m."DocDate" >= s.from_day
      AND m."SyncedAtUtc" <= s.settled_before
      AND m."SapDocNum" > 0
      AND COALESCE(m."CardCode", '') NOT ILIKE 'EXP%'
),
evidence AS (
    SELECT DISTINCT t."DocNum"
    FROM "DesktopFiscalTransactions" t
    WHERE t."DocumentType" = 'CreditNote'
      AND (lower(t."Status") IN ('success', 'fiscalised')
           OR t."ReceiptGlobalNo" IS NOT NULL
           OR COALESCE(trim(t."QRCode"), '') <> ''
           OR COALESCE(trim(t."VerificationCode"), '') <> '')
),
till_filed AS (
    SELECT DISTINCT c."SapDocNum" AS "DocNum"
    FROM "DesktopCreditNotes" c
    WHERE c."SapDocNum" IS NOT NULL AND c."Status" = 'Fiscalised'
),
unresolved AS (
    SELECT DISTINCT t."DocNum"
    FROM "DesktopFiscalTransactions" t
    WHERE t."DocumentType" = 'CreditNote'
      AND lower(COALESCE(t."Message", '')) ~ '(unresolved|reconcil|indeterminate|idempotency_|chainbreak)'
)
SELECT m."DocDate"::date                                   AS day,
       count(*)                                            AS memos,
       sum(m."DocTotal")                                   AS credited,
       string_agg(m."SapDocNum"::text, ', ' ORDER BY m."SapDocNum") AS doc_nums
FROM memos m
WHERE m."SapDocNum" NOT IN (SELECT "DocNum" FROM evidence)
  AND m."SapDocNum" NOT IN (SELECT "DocNum" FROM till_filed)
  AND m."SapDocNum" NOT IN (SELECT "DocNum" FROM unresolved)
GROUP BY m."DocDate"::date
ORDER BY day;

\echo '2. complete_without_receipt_number'
SELECT t."DocNum", t."Status", t."SourceSystem", t."TimestampUtc", left(t."Message", 120) AS message
FROM "DesktopFiscalTransactions" t
WHERE t."DocumentType" = 'CreditNote'
  AND t."TimestampUtc" >= now() - interval '14 days'
  AND lower(t."Status") IN ('success', 'fiscalised')
  AND t."ReceiptGlobalNo" IS NULL
ORDER BY t."TimestampUtc" DESC
LIMIT 50;
