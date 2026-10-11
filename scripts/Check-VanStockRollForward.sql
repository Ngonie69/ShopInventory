-- How close is a van's stock, rolled forward from yesterday's morning read, to what SAP holds at this
-- morning's read? Read from production.
--
-- Read-only. No INSERT, UPDATE, DELETE or DDL; safe to run against the live database. The transaction
-- is opened READ ONLY and rolled back. Run it with Check-VanStockRollForward.ps1, or
--   psql -X -P pager=off -v days=31 -f Check-VanStockRollForward.sql
--
-- WHY THIS EXISTS
--
-- A van's opening stock in this system is the 07:00 read from SAP. From midnight until that read, and
-- all day when the read does not finish for a van, the system has no figure of its own for it. If SAP
-- then leaves a sale's stock read unanswered, the sale is refused (STOCK_NOT_COUNTED): VAN004,
-- 2026-10-11, three times before seven.
--
-- The alternative is to check the sale against yesterday's position rolled forward. That was not
-- built, because nobody knows how wrong the rolled-forward figure is. This measures it. For every van
-- and every morning in the period that has a finished read on both that day and the day before, it
-- works the figure out as the server would have just before the read, and sets it beside what the
-- read then brought back from SAP.
--
-- THE ARITHMETIC, item by item
--
--   rolled     = yesterday's opening stock (OriginalQuantity of yesterday's read)
--              + transfer adjustments filed under yesterday's ledger day (07:00 to 07:00), and any
--                filed under today's before this morning's read finished
--              - every sale received from midnight CAT yesterday up to this morning's read
--                floored at zero. This is VanStockPosition's arithmetic carried over one midnight.
--
--   sap        = this morning's opening stock (OriginalQuantity of this morning's read).
--
--   not_in_sap = units on sales this system had received by the read which SAP had not been given
--                yet (PostedAt null or later than the read), looking back a week. SAP still counted
--                them at the read and stops counting them when they post.
--
--   gap        = rolled - (sap - not_in_sap)
--
-- HOW TO READ THE ANSWER
--
--   gap > 0   READS HIGH. The fallback would have offered units SAP does not have. A sale of them is
--             signed, and SAP then refuses to go negative. This is the failure the stock check exists
--             to prevent, so this is the number that decides. Causes to expect: a goods issue for
--             breakages, a posted stock count, a return to the depot the listener did not deliver,
--             an invoice raised in SAP against the van that is not one of this system's sales.
--
--   gap < 0   READS LOW. The fallback would have refused stock the van was holding. It costs a sale
--             and tells the rep a number that is wrong, but nothing is signed. Causes to expect: a
--             load the listener did not deliver, a credit note putting stock back, and sales made
--             between midnight and yesterday's read that SAP already had (taken off twice).
--
--   Section 2 is the decision. If "mornings_any_high" is a small share of "mornings" and the units
--   are small, the fallback is safe enough to build. If vans read high on many mornings, it is not,
--   and section 4 names the items and days to trace in SAP.
--
--   Section 1 says how often the fallback would have been needed all day: every "failed", "pending"
--   or "no_read" day is a day the van had no figure of its own.
--
-- WHAT IT CANNOT SEE
--
--   A morning is only judged when both reads finished, so a morning after a failed read is absent
--   from sections 2 to 5. Stock that is physically missing is invisible to both sides: this compares
--   two book figures.

\if :{?days}
\else
\set days 31
\endif
\if :{?van_like}
\else
\set van_like 'VAN%'
\endif

\pset footer on
BEGIN TRANSACTION READ ONLY;

\echo
\echo '== Period and vans'
SELECT current_date - :days + 1 AS first_morning,
       current_date            AS last_morning,
       :'van_like'             AS vans_like,
       now() AT TIME ZONE 'Africa/Harare' AS run_at_cat;

\echo
\echo '== 1. The morning reads themselves: how many days each van had no finished read'
WITH vans AS (
    SELECT DISTINCT "WarehouseCode" AS van
    FROM "DailyStockSnapshots"
    WHERE "WarehouseCode" LIKE :'van_like'
      AND "SnapshotDate" > current_date - :days
),
days AS (
    SELECT generate_series(current_date - :days + 1, current_date, interval '1 day')::date AS day
),
reads AS (
    SELECT v.van, d.day, s."Status",
           (COALESCE(s."CompletedAt", s."CreatedAt") AT TIME ZONE 'Africa/Harare')::time(0) AS read_time_cat
    FROM vans v
    CROSS JOIN days d
    LEFT JOIN "DailyStockSnapshots" s
           ON s."WarehouseCode" = v.van AND s."SnapshotDate" = d.day
)
SELECT van,
       count(*)                                              AS days,
       count(*) FILTER (WHERE "Status" = 1)                  AS finished,
       count(*) FILTER (WHERE "Status" = 2)                  AS failed,
       count(*) FILTER (WHERE "Status" = 0)                  AS pending,
       count(*) FILTER (WHERE "Status" IS NULL)              AS no_read,
       count(*) FILTER (WHERE "Status" = 1 AND read_time_cat > time '08:00') AS finished_after_8,
       min(read_time_cat) FILTER (WHERE "Status" = 1)        AS earliest_read,
       max(read_time_cat) FILTER (WHERE "Status" = 1)        AS latest_read
FROM reads
GROUP BY van
ORDER BY van;

-- The comparison, built once and used by every section below. format() puts the two settings in, so
-- nothing depends on psql expanding a variable inside text it has already substituted.
SELECT format($core$
WITH reads AS (
    SELECT s."Id"                                   AS read_id,
           s."WarehouseCode"                        AS van,
           s."SnapshotDate"                         AS day,
           COALESCE(s."CompletedAt", s."CreatedAt") AS read_at
    FROM "DailyStockSnapshots" s
    WHERE s."WarehouseCode" LIKE %2$L
      AND s."Status" = 1
      AND s."SnapshotDate" >= current_date - %1$s
),
mornings AS (
    SELECT today.van, today.day, today.read_id, today.read_at,
           prev.read_id                                        AS prev_read_id,
           (prev.day::timestamp AT TIME ZONE 'Africa/Harare')  AS sales_from
    FROM reads today
    JOIN reads prev ON prev.van = today.van AND prev.day = today.day - 1
    WHERE today.day > current_date - %1$s
),
opening_prev AS (
    SELECT m.van, m.day, upper(i."ItemCode") AS item, sum(i."OriginalQuantity") AS qty
    FROM mornings m
    JOIN "DailyStockSnapshotItems" i ON i."SnapshotId" = m.prev_read_id
    GROUP BY m.van, m.day, upper(i."ItemCode")
),
sap AS (
    SELECT m.van, m.day, upper(i."ItemCode") AS item, sum(i."OriginalQuantity") AS qty
    FROM mornings m
    JOIN "DailyStockSnapshotItems" i ON i."SnapshotId" = m.read_id
    GROUP BY m.van, m.day, upper(i."ItemCode")
),
moved AS (
    SELECT m.van, m.day, upper(a."ItemCode") AS item, sum(a."AdjustmentQuantity") AS qty
    FROM mornings m
    JOIN "StockTransferAdjustments" a
      ON a."WarehouseCode" = m.van
     AND (a."SnapshotDate" = m.day - 1
          OR (a."SnapshotDate" = m.day AND a."DetectedAt" < m.read_at))
    GROUP BY m.van, m.day, upper(a."ItemCode")
),
sold AS (
    SELECT m.van, m.day, upper(l."ItemCode") AS item, sum(l."Quantity") AS qty
    FROM mornings m
    JOIN "DesktopSales" s ON s."CreatedAt" >= m.sales_from AND s."CreatedAt" < m.read_at
    JOIN "DesktopSaleLines" l ON l."SaleId" = s."Id" AND l."WarehouseCode" = m.van
    GROUP BY m.van, m.day, upper(l."ItemCode")
),
not_in_sap AS (
    SELECT m.van, m.day, upper(l."ItemCode") AS item, sum(l."Quantity") AS qty
    FROM mornings m
    JOIN "DesktopSales" s
      ON s."CreatedAt" >= m.sales_from - interval '7 days'
     AND s."CreatedAt" < m.read_at
     AND (s."PostedAt" IS NULL OR s."PostedAt" >= m.read_at)
    JOIN "DesktopSaleLines" l ON l."SaleId" = s."Id" AND l."WarehouseCode" = m.van
    GROUP BY m.van, m.day, upper(l."ItemCode")
),
keys AS (
    SELECT van, day, item FROM opening_prev
    UNION SELECT van, day, item FROM sap
    UNION SELECT van, day, item FROM moved
    UNION SELECT van, day, item FROM sold
    UNION SELECT van, day, item FROM not_in_sap
),
terms AS (
    SELECT k.van, k.day, k.item,
           COALESCE(o.qty, 0)  AS opening_prev,
           COALESCE(mv.qty, 0) AS moved,
           COALESCE(sd.qty, 0) AS sold,
           COALESCE(sp.qty, 0) AS sap,
           COALESCE(n.qty, 0)  AS not_in_sap
    FROM keys k
    LEFT JOIN opening_prev o USING (van, day, item)
    LEFT JOIN moved mv       USING (van, day, item)
    LEFT JOIN sold sd        USING (van, day, item)
    LEFT JOIN sap sp         USING (van, day, item)
    LEFT JOIN not_in_sap n   USING (van, day, item)
),
gaps AS (
    SELECT t.*,
           GREATEST(0, t.opening_prev + t.moved - t.sold)                          AS rolled,
           GREATEST(0, t.opening_prev + t.moved - t.sold) - (t.sap - t.not_in_sap) AS gap,
           (m.read_at AT TIME ZONE 'Africa/Harare')::time(0)                       AS read_time_cat
    FROM terms t
    JOIN mornings m USING (van, day)
)
$core$, :days, :'van_like') AS core \gset

\echo
\echo '== 2. THE DECISION: per van, how often and by how much the rolled-forward figure was wrong'
\echo '      high = would have offered units SAP did not have; low = would have refused units it had'
:core
, per_morning AS (
    SELECT van, day,
           bool_or(gap > 0.001)                           AS any_high,
           COALESCE(sum(gap) FILTER (WHERE gap > 0.001), 0) AS high_units
    FROM gaps
    GROUP BY van, day
),
by_van AS (
    SELECT van,
           count(*)                                                     AS item_mornings,
           count(*) FILTER (WHERE abs(gap) <= 0.001)                    AS agree,
           count(*) FILTER (WHERE gap > 0.001)                          AS high,
           COALESCE(sum(gap) FILTER (WHERE gap > 0.001), 0)             AS high_units,
           COALESCE(max(gap) FILTER (WHERE gap > 0.001), 0)             AS worst_high,
           count(*) FILTER (WHERE gap < -0.001)                         AS low,
           COALESCE(-sum(gap) FILTER (WHERE gap < -0.001), 0)           AS low_units,
           COALESCE(-min(gap) FILTER (WHERE gap < -0.001), 0)           AS worst_low
    FROM gaps
    GROUP BY ROLLUP (van)
),
mornings_by_van AS (
    SELECT van,
           count(*)                         AS mornings,
           count(*) FILTER (WHERE any_high) AS mornings_any_high
    FROM per_morning
    GROUP BY ROLLUP (van)
)
SELECT COALESCE(b.van, 'ALL VANS')                                    AS van,
       m.mornings,
       m.mornings_any_high,
       round(100.0 * m.mornings_any_high / NULLIF(m.mornings, 0), 1)  AS pct_mornings_high,
       b.item_mornings,
       b.agree,
       b.high,
       round(b.high_units, 3)  AS high_units,
       round(b.worst_high, 3)  AS worst_high,
       b.low,
       round(b.low_units, 3)   AS low_units,
       round(b.worst_low, 3)   AS worst_low
FROM by_van b
JOIN mornings_by_van m ON m.van IS NOT DISTINCT FROM b.van
ORDER BY b.van NULLS LAST;

\echo
\echo '== 3. How big the wrong readings were (item-mornings, all vans)'
:core
SELECT CASE WHEN gap > 0.001 THEN 'high' ELSE 'low' END AS reads,
       CASE
           WHEN abs(gap) <= 1  THEN '1  up to 1 unit'
           WHEN abs(gap) <= 5  THEN '2  over 1, up to 5'
           WHEN abs(gap) <= 20 THEN '3  over 5, up to 20'
           ELSE                     '4  over 20'
       END                                              AS size,
       count(*)                                         AS item_mornings,
       round(sum(abs(gap)), 3)                          AS units
FROM gaps
WHERE abs(gap) > 0.001
GROUP BY 1, 2
ORDER BY 1, 2;

\echo
\echo '== 4. The 40 worst HIGH readings: trace these in SAP (goods issues, counts, returns, SAP-side invoices)'
:core
SELECT van, day, read_time_cat AS read_cat, item,
       round(opening_prev, 3) AS opening_prev,
       round(moved, 3)        AS moved,
       round(sold, 3)         AS sold,
       round(rolled, 3)       AS rolled,
       round(sap, 3)          AS sap,
       round(not_in_sap, 3)   AS not_in_sap,
       round(gap, 3)          AS gap
FROM gaps
WHERE gap > 0.001
ORDER BY gap DESC, day DESC, van, item
LIMIT 40;

\echo
\echo '== 5. The 20 worst LOW readings: a load not journalled, a credit, or a sale taken off twice'
:core
SELECT van, day, read_time_cat AS read_cat, item,
       round(opening_prev, 3) AS opening_prev,
       round(moved, 3)        AS moved,
       round(sold, 3)         AS sold,
       round(rolled, 3)       AS rolled,
       round(sap, 3)          AS sap,
       round(not_in_sap, 3)   AS not_in_sap,
       round(gap, 3)          AS gap
FROM gaps
WHERE gap < -0.001
ORDER BY gap, day DESC, van, item
LIMIT 20;

ROLLBACK;
