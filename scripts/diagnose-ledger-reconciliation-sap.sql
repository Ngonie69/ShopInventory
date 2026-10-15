-- Which SAP document moved these items? Run in SAP Business One Query Manager (HANA), production
-- company. Read-only. Pairs with diagnose-ledger-reconciliation.sql: that one shows the platform's
-- "No document" correction; this one shows what SAP did in the same hours.
--
-- TransType: 13 A/R invoice, 14 A/R credit memo, 15 delivery, 16 return, 20 goods receipt PO,
-- 59 goods receipt, 60 goods issue, 67 inventory transfer, 162 inventory revaluation,
-- 10000071 inventory posting (count), 202 production order, 310000001 opening balance.
-- A 13 here that the platform did not raise was keyed in the SAP client; a 60 or 10000071 is a
-- manual adjustment; UserSign names who.
SELECT  T0."CreateDate",
        T0."DocTime",
        T0."DocDate",
        T0."TransType",
        T0."BASE_REF"  AS "DocNum",
        T0."ItemCode",
        T0."Warehouse",
        T0."InQty",
        T0."OutQty",
        T1."U_NAME"    AS "User",
        T0."CardCode",
        T0."Comments",
        T0."JrnlMemo"
FROM    OINM T0
LEFT JOIN OUSR T1 ON T1."USERID" = T0."UserSign"
WHERE   T0."ItemCode" IN ('ICV006', 'RMA001')
  AND   T0."CreateDate" >= '2026-10-14'
  AND   T0."CreateDate" <  '2026-10-15'
ORDER BY T0."Warehouse", T0."ItemCode", T0."CreateDate", T0."DocTime";
