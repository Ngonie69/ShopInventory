-- Did a van sale whose reply was lost ever reach the server, and if it did, where did it stop?
--
-- Written for the Dinns sale ($226.52) the handset flagged "Your last sale is not confirmed" on
-- 2026-10-02. The handset knows the sale's van_order, but the rep only sees the shop and the total,
-- so this finds it by shop name over the last few days instead.
--
--   psql -h localhost -U postgres -d ShopInventory -P pager=off -v shop="'%dinn%'" -f diagnose-van-sale-in-doubt.sql
--
-- What the answers mean:
--   * nothing anywhere        the POST never reached the API; resending the same basket is safe.
--   * reservation, no sale    the orphan case. The request died after reserving stock and before
--                             writing the receipt row. Once the reservation is Expired, every resend
--                             of the unchanged basket is refused ("Reservation … is Expired, so its
--                             stock is not held. Nothing was signed.") — the basket is stuck for good.
--   * sale row, Success       it went through; do not resend.
--
-- Nothing here writes.

\set ON_ERROR_STOP on
\if :{?shop} \else \set shop '''%dinn%''' \endif

\echo '== 1. Reservations for the shop, last 3 days (Status is the string the poster tests) =='
SELECT  r."Id", r."ReservationId", r."ExternalReferenceId", r."SourceSystem",
        r."CardCode", r."CardName", r."RouteCustomerCode", r."RouteCustomerName",
        r."TotalValue", r."Status", r."CreatedAt", r."ExpiresAt", r."CancelledAt",
        r."CancellationReason", r."SAPDocNum", r."CreatedBy"
FROM    "StockReservations" r
WHERE   r."CreatedAt" > now() - interval '3 days'
  AND  (r."CardName" ILIKE :shop OR r."RouteCustomerName" ILIKE :shop)
ORDER BY r."CreatedAt" DESC;

\echo ''
\echo '== 2. Receipt rows under those references (FiscalizationStatus 0 Pending, 1 Success, 2 Failed, 3 Skipped) =='
SELECT  s."Id", s."ExternalReferenceId", s."SourceSystem", s."FiscalizationStatus"::text,
        s."FiscalReceiptNumber", s."SapDocNum", s."CreatedAt", s."CreatedBy"
FROM    "DesktopSales" s
WHERE   s."ExternalReferenceId" IN (
            SELECT r."ExternalReferenceId" FROM "StockReservations" r
            WHERE  r."CreatedAt" > now() - interval '3 days'
              AND (r."CardName" ILIKE :shop OR r."RouteCustomerName" ILIKE :shop))
   OR  (s."CreatedAt" > now() - interval '3 days'
        AND (s."CardName" ILIKE :shop OR s."RouteCustomerName" ILIKE :shop))
ORDER BY s."CreatedAt" DESC;

\echo ''
\echo '== 3. Invoice queue entries (Status 0 Pending, 1 Processing, 2 Completed, 3 Failed, 4 RequiresReview, ...) =='
SELECT  q."Id", q."ExternalReference", q."Status"::text, q."LastError", q."CreatedAt", q."ProcessedAt"
FROM    "InvoiceQueue" q
WHERE   q."ExternalReference" IN (
            SELECT r."ExternalReferenceId" FROM "StockReservations" r
            WHERE  r."CreatedAt" > now() - interval '3 days'
              AND (r."CardName" ILIKE :shop OR r."RouteCustomerName" ILIKE :shop))
ORDER BY q."CreatedAt" DESC;

\echo ''
\echo '== 4. The orphans fleet-wide: van reservations with no receipt row, last 7 days =='
\echo '   Each of these is a basket a handset may still be holding and can no longer send.'
SELECT  r."ExternalReferenceId", r."RouteCustomerName", r."CardName", r."TotalValue",
        r."Status", r."CreatedAt", r."ExpiresAt", r."CreatedBy"
FROM    "StockReservations" r
WHERE   r."CreatedAt" > now() - interval '7 days'
  AND   r."ExternalReferenceId" LIKE 'VAN%'
  AND   r."SAPDocNum" IS NULL
  AND   NOT EXISTS (SELECT 1 FROM "DesktopSales" s WHERE s."ExternalReferenceId" = r."ExternalReferenceId")
ORDER BY r."CreatedAt" DESC;
