-- =====================================================================================================
-- KEF_CREDIT_MEMO_CHECK: refuses an A/R credit memo (or a credit memo draft) that ZIMRA would refuse.
--
-- Run this in the COMPANY schema (e.g. SET SCHEMA "KEFALOS_USD_NEW2";) as a user that may create
-- procedures there. It only reads. It does nothing until SBO_SP_TransactionNotification calls it
-- (02-TransactionNotification-call.sql).
--
-- Why: a credit note is filed with ZIMRA against ONE invoice, and FDMS refuses it (RCPT035) when the
-- credit notes against that invoice add up to more than the invoice. SAP itself stops a COPIED line
-- going past the invoice's open quantity, but a line typed in by hand (crates, usually) is not linked
-- to anything, and a credit memo with no copied line at all is linked only by the number typed into
-- Customer Ref. No. So SAP lets the credit note exceed the invoice. Two credit notes refused on
-- 9 Nov 2026 (311.85 against 265.65, and 115.50 against 46.20 left) were both built that way.
--
-- WHICH INVOICE - the same rule the fiscal platform uses (Fiscalisation SapCreditNoteLinkResolver):
--   * any line copied from an A/R invoice: that invoice. Customer Ref. No. is ignored.
--   * no line copied: Customer Ref. No. (NumAtCard) is the invoice's DocNum, and it must be exactly one
--     non-cancelled A/R invoice of the same customer.
--
-- Refuses, for a credit memo being added (object 14, 'A') or a credit memo draft being added or
-- changed (object 112 with ObjType 14, 'A' or 'U' - this is what stops the wash bay at "send for
-- approval"):
--   70001  lines copied from more than one invoice
--   70002  a currency different from the invoice's
--   70003  a total above what is left on the invoice:
--          invoice total - credit memos already added against it (not cancelled), by either route
--   70004  nothing copied, and Customer Ref. No. is not exactly one invoice of this customer - the
--          platform cannot fiscalise it, so it is stopped here instead of failing at the console
--
-- Deliberately NOT refused:
--   * hand-typed lines next to copied ones (nearly all crates). They are allowed; their amount counts
--     toward the total, which is what ZIMRA checks.
--   * updates to a credit memo that is already added, so remarks can still be edited on old ones.
--
-- Amounts are compared in the invoice's currency: DocTotal when that is the local currency,
-- DocTotalFC otherwise. Compared to the cent.
-- =====================================================================================================
CREATE OR REPLACE PROCEDURE KEF_CREDIT_MEMO_CHECK (
    IN  object_type      NVARCHAR(30),
    IN  transaction_type NCHAR(1),
    IN  doc_key          NVARCHAR(255),
    OUT error            INT,
    OUT error_message    NVARCHAR(200)
)
LANGUAGE SQLSCRIPT
SQL SECURITY INVOKER
READS SQL DATA
AS
BEGIN
    DECLARE kef_entry      INT := 0;
    DECLARE kef_found      INT := 0;
    DECLARE kef_card       NVARCHAR(50) := '';
    DECLARE kef_ref        NVARCHAR(100) := '';
    DECLARE kef_ref_ok     INT := 0;
    DECLARE kef_ref_count  INT := 0;
    DECLARE kef_cur        NVARCHAR(3) := '';
    DECLARE kef_total      DECIMAL(21, 6) := 0;
    DECLARE kef_invoices   INT := 0;
    DECLARE kef_invoice    INT := 0;
    DECLARE kef_main_cur   NVARCHAR(3) := '';
    DECLARE kef_inv_found  INT := 0;
    DECLARE kef_inv_num    INT := 0;
    DECLARE kef_inv_card   NVARCHAR(50) := '';
    DECLARE kef_inv_cur    NVARCHAR(3) := '';
    DECLARE kef_inv_total  DECIMAL(21, 6) := 0;
    DECLARE kef_credited   DECIMAL(21, 6) := 0;
    DECLARE kef_left       DECIMAL(21, 6) := 0;
    -- Credits added before this one count against the invoice. At add time that is every other credit
    -- memo; for a draft, every credit memo. Bounding by DocEntry is what lets the replay script ask
    -- the same question of a credit memo added weeks ago.
    DECLARE kef_before     INT := 2147483647;

    error := 0;
    error_message := N'';

    IF (:object_type = '14' AND :transaction_type = 'A')
       OR (:object_type = '112' AND (:transaction_type = 'A' OR :transaction_type = 'U')) THEN

        kef_entry := TO_INTEGER(:doc_key);
        SELECT IFNULL(MAX("MainCurncy"), '') INTO kef_main_cur FROM OADM;

        IF :object_type = '14' THEN
            -- CANCELED 'C' is the cancellation document SAP adds when a memo is cancelled: not a credit.
            SELECT COUNT(*), IFNULL(MAX("CardCode"), ''), IFNULL(MAX(TRIM("NumAtCard")), ''), IFNULL(MAX("DocCur"), ''),
                   IFNULL(MAX(CASE WHEN "DocCur" = :kef_main_cur THEN "DocTotal" ELSE "DocTotalFC" END), 0)
              INTO kef_found, kef_card, kef_ref, kef_cur, kef_total
              FROM ORIN
             WHERE "DocEntry" = :kef_entry AND "CANCELED" = 'N';

            SELECT COUNT(DISTINCT "BaseEntry"), IFNULL(MIN("BaseEntry"), 0)
              INTO kef_invoices, kef_invoice
              FROM RIN1
             WHERE "DocEntry" = :kef_entry AND "BaseType" = 13 AND "BaseEntry" IS NOT NULL;

            kef_before := :kef_entry;
        ELSE
            -- Only open credit memo drafts. A draft closes when it is added, and the credit memo it
            -- became is checked on its own add.
            SELECT COUNT(*), IFNULL(MAX("CardCode"), ''), IFNULL(MAX(TRIM("NumAtCard")), ''), IFNULL(MAX("DocCur"), ''),
                   IFNULL(MAX(CASE WHEN "DocCur" = :kef_main_cur THEN "DocTotal" ELSE "DocTotalFC" END), 0)
              INTO kef_found, kef_card, kef_ref, kef_cur, kef_total
              FROM ODRF
             WHERE "DocEntry" = :kef_entry AND "ObjType" = '14' AND "DocStatus" = 'O' AND "CANCELED" = 'N';

            SELECT COUNT(DISTINCT "BaseEntry"), IFNULL(MIN("BaseEntry"), 0)
              INTO kef_invoices, kef_invoice
              FROM DRF1
             WHERE "DocEntry" = :kef_entry AND "BaseType" = 13 AND "BaseEntry" IS NOT NULL;
        END IF;

        IF :kef_found > 0 AND :kef_invoices > 1 THEN
            error := 70001;
            error_message := LEFT(N'KEF: this credit note copies from ' || TO_NVARCHAR(:kef_invoices)
                || N' invoices. ZIMRA takes one invoice per credit note: raise a separate credit note for each invoice.', 200);

        ELSEIF :kef_found > 0 AND :kef_invoices = 0 THEN
            -- Nothing copied: Customer Ref. No. names the invoice. Up to 9 digits, so TO_INTEGER cannot overflow.
            SELECT CASE WHEN :kef_ref LIKE_REGEXPR '^[0-9]{1,9}$' THEN 1 ELSE 0 END INTO kef_ref_ok FROM DUMMY;
            IF :kef_ref_ok = 1 THEN
                SELECT COUNT(*), IFNULL(MAX("DocEntry"), 0)
                  INTO kef_ref_count, kef_invoice
                  FROM OINV
                 WHERE "DocNum" = TO_INTEGER(:kef_ref) AND "CardCode" = :kef_card AND "CANCELED" = 'N';
            END IF;

            IF :kef_ref_count = 1 THEN
                kef_invoices := 1;
            ELSE
                error := 70004;
                error_message := LEFT(CASE WHEN :kef_ref_count > 1
                    THEN N'KEF: ' || :kef_card || N' has ' || TO_NVARCHAR(:kef_ref_count) || N' invoices numbered '
                         || :kef_ref || N'. Create the credit note with Copy From on the invoice instead.'
                    ELSE N'KEF: nothing is copied from an invoice, and Customer Ref. No. ''' || LEFT(:kef_ref, 20)
                         || N''' is not an invoice of ' || :kef_card
                         || N'. Use Copy From, or put the invoice number in Customer Ref. No.'
                    END, 200);
            END IF;
        END IF;

        IF :error = 0 AND :kef_found > 0 AND :kef_invoices = 1 THEN
            SELECT COUNT(*), IFNULL(MAX("DocNum"), 0), IFNULL(MAX("CardCode"), ''), IFNULL(MAX("DocCur"), ''),
                   IFNULL(MAX(CASE WHEN "DocCur" = :kef_main_cur THEN "DocTotal" ELSE "DocTotalFC" END), 0)
              INTO kef_inv_found, kef_inv_num, kef_inv_card, kef_inv_cur, kef_inv_total
              FROM OINV
             WHERE "DocEntry" = :kef_invoice;

            IF :kef_inv_found > 0 AND :kef_cur <> :kef_inv_cur THEN
                error := 70002;
                error_message := LEFT(N'KEF: this credit note is in ' || :kef_cur || N' but invoice '
                    || TO_NVARCHAR(:kef_inv_num) || N' is in ' || :kef_inv_cur
                    || N'. A credit note must be in its invoice''s currency.', 200);

            ELSEIF :kef_inv_found > 0 THEN
                -- Every earlier credit memo the platform would file against this invoice, at its full
                -- total: copied from it, or nothing copied and its number in Customer Ref. No.
                SELECT IFNULL(SUM(CASE WHEN o."DocCur" = :kef_main_cur THEN o."DocTotal" ELSE o."DocTotalFC" END), 0)
                  INTO kef_credited
                  FROM ORIN o
                 WHERE o."CANCELED" = 'N'
                   AND o."DocCur" = :kef_inv_cur
                   AND o."DocEntry" < :kef_before
                   AND (EXISTS (SELECT 1 FROM RIN1 l
                                 WHERE l."DocEntry" = o."DocEntry"
                                   AND l."BaseType" = 13
                                   AND l."BaseEntry" = :kef_invoice)
                        OR (o."CardCode" = :kef_inv_card
                            AND LTRIM(TRIM(o."NumAtCard"), '0') = TO_NVARCHAR(:kef_inv_num)
                            AND NOT EXISTS (SELECT 1 FROM RIN1 l
                                             WHERE l."DocEntry" = o."DocEntry"
                                               AND l."BaseType" = 13
                                               AND l."BaseEntry" IS NOT NULL)));

                kef_left := :kef_inv_total - :kef_credited;

                IF ROUND(:kef_total, 2) > ROUND(:kef_left, 2) THEN
                    error := 70003;
                    error_message := LEFT(N'KEF: ' || TO_NVARCHAR(TO_DECIMAL(:kef_total, 19, 2))
                        || N' is more than is left on invoice ' || TO_NVARCHAR(:kef_inv_num) || N': '
                        || TO_NVARCHAR(TO_DECIMAL(:kef_inv_total, 19, 2)) || N' invoiced, '
                        || TO_NVARCHAR(TO_DECIMAL(:kef_credited, 19, 2)) || N' already credited, '
                        || TO_NVARCHAR(TO_DECIMAL(GREATEST(:kef_left, 0), 19, 2)) || N' left. Reduce the credit note.', 200);
                END IF;
            END IF;
        END IF;
    END IF;
END;
