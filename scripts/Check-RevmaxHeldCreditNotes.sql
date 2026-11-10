-- Credit notes held back because REVMax could not be asked whether it filed their original invoice.
--
-- Read-only. No INSERT, UPDATE, DELETE or DDL; safe to run against the live database. Run it with
-- PGOPTIONS='-c default_transaction_read_only=on' and psql -f, as the other scripts here are run.
--
-- WHY THIS EXISTS
--
-- A credit note against an invoice REVMax fiscalised can only be filed on REVMax. When REVMax does not
-- answer, RevmaxHistoryFiscalizationService holds the credit note back (REVMAX_HISTORY_UNAVAILABLE).
-- Nothing is written to DesktopFiscalTransactions for it, so the only trace is the Exception Center
-- incident the approval add (wash bay) raises, and the credit memo staying without a receipt.
--
-- HOW TO READ THE ANSWER
--
--   1. held_back_incidents  every "REVMax could not be asked" incident in the last 30 days, newest
--                           first. last_error ends with REVMax's own reason: a timeout, a refused
--                           connection, or "did not say (Code ..)". That reason is what to fix.
--   2. still_without_receipt the credit memos behind those incidents that still have no receipt.
--                           The oldest of these is what the scheduled sweep stops on each pass.
--   3. blocked_behind_oldest credit memos newer than that one with no receipt, which the sweep does
--                           not reach while REVMax stays unreachable.
--   4-6.                    every credit-note fiscalisation failure, whatever it says: the incidents,
--                           which device each credit note was filed on, and the latest failures with
--                           the original invoice they credit.

\echo '1. held_back_incidents'
SELECT i."Id",
       i."CreatedAtUtc" AT TIME ZONE 'UTC' AT TIME ZONE 'Africa/Harare' AS raised_cat,
       i."Reference",
       (i."DetailsJson"::json ->> 'SapDocNum')                         AS credit_note_docnum,
       i."Status",
       i."LastError"                                                   AS last_error
FROM "ExceptionCenterIncidents" i
WHERE i."Source" = 'credit-note-fiscalization'
  AND i."CreatedAtUtc" >= now() - interval '30 days'
  AND i."LastError" ILIKE '%REVMax could not be asked%'
ORDER BY i."CreatedAtUtc" DESC
LIMIT 100;

\echo '2. still_without_receipt'
WITH held AS (
    SELECT DISTINCT (i."DetailsJson"::json ->> 'SapDocNum')::int AS "DocNum"
    FROM "ExceptionCenterIncidents" i
    WHERE i."Source" = 'credit-note-fiscalization'
      AND i."CreatedAtUtc" >= now() - interval '30 days'
      AND i."LastError" ILIKE '%REVMax could not be asked%'
      AND (i."DetailsJson"::json ->> 'SapDocNum') ~ '^[0-9]+$'
),
evidence AS (
    SELECT DISTINCT t."DocNum"
    FROM "DesktopFiscalTransactions" t
    WHERE t."DocumentType" = 'CreditNote'
      AND (lower(t."Status") IN ('success', 'fiscalised')
           OR t."ReceiptGlobalNo" IS NOT NULL
           OR COALESCE(trim(t."QRCode"), '') <> ''
           OR COALESCE(trim(t."VerificationCode"), '') <> '')
)
SELECT m."SapDocNum", m."DocDate"::date AS doc_date, m."CardCode", m."DocTotal", m."DocCurrency"
FROM "SapCreditNoteSnapshots" m
JOIN held h ON h."DocNum" = m."SapDocNum"
WHERE NOT m."IsCancelled"
  AND m."SapDocNum" NOT IN (SELECT "DocNum" FROM evidence)
ORDER BY m."DocDate", m."SapDocNum";

\echo '3. blocked_behind_oldest'
-- Mirrors SapCreditNoteFiscalisationSweep.FindCandidatesAsync (14 days, from 2026-10-01, EXP* excluded),
-- counting the memos dated on or after the oldest held-back one.
WITH held AS (
    SELECT DISTINCT (i."DetailsJson"::json ->> 'SapDocNum')::int AS "DocNum"
    FROM "ExceptionCenterIncidents" i
    WHERE i."Source" = 'credit-note-fiscalization'
      AND i."CreatedAtUtc" >= now() - interval '30 days'
      AND i."LastError" ILIKE '%REVMax could not be asked%'
      AND (i."DetailsJson"::json ->> 'SapDocNum') ~ '^[0-9]+$'
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
window_memos AS (
    SELECT m.*
    FROM "SapCreditNoteSnapshots" m
    WHERE NOT m."IsCancelled"
      AND m."SapDocNum" > 0
      AND m."DocDate" >= GREATEST((now() AT TIME ZONE 'Africa/Harare')::date - 13, DATE '2026-10-01')
      AND COALESCE(m."CardCode", '') NOT ILIKE 'EXP%'
      AND m."SapDocNum" NOT IN (SELECT "DocNum" FROM evidence)
),
oldest_held AS (
    SELECT min(w."DocDate") AS "DocDate"
    FROM window_memos w
    JOIN held h ON h."DocNum" = w."SapDocNum"
)
SELECT o."DocDate"::date                    AS oldest_held_back_day,
       count(w.*)                           AS memos_without_receipt_from_that_day,
       sum(w."DocTotal")                    AS credited
FROM oldest_held o
LEFT JOIN window_memos w ON w."DocDate" >= o."DocDate"
GROUP BY o."DocDate";

-- Every credit-note fiscalisation problem, whatever it says. Sections 1-3 look only for "REVMax
-- could not be asked"; these show what else is going wrong.

\echo '4. credit_note_incidents (all, last 30 days, newest first)'
SELECT i."Id",
       i."CreatedAtUtc" AT TIME ZONE 'UTC' AT TIME ZONE 'Africa/Harare' AS raised_cat,
       i."Reference",
       i."Status",
       i."LastError"                                                   AS last_error
FROM "ExceptionCenterIncidents" i
WHERE i."Source" = 'credit-note-fiscalization'
  AND i."CreatedAtUtc" >= now() - interval '30 days'
ORDER BY i."CreatedAtUtc" DESC
LIMIT 40;

\echo '5. where_credit_notes_went (last 30 days; DeviceId 22862 = REVMax, 46668-46670 = platform)'
SELECT t."SourceSystem",
       t."Status",
       COALESCE(t."DeviceId", '-')                AS device,
       count(*)                                   AS rows,
       count(DISTINCT t."DocNum")                 AS credit_notes,
       max(t."TimestampUtc")                      AS latest_utc
FROM "DesktopFiscalTransactions" t
WHERE t."DocumentType" = 'CreditNote'
  AND t."TimestampUtc" >= now() - interval '30 days'
GROUP BY 1, 2, 3
ORDER BY 1, 2, 3;

\echo '6. latest_failures (last 30 days, not a success, newest first)'
SELECT t."TimestampUtc" AT TIME ZONE 'UTC' AT TIME ZONE 'Africa/Harare' AS at_cat,
       t."DocNum"                 AS credit_note,
       t."OriginalInvoiceNumber"  AS original_invoice,
       t."SourceSystem",
       t."Status",
       t."DeviceId",
       t."CreatedByUsername"      AS by_user,
       t."Message"
FROM "DesktopFiscalTransactions" t
WHERE t."DocumentType" = 'CreditNote'
  AND t."TimestampUtc" >= now() - interval '30 days'
  AND lower(t."Status") NOT IN ('success', 'fiscalised')
ORDER BY t."TimestampUtc" DESC
LIMIT 40;
