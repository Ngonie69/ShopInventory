-- =====================================================================================================
-- Paste this block INTO the company's existing SBO_SP_TransactionNotification, between
--   "ADD YOUR CODE HERE" and the final "select :error, :error_message FROM dummy;".
--
-- Do not replace the procedure: other add-ons may have their own blocks in it. Open it (HANA Studio /
-- DBeaver: the procedure's definition, or SELECT DEFINITION FROM PROCEDURES WHERE SCHEMA_NAME = '<company>'
-- AND PROCEDURE_NAME = 'SBO_SP_TransactionNotification'), save a copy of the definition first, add
-- the block, and re-create it with the rest unchanged.
--
-- It only acts when nothing earlier has already refused the document, and it leaves error and
-- error_message untouched unless it refuses. To switch the guard off, delete this block again.
-- =====================================================================================================

-- KEF credit memo guard (ShopInventory scripts/sap-credit-memo-guard): no credit note past what is left on its invoice.
IF :error = 0 AND (:object_type = '14' OR :object_type = '112') THEN
    BEGIN
        DECLARE kef_error   INT := 0;
        DECLARE kef_message NVARCHAR(200) := N'';
        CALL KEF_CREDIT_MEMO_CHECK(:object_type, :transaction_type, :list_of_cols_val_tab_del, kef_error, kef_message);
        IF :kef_error <> 0 THEN
            error := :kef_error;
            error_message := :kef_message;
        END IF;
    END;
END IF;
-- end KEF credit memo guard
