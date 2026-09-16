-- Who actually signs a van sale's ZIMRA receipt, read from production.
--
-- Read-only. No INSERT, UPDATE, DELETE or DDL; safe to run against the live database.
--
-- WHY THIS EXISTS
--
-- Van sales can be credited through the desktop credit-note dialog only if REVMax holds the original
-- receipt, because RevmaxDesktopCreditGateway.ReadOriginalAsync looks it up there and refuses with
-- ORIGINAL_RECEIPT_NOT_FOUND otherwise.
--
-- Under the live provider (Fiscalisation:Provider = Revmax) it should: no handset can sign, because
-- GetVanSalesFiscalLeaseHandler hands out an office-fiscalised lease when the platform is off, so an
-- offline van sale arrives unstamped and DesktopSaleFiscalisationSweep signs it here, on the REVMax
-- device. That is the reading this whole van-credit path rests on, and it is a claim about production
-- data rather than about the code — so it is checked rather than assumed.
--
-- HOW TO READ THE ANSWER
--
--   server_fiscalised   the expected state. FiscalizationAttempts > 0 and the device is REVMax's, so
--                       REVMax holds the receipt and a van sale can be credited from the dialog.
--
--   handset_stamped     van credit notes are BLOCKED. The receipt came off a handset's own chain,
--                       REVMax has never seen it, and no amount of UI work changes that — the credit
--                       would have to be signed by the handset. Say so rather than building for it.
--
--   not_fiscalised      neither. Since the posting gate now requires a receipt, these do not reach
--                       SAP at all and want a person: check the fiscalisation console.
--
-- Expect server_fiscalised to be effectively all of them. Any handset_stamped row is the finding.

\echo '== 1. How recent van sales were fiscalised =='

SELECT
    CASE
        WHEN s."FiscalizationStatus" <> 1 THEN 'not_fiscalised'
        WHEN s."FiscalizationAttempts" > 0 AND s."FiscalDeviceNumber" = '22862' THEN 'server_fiscalised'
        WHEN s."ReceiptGlobalNo" IS NOT NULL AND s."FiscalizationAttempts" = 0 THEN 'handset_stamped'
        ELSE 'unclear_look_at_the_row'
    END                                  AS fiscal_owner,
    count(*)                             AS sales,
    min(s."DocDate")                     AS earliest,
    max(s."DocDate")                     AS latest,
    count(*) FILTER (WHERE s."SapDocNum" IS NOT NULL) AS reached_sap
FROM "DesktopSales" s
WHERE s."SourceSystem" = 'KefalosVanSales'
  AND s."DocDate" >= CURRENT_DATE - INTERVAL '30 days'
GROUP BY 1
ORDER BY sales DESC;

\echo ''
\echo '== 2. A few rows in full, newest first, so an unclear verdict can be looked at =='

SELECT
    s."ExternalReferenceId",
    s."DocDate",
    s."WarehouseCode",
    s."RouteCustomerCode",
    s."RouteCustomerName",
    s."FiscalizationStatus"    AS fiscal_status_0pending_1success_2failed_3skipped,
    s."FiscalizationAttempts"  AS server_attempts,
    s."FiscalDeviceNumber"     AS device,
    s."ReceiptGlobalNo",
    s."ReceiptIngestStatus"    AS ingest_0na_1pending_6unstamped,
    s."SapDocNum",
    s."NumAtCard"
FROM "DesktopSales" s
WHERE s."SourceSystem" = 'KefalosVanSales'
  AND s."DocDate" >= CURRENT_DATE - INTERVAL '30 days'
ORDER BY s."DocDate" DESC, s."Id" DESC
LIMIT 15;

\echo ''
\echo '== 3. What the new posting gate would hold back =='
\echo '   Van and till/vending sales that reached SAP WITHOUT a fiscal receipt, and so would no longer.'
\echo '   Historic rows only: nothing new can be created this way any more.'

SELECT
    s."SourceSystem",
    s."FiscalizationStatus" AS fiscal_status_0pending_1success_2failed_3skipped,
    count(*)                AS already_in_sap,
    min(s."DocDate")        AS earliest,
    max(s."DocDate")        AS latest
FROM "DesktopSales" s
WHERE s."SourceSystem" IN ('KefalosVanSales', 'KefalosShopTill', 'KefalosVending')
  AND s."SapDocNum" IS NOT NULL
  AND s."FiscalizationStatus" <> 1
GROUP BY 1, 2
ORDER BY 1, 2;

\echo ''
\echo '== 4. What the new posting gate is holding back RIGHT NOW =='
\echo '   Unposted sales with no receipt. These need a person: retry fiscalisation from the console.'

SELECT
    s."SourceSystem",
    s."FiscalizationStatus" AS fiscal_status_0pending_1success_2failed_3skipped,
    count(*)                AS awaiting_sap,
    sum(s."TotalAmount")    AS value,
    min(s."DocDate")        AS earliest,
    max(s."DocDate")        AS latest
FROM "DesktopSales" s
WHERE s."SourceSystem" IN ('KefalosVanSales', 'KefalosShopTill', 'KefalosVending')
  AND s."SapDocNum" IS NULL
  AND s."ConsolidationStatus" = 0
  AND s."FiscalizationStatus" <> 1
GROUP BY 1, 2
ORDER BY 1, 2;
