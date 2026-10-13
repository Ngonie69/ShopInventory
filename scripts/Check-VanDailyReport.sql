-- Sets the hand-kept VANSALES DAILY REPORT workbook beside what this system recorded for the same
-- trucks on the same days. Read from production.
--
-- Read-only. No INSERT, UPDATE, DELETE or DDL; safe to run against the live database. The transaction
-- is opened READ ONLY and rolled back. Run it with Check-VanDailyReport.ps1, or
--   psql -X -P pager=off -v ON_ERROR_STOP=1 -f van-daily-report-sheet.sql -f Check-VanDailyReport.sql
--
-- The sheet's rows arrive in the psql variable sheet_json, which van-daily-report-sheet.sql sets.
-- scripts/VanDailyReport/export_sheet.py writes that file from the workbook.
--
-- WHY THIS EXISTS
--
-- The office types one row per truck per day into the workbook: route, customers, departure, CCR,
-- PCR, sales, Ecocash, odometer, fuel. /van-sales/compliance holds most of the same columns, taken
-- from the handset's Start Day and End Day, its check-ins, its invoices and the Cartrack unit. Before
-- the workbook is retired, two things have to be known: whether the system has a figure at all for
-- the days the sheet covers, and whether the figures it has agree.
--
-- HOW THE SYSTEM SIDE IS MEASURED
--
-- As the reports measure it (VanSalesFactReader, VanTruckDays), per truck-day:
--
--   truck      = the van account the reps sign in under (Users.AssignedBusinessPartnerCode). Two
--                reps share a truck, so everything is pooled across them.
--   sales      = offline sales (DesktopSales, by DocDate) plus online invoices (StockReservations
--                with Status 'Confirmed', by CreatedAt in CAT), credited to the truck of the rep in
--                CreatedBy.
--   checkins   = distinct shops either rep checked into (TimesheetEntries, van channel).
--   productive = distinct shops that bought, plus one if any sale names no shop.
--   calls      = shops checked into or sold to.
--   CCR        = checkins / planned, where planned is the count taken at Start Day.
--   PCR        = productive / calls, only on a day that has a check-in.
--   AOV        = sales / productive.
--
-- HOW TO READ THE ANSWER
--
--   Section 3 is the first decision. Each column counts the sheet's submitted days on which the
--   system holds that piece. A column far below "sheet_days" is data only the sheet has today.
--
--   Section 4 is the second. "diff" is system minus sheet. Before calling a gap a discrepancy:
--     - online invoices are stored net of VAT and offline sales gross, so a truck that sells online
--       reads low by about the VAT on "online_usd";
--     - "expired" counts online sales made while SAP was down. They were sold and are in no report;
--     - "other_ccy" is money in another currency and is not added to "sys_usd".
--
--   Section 7: "planned" is the truck's whole customer list, not the day's area, so the system's CCR
--   reads far lower than the sheet's on a truck that works a different area each day. That is a
--   difference of definition, not a rep missing calls.
--
-- WHAT IT CANNOT SEE
--
--   Fuel issued and monthly targets are in the sheet only. A sale by a login with no van account is
--   on no truck and appears only in section 11.

\if :{?sheet_json}
\else
DO $$ BEGIN RAISE EXCEPTION 'sheet_json is not set: run van-daily-report-sheet.sql first, in the same psql session'; END $$;
\endif

\pset footer on
BEGIN TRANSACTION READ ONLY;
SET LOCAL statement_timeout = '120s';

-- The comparison, built once and used by every section below. format() puts the sheet in, because
-- psql does not expand a variable inside text it has already substituted. Nothing in this block may
-- contain a percent sign other than the one placeholder.
SELECT format($core$
WITH sheet AS (
    SELECT s.d::date AS day, s.territory, upper(btrim(s.truck)) AS truck, s.route, s.submitted,
           s.customers, s.dep::time AS dep, s.back::time AS back, s.ccr, s.pcr, s.rti, s.aos,
           s.sales, s.new_customers, s.ecocash, s.account, s.odo_open, s.odo_close, s.km, s.fuel
    FROM jsonb_to_recordset(%1$L::jsonb) AS s(
        d text, territory text, truck text, route text, submitted boolean, customers numeric,
        dep text, back text, ccr numeric, pcr numeric, rti numeric, aos numeric, sales numeric,
        new_customers numeric, ecocash numeric, account numeric, odo_open numeric,
        odo_close numeric, km numeric, fuel numeric)
),
period AS (
    SELECT min(day) AS first_day,
           max(day) AS last_day,
           (min(day)::timestamp AT TIME ZONE 'Africa/Harare')       AS from_utc,
           ((max(day) + 1)::timestamp AT TIME ZONE 'Africa/Harare') AS to_utc
    FROM sheet
),
reps AS (
    SELECT u."Id" AS user_id, u."Username" AS username, u."Role" AS role, u."IsActive" AS active,
           u."RouteId" AS route_id, upper(btrim(u."AssignedBusinessPartnerCode")) AS truck
    FROM "Users" u
    WHERE btrim(coalesce(u."AssignedBusinessPartnerCode", '')) <> ''
),
raw_sales AS (
    SELECT s."CreatedBy" AS created_by, s."DocDate" AS day, 'offline' AS source, 'Confirmed' AS status,
           s."TotalAmount" AS amount, s."Currency" AS currency, s."PaymentMethod" AS payment_method,
           s."RouteCustomerCode" AS shop
    FROM "DesktopSales" s
    CROSS JOIN period p
    WHERE s."SourceSystem" = 'KefalosVanSales'
      AND s."DocDate" BETWEEN p.first_day AND p.last_day
    UNION ALL
    SELECT r."CreatedBy", (r."CreatedAt" AT TIME ZONE 'Africa/Harare')::date, 'online', r."Status",
           r."TotalValue", r."Currency", r."PaymentMethod", r."RouteCustomerCode"
    FROM "StockReservations" r
    CROSS JOIN period p
    WHERE r."SourceSystem" = 'KefalosVanSales'
      AND r."CreatedAt" >= p.from_utc
      AND r."CreatedAt" < p.to_utc
),
cleaned AS (
    SELECT coalesce(rep.truck, '(no van account)')            AS truck,
           rs.day, rs.source, rs.status, rs.amount,
           upper(nullif(btrim(coalesce(rs.currency, '')), '')) AS currency,
           nullif(btrim(coalesce(rs.payment_method, '')), '')  AS payment_method,
           upper(nullif(btrim(coalesce(rs.shop, '')), ''))     AS shop
    FROM raw_sales rs
    LEFT JOIN reps rep
           ON rep.user_id = CASE
                  WHEN rs.created_by ~ '^[{]?[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}[}]?$'
                  THEN rs.created_by::uuid
              END
),
sold AS (
    SELECT c.*,
           (c.status = 'Confirmed')                   AS confirmed,
           (c.currency IS NULL OR c.currency = 'USD') AS usd,
           CASE
               WHEN c.payment_method IS NULL THEN 'untendered'
               WHEN lower(c.payment_method) = 'cash' THEN 'cash'
               WHEN position('ecocash' IN lower(c.payment_method)) > 0 THEN 'ecocash'
               WHEN position('innbucks' IN lower(c.payment_method)) > 0
                 OR position('inbucks' IN lower(c.payment_method)) > 0 THEN 'innbucks'
               ELSE 'other'
           END                                        AS tender
    FROM cleaned c
),
sys_sales AS (
    SELECT truck, day,
           count(*) FILTER (WHERE confirmed)                                               AS docs,
           count(*) FILTER (WHERE confirmed AND source = 'online')                         AS online_docs,
           coalesce(sum(amount) FILTER (WHERE confirmed AND usd), 0)                       AS usd,
           coalesce(sum(amount) FILTER (WHERE confirmed AND usd AND source = 'online'), 0) AS online_usd,
           coalesce(sum(amount) FILTER (WHERE confirmed AND NOT usd), 0)                   AS other_ccy,
           coalesce(sum(amount) FILTER (WHERE confirmed AND usd AND tender = 'cash'), 0)       AS cash,
           coalesce(sum(amount) FILTER (WHERE confirmed AND usd AND tender = 'ecocash'), 0)    AS ecocash,
           coalesce(sum(amount) FILTER (WHERE confirmed AND usd AND tender = 'innbucks'), 0)   AS innbucks,
           coalesce(sum(amount) FILTER (WHERE confirmed AND usd AND tender = 'other'), 0)      AS other_tender,
           coalesce(sum(amount) FILTER (WHERE confirmed AND usd AND tender = 'untendered'), 0) AS untendered,
           coalesce(bool_or(shop IS NULL) FILTER (WHERE confirmed), false)                 AS unattributed,
           count(*) FILTER (WHERE status = 'Expired')                                      AS expired_docs,
           coalesce(sum(amount) FILTER (WHERE status = 'Expired'), 0)                      AS expired_value
    FROM sold
    GROUP BY truck, day
),
visits AS (
    SELECT DISTINCT rep.truck,
           (t."CheckInTime" AT TIME ZONE 'Africa/Harare')::date AS day,
           upper(btrim(t."CustomerCode"))                       AS shop
    FROM "TimesheetEntries" t
    CROSS JOIN period p
    JOIN reps rep ON rep.user_id = t."UserId"
    WHERE t."Channel" = 1
      AND t."CheckInTime" >= p.from_utc
      AND t."CheckInTime" < p.to_utc
),
calls AS (
    SELECT truck, day,
           count(*) FILTER (WHERE visited) AS checkins,
           count(*) FILTER (WHERE bought)  AS buyers,
           count(*)                        AS called
    FROM (
        SELECT truck, day, shop, bool_or(visited) AS visited, bool_or(bought) AS bought
        FROM (
            SELECT truck, day, shop, true AS visited, false AS bought FROM visits
            UNION ALL
            SELECT truck, day, shop, false, true FROM sold WHERE confirmed AND shop IS NOT NULL
        ) either
        GROUP BY truck, day, shop
    ) per_shop
    GROUP BY truck, day
),
route_days AS (
    SELECT rep.truck, d."TradingDate" AS day,
           count(*)                                                 AS start_days,
           count(*) FILTER (WHERE d."ReturnedAt" IS NOT NULL)       AS end_days,
           max(d."PlannedCustomerCount")                            AS planned,
           (min(d."DepartedAt") AT TIME ZONE 'Africa/Harare')       AS departed,
           (max(d."ReturnedAt") AT TIME ZONE 'Africa/Harare')       AS returned,
           min(d."StartingMileage")                                 AS odo_open,
           max(d."ClosingMileage")                                  AS odo_close,
           max(d."ClosingMileage" - d."StartingMileage")
               FILTER (WHERE d."ClosingMileage" >= d."StartingMileage") AS km,
           sum(d."DeclaredCash")                                    AS declared_cash,
           sum(d."DeclaredEcocash")                                 AS declared_ecocash,
           sum(d."DeclaredInnbucks")                                AS declared_innbucks,
           max(d."RtiOut")                                          AS rti_out,
           max(d."RtiReturned")                                     AS rti_returned,
           string_agg(DISTINCT d."RouteName", ' / ')                AS route_name
    FROM "VanRouteDays" d
    CROSS JOIN period p
    JOIN reps rep ON rep.user_id = d."UserId"
    WHERE d."TradingDate" BETWEEN p.first_day AND p.last_day
    GROUP BY rep.truck, d."TradingDate"
),
day_regs AS (
    SELECT DISTINCT rep.truck, d."TradingDate" AS day,
           upper(regexp_replace(d."TruckRegNo", '[^A-Za-z0-9]', '', 'g')) AS reg
    FROM "VanRouteDays" d
    CROSS JOIN period p
    JOIN reps rep ON rep.user_id = d."UserId"
    WHERE d."TradingDate" BETWEEN p.first_day AND p.last_day
      AND regexp_replace(coalesce(d."TruckRegNo", ''), '[^A-Za-z0-9]', '', 'g') <> ''
),
default_regs AS (
    SELECT upper(btrim(v."BusinessPartnerCode")) AS truck, v."RegistrationNormalized" AS reg
    FROM "TelematicsVehicles" v
    WHERE btrim(coalesce(v."BusinessPartnerCode", '')) <> ''
    UNION
    SELECT rep.truck, upper(regexp_replace(r."TruckRegNo", '[^A-Za-z0-9]', '', 'g'))
    FROM reps rep
    JOIN "Routes" r ON r."Id" = rep.route_id
    WHERE regexp_replace(coalesce(r."TruckRegNo", ''), '[^A-Za-z0-9]', '', 'g') <> ''
),
truck_day_regs AS (
    SELECT truck, day, reg FROM day_regs
    UNION
    SELECT sh.truck, sh.day, df.reg
    FROM sheet sh
    JOIN default_regs df ON df.truck = sh.truck
    WHERE NOT EXISTS (SELECT 1 FROM day_regs dr WHERE dr.truck = sh.truck AND dr.day = sh.day)
),
gps AS (
    SELECT tr.truck, tr.day,
           string_agg(DISTINCT tr.reg, ', ')                                          AS regs,
           (min(coalesce(r."FirstDepartureUtc", r."FirstIgnitionOnUtc"))
               AT TIME ZONE 'Africa/Harare')                                          AS departed,
           (max(r."LastIgnitionOffUtc") AT TIME ZONE 'Africa/Harare')                 AS last_off,
           sum(r."DistanceMetres") / 1000.0                                           AS km,
           coalesce(bool_or(r."DistanceMetres" > 0 OR r."FirstDepartureUtc" IS NOT NULL), false) AS moved
    FROM truck_day_regs tr
    JOIN "VehicleDayRollups" r
      ON r."RegistrationNormalized" = tr.reg AND r."TradingDate" = tr.day
    GROUP BY tr.truck, tr.day
),
new_shops AS (
    SELECT rep.truck, (c."CreatedAt" AT TIME ZONE 'Africa/Harare')::date AS day, count(*) AS added
    FROM "RouteCustomers" c
    CROSS JOIN period p
    JOIN reps rep ON rep.user_id = c."CreatedByUserId"
    WHERE c."CreatedAt" >= p.from_utc
      AND c."CreatedAt" < p.to_utc
    GROUP BY 1, 2
),
j AS (
    SELECT sh.day, sh.territory, sh.truck, sh.route, sh.submitted,
           sh.customers, sh.dep, sh.back, sh.ccr, sh.pcr, sh.rti, sh.aos, sh.sales, sh.new_customers,
           sh.ecocash, sh.account, sh.odo_open, sh.odo_close, sh.km, sh.fuel,
           coalesce(ss.docs, 0)        AS docs,
           coalesce(ss.online_docs, 0) AS online_docs,
           ss.usd                      AS sys_usd,
           ss.online_usd, ss.other_ccy,
           ss.cash                     AS sys_cash,
           ss.ecocash                  AS sys_ecocash,
           ss.innbucks                 AS sys_innbucks,
           ss.other_tender             AS sys_other,
           ss.untendered               AS sys_untendered,
           coalesce(ss.expired_docs, 0) AS expired_docs,
           ss.expired_value,
           coalesce(c.checkins, 0)     AS checkins,
           coalesce(c.buyers, 0) + CASE WHEN ss.unattributed THEN 1 ELSE 0 END AS productive,
           GREATEST(coalesce(c.called, 0),
                    coalesce(c.buyers, 0) + CASE WHEN ss.unattributed THEN 1 ELSE 0 END) AS calls_made,
           coalesce(rd.start_days, 0)  AS start_days,
           coalesce(rd.end_days, 0)    AS end_days,
           rd.planned, rd.route_name,
           rd.departed                 AS h_departed,
           rd.returned                 AS h_returned,
           rd.odo_open                 AS h_open,
           rd.odo_close                AS h_close,
           rd.km                       AS h_km,
           rd.declared_cash, rd.declared_ecocash, rd.declared_innbucks, rd.rti_out, rd.rti_returned,
           g.regs,
           g.departed                  AS gps_departed,
           g.last_off                  AS gps_last_off,
           g.km                        AS gps_km,
           coalesce(g.moved, false)    AS gps_moved,
           coalesce(n.added, 0)        AS new_shops
    FROM sheet sh
    LEFT JOIN sys_sales ss  USING (truck, day)
    LEFT JOIN calls c       USING (truck, day)
    LEFT JOIN route_days rd USING (truck, day)
    LEFT JOIN gps g         USING (truck, day)
    LEFT JOIN new_shops n   USING (truck, day)
)
$core$, :'sheet_json') AS core \gset

\echo
\echo '== 1. The sheet as loaded. These totals must equal the workbook''s own Monthly Summary'
:core
SELECT count(*)                           AS sheet_rows,
       count(*) FILTER (WHERE submitted)  AS submitted,
       min(day)                           AS first_day,
       max(day)                           AS last_day,
       count(DISTINCT truck)              AS trucks,
       round(sum(sales), 2)               AS sales,
       round(sum(ecocash), 2)             AS ecocash,
       round(sum(account), 2)             AS account,
       round(sum(km), 2)                  AS km,
       round(sum(fuel), 2)                AS fuel,
       (now() AT TIME ZONE 'Africa/Harare')::timestamp(0) AS run_at_cat
FROM sheet;

\echo
\echo '== 2. Which van account each sheet territory was read as. CHECK THIS FIRST: the pairing was'
\echo '      made by name. A territory with no reps, or the wrong ones, makes every section below wrong'
:core
SELECT t.territory,
       t.truck AS van_account,
       (SELECT string_agg(rep.username || ' (' || coalesce(rep.role, '?')
                          || CASE WHEN rep.active THEN '' ELSE ', inactive' END || ')',
                          ', ' ORDER BY rep.username)
        FROM reps rep WHERE rep.truck = t.truck)                         AS reps_on_account,
       (SELECT string_agg(DISTINCT r."Code" || ' ' || r."Name", ' / ')
        FROM reps rep JOIN "Routes" r ON r."Id" = rep.route_id
        WHERE rep.truck = t.truck)                                       AS system_route,
       (SELECT string_agg(DISTINCT df.reg, ', ')
        FROM default_regs df WHERE df.truck = t.truck)                   AS vehicles
FROM (SELECT DISTINCT territory, truck FROM sheet) t
ORDER BY t.truck;

\echo
\echo '== 3. FIRST DECISION: of the days the sheet has a row for, on how many does the system hold each piece'
:core
SELECT CASE WHEN GROUPING(truck) = 1 THEN 'ALL TRUCKS' ELSE max(territory) END    AS territory,
       truck,
       count(*) FILTER (WHERE submitted)                                          AS sheet_days,
       count(*) FILTER (WHERE submitted AND start_days > 0)                       AS start_day,
       count(*) FILTER (WHERE submitted AND end_days > 0)                         AS end_day,
       count(*) FILTER (WHERE submitted AND h_open IS NOT NULL AND h_close IS NOT NULL) AS odometer,
       count(*) FILTER (WHERE submitted AND checkins > 0)                         AS check_ins,
       count(*) FILTER (WHERE submitted AND docs > 0)                             AS sales,
       count(*) FILTER (WHERE submitted AND coalesce(declared_cash, declared_ecocash, declared_innbucks) IS NOT NULL) AS declared,
       count(*) FILTER (WHERE submitted AND gps_moved)                            AS gps_moved
FROM j
GROUP BY ROLLUP (truck)
ORDER BY truck NULLS LAST;

\echo
\echo '== 4. SECOND DECISION: sales, day by day. diff = system minus sheet'
:core
SELECT to_char(day, 'Dy DD')                                  AS day,
       territory,
       left(coalesce(route, '(no row)'), 24)                  AS sheet_route,
       round(sales, 2)                                        AS sheet_sales,
       round(sys_usd, 2)                                      AS sys_usd,
       round(coalesce(sys_usd, 0) - coalesce(sales, 0), 2)    AS diff,
       round(100.0 * (coalesce(sys_usd, 0) - sales) / NULLIF(sales, 0), 1) AS diff_pct,
       docs,
       online_docs,
       round(online_usd, 2)                                   AS online_usd,
       NULLIF(round(other_ccy, 2), 0)                         AS other_ccy,
       NULLIF(expired_docs, 0)                                AS expired,
       NULLIF(round(expired_value, 2), 0)                     AS expired_value
FROM j
ORDER BY j.day, truck;

\echo
\echo '== 5. Sales and how they were paid, per truck over the period'
\echo '      sheet_cash = sheet sales less Ecocash. The sheet''s account column is shown, not subtracted'
:core
SELECT CASE WHEN GROUPING(truck) = 1 THEN 'ALL TRUCKS' ELSE max(territory) END AS territory,
       round(sum(sales), 2)                                      AS sheet_sales,
       round(sum(sys_usd), 2)                                    AS sys_usd,
       round(coalesce(sum(sys_usd), 0) - coalesce(sum(sales), 0), 2) AS diff,
       round(100.0 * (coalesce(sum(sys_usd), 0) - sum(sales)) / NULLIF(sum(sales), 0), 1) AS diff_pct,
       round(coalesce(sum(sales), 0) - coalesce(sum(ecocash), 0), 2) AS sheet_cash,
       round(sum(sys_cash), 2)                                   AS sys_cash,
       round(sum(declared_cash), 2)                              AS declared_cash,
       round(sum(ecocash), 2)                                    AS sheet_ecocash,
       round(sum(sys_ecocash), 2)                                AS sys_ecocash,
       round(sum(declared_ecocash), 2)                           AS declared_ecocash,
       round(sum(sys_innbucks), 2)                               AS sys_innbucks,
       round(sum(sys_other), 2)                                  AS sys_other,
       round(sum(sys_untendered), 2)                             AS sys_untendered,
       round(sum(account), 2)                                    AS sheet_account
FROM j
GROUP BY ROLLUP (truck)
ORDER BY truck NULLS LAST;

\echo
\echo '== 6. Every way a van sale in the period says it was paid. An on-account sale, if the system'
\echo '      records one at all, is a payment method in this list'
:core
SELECT source, status, coalesce(currency, '(none)') AS currency, tender AS counted_as,
       coalesce(payment_method, '(none)') AS payment_method,
       count(*) AS docs, round(sum(amount), 2) AS value,
       string_agg(DISTINCT truck, ', ' ORDER BY truck) AS trucks
FROM sold
GROUP BY source, status, currency, tender, payment_method
ORDER BY (status = 'Confirmed') DESC, status, source, sum(amount) DESC;

\echo
\echo '== 7. Calls. The sheet''s figures are typed; the system''s are counted from check-ins and invoices'
\echo '      "planned" is the whole customer list. rti_out / rti_back are crates, the sheet''s rti is a fraction'
:core
SELECT to_char(day, 'Dy DD')                                  AS day,
       territory,
       customers                                              AS sheet_cust,
       planned,
       checkins,
       productive                                             AS bought,
       calls_made,
       round(100 * ccr)                                       AS sheet_ccr,
       round(100.0 * checkins / NULLIF(planned, 0))           AS sys_ccr,
       round(100 * pcr)                                       AS sheet_pcr,
       CASE WHEN checkins > 0 THEN round(100.0 * productive / NULLIF(calls_made, 0)) END AS sys_pcr,
       round(aos, 2)                                          AS sheet_aos,
       round(sys_usd / NULLIF(productive, 0), 2)              AS sys_aov,
       new_customers                                          AS sheet_new,
       new_shops                                              AS sys_new,
       rti                                                    AS sheet_rti,
       rti_out,
       rti_returned                                           AS rti_back
FROM j
WHERE submitted
ORDER BY j.day, truck;

\echo
\echo '== 8. Departure and return. h_ = typed or tapped on the handset, gps_ = the Cartrack unit'
\echo '      late_min = handset departure minus the sheet''s, in minutes'
:core
SELECT to_char(day, 'Dy DD')                 AS day,
       territory,
       dep                                   AS sheet_dep,
       h_departed::time(0)                   AS h_dep,
       gps_departed::time(0)                 AS gps_dep,
       round(extract(epoch FROM (h_departed::time - dep)) / 60) AS late_min,
       back                                  AS sheet_back,
       h_returned::time(0)                   AS h_back,
       CASE WHEN h_returned::date > day THEN '+' || (h_returned::date - day) || 'd' END AS back_day,
       gps_last_off::time(0)                 AS gps_last_off,
       left(coalesce(route_name, ''), 20)    AS system_route,
       regs                                  AS vehicle
FROM j
WHERE submitted
ORDER BY j.day, truck;

\echo
\echo '== 9. Distance. The sheet converts CBD, WEST 1 and Bulawayo UPC from miles; the handset figure'
\echo '      is whatever the rep typed off the same odometer, so compare the readings before the km'
:core
SELECT to_char(day, 'Dy DD')   AS day,
       territory,
       odo_open                AS sheet_open,
       h_open,
       odo_close               AS sheet_close,
       h_close,
       round(km, 1)            AS sheet_km,
       h_km,
       round(gps_km, 1)        AS gps_km,
       round(gps_km - km, 1)   AS gps_minus_sheet,
       fuel                    AS sheet_fuel
FROM j
WHERE submitted
ORDER BY j.day, truck;

\echo
\echo '== 10. The verdict in one table: the sheet''s submitted truck-days by how the two sales figures compare'
:core
SELECT CASE
           WHEN coalesce(sales, 0) = 0 AND docs = 0 THEN '5  no sales on either'
           WHEN coalesce(sales, 0) = 0              THEN '4  system only'
           WHEN docs = 0                            THEN '1  system has no sales'
           WHEN abs(sys_usd - sales) <= 0.02 * sales THEN '0  agree within 2 pct'
           WHEN sys_usd < sales                     THEN '2  system lower'
           ELSE                                          '3  system higher'
       END                                           AS sales_compare,
       count(*)                                      AS truck_days,
       round(sum(sales), 2)                          AS sheet_sales,
       round(sum(sys_usd), 2)                        AS sys_usd,
       round(coalesce(sum(sys_usd), 0) - coalesce(sum(sales), 0), 2) AS diff
FROM j
WHERE submitted
GROUP BY 1
ORDER BY 1;

\echo
\echo '== 11. What the system recorded in the period that the sheet has no row for'
:core
, activity AS (
    SELECT truck, day FROM sys_sales WHERE docs > 0 OR expired_docs > 0
    UNION
    SELECT truck, day FROM calls WHERE checkins > 0
    UNION
    SELECT truck, day FROM route_days
)
SELECT to_char(a.day, 'Dy DD')                              AS day,
       a.truck,
       CASE
           WHEN NOT EXISTS (SELECT 1 FROM sheet s WHERE s.truck = a.truck) THEN 'truck not on the sheet'
           WHEN EXISTS (SELECT 1 FROM sheet s WHERE s.truck = a.truck AND s.day = a.day) THEN 'sheet row is blank'
           ELSE 'no sheet row that day'
       END                                                  AS why,
       coalesce(ss.docs, 0)                                 AS docs,
       round(ss.usd, 2)                                     AS sys_usd,
       NULLIF(round(ss.other_ccy, 2), 0)                    AS other_ccy,
       NULLIF(ss.expired_docs, 0)                           AS expired,
       coalesce(c.checkins, 0)                              AS checkins,
       coalesce(rd.start_days, 0)                           AS start_days
FROM activity a
LEFT JOIN sys_sales ss  USING (truck, day)
LEFT JOIN calls c       USING (truck, day)
LEFT JOIN route_days rd USING (truck, day)
WHERE NOT EXISTS (SELECT 1 FROM sheet s WHERE s.truck = a.truck AND s.day = a.day AND s.submitted)
ORDER BY a.day, a.truck;

ROLLBACK;
