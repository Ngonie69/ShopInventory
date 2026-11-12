-- Export-FleetMonth.sql: the van half of the monthly fleet report, as CSV files. Read-only.
--
-- Run it through Export-FleetMonth.ps1, which opens the session read-only, sets :from_day and
-- :to_day (inclusive CAT trading days) and runs psql from the output folder. Every \o below is
-- relative to that folder.
--
-- Only one van (AFQ9644, route UPC2) carries a Cartrack tracker. For the others, the handset's
-- GPS is all there is, and it is taken only at events: Start Day, End Day, each shop check-in
-- and check-out, and each online sales order. No GPS breadcrumb is recorded between those, so
-- distance between points is a straight line. The odometer the rep types at Start and End Day
-- is the mileage of record.
--
-- Times are exported as CAT wall-clock times. The database holds UTC.

BEGIN TRANSACTION READ ONLY;
SET LOCAL statement_timeout = '300s';

-- Who is on which van. Both reps on a truck sign in under the same van account (VAN0xx).
\o reps.csv
COPY (
    SELECT u."Id"                                       AS user_id,
           u."Username"                                 AS username,
           u."FirstName"                                AS first_name,
           u."LastName"                                 AS last_name,
           u."Role"                                     AS role,
           u."IsActive"                                 AS is_active,
           upper(btrim(u."AssignedBusinessPartnerCode")) AS van_account,
           r."Code"                                     AS route_code,
           r."Name"                                     AS route_name,
           r."TruckRegNo"                               AS route_truck_reg,
           (u."LastLoginAt" AT TIME ZONE 'Africa/Harare')::timestamp(0) AS last_login_cat
    FROM "Users" u
    LEFT JOIN "Routes" r ON r."Id" = u."RouteId"
    WHERE btrim(coalesce(u."AssignedBusinessPartnerCode", '')) <> ''
    ORDER BY 7, 2
) TO STDOUT WITH CSV HEADER;

\o routes.csv
COPY (
    SELECT r."Id"           AS route_id,
           r."Code"         AS route_code,
           r."Name"         AS route_name,
           r."Territory"    AS territory,
           r."TruckRegNo"   AS truck_reg,
           r."IsActive"     AS is_active,
           r."DeletedAt" IS NOT NULL AS is_deleted
    FROM "Routes" r
    ORDER BY 2
) TO STDOUT WITH CSV HEADER;

-- One row per rep per trading day: Start Day / End Day, typed odometer, GPS at each end.
\o route_days.csv
COPY (
    SELECT d."Id"                    AS route_day_id,
           d."UserId"                AS user_id,
           d."Username"              AS username,
           upper(btrim(u."AssignedBusinessPartnerCode")) AS van_account,
           d."TradingDate"           AS trading_date,
           d."RouteId"               AS route_id,
           d."RouteCode"             AS route_code,
           d."RouteName"             AS route_name,
           d."Territory"             AS territory,
           d."TruckRegNo"            AS truck_reg,
           d."AreaName"              AS area_name,
           (d."DepartedAt" AT TIME ZONE 'Africa/Harare')::timestamp(0) AS departed_cat,
           d."DepartedLatitude"      AS departed_lat,
           d."DepartedLongitude"     AS departed_lon,
           d."StartingMileage"       AS starting_mileage,
           (d."ReturnedAt" AT TIME ZONE 'Africa/Harare')::timestamp(0) AS returned_cat,
           d."ReturnedLatitude"      AS returned_lat,
           d."ReturnedLongitude"     AS returned_lon,
           d."ClosingMileage"        AS closing_mileage,
           d."PlannedCustomerCount"  AS planned_customers
    FROM "VanRouteDays" d
    LEFT JOIN "Users" u ON u."Id" = d."UserId"
    WHERE d."TradingDate" BETWEEN :'from_day'::date AND :'to_day'::date
    ORDER BY 4, 5, 3
) TO STDOUT WITH CSV HEADER;

-- Van-channel shop visits (Channel 1 = VanSales). Each carries the GPS fix the phone had.
\o checkins.csv
COPY (
    SELECT t."Id"                    AS checkin_id,
           t."UserId"                AS user_id,
           t."Username"              AS username,
           upper(btrim(u."AssignedBusinessPartnerCode")) AS van_account,
           t."CustomerCode"          AS customer_code,
           t."CustomerName"          AS customer_name,
           (t."CheckInTime" AT TIME ZONE 'Africa/Harare')::timestamp(0)  AS checkin_cat,
           (t."CheckOutTime" AT TIME ZONE 'Africa/Harare')::timestamp(0) AS checkout_cat,
           t."CheckInLatitude"       AS checkin_lat,
           t."CheckInLongitude"      AS checkin_lon,
           t."CheckInLocationSource" AS checkin_source,
           t."CheckInLocationAccuracyMetres" AS checkin_accuracy_m,
           t."CheckOutLatitude"      AS checkout_lat,
           t."CheckOutLongitude"     AS checkout_lon,
           t."CheckOutLocationSource" AS checkout_source,
           t."DurationMinutes"       AS duration_minutes
    FROM "TimesheetEntries" t
    LEFT JOIN "Users" u ON u."Id" = t."UserId"
    WHERE t."Channel" = 1
      AND t."CheckInTime" >= (:'from_day'::date)::timestamp AT TIME ZONE 'Africa/Harare'
      AND t."CheckInTime" <  ((:'to_day'::date) + 1)::timestamp AT TIME ZONE 'Africa/Harare'
    ORDER BY 4, 7
) TO STDOUT WITH CSV HEADER;

-- Online van sales orders with the phone's position. Direct invoices and offline sales carry no
-- coordinates, so they are not here.
\o sales_orders.csv
COPY (
    SELECT o."Id"                  AS sales_order_id,
           upper(btrim(o."CardCode")) AS van_account,
           o."CreatedByUserId"     AS user_id,
           u."Username"            AS username,
           o."RouteCustomerCode"   AS customer_code,
           o."RouteCustomerName"   AS customer_name,
           (o."CreatedAt" AT TIME ZONE 'Africa/Harare')::timestamp(0) AS created_cat,
           o."Latitude"            AS lat,
           o."Longitude"           AS lon
    FROM "SalesOrders" o
    LEFT JOIN "Users" u ON u."Id" = o."CreatedByUserId"
    WHERE o."Source" = 1
      AND upper(o."CardCode") LIKE 'VAN%'
      AND o."CreatedAt" >= (:'from_day'::date)::timestamp AT TIME ZONE 'Africa/Harare'
      AND o."CreatedAt" <  ((:'to_day'::date) + 1)::timestamp AT TIME ZONE 'Africa/Harare'
    ORDER BY 2, 7
) TO STDOUT WITH CSV HEADER;

-- Fuel issued per truck-day, as the office keys it in (Van Sales -> daily truck report).
\o fuel.csv
COPY (
    SELECT e."RouteId"       AS route_id,
           r."Code"          AS route_code,
           r."Name"          AS route_name,
           e."TradingDate"   AS trading_date,
           e."FuelLitres"    AS fuel_litres,
           e."AreaName"      AS area_name,
           e."Notes"         AS notes,
           e."UpdatedByName" AS entered_by
    FROM "VanTruckDayEntries" e
    JOIN "Routes" r ON r."Id" = e."RouteId"
    WHERE e."TradingDate" BETWEEN :'from_day'::date AND :'to_day'::date
    ORDER BY 2, 4
) TO STDOUT WITH CSV HEADER;

-- Which Cartrack vehicle is linked to which van account.
\o telematics_vehicles.csv
COPY (
    SELECT v."Registration"        AS registration,
           v."BusinessPartnerCode" AS van_account,
           v."BusinessPartnerName" AS van_account_name,
           v."IsActiveInFleet"     AS is_active
    FROM "TelematicsVehicles" v
    ORDER BY 1
) TO STDOUT WITH CSV HEADER;

\o
ROLLBACK;
