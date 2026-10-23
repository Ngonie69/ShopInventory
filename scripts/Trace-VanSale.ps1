<#
.SYNOPSIS
    Read-only trace of a van sale the rep made but the web does not show: did the handset's request
    ever reach the server, and if it did, where did it stop?

.DESCRIPTION
    Run on KFL-DNS2 (10.10.10.9) in Windows PowerShell 5.1. It changes nothing: every database
    session is read-only and the log files are opened for reading.

    It lays the server's record of one van's day side by side, so a sale with no trace stands out:

      1. Who sells from the van, when each last signed in, and how often their handsets renewed their
         session during the window. A renewal proves the handset was talking to the server then.
      2. What the handsets wrote to the audit log during the window, with app version and device.
      3. Customer visits (check-ins) the server holds for those reps.
      4. Every sale and sales order the server holds for the van: reservation, fiscal receipt, SAP
         invoice and posting-queue state. A sale the server has is here, whatever the web shows.
      5. Every van-sales POST that reached IIS during the window (all vans, as IIS cannot tell
         them apart), each matched to the stored document it produced. A POST that produced nothing
         is a request that reached the server and was refused or failed. Any request whose URL
         carries this van's reference (the handset asking "did my sale arrive?") is listed too.
      6. API warnings and errors in the window that mention this van.

    Times print in CAT (UTC+2). Nothing here prints a secret.

    How to read it:
      * The sale is in 4             The server has it. Its columns say whether it is fiscalised,
                                     in SAP, or queued. Look on /van-sales/invoices, not SAP lists.
      * Not in 4, but a POST in 5 at that time produced nothing
                                     The request arrived and was refused or failed. The HTTP status
                                     and section 6 say why. The rep resends from the phone after
                                     the cause is fixed.
      * Not in 4, no POST in 5 near that time
                                     The request never reached the server: no signal, the phone
                                     network, or the proxy in front. If 1-3 show the handset was in
                                     contact around then, the sale was probably never sent (the app
                                     refused it, or the rep did not finish). Either way the rep
                                     sends it again from the phone; an unchanged basket goes out
                                     under the same reference, so it cannot be posted twice.

.PARAMETER Van
    The van code the handset's references start with, e.g. VAN005.

.PARAMETER Customer
    Part of the customer's name or account code. Optional: without it every sale of the van is shown.

.PARAMETER Date
    The trading day, yyyy-MM-dd in CAT. Default today.

.PARAMETER From
    Start of the window, HH:mm CAT. Default 00:00.

.PARAMETER To
    End of the window, HH:mm CAT. Default 23:59.

.PARAMETER ConnectionString
    Overrides the connection string read from the API's web.config. For testing elsewhere.

.EXAMPLE
    .\Trace-VanSale.ps1 -Van VAN005 -Customer stafu -From 10:00 -To 13:00
#>
param(
    [Parameter(Mandatory = $true)][string]$Van,
    [string]$Customer,
    [string]$Date,
    [string]$From = '00:00',
    [string]$To = '23:59',
    [string]$InetpubRoot = 'C:\inetpub',
    [string]$IisLogRoot = 'C:\inetpub\logs\LogFiles',
    [string]$ConnectionString
)

$ErrorActionPreference = 'Stop'

# --- Inputs -------------------------------------------------------------------------------
# Everything typed here ends up inside SQL, so it is held to a shape that cannot break out of it.
if ($Van -notmatch '^[A-Za-z0-9_]{2,30}$') { throw "Van '$Van' is not a van code (letters, digits and _ only), e.g. VAN005." }
if ($Customer -and $Customer -notmatch "^[A-Za-z0-9 .&'()/_-]{1,60}$") { throw "Customer '$Customer' has characters this script does not accept." }
if (-not $Date) { $Date = [System.TimeZoneInfo]::ConvertTimeBySystemTimeZoneId([datetime]::UtcNow, 'South Africa Standard Time').ToString('yyyy-MM-dd') }
if ($Date -notmatch '^\d{4}-\d{2}-\d{2}$') { throw "Date '$Date' is not yyyy-MM-dd." }
if ($From -notmatch '^\d{1,2}:\d{2}$' -or $To -notmatch '^\d{1,2}:\d{2}$') { throw 'From and To are HH:mm, e.g. 10:30.' }

$Van = $Van.ToUpperInvariant()
$cat = [System.Globalization.CultureInfo]::InvariantCulture
$fromCat = [datetime]::ParseExact("$Date $From", 'yyyy-MM-dd H:mm', $cat)
$toCat = [datetime]::ParseExact("$Date $To", 'yyyy-MM-dd H:mm', $cat).AddSeconds(59)
if ($toCat -le $fromCat) { throw 'To must be after From.' }
# CAT is UTC+2 all year (no daylight saving), so a fixed offset is exact.
$fromUtc = $fromCat.AddHours(-2)
$toUtc = $toCat.AddHours(-2)

# The window and the customer filter as SQL. Customer text is passed in dollar quotes, which the
# input check above keeps free of '$'.
$windowSql = "'{0:yyyy-MM-dd HH:mm:ss}+02'::timestamptz AND '{1:yyyy-MM-dd HH:mm:ss}+02'::timestamptz" -f $fromCat, $toCat
$customerPattern = if ($Customer) { '$c$%' + $Customer + '%$c$' } else { $null }

function Write-Section([string]$Title) {
    Write-Host ''
    Write-Host ('=' * 100)
    Write-Host $Title
    Write-Host ('=' * 100)
}

# --- Postgres -----------------------------------------------------------------------------
if (-not $ConnectionString) {
    foreach ($dir in (Get-ChildItem -Path $InetpubRoot -Directory -Filter 'ShopInventory-API*' -ErrorAction SilentlyContinue)) {
        $configPath = Join-Path $dir.FullName 'web.config'
        if (-not (Test-Path $configPath)) { continue }
        [xml]$config = Get-Content -Path $configPath -Raw
        foreach ($var in $config.SelectNodes('//environmentVariable')) {
            if ($var.GetAttribute('name') -eq 'ConnectionStrings__DefaultConnection' -and -not $ConnectionString) {
                $ConnectionString = $var.GetAttribute('value')
            }
        }
    }
}
if (-not $ConnectionString) {
    $prodJson = Join-Path $InetpubRoot 'ShopInventory-API\appsettings.Production.json'
    if (Test-Path $prodJson) {
        $ConnectionString = (Get-Content $prodJson -Raw | ConvertFrom-Json).ConnectionStrings.DefaultConnection
    }
}
if (-not $ConnectionString) { throw 'No connection string found in any API web.config or appsettings.Production.json.' }

$parts = @{}
foreach ($pair in $ConnectionString -split ';') {
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
# name here needs. Read-only, and in CAT so every stamp prints as the rep would say it.
function Invoke-Psql([string]$Sql, [switch]$Table) {
    $sqlFile = Join-Path $env:TEMP ("trace-van-sale-{0}.sql" -f [guid]::NewGuid().ToString('N'))
    $Sql | Set-Content -Path $sqlFile -Encoding ASCII
    $env:PGPASSWORD = $parts['password']
    $env:PGOPTIONS = '-c default_transaction_read_only=on -c timezone=Africa/Harare'
    try {
        if ($Table) {
            $raw = & $psql -w -h $pgHost -p $pgPort -U $pgUser -d $pgDb -q -P pager=off -P footer=on -f $sqlFile
        } else {
            $raw = & $psql -w -h $pgHost -p $pgPort -U $pgUser -d $pgDb -A -t -q -P pager=off -f $sqlFile
        }
        if ($LASTEXITCODE -ne 0) { throw "psql exited with $LASTEXITCODE" }
        return $raw
    } finally {
        Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
        Remove-Item Env:PGOPTIONS -ErrorAction SilentlyContinue
        Remove-Item $sqlFile -ErrorAction SilentlyContinue
    }
}

function Show-Table([string]$Sql) {
    Invoke-Psql $Sql -Table | ForEach-Object { Write-Host "  $_" }
}

function Invoke-JsonQuery([string]$Sql) {
    $raw = Invoke-Psql "SELECT coalesce(json_agg(t), '[]'::json) FROM ($Sql) t;"
    # ConvertFrom-Json hands a JSON array back as one object in 5.1; unroll it.
    return @((($raw -join "`n") | ConvertFrom-Json) | ForEach-Object { $_ })
}

$customerLabel = if ($Customer) { "customer like '$Customer'" } else { 'every customer' }
Write-Host ("Trace of {0}, {1}, {2:yyyy-MM-dd HH:mm} to {3:HH:mm} CAT. Read-only." -f $Van, $customerLabel, $fromCat, $toCat)

# The van's reps: the van's warehouse is assigned to them, or the van account is their partner.
$repsWhere = @"
(u."AssignedWarehouseCodes" ILIKE '%"$Van"%' OR upper(u."AssignedBusinessPartnerCode") = '$Van' OR upper(u."Username") = '$Van')
"@

# --- 1. The reps and their sessions ---------------------------------------------------------
Write-Section "1. Who sells from $Van, and when their handsets were talking to the server"
Show-Table @"
SELECT u."Username", trim(coalesce(u."FirstName", '') || ' ' || coalesce(u."LastName", '')) AS "Name",
       u."Role", u."IsActive" AS "Active", u."AssignedWarehouseCodes" AS "Warehouses",
       to_char(u."LastLoginAt", 'YYYY-MM-DD HH24:MI') AS "LastSignIn",
       (SELECT count(*) FROM "RefreshTokens" t WHERE t."UserId" = u."Id" AND t."CreatedAt" BETWEEN $windowSql) AS "SessionRenewals",
       (SELECT to_char(min(t."CreatedAt"), 'HH24:MI:SS') FROM "RefreshTokens" t WHERE t."UserId" = u."Id" AND t."CreatedAt" BETWEEN $windowSql) AS "FirstRenewal",
       (SELECT to_char(max(t."CreatedAt"), 'HH24:MI:SS') FROM "RefreshTokens" t WHERE t."UserId" = u."Id" AND t."CreatedAt" BETWEEN $windowSql) AS "LastRenewal"
FROM "Users" u
WHERE $repsWhere
ORDER BY u."Username";
"@
Write-Host '  A renewal is the handset refreshing its sign-in, so it proves contact at that moment. No renewals'
Write-Host '  in the window means the handset was off, out of signal, or not opened.'

Write-Host ''
Write-Host '  Session renewals in the window, by rep (CAT):'
Show-Table @"
SELECT u."Username", to_char(t."CreatedAt", 'HH24:MI:SS') AS "RenewedAt"
FROM "RefreshTokens" t JOIN "Users" u ON u."Id" = t."UserId"
WHERE $repsWhere AND t."CreatedAt" BETWEEN $windowSql
ORDER BY t."CreatedAt"
LIMIT 200;
"@

# --- 2. Audit log ---------------------------------------------------------------------------
Write-Section '2. What those reps did, as the audit log has it (CAT)'
Show-Table @"
SELECT to_char(a."Timestamp", 'HH24:MI:SS') AS "At", a."Username", a."Action", a."EntityType", a."EntityId",
       a."IsSuccess" AS "Ok", left(coalesce(a."ErrorMessage", ''), 80) AS "Error",
       a."AppVersion", a."DeviceModel"
FROM "AuditLogs" a
WHERE a."Timestamp" BETWEEN $windowSql
  AND (a."UserId" IN (SELECT u."Id"::text FROM "Users" u WHERE $repsWhere)
       OR upper(a."Username") IN (SELECT upper(u."Username") FROM "Users" u WHERE $repsWhere))
ORDER BY a."Timestamp"
LIMIT 300;
"@

# --- 3. Customer visits ---------------------------------------------------------------------
Write-Section '3. Customer visits the server holds for those reps (CAT)'
$visitCustomer = if ($customerPattern) { "AND (v.""CustomerName"" ILIKE $customerPattern OR v.""CustomerCode"" ILIKE $customerPattern)" } else { '' }
Show-Table @"
SELECT v."Username", v."CustomerCode", left(v."CustomerName", 40) AS "Customer",
       to_char(v."CheckInTime", 'HH24:MI:SS') AS "CheckIn",
       to_char(v."CheckOutTime", 'HH24:MI:SS') AS "CheckOut",
       to_char(v."CheckInRecordedAt", 'HH24:MI:SS') AS "CheckInReachedServer",
       to_char(v."CheckOutRecordedAt", 'HH24:MI:SS') AS "CheckOutReachedServer"
FROM "TimesheetEntries" v
WHERE upper(v."Username") IN (SELECT upper(u."Username") FROM "Users" u WHERE $repsWhere)
  AND (v."CheckInTime" BETWEEN $windowSql OR v."CheckOutTime" BETWEEN $windowSql)
  $visitCustomer
ORDER BY v."CheckInTime";
"@
Write-Host '  A check-in that reached the server long after it was made was queued on the phone: the handset'
Write-Host '  had no signal at that customer.'

# --- 4. Sales the server holds --------------------------------------------------------------
Write-Section "4. Every sale and sales order the server holds for $Van in the window (CAT)"
$saleCustomer = if ($customerPattern) {
    "AND (r.""CardName"" ILIKE $customerPattern OR r.""RouteCustomerName"" ILIKE $customerPattern OR r.""RouteCustomerCode"" ILIKE $customerPattern OR r.""CardCode"" ILIKE $customerPattern)"
} else { '' }
$deskCustomer = if ($customerPattern) {
    "AND (s.""CardName"" ILIKE $customerPattern OR s.""RouteCustomerName"" ILIKE $customerPattern OR s.""RouteCustomerCode"" ILIKE $customerPattern OR s.""CardCode"" ILIKE $customerPattern)"
} else { '' }
$orderCustomer = if ($customerPattern) {
    "AND (o.""CardName"" ILIKE $customerPattern OR o.""RouteCustomerName"" ILIKE $customerPattern OR o.""RouteCustomerCode"" ILIKE $customerPattern OR o.""CardCode"" ILIKE $customerPattern)"
} else { '' }

Write-Host '  Online sales (one row per reservation the sale was made from):'
Show-Table @"
SELECT to_char(r."CreatedAt", 'HH24:MI:SS') AS "At", r."ExternalReferenceId" AS "Reference",
       left(coalesce(r."RouteCustomerName", r."CardName"), 32) AS "Customer", r."TotalValue" AS "Total",
       r."Status" AS "Reservation",
       CASE s."FiscalizationStatus" WHEN 0 THEN 'Pending' WHEN 1 THEN 'Success' WHEN 2 THEN 'Failed' WHEN 3 THEN 'Skipped' END AS "Fiscal",
       s."FiscalReceiptNumber" AS "Receipt",
       coalesce(r."SAPDocNum", s."SapDocNum", q."SapDocNum") AS "SapInvoice",
       CASE q."Status" WHEN 0 THEN 'Pending' WHEN 1 THEN 'Processing' WHEN 2 THEN 'Completed' WHEN 3 THEN 'Failed'
                       WHEN 4 THEN 'RequiresReview' WHEN 5 THEN 'Cancelled' WHEN 6 THEN 'PartiallyCompleted' WHEN 7 THEN 'Fiscalized' END AS "Queue",
       left(coalesce(q."LastError", s."FiscalError", s."LastPostingError", r."CancellationReason", ''), 70) AS "LastError",
       r."CreatedBy"
FROM "StockReservations" r
LEFT JOIN LATERAL (SELECT * FROM "DesktopSales" d WHERE d."ExternalReferenceId" = r."ExternalReferenceId" ORDER BY d."Id" DESC LIMIT 1) s ON true
LEFT JOIN LATERAL (SELECT * FROM "InvoiceQueue" iq WHERE iq."ExternalReference" = r."ExternalReferenceId" ORDER BY iq."Id" DESC LIMIT 1) q ON true
WHERE r."CreatedAt" BETWEEN $windowSql
  AND (upper(r."ExternalReferenceId") LIKE '$Van-%' OR upper(r."CardCode") = '$Van')
  $saleCustomer
ORDER BY r."CreatedAt";
"@

Write-Host ''
Write-Host '  Sales stored without a reservation (offline uploads and receipts), by reference:'
Show-Table @"
SELECT to_char(s."CreatedAt", 'HH24:MI:SS') AS "At", s."ExternalReferenceId" AS "Reference", s."SourceSystem",
       left(coalesce(s."RouteCustomerName", s."CardName"), 32) AS "Customer", s."TotalAmount" AS "Total",
       CASE s."FiscalizationStatus" WHEN 0 THEN 'Pending' WHEN 1 THEN 'Success' WHEN 2 THEN 'Failed' WHEN 3 THEN 'Skipped' END AS "Fiscal",
       s."FiscalReceiptNumber" AS "Receipt", s."SapDocNum" AS "SapInvoice",
       CASE s."ConsolidationStatus" WHEN 0 THEN 'Pending' WHEN 1 THEN 'Consolidated' WHEN 2 THEN 'Failed' WHEN 3 THEN 'Excluded' END AS "Posting",
       left(coalesce(s."LastPostingError", s."FiscalError", ''), 70) AS "LastError"
FROM "DesktopSales" s
WHERE s."CreatedAt" BETWEEN $windowSql
  AND (upper(s."ExternalReferenceId") LIKE '$Van-%' OR upper(s."CardCode") = '$Van' OR upper(s."WarehouseCode") = '$Van')
  AND NOT EXISTS (SELECT 1 FROM "StockReservations" r WHERE r."ExternalReferenceId" = s."ExternalReferenceId")
  $deskCustomer
ORDER BY s."CreatedAt";
"@

Write-Host ''
Write-Host '  Sales orders:'
Show-Table @"
SELECT to_char(o."CreatedAt", 'HH24:MI:SS') AS "At", o."ClientRequestId" AS "Reference", o."OrderNumber",
       left(coalesce(o."RouteCustomerName", o."CardName"), 32) AS "Customer", o."DocTotal" AS "Total",
       o."Status" AS "StatusCode", o."SAPDocNum" AS "SapOrder", left(coalesce(o."SyncError", ''), 60) AS "SyncError"
FROM "SalesOrders" o
WHERE o."CreatedAt" BETWEEN $windowSql
  AND upper(coalesce(o."ClientRequestId", '')) LIKE '$Van-%'
  $orderCustomer
ORDER BY o."CreatedAt";
"@

# --- 5. IIS ---------------------------------------------------------------------------------
Write-Section '5. Van-sales requests that reached IIS in the window (CAT)'

# Every document any van stored in the window, to match each POST to what it produced.
$stored = Invoke-JsonQuery @"
SELECT r."ExternalReferenceId" AS "Ref", to_char(r."CreatedAt" AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS.MS') AS "AtUtc"
FROM "StockReservations" r WHERE r."CreatedAt" BETWEEN $windowSql AND r."ExternalReferenceId" ILIKE 'VAN%'
UNION ALL
SELECT s."ExternalReferenceId", to_char(s."CreatedAt" AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS.MS')
FROM "DesktopSales" s WHERE s."CreatedAt" BETWEEN $windowSql AND s."ExternalReferenceId" ILIKE 'VAN%'
  AND NOT EXISTS (SELECT 1 FROM "StockReservations" r WHERE r."ExternalReferenceId" = s."ExternalReferenceId")
UNION ALL
SELECT o."ClientRequestId", to_char(o."CreatedAt" AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI:SS.MS')
FROM "SalesOrders" o WHERE o."CreatedAt" BETWEEN $windowSql AND o."ClientRequestId" ILIKE 'VAN%'
"@
$storedDocs = @($stored | ForEach-Object {
    [pscustomobject]@{
        Ref   = $_.Ref
        AtUtc = [datetime]::ParseExact($_.AtUtc, 'yyyy-MM-dd HH:mm:ss.fff', $cat,
            [System.Globalization.DateTimeStyles]::AssumeUniversal -bor [System.Globalization.DateTimeStyles]::AdjustToUniversal)
    }
})

$saleEndpoints = @{
    '/api/vansales/order'                      = 'direct sale'
    '/api/vansales/order/with-batches'         = 'direct sale'
    '/api/vansales/sales'                      = 'offline upload'
    '/api/vansales/sales-order'                = 'sales order'
    '/api/vansales/order/convert-to-invoice'   = 'convert to invoice'
}
$posts = New-Object System.Collections.Generic.List[object]
$mentions = New-Object System.Collections.Generic.List[object]
$perHour = @{}
$vanToken = "/$Van-"

if (-not (Test-Path $IisLogRoot)) {
    Write-Host "  $IisLogRoot does not exist. Pass -IisLogRoot (IIS Manager > site > Logging shows the directory)."
} else {
    # IIS names its files by UTC date, so a CAT day can span two of them.
    $wanted = @(('u_ex{0:yyMMdd}.log' -f $fromUtc), ('u_ex{0:yyMMdd}.log' -f $toUtc)) | Select-Object -Unique
    $logFiles = @(Get-ChildItem -Path $IisLogRoot -Recurse -Filter 'u_ex*.log' -ErrorAction SilentlyContinue |
        Where-Object { $wanted -contains $_.Name })
    if ($logFiles.Count -eq 0) {
        Write-Host "  No IIS log named $($wanted -join ' or ') under $IisLogRoot."
    } else {
        Write-Host "  Read: $(($logFiles | ForEach-Object { '{0}\{1}' -f $_.Directory.Name, $_.Name }) -join ', ')"
    }

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
                if ($line.StartsWith('#')) { continue }
                if ($line.IndexOf('/api/vansales', [System.StringComparison]::OrdinalIgnoreCase) -lt 0) { continue }
                if (-not $fields.ContainsKey('cs-uri-stem')) { continue }
                $cols = $line -split ' '
                $endUtc = [datetime]::ParseExact("$($cols[$fields['date']]) $($cols[$fields['time']])", 'yyyy-MM-dd HH:mm:ss', $cat,
                    [System.Globalization.DateTimeStyles]::AssumeUniversal -bor [System.Globalization.DateTimeStyles]::AdjustToUniversal)
                if ($endUtc -lt $fromUtc -or $endUtc -gt $toUtc.AddMinutes(2)) { continue }

                $stemRaw = $cols[$fields['cs-uri-stem']]
                $stem = $stemRaw.ToLowerInvariant().TrimEnd('/')
                $method = $cols[$fields['cs-method']]
                $ms = 0
                if ($fields.ContainsKey('time-taken')) { [void][int]::TryParse($cols[$fields['time-taken']], [ref]$ms) }
                $status = $cols[$fields['sc-status']]
                if ($fields.ContainsKey('sc-substatus') -and $cols[$fields['sc-substatus']] -ne '0') { $status = "$status.$($cols[$fields['sc-substatus']])" }
                $win32 = if ($fields.ContainsKey('sc-win32-status')) { $cols[$fields['sc-win32-status']] } else { '' }

                $hour = '{0:HH}:00' -f $endUtc.AddHours(2)
                $key = "$site|$hour"
                if ($perHour.ContainsKey($key)) { $perHour[$key]++ } else { $perHour[$key] = 1 }

                $entry = [pscustomobject]@{
                    Site = $site; Method = $method; Stem = $stemRaw; Status = $status; Win32 = $win32; Ms = $ms
                    EndUtc = $endUtc; StartUtc = $endUtc.AddMilliseconds(-$ms)
                }
                if ($stemRaw.IndexOf($vanToken, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) { $mentions.Add($entry) }
                if ($method -eq 'POST' -and $saleEndpoints.ContainsKey($stem)) {
                    $entry | Add-Member -NotePropertyName Kind -NotePropertyValue $saleEndpoints[$stem]
                    $posts.Add($entry)
                }
            }
        } finally {
            $reader.Dispose()
            $stream.Dispose()
        }
    }

    Write-Host '  Each request is logged once by every IIS site it passes through (the public site and the live'
    Write-Host '  slot), so the same POST can appear twice under different sites. IIS cannot tell vans apart: a'
    Write-Host '  POST is tied to a van only through the document it stored.'
    Write-Host ''
    Write-Host '  Requests to /api/vansales per hour, every van (shows whether handsets were reaching the server):'
    foreach ($group in ($perHour.Keys | ForEach-Object { $p = $_ -split '\|'; [pscustomobject]@{ Site = $p[0]; Hour = $p[1]; Count = $perHour[$_] } } |
            Group-Object Site | Sort-Object Name)) {
        $row = ($group.Group | Sort-Object Hour | ForEach-Object { '{0} {1}' -f $_.Hour, $_.Count }) -join ' | '
        Write-Host ("    {0,-24} {1}" -f $group.Name, $row)
    }

    Write-Host ''
    Write-Host '  Sale POSTs, each with the document it stored (any van). "-- nothing stored --" is a request'
    Write-Host '  that reached the server and left no sale behind: refused (4xx) or failed (5xx).'
    if ($posts.Count -eq 0) {
        Write-Host '    (no sale POSTs in the window)'
    } else {
        Write-Host ('    {0,-24} {1,-9} {2,-9} {3,-19} {4,-8} {5,7}  {6}' -f 'site', 'arrived', 'ended', 'kind', 'status', 'ms', 'stored')
        foreach ($post in ($posts | Sort-Object EndUtc, Site)) {
            # A document made by this request was written between its first and last byte. Two seconds
            # of slack either side covers IIS's whole-second end stamp.
            $made = @($storedDocs | Where-Object { $_.AtUtc -ge $post.StartUtc.AddSeconds(-2) -and $_.AtUtc -le $post.EndUtc.AddSeconds(2) } |
                Select-Object -ExpandProperty Ref -Unique)
            $madeText = if ($made.Count -eq 0) { '-- nothing stored --' } else { $made -join ', ' }
            $ours = ($made | Where-Object { $_.ToUpperInvariant().StartsWith("$Van-") }).Count -gt 0
            $marker = if ($ours) { '>>' } else { '  ' }
            $statusText = if ($post.Win32 -and $post.Win32 -ne '0') { "$($post.Status) w$($post.Win32)" } else { $post.Status }
            Write-Host ('  {0}{1,-24} {2:HH:mm:ss}  {3:HH:mm:ss}  {4,-19} {5,-8} {6,7}  {7}' -f `
                $marker, $post.Site, $post.StartUtc.AddHours(2), $post.EndUtc.AddHours(2), $post.Kind, $statusText, $post.Ms, $madeText)
        }
        Write-Host "    >> marks a POST that stored a $Van document. w64 is the client hanging up before the answer."
    }

    Write-Host ''
    Write-Host "  Requests whose URL carries a $Van reference (the handset asking about a sale it sent):"
    if ($mentions.Count -eq 0) {
        Write-Host '    (none)'
    } else {
        foreach ($m in ($mentions | Sort-Object EndUtc, Site)) {
            Write-Host ('    {0:HH:mm:ss}  {1,-24} {2,-5} {3,-8} {4}' -f $m.EndUtc.AddHours(2), $m.Site, $m.Method, $m.Status, $m.Stem)
        }
    }
}

# --- 6. API log -----------------------------------------------------------------------------
Write-Section "6. API log lines in the window that mention $Van (CAT, as the log is written)"
$logName = 'shopinventory-api-{0:yyyyMMdd}.log' -f $fromCat
$logs = @(Get-ChildItem -Path $InetpubRoot -Directory -Filter 'ShopInventory-API*' -ErrorAction SilentlyContinue |
    ForEach-Object { Join-Path $_.FullName "logs\$logName" } | Where-Object { Test-Path $_ })
if ($logs.Count -eq 0) { Write-Host "  No $logName under any $InetpubRoot\ShopInventory-API*\logs." }
foreach ($log in $logs) {
    Write-Host ''
    Write-Host "  [$log]"
    $shown = 0
    $stream = New-Object System.IO.FileStream($log, 'Open', 'Read', 'ReadWrite')
    $reader = New-Object System.IO.StreamReader($stream)
    try {
        while ($null -ne ($line = $reader.ReadLine())) {
            if ($line.Length -lt 23 -or $shown -ge 200) { continue }
            $stamp = [datetime]::MinValue
            if (-not [datetime]::TryParseExact($line.Substring(0, 23), 'yyyy-MM-dd HH:mm:ss.fff', $cat,
                    [System.Globalization.DateTimeStyles]::None, [ref]$stamp)) { continue }
            if ($stamp -lt $fromCat -or $stamp -gt $toCat) { continue }
            if ($line.IndexOf($Van, [System.StringComparison]::OrdinalIgnoreCase) -lt 0) { continue }
            $text = if ($line.Length -gt 300) { $line.Substring(0, 300) + '...' } else { $line }
            Write-Host "    $text"
            $shown++
        }
    } finally {
        $reader.Dispose()
        $stream.Dispose()
    }
    if ($shown -eq 0) { Write-Host "    (no line in the window mentions $Van)" }
    if ($shown -ge 200) { Write-Host '    (stopped at 200 lines; narrow -From/-To)' }
}

Write-Host ''
Write-Host 'Done. Nothing was changed.'
