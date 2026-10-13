<#
.SYNOPSIS
    Read-only check of where a van sales order's response time went, on the server's side.

.DESCRIPTION
    Run on KFL-DNS2 (10.10.10.9) in Windows PowerShell 5.1. It changes nothing.

    The handset logs how long POST /api/vansales/sales-order took as it saw it ("Order timing:
    van_order=..., office=7081ms"). This lays the server's own record beside that figure:

      1. The order's timeline from Postgres: when the order row was built (after everything the
         capture reads first), when its queue entry and audit rows were written, and when the
         post-save queue priced it and posted it to SAP.
      2. Whether anything local could have answered the unit of its lines, or SAP had to be asked.
      3. IIS's own duration (time-taken) for every van sales order and direct van sale in the window,
         with the request for this order picked out. This is the number to hold against the handset's:
         what is left over is the network, not the server.
      4. The API log around the order, and the day's count of SAP slot waits.
      5. How long the store itself took for every van order in the window, and how long the queue
         then took to get each one into SAP.
      6. What the two database reads the capture makes before storing cost right now.

    Times print in UTC unless marked CAT. Nothing here prints a secret.

.PARAMETER VanOrder
    The handset's order reference, e.g. VAN004-SO-20261013-919D5A. Without it only the window
    figures (3, 5 and 6) are printed.

.PARAMETER Days
    How far back the window figures look. Default 7.

.PARAMETER IisLogRoot
    Where IIS keeps its W3SVC folders. Default C:\inetpub\logs\LogFiles.
#>
param(
    [string]$VanOrder,
    [int]$Days = 7,
    [string]$InetpubRoot = 'C:\inetpub',
    [string]$IisLogRoot = 'C:\inetpub\logs\LogFiles'
)

$ErrorActionPreference = 'Stop'

if ($VanOrder -and $VanOrder -notmatch '^[A-Za-z0-9_\-]{1,100}$') {
    throw "VanOrder '$VanOrder' is not an order reference (letters, digits, - and _ only)."
}

function Write-Section([string]$Title) {
    Write-Host ''
    Write-Host ('=' * 78)
    Write-Host $Title
    Write-Host ('=' * 78)
}

# --- Postgres -----------------------------------------------------------------------------
$connectionString = $null
foreach ($dir in (Get-ChildItem -Path $InetpubRoot -Directory -Filter 'ShopInventory-API*')) {
    $configPath = Join-Path $dir.FullName 'web.config'
    if (-not (Test-Path $configPath)) { continue }
    [xml]$config = Get-Content -Path $configPath -Raw
    foreach ($var in $config.SelectNodes('//environmentVariable')) {
        if ($var.GetAttribute('name') -eq 'ConnectionStrings__DefaultConnection' -and -not $connectionString) {
            $connectionString = $var.GetAttribute('value')
        }
    }
}
if (-not $connectionString) {
    $prodJson = Join-Path $InetpubRoot 'ShopInventory-API\appsettings.Production.json'
    if (Test-Path $prodJson) {
        $connectionString = (Get-Content $prodJson -Raw | ConvertFrom-Json).ConnectionStrings.DefaultConnection
    }
}
if (-not $connectionString) { throw 'No connection string found in any API web.config or appsettings.Production.json.' }

$parts = @{}
foreach ($pair in $connectionString -split ';') {
    $kv = $pair -split '=', 2
    if ($kv.Count -eq 2) { $parts[$kv[0].Trim().ToLowerInvariant()] = $kv[1].Trim() }
}
$pgHost = if ($parts['host']) { $parts['host'] } else { $parts['server'] }
$pgPort = if ($parts['port']) { $parts['port'] } else { '5432' }
$pgDb   = $parts['database']
$pgUser = if ($parts['username']) { $parts['username'] } elseif ($parts['user id']) { $parts['user id'] } else { $parts['user'] }

$psql = (Get-Command psql -ErrorAction SilentlyContinue).Source
if (-not $psql) {
    $psql = Get-ChildItem 'C:\Program Files\PostgreSQL\*\bin\psql.exe' -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $psql) { throw 'psql.exe not found.' }

# SQL goes through a file, never -c: PowerShell 5.1 strips the double quotes every table and column
# name here needs. The session is read-only and in UTC, so to_char prints what the application stored.
function Invoke-Psql([string]$Sql) {
    $sqlFile = Join-Path $env:TEMP ("check-van-order-{0}.sql" -f [guid]::NewGuid().ToString('N'))
    $Sql | Set-Content -Path $sqlFile -Encoding ASCII
    $env:PGPASSWORD = $parts['password']
    $env:PGOPTIONS = '-c default_transaction_read_only=on -c timezone=UTC'
    try {
        $raw = & $psql -w -h $pgHost -p $pgPort -U $pgUser -d $pgDb -A -t -q -P pager=off -f $sqlFile
        if ($LASTEXITCODE -ne 0) { throw "psql exited with $LASTEXITCODE" }
        return $raw
    } finally {
        Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
        Remove-Item Env:PGOPTIONS -ErrorAction SilentlyContinue
        Remove-Item $sqlFile -ErrorAction SilentlyContinue
    }
}

function Invoke-JsonQuery([string]$Sql) {
    $raw = Invoke-Psql "SELECT coalesce(json_agg(t), '[]'::json) FROM ($Sql) t;"
    # ConvertFrom-Json hands a JSON array back as one object in 5.1; unroll it.
    return @((($raw -join "`n") | ConvertFrom-Json) | ForEach-Object { $_ })
}

function ConvertTo-Utc([string]$Stamp) {
    if ([string]::IsNullOrWhiteSpace($Stamp)) { return $null }
    return [datetime]::ParseExact(
        $Stamp, 'yyyy-MM-dd HH:mm:ss.fff', [cultureinfo]::InvariantCulture,
        [System.Globalization.DateTimeStyles]::AssumeUniversal -bor [System.Globalization.DateTimeStyles]::AdjustToUniversal)
}

$stampFormat = 'YYYY-MM-DD HH24:MI:SS.MS'
$order = $null
$orderBuiltUtc = $null
$requestEndUtc = $null

# --- 1. The order's own timeline ----------------------------------------------------------
if ($VanOrder) {
    Write-Section "1. Timeline of $VanOrder (UTC; CAT is +2h)"
    $order = Invoke-JsonQuery @"
SELECT o."Id", o."OrderNumber", o."CardCode", o."CardName", o."Status", o."SAPDocNum", o."CreatedByUserId",
       to_char(o."OrderDate", '$stampFormat') AS "OrderBuilt",
       to_char(q."CreatedAt", '$stampFormat') AS "QueueEntryWritten",
       to_char(q."PricesResolvedAt", '$stampFormat') AS "QueuePriced",
       to_char(q."NotificationSentAt", '$stampFormat') AS "QueueNotified",
       to_char(q."AutoPostedAt", '$stampFormat') AS "QueuePostedToSap",
       q."AutoPostToSap", q."RetryCount", q."LastError"
FROM "SalesOrders" o
LEFT JOIN "MobileOrderPostProcessingQueue" q ON q."SalesOrderId" = o."Id"
WHERE o."ClientRequestId" = '$VanOrder'
"@ | Select-Object -First 1

    if (-not $order) {
        Write-Host "  No sales order is stored under ClientRequestId $VanOrder."
    } else {
        $orderBuiltUtc = ConvertTo-Utc $order.OrderBuilt
        Write-Host ("  Order {0} (id {1}) for {2} '{3}', status {4}, SAP DocNum {5}" -f `
            $order.OrderNumber, $order.Id, $order.CardCode, $order.CardName, $order.Status, $order.SAPDocNum)
        Write-Host '  (a CardName equal to the CardCode means the name had to be read from SAP)'

        $audits = Invoke-JsonQuery @"
SELECT to_char(a."Timestamp", '$stampFormat') AS "At", a."Action", a."Username", a."AppVersion", a."DeviceModel", a."Details"
FROM "AuditLogs" a
WHERE a."Timestamp" BETWEEN (SELECT "OrderDate" FROM "SalesOrders" WHERE "Id" = $($order.Id)) - interval '60 seconds'
                        AND (SELECT "OrderDate" FROM "SalesOrders" WHERE "Id" = $($order.Id)) + interval '60 seconds'
  AND a."UserId" = '$($order.CreatedByUserId)'
ORDER BY a."Timestamp"
"@

        $events = @()
        $events += [pscustomobject]@{ At = $order.OrderBuilt; What = 'order row built (every read the capture makes first is before this)' }
        $events += [pscustomobject]@{ At = $order.QueueEntryWritten; What = 'queue entry written (order row saved and re-counted)' }
        foreach ($audit in $audits) {
            $what = "audit: $($audit.Action)"
            if ($audit.Action -eq 'VanSalesCreateSalesOrder') {
                $what += '  <- the action returned; the reply is written next'
                $requestEndUtc = ConvertTo-Utc $audit.At
            }
            $events += [pscustomobject]@{ At = $audit.At; What = $what }
        }
        $events += [pscustomobject]@{ At = $order.QueuePriced; What = 'queue: priced (off the request)' }
        $events += [pscustomobject]@{ At = $order.QueueNotified; What = 'queue: office notified (off the request)' }
        $events += [pscustomobject]@{ At = $order.QueuePostedToSap; What = 'queue: posted to SAP (off the request)' }

        foreach ($event in ($events | Where-Object { $_.At } | Sort-Object At)) {
            $delta = [int]((ConvertTo-Utc $event.At) - $orderBuiltUtc).TotalMilliseconds
            Write-Host ("  {0}  {1,8:+#;-#;0} ms  {2}" -f $event.At, $delta, $event.What)
        }
        if ($order.LastError) { Write-Host "  queue last error: $($order.LastError)" }

        $requestAudit = $audits | Where-Object { $_.Action -eq 'VanSalesCreateSalesOrder' } | Select-Object -First 1
        if ($requestAudit) {
            Write-Host ("  Handset: app {0} on {1}" -f $requestAudit.AppVersion, $requestAudit.DeviceModel)
        }

        # --- 2. Could the unit be answered locally? -------------------------------------------
        Write-Section '2. Could the unit of each line be answered without SAP?'
        $lines = Invoke-JsonQuery @"
SELECT l."ItemCode", l."Quantity", l."UoMCode",
       (SELECT count(*) FROM "Products" p WHERE upper(p."ItemCode") = upper(l."ItemCode")) AS "InProducts",
       (SELECT count(*) FROM "MerchandiserProducts" m WHERE m."IsActive" AND upper(m."ItemCode") = upper(l."ItemCode")) AS "InMerchandiserProducts"
FROM "SalesOrderLines" l WHERE l."SalesOrderId" = $($order.Id) ORDER BY l."LineNum"
"@
        foreach ($line in $lines) {
            $local = if ($line.InProducts -gt 0 -or $line.InMerchandiserProducts -gt 0) { 'a local table knows it' } else { 'NOT known locally: SAP was asked unless the API had read this item in the last 30 minutes' }
            Write-Host ("  {0} x{1} stored with unit '{2}': {3}" -f $line.ItemCode, $line.Quantity, $line.UoMCode, $local)
        }
        $productCount = (Invoke-Psql 'SELECT count(*) FROM "Products";' | Select-Object -First 1)
        Write-Host "  Rows in the API's Products table: $productCount (nothing fills it, so 0 is expected)"
    }
}

# --- 3. IIS's own request durations --------------------------------------------------------
Write-Section "3. Request durations as IIS recorded them, last $Days day(s)"
$endpoints = @{ '/api/vansales/sales-order' = 'sales order'; '/api/vansales/order/with-batches' = 'direct van sale' }
$since = (Get-Date).ToUniversalTime().AddDays(-$Days)
$requests = New-Object System.Collections.Generic.List[object]

if (-not (Test-Path $IisLogRoot)) {
    Write-Host "  $IisLogRoot does not exist. Pass -IisLogRoot (IIS Manager > site > Logging shows the directory)."
} else {
    $logFiles = Get-ChildItem -Path $IisLogRoot -Recurse -Filter 'u_ex*.log' -ErrorAction SilentlyContinue |
        Where-Object { $_.LastWriteTimeUtc -ge $since }
    foreach ($file in $logFiles) {
        $site = $file.Directory.Name
        $fields = @{}
        # IIS holds today's file open for writing, so it has to be opened shared.
        $stream = New-Object System.IO.FileStream($file.FullName, 'Open', 'Read', 'ReadWrite')
        $reader = New-Object System.IO.StreamReader($stream)
        try {
            while ($null -ne ($line = $reader.ReadLine())) {
                if ($line.StartsWith('#Fields:')) {
                    $fields = @{}
                    $names = $line.Substring(8).Trim() -split ' '
                    for ($i = 0; $i -lt $names.Count; $i++) { $fields[$names[$i]] = $i }
                    continue
                }
                if ($line.IndexOf('/api/vansales/', [System.StringComparison]::OrdinalIgnoreCase) -lt 0) { continue }
                if (-not $fields.ContainsKey('time-taken')) { continue }
                $cols = $line -split ' '
                if ($cols[$fields['cs-method']] -ne 'POST') { continue }
                $stem = $cols[$fields['cs-uri-stem']].ToLowerInvariant().TrimEnd('/')
                if (-not $endpoints.ContainsKey($stem)) { continue }
                $requests.Add([pscustomobject]@{
                    Site     = $site
                    Endpoint = $endpoints[$stem]
                    EndUtc   = [datetime]::ParseExact(
                        "$($cols[$fields['date']]) $($cols[$fields['time']])", 'yyyy-MM-dd HH:mm:ss',
                        [cultureinfo]::InvariantCulture,
                        [System.Globalization.DateTimeStyles]::AssumeUniversal -bor [System.Globalization.DateTimeStyles]::AdjustToUniversal)
                    Status   = $cols[$fields['sc-status']]
                    Ms       = [int]$cols[$fields['time-taken']]
                })
            }
        } finally {
            $reader.Dispose()
            $stream.Dispose()
        }
    }

    $recent = @($requests | Where-Object { $_.EndUtc -ge $since })
    if ($recent.Count -eq 0) {
        Write-Host "  No POSTs to the van order endpoints in the IIS logs under $IisLogRoot."
        Write-Host '  Either IIS logging is off for the API site, time-taken is not a logged field, or the logs are elsewhere.'
    } else {
        Write-Host '  A request behind the public binding and its slot is logged once per site, so read each site apart.'
        Write-Host ''
        Write-Host ('  {0,-12} {1,-16} {2,6} {3,8} {4,8} {5,8} {6,7} {7,7}' -f 'site', 'endpoint', 'count', 'median', 'p90', 'max', '>=3s', '>=7s')
        foreach ($group in ($recent | Group-Object Site, Endpoint | Sort-Object Name)) {
            $sorted = @($group.Group | Sort-Object Ms | ForEach-Object { $_.Ms })
            $median = $sorted[[int][math]::Floor(($sorted.Count - 1) * 0.5)]
            $p90 = $sorted[[int][math]::Floor(($sorted.Count - 1) * 0.9)]
            Write-Host ('  {0,-12} {1,-16} {2,6} {3,8} {4,8} {5,8} {6,7} {7,7}' -f `
                $group.Group[0].Site, $group.Group[0].Endpoint, $sorted.Count, $median, $p90, $sorted[-1],
                @($sorted | Where-Object { $_ -ge 3000 }).Count, @($sorted | Where-Object { $_ -ge 7000 }).Count)
        }
        Write-Host '  (milliseconds, from the first byte IIS received to the last byte it sent)'

        $anchor = if ($requestEndUtc) { $requestEndUtc } else { $orderBuiltUtc }
        if ($anchor) {
            Write-Host ''
            Write-Host "  Sales order requests that ended within 20 s of this order's audit row:"
            $near = @($requests | Where-Object { $_.Endpoint -eq 'sales order' -and [math]::Abs(($_.EndUtc - $anchor).TotalSeconds) -le 20 })
            if ($near.Count -eq 0) { Write-Host '    (none found)' }
            foreach ($request in ($near | Sort-Object EndUtc)) {
                Write-Host ("    {0} ended {1:HH:mm:ss} UTC, HTTP {2}, took {3} ms, so it arrived about {4:HH:mm:ss.f} UTC" -f `
                    $request.Site, $request.EndUtc, $request.Status, $request.Ms, $request.EndUtc.AddMilliseconds(-$request.Ms))
            }
            Write-Host '    Hold "took" against the handset''s office= figure: the difference is the network.'
            Write-Host '    Hold "arrived" against "order row built" above: that gap is what was read before the order was stored.'
        }
    }
}

# --- 4. The API log around the order ------------------------------------------------------
if ($orderBuiltUtc) {
    Write-Section '4. API log around the order (CAT, as the log is written)'
    $localBuilt = $orderBuiltUtc.ToLocalTime()
    $from = $localBuilt.AddSeconds(-15)
    $to = $localBuilt.AddSeconds(10)
    $logName = 'shopinventory-api-{0:yyyyMMdd}.log' -f $localBuilt
    $logs = Get-ChildItem -Path $InetpubRoot -Directory -Filter 'ShopInventory-API*' |
        ForEach-Object { Join-Path $_.FullName "logs\$logName" } | Where-Object { Test-Path $_ }
    if (-not $logs) { Write-Host "  No $logName under any $InetpubRoot\ShopInventory-API*\logs." }
    $interesting = 'sales order|SAP|concurrency slot|UoM|Login|session|\[WRN\]|\[ERR\]'
    foreach ($log in $logs) {
        Write-Host ''
        Write-Host "  [$log]"
        $shown = 0
        $slotWaits = 0
        $uomFailures = 0
        $transient = 0
        $stream = New-Object System.IO.FileStream($log, 'Open', 'Read', 'ReadWrite')
        $reader = New-Object System.IO.StreamReader($stream)
        try {
            while ($null -ne ($line = $reader.ReadLine())) {
                if ($line.Contains('for a concurrency slot')) { $slotWaits++ }
                if ($line.Contains('Failed to resolve sales order UoMs')) { $uomFailures++ }
                if ($line.Contains('Transient SAP error')) { $transient++ }
                if ($line.Length -lt 23 -or $shown -ge 120) { continue }
                $stamp = [datetime]::MinValue
                if (-not [datetime]::TryParseExact($line.Substring(0, 23), 'yyyy-MM-dd HH:mm:ss.fff',
                        [cultureinfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::None, [ref]$stamp)) { continue }
                if ($stamp -lt $from -or $stamp -gt $to) { continue }
                if ($line -notmatch $interesting -and $line.IndexOf($order.OrderNumber) -lt 0) { continue }
                $text = if ($line.Length -gt 320) { $line.Substring(0, 320) + '...' } else { $line }
                Write-Host "    $text"
                $shown++
            }
        } finally {
            $reader.Dispose()
            $stream.Dispose()
        }
        if ($shown -eq 0) { Write-Host '    (no SAP, order, warning or error lines in the 25 seconds around the order)' }
        Write-Host ("    Whole day in this file: {0} SAP slot wait(s) of 2 s or more, {1} transient SAP error(s), {2} failed unit read(s)." -f `
            $slotWaits, $transient, $uomFailures)
    }
}

# --- 5. Every van order in the window ------------------------------------------------------
Write-Section "5. Van sales orders of the last $Days day(s): the store, and the queue behind it"
$window = Invoke-JsonQuery @"
SELECT count(*) AS "Orders",
       round(percentile_cont(0.5) WITHIN GROUP (ORDER BY s."StoreMs")) AS "StoreMedian",
       round(percentile_cont(0.9) WITHIN GROUP (ORDER BY s."StoreMs")) AS "StoreP90",
       round(max(s."StoreMs")) AS "StoreMax",
       round(percentile_cont(0.5) WITHIN GROUP (ORDER BY s."ToSapSeconds")) AS "ToSapMedian",
       round(percentile_cont(0.9) WITHIN GROUP (ORDER BY s."ToSapSeconds")) AS "ToSapP90",
       count(*) FILTER (WHERE s."NoUnit") AS "OrdersWithALineWithoutUnit"
FROM (
    SELECT extract(epoch FROM (a."Timestamp" - o."OrderDate")) * 1000 AS "StoreMs",
           extract(epoch FROM (q."AutoPostedAt" - q."CreatedAt")) AS "ToSapSeconds",
           EXISTS (SELECT 1 FROM "SalesOrderLines" l WHERE l."SalesOrderId" = o."Id" AND coalesce(l."UoMCode", '') = '') AS "NoUnit"
    FROM "SalesOrders" o
    JOIN "MobileOrderPostProcessingQueue" q ON q."SalesOrderId" = o."Id" AND q."AutoPostToSap"
    LEFT JOIN "AuditLogs" a ON a."Action" = 'CreateSalesOrder' AND a."EntityType" = 'SalesOrder'
                           AND a."EntityId" = o."Id"::text AND a."Timestamp" >= now() - interval '$Days days'
    WHERE o."CreatedAt" >= now() - interval '$Days days'
) s
"@ | Select-Object -First 1
Write-Host ("  {0} van sales order(s)." -f $window.Orders)
Write-Host ("  Order row built -> stored and audited: median {0} ms, p90 {1} ms, max {2} ms" -f $window.StoreMedian, $window.StoreP90, $window.StoreMax)
Write-Host '    (the database half of the request; if this is small, a slow order was slow before the row was built)'
Write-Host ("  Stored -> in SAP, by the queue: median {0} s, p90 {1} s" -f $window.ToSapMedian, $window.ToSapP90)
Write-Host ("  Orders with a line that has no unit: {0}" -f $window.OrdersWithALineWithoutUnit)

# --- 6. The capture's own database reads ---------------------------------------------------
Write-Section '6. The two reads the capture makes before it stores an order, timed now'
$prefix = 'SO-{0:yyyyMMdd}-' -f (Get-Date).ToUniversalTime()
$probe = if ($VanOrder) { $VanOrder } else { 'NO-SUCH-VAN-ORDER' }
Write-Host "  Next order number (the last $prefix order):"
Invoke-Psql @"
EXPLAIN (ANALYZE, COSTS OFF, TIMING OFF)
SELECT * FROM "SalesOrders" o WHERE o."OrderNumber" LIKE '$prefix%'
ORDER BY length(o."OrderNumber") DESC, o."OrderNumber" DESC LIMIT 1;
"@ | Where-Object { $_ -match 'Scan|Execution Time|Planning Time' } | ForEach-Object { Write-Host "    $($_.Trim())" }
Write-Host '  Has this van order been captured already (the idempotency read):'
Invoke-Psql @"
EXPLAIN (ANALYZE, COSTS OFF, TIMING OFF)
SELECT * FROM "SalesOrders" o WHERE o."ClientRequestId" = '$probe' LIMIT 1;
"@ | Where-Object { $_ -match 'Scan|Execution Time|Planning Time' } | ForEach-Object { Write-Host "    $($_.Trim())" }
Write-Host ("  Sales orders stored in all: {0}" -f (Invoke-Psql 'SELECT count(*) FROM "SalesOrders";' | Select-Object -First 1))

Write-Host ''
Write-Host 'Done. Nothing was changed.'
