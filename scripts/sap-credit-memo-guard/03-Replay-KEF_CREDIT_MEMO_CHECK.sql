-- =====================================================================================================
-- Read-only. Asks KEF_CREDIT_MEMO_CHECK about every credit memo added since a date, exactly as it
-- would have been asked at the moment each one was added, and lists the ones it would have refused.
-- Run after 01 and BEFORE 02, in the company schema, to see what switching the guard on would stop.
--
-- Expected on production: the two credit notes the console refused on 9 Nov 2026 both answer 70003.
-- =====================================================================================================

-- The two known over-credits, one at a time. Fill in their DocEntries (ORIN."DocEntry", not DocNum) and
-- uncomment; expected 70003 with "311.85 ... 265.65 left" and "115.50 ... 46.20 left":
-- CALL KEF_CREDIT_MEMO_CHECK('14', 'A', '<DocEntry of credit note A>', ?, ?);
-- CALL KEF_CREDIT_MEMO_CHECK('14', 'A', '<DocEntry of credit note B>', ?, ?);

-- Everything since a date:
DO (IN since DATE => '2026-10-01')
BEGIN
    DECLARE kef_error   INT := 0;
    DECLARE kef_message NVARCHAR(200) := N'';
    DECLARE results TABLE ("DocNum" INT, "DocDate" DATE, "CardCode" NVARCHAR(50), "UserSign" INT,
                           "DocTotal" DECIMAL(21, 6), "Error" INT, "Message" NVARCHAR(200));
    DECLARE CURSOR memos FOR
        SELECT "DocEntry", "DocNum", "DocDate", "CardCode", "UserSign", "DocTotal"
          FROM ORIN
         WHERE "DocDate" >= :since AND "CANCELED" = 'N'
         ORDER BY "DocEntry";

    FOR m AS memos DO
        CALL KEF_CREDIT_MEMO_CHECK('14', 'A', TO_NVARCHAR(m."DocEntry"), kef_error, kef_message);
        IF :kef_error <> 0 THEN
            :results.INSERT((m."DocNum", m."DocDate", m."CardCode", m."UserSign", m."DocTotal", :kef_error, :kef_message));
        END IF;
    END FOR;

    SELECT * FROM :results ORDER BY "DocNum";
END;
