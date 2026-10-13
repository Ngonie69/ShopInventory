-- Two questions the van daily report reconciliation (Check-VanDailyReport.sql) left open.
-- Read from production.
--
-- Read-only. No INSERT, UPDATE, DELETE or DDL; safe to run against the live database. The transaction
-- is opened READ ONLY and rolled back. Run it with Check-VanRosterGrowth.ps1, or
--   psql -X -P pager=off -v since=2026-10-01 -f Check-VanRosterGrowth.sql
--
-- PART A. WHAT ARE THE NEW ROUTE CUSTOMERS?
--
-- In the week of 1 October the van reps registered 1,236 route customers against 1,121 check-ins, and
-- the count each truck snapshots at Start Day (the CCR's denominator) climbed by 40 to 60 a day. Either
-- the shops are being registered for the first time, which settles, or a shop is being registered
-- again on every call, which never does and makes every customer report wrong.
--
--   A3 is the decision. "seen_before" counts a new record whose name or phone was already on the same
--   van account. A high share means duplicates.
--   A4 is the second opinion. A record that is never sold to or checked into on a later day was made
--   for one call. A roster being built is used again; a duplicate is not.
--   A5 shows whether the codes are typed or generated, A8 who is creating them.
--
-- PART B. WHERE ARE THE ACCOUNT SALES?
--
-- The sheet's "Account Customers" column has money on one truck only (Bulawayo Local, VAN018) and the
-- van app records no such tender. B1 to B4 list everything that truck's reps created in the week, in
-- every table a sale can land in. B5 looks for the sheet's four amounts, gross or net of 15.5 percent
-- VAT, on any customer. If B5 is empty the account sales are keyed straight into SAP and this system
-- never sees them.

\if :{?since}
\else
\set since '2026-10-01'
\endif
\if :{?days}
\else
\set days 7
\endif
\if :{?account}
\else
\set account 'VAN018'
\endif
\if :{?amounts}
\else
\set amounts '388,1724,3297.39,435'
\endif

\pset footer on
BEGIN TRANSACTION READ ONLY;
SET LOCAL statement_timeout = '120s';

\echo
\echo '== Settings'
SELECT :'since'::date AS since, :days AS days, :'account' AS account, :'amounts' AS amounts,
       (now() AT TIME ZONE 'Africa/Harare')::timestamp(0) AS run_at_cat;

\echo
\echo '== A1. The roster today, per van account'
SELECT upper(btrim(c."AssignedBusinessPartnerCode"))                                   AS account,
       count(*)                                                                         AS records,
       count(*) FILTER (WHERE c."IsActive")                                             AS active,
       count(*) FILTER (WHERE (c."CreatedAt" AT TIME ZONE 'Africa/Harare')::date < :'since'::date)  AS before_since,
       count(*) FILTER (WHERE (c."CreatedAt" AT TIME ZONE 'Africa/Harare')::date >= :'since'::date) AS since,
       min((c."CreatedAt" AT TIME ZONE 'Africa/Harare')::date)                          AS first_record
FROM "RouteCustomers" c
GROUP BY ROLLUP (upper(btrim(c."AssignedBusinessPartnerCode")))
ORDER BY 1 NULLS LAST;

\echo
\echo '== A2. New records per day, all accounts, from three weeks before "since"'
SELECT (c."CreatedAt" AT TIME ZONE 'Africa/Harare')::date         AS day,
       to_char((c."CreatedAt" AT TIME ZONE 'Africa/Harare'), 'Dy') AS dow,
       count(*)                                                    AS new_records,
       count(DISTINCT upper(btrim(c."AssignedBusinessPartnerCode"))) AS accounts
FROM "RouteCustomers" c
WHERE (c."CreatedAt" AT TIME ZONE 'Africa/Harare')::date >= :'since'::date - 21
GROUP BY 1, 2
ORDER BY 1;

\echo
\echo '== A3. THE DECISION: new records since "since" whose name or phone was already on the same account'
WITH keyed AS (
    SELECT c."Id", upper(btrim(c."AssignedBusinessPartnerCode")) AS account, c."CreatedAt",
           (c."CreatedAt" AT TIME ZONE 'Africa/Harare')::date     AS day,
           NULLIF(regexp_replace(lower(coalesce(c."Name", '') || coalesce(c."Surname", '')), '[^a-z0-9]', '', 'g'), '') AS name_key,
           NULLIF(right(regexp_replace(coalesce(c."Phone", ''), '[^0-9]', '', 'g'), 9), '')                             AS phone_key
    FROM "RouteCustomers" c
),
fresh AS (
    SELECT k.*,
           EXISTS (SELECT 1 FROM keyed o WHERE o.account = k.account AND o."Id" <> k."Id"
                     AND o."CreatedAt" < k."CreatedAt" AND o.name_key = k.name_key)   AS name_seen,
           EXISTS (SELECT 1 FROM keyed o WHERE o.account = k.account AND o."Id" <> k."Id"
                     AND o."CreatedAt" < k."CreatedAt" AND o.phone_key = k.phone_key
                     AND length(k.phone_key) >= 7)                                    AS phone_seen
    FROM keyed k
    WHERE k.day >= :'since'::date
)
SELECT account,
       count(*)                                          AS new_records,
       count(*) FILTER (WHERE name_seen)                 AS name_seen_before,
       count(*) FILTER (WHERE phone_seen)                AS phone_seen_before,
       count(*) FILTER (WHERE name_seen OR phone_seen)   AS seen_before,
       round(100.0 * count(*) FILTER (WHERE name_seen OR phone_seen) / NULLIF(count(*), 0), 1) AS seen_before_pct,
       count(*) FILTER (WHERE name_key IS NULL)          AS no_name,
       count(*) FILTER (WHERE phone_key IS NULL)         AS no_phone
FROM fresh
GROUP BY ROLLUP (account)
ORDER BY account NULLS LAST;

\echo
\echo '== A4. SECOND OPINION: were the new records used again on a later day'
WITH fresh AS (
    SELECT c."Id", upper(btrim(c."AssignedBusinessPartnerCode")) AS account, upper(btrim(c."Code")) AS code,
           (c."CreatedAt" AT TIME ZONE 'Africa/Harare')::date     AS day
    FROM "RouteCustomers" c
    WHERE (c."CreatedAt" AT TIME ZONE 'Africa/Harare')::date >= :'since'::date
),
touches AS (
    SELECT upper(btrim(u."AssignedBusinessPartnerCode")) AS account, upper(btrim(t."CustomerCode")) AS code,
           (t."CheckInTime" AT TIME ZONE 'Africa/Harare')::date AS day
    FROM "TimesheetEntries" t
    JOIN "Users" u ON u."Id" = t."UserId"
    WHERE t."Channel" = 1 AND t."CheckInTime" >= :'since'::date - 1
    UNION ALL
    SELECT upper(btrim(r."CardCode")), upper(btrim(r."RouteCustomerCode")),
           (r."CreatedAt" AT TIME ZONE 'Africa/Harare')::date
    FROM "StockReservations" r
    WHERE r."SourceSystem" = 'KefalosVanSales' AND r."Status" = 'Confirmed'
      AND r."RouteCustomerCode" IS NOT NULL AND r."CreatedAt" >= :'since'::date - 1
    UNION ALL
    SELECT upper(btrim(s."CardCode")), upper(btrim(s."RouteCustomerCode")), s."DocDate"
    FROM "DesktopSales" s
    WHERE s."SourceSystem" = 'KefalosVanSales' AND s."RouteCustomerCode" IS NOT NULL
      AND s."DocDate" >= :'since'::date - 1
),
used AS (
    SELECT f."Id", f.account,
           bool_or(t.day = f.day)  AS same_day,
           bool_or(t.day > f.day)  AS later_day
    FROM fresh f
    LEFT JOIN touches t ON t.account = f.account AND t.code = f.code
    GROUP BY f."Id", f.account
)
SELECT account,
       count(*)                                                            AS new_records,
       count(*) FILTER (WHERE coalesce(later_day, false))                  AS used_on_a_later_day,
       count(*) FILTER (WHERE coalesce(same_day, false) AND NOT coalesce(later_day, false)) AS creation_day_only,
       count(*) FILTER (WHERE same_day IS NULL AND later_day IS NULL)      AS never_used,
       round(100.0 * count(*) FILTER (WHERE coalesce(later_day, false)) / NULLIF(count(*), 0), 1) AS later_pct
FROM used
GROUP BY ROLLUP (account)
ORDER BY account NULLS LAST;

\echo
\echo '== A5. What the codes look like, digits shown as #. A generated code repeats one shape'
SELECT CASE WHEN (c."CreatedAt" AT TIME ZONE 'Africa/Harare')::date >= :'since'::date THEN 'since' ELSE 'before' END AS created,
       left(regexp_replace(c."Code", '[0-9]', '#', 'g'), 30) AS code_shape,
       count(*)                                              AS records,
       min(c."Code")                                         AS example
FROM "RouteCustomers" c
GROUP BY 1, 2
ORDER BY 1 DESC, 3 DESC
LIMIT 24;

\echo
\echo '== A6. How much of each record is filled in'
SELECT CASE WHEN (c."CreatedAt" AT TIME ZONE 'Africa/Harare')::date >= :'since'::date THEN 'since' ELSE 'before' END AS created,
       count(*)                                                       AS records,
       count(*) FILTER (WHERE btrim(coalesce(c."Phone", '')) <> '')   AS has_phone,
       count(*) FILTER (WHERE btrim(coalesce(c."Address", '')) <> '') AS has_address,
       count(*) FILTER (WHERE btrim(coalesce(c."VatNumber", '')) <> '') AS has_vat_number,
       count(*) FILTER (WHERE btrim(coalesce(c."Surname", '')) <> '') AS has_surname,
       round(avg(length(btrim(coalesce(c."Name", '')))), 1)           AS avg_name_length
FROM "RouteCustomers" c
GROUP BY 1
ORDER BY 1 DESC;

\echo
\echo '== A7. The 25 names registered most often on one account since "since"'
SELECT upper(btrim(c."AssignedBusinessPartnerCode")) AS account,
       min(c."Name")                                 AS name,
       count(*)                                      AS records,
       count(DISTINCT (c."CreatedAt" AT TIME ZONE 'Africa/Harare')::date) AS on_days,
       min((c."CreatedAt" AT TIME ZONE 'Africa/Harare')::date) AS first_day,
       max((c."CreatedAt" AT TIME ZONE 'Africa/Harare')::date) AS last_day
FROM "RouteCustomers" c
WHERE (c."CreatedAt" AT TIME ZONE 'Africa/Harare')::date >= :'since'::date
GROUP BY 1, regexp_replace(lower(coalesce(c."Name", '') || coalesce(c."Surname", '')), '[^a-z0-9]', '', 'g')
HAVING count(*) > 1
ORDER BY records DESC, account
LIMIT 25;

\echo
\echo '== A8. Who creates them'
SELECT CASE WHEN (c."CreatedAt" AT TIME ZONE 'Africa/Harare')::date >= :'since'::date THEN 'since' ELSE 'before' END AS created,
       coalesce(u."Role", '(no creator recorded)') AS creator_role,
       count(*)                                    AS records,
       count(DISTINCT c."CreatedByUserId")         AS creators
FROM "RouteCustomers" c
LEFT JOIN "Users" u ON u."Id" = c."CreatedByUserId"
GROUP BY 1, 2
ORDER BY 1 DESC, 3 DESC;

\echo
\echo '== B1. Reservations the account''s reps created in the window, every source and status'
WITH reps AS (
    SELECT u."Id", u."Id"::text AS id_text, u."Username"
    FROM "Users" u WHERE upper(btrim(u."AssignedBusinessPartnerCode")) = upper(:'account')
)
SELECT (r."CreatedAt" AT TIME ZONE 'Africa/Harare')::date AS day, r."SourceSystem", r."Status", r."CardCode",
       left(r."CardName", 30) AS card_name, r."DocumentType", count(*) AS docs, round(sum(r."TotalValue"), 2) AS net_value
FROM "StockReservations" r
JOIN reps ON lower(r."CreatedBy") = reps.id_text
WHERE (r."CreatedAt" AT TIME ZONE 'Africa/Harare')::date BETWEEN :'since'::date AND :'since'::date + :days - 1
GROUP BY 1, 2, 3, 4, 5, 6
ORDER BY 1, 2, 3;

\echo
\echo '== B2. Sales orders the account''s reps raised in the window (Source 0 web, 1 mobile)'
WITH reps AS (
    SELECT u."Id" FROM "Users" u WHERE upper(btrim(u."AssignedBusinessPartnerCode")) = upper(:'account')
)
SELECT (o."CreatedAt" AT TIME ZONE 'Africa/Harare')::date AS day, o."Source", o."Status", o."CardCode",
       left(o."CardName", 30) AS card_name, left(coalesce(o."RouteCustomerName", ''), 24) AS route_customer,
       count(*) AS orders, round(sum(o."DocTotal"), 2) AS doc_total, count(o."SAPDocNum") AS in_sap
FROM "SalesOrders" o
JOIN reps ON reps."Id" = o."CreatedByUserId"
WHERE (o."CreatedAt" AT TIME ZONE 'Africa/Harare')::date BETWEEN :'since'::date AND :'since'::date + :days - 1
GROUP BY 1, 2, 3, 4, 5, 6
ORDER BY 1, doc_total DESC;

\echo
\echo '== B3. Sale rows the account''s reps created in the window, every source'
WITH reps AS (
    SELECT u."Id"::text AS id_text FROM "Users" u WHERE upper(btrim(u."AssignedBusinessPartnerCode")) = upper(:'account')
)
SELECT s."DocDate" AS day, s."SourceSystem", s."CardCode", coalesce(s."PaymentMethod", '(none)') AS payment_method,
       count(*) AS docs, round(sum(s."TotalAmount"), 2) AS gross_value, round(sum(s."VatAmount"), 2) AS vat
FROM "DesktopSales" s
JOIN reps ON lower(s."CreatedBy") = reps.id_text
WHERE s."DocDate" BETWEEN :'since'::date AND :'since'::date + :days - 1
GROUP BY 1, 2, 3, 4
ORDER BY 1, 2;

\echo
\echo '== B4. Confirmed reservations out of that account''s van warehouse in the window, by customer card'
SELECT 'reservation' AS kind, r."CardCode", left(r."CardName", 30) AS card_name, count(*) AS docs,
       round(sum(r."TotalValue"), 2) AS value
FROM "StockReservations" r
WHERE r."Status" = 'Confirmed'
  AND (r."CreatedAt" AT TIME ZONE 'Africa/Harare')::date BETWEEN :'since'::date AND :'since'::date + :days - 1
  AND EXISTS (SELECT 1 FROM "StockReservationLines" l
              WHERE l."ReservationId" = r."Id"
                AND upper(l."WarehouseCode") IN (
                    SELECT upper(btrim(w)) FROM "Users" u, unnest(string_to_array(coalesce(u."AssignedWarehouseCodes", ''), ',')) AS w
                    WHERE upper(btrim(u."AssignedBusinessPartnerCode")) = upper(:'account') AND btrim(w) <> ''))
GROUP BY 1, 2, 3
ORDER BY value DESC
LIMIT 30;

\echo
\echo '== B5. Any document in the window whose total is one of the sheet''s account amounts, gross or net'
WITH wanted AS (
    SELECT a::numeric AS amount FROM unnest(string_to_array(:'amounts', ',')) AS a
),
docs AS (
    SELECT 'Invoices' AS source, (i."DocDate" AT TIME ZONE 'Africa/Harare')::date AS day, i."CardCode" AS card,
           i."CardName" AS name, i."DocTotal" AS total, i."SAPDocNum"::text AS ref
    FROM "Invoices" i
    UNION ALL
    SELECT 'SalesOrders', (o."CreatedAt" AT TIME ZONE 'Africa/Harare')::date, o."CardCode", o."CardName", o."DocTotal", o."OrderNumber"
    FROM "SalesOrders" o
    UNION ALL
    SELECT 'StockReservations ' || r."Status", (r."CreatedAt" AT TIME ZONE 'Africa/Harare')::date, r."CardCode",
           coalesce(r."RouteCustomerName", r."CardName"), r."TotalValue", r."ExternalReferenceId"
    FROM "StockReservations" r
    UNION ALL
    SELECT 'DesktopSales ' || s."SourceSystem", s."DocDate", s."CardCode", coalesce(s."RouteCustomerName", s."CardName"),
           s."TotalAmount", s."ExternalReferenceId"
    FROM "DesktopSales" s
    UNION ALL
    SELECT 'InvoiceQueue', (q."CreatedAt" AT TIME ZONE 'Africa/Harare')::date, q."CustomerCode", NULL, q."TotalAmount", q."ExternalReference"
    FROM "InvoiceQueue" q
)
SELECT w.amount AS sheet_amount,
       CASE WHEN abs(d.total - w.amount) <= 0.02 THEN 'gross' ELSE 'net' END AS matched_as,
       d.source, d.day, d.card, left(coalesce(d.name, ''), 32) AS name, d.total, d.ref
FROM wanted w
JOIN docs d
  ON d.day BETWEEN :'since'::date AND :'since'::date + :days - 1
 AND (abs(d.total - w.amount) <= 0.02 OR abs(d.total * 1.155 - w.amount) <= 0.02)
ORDER BY w.amount, d.day, d.source;

ROLLBACK;
