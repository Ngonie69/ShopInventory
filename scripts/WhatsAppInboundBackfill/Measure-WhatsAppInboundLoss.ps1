<#
.SYNOPSIS
    Read-only: measures how many inbound WhatsApp messages the API answered but never stored.

.DESCRIPTION
    Until ShopInventory#762, OpenWA keyed every message.received webhook `msg_unknown` (it read
    data.messageId; a message carries data.id). The API deduplicated on that key, so it stored the
    first inbound message ever received and then either answered 409 (OpenWA retries, then drops the
    message) or, once the 60-minute claim had expired, answered 202 with the first message's receipt
    and stored nothing.

    Run on KFL-DNS2 (10.10.10.9) in Windows PowerShell 5.1. It changes nothing:
      1. What WhatsAppWebhookEvents holds, grouped by event type and idempotency key, and per session.
      2. The claims the webhook made in IdempotencyRequests.
      3. Every POST to /api/whatsapp/webhook/openwa in the IIS logs, by day and status. Each one is a
         message OpenWA tried to deliver, so 202s minus stored rows plus 409 dispatches is the loss.
      4. OpenWA's sessions and the WhatsApp Web profiles on disk: only a session whose profile still
         exists can have its history read back by Export-WhatsAppReceivedMessages.js.

    Postgres runs with default_transaction_read_only=on. Sender numbers print as last four digits.

.PARAMETER Since
    First day of IIS logs to read (UTC). Default 2026-09-01: no session had a webhook before then.

.PARAMETER ConnectionString
    Overrides ConnectionStrings__DefaultConnection from web.config, for a non-production database.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Measure-WhatsAppInboundLoss.ps1
#>
param(
    [datetime]$Since = '2026-09-01',
    [string]$InetpubRoot = 'C:\inetpub',
    [string]$IisLogRoot = 'C:\inetpub\logs\LogFiles',
    [string]$OpenWaRoot = 'C:\ShopInventory\OpenWA',
    [string]$WebhookPath = '/api/whatsapp/webhook/openwa',
    [string]$ConnectionString
)

$ErrorActionPreference = 'Stop'

function Write-Section([string]$Title) {
    Write-Host ''
    Write-Host ('=' * 78)
    Write-Host $Title
    Write-Host ('=' * 78)
}

function Get-Masked([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return '(none)' }
    $digits = ($Value -replace '\D', '')
    if ($digits.Length -le 4) { return '****' }
    return '****' + $digits.Substring($digits.Length - 4)
}

# --- Settings ------------------------------------------------------------------------------
$connectionString = $ConnectionString
$openWaBaseUrl = $null
$openWaApiKey = $null
foreach ($dir in Get-ChildItem -Path $InetpubRoot -Directory -Filter 'ShopInventory-API*' -ErrorAction SilentlyContinue) {
    $configPath = Join-Path $dir.FullName 'web.config'
    if (-not (Test-Path $configPath)) { continue }
    [xml]$config = Get-Content -Path $configPath -Raw
    foreach ($var in $config.SelectNodes('//environmentVariable')) {
        $name = $var.GetAttribute('name')
        $value = $var.GetAttribute('value')
        if ($name -eq 'ConnectionStrings__DefaultConnection' -and -not $connectionString) { $connectionString = $value }
        if ($name -eq 'OpenWA__BaseUrl' -and -not $openWaBaseUrl) { $openWaBaseUrl = $value.TrimEnd('/') }
        if ($name -eq 'OpenWA__ApiKey' -and -not $openWaApiKey) { $openWaApiKey = $value }
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

# One JSON document per query, so the quoting survives PowerShell 5.1 (SQL goes through a file, never -c).
function Invoke-JsonQuery([string]$Sql) {
    $sqlFile = Join-Path $env:TEMP ("measure-whatsapp-loss-{0}.sql" -f [guid]::NewGuid().ToString('N'))
    "SELECT coalesce(json_agg(t), '[]'::json) FROM ($Sql) t;" | Set-Content -Path $sqlFile -Encoding ASCII
    $env:PGPASSWORD = $parts['password']
    $env:PGOPTIONS = '-c default_transaction_read_only=on'
    try {
        $raw = & $psql -w -h $pgHost -p $pgPort -U $pgUser -d $pgDb -A -t -q -P pager=off -f $sqlFile
        if ($LASTEXITCODE -ne 0) { throw "psql exited with $LASTEXITCODE" }
        # The comma keeps a one-row result an array: returned bare, 5.1 hands back a PSCustomObject with no Count.
        return ,@((($raw -join "`n") | ConvertFrom-Json) | ForEach-Object { $_ })
    } finally {
        Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
        Remove-Item Env:PGOPTIONS -ErrorAction SilentlyContinue
        Remove-Item $sqlFile -ErrorAction SilentlyContinue
    }
}

# --- 1. What the inbox holds ---------------------------------------------------------------
Write-Section '1. WhatsAppWebhookEvents by event type and idempotency key'
$byKey = Invoke-JsonQuery @'
SELECT "EventType", "IdempotencyKey", count(*) AS "Rows",
       min("ReceivedAtUtc") AS "FirstReceived", max("ReceivedAtUtc") AS "LastReceived"
FROM "WhatsAppWebhookEvents"
GROUP BY 1, 2
ORDER BY 3 DESC, 1, 2
'@
if ($byKey.Count -eq 0) { Write-Host '  (the table is empty)' }
foreach ($row in $byKey) {
    Write-Host ("  {0,-20} {1,-60} {2,5} rows  {3} .. {4}" -f $row.EventType, $row.IdempotencyKey, $row.Rows, $row.FirstReceived, $row.LastReceived)
}

Write-Host ''
Write-Host '  Received messages per session (the sessionId OpenWA sends; the key is built on it):'
$bySession = Invoke-JsonQuery @'
SELECT "SessionName", "Direction", count(*) AS "Rows", count(DISTINCT "MessageId") AS "Messages",
       min("OccurredAtUtc") AS "FirstOccurred", max("OccurredAtUtc") AS "LastOccurred"
FROM "WhatsAppWebhookEvents"
WHERE "EventType" = 'message.received'
GROUP BY 1, 2
ORDER BY 1, 2
'@
if ($bySession.Count -eq 0) { Write-Host '    (none)' }
foreach ($row in $bySession) {
    Write-Host ("    {0,-38} {1,-9} {2,5} rows  {3,5} distinct ids  {4} .. {5}" -f $row.SessionName, $row.Direction, $row.Rows, $row.Messages, $row.FirstOccurred, $row.LastOccurred)
}

Write-Host ''
Write-Host '  The most recent 10 received rows:'
$recent = Invoke-JsonQuery @'
SELECT "Id", "IdempotencyKey", "DeliveryId", "SessionName", "MessageId", "SenderNumber", "MessageType",
       "OccurredAtUtc", "ReceivedAtUtc"
FROM "WhatsAppWebhookEvents"
WHERE "EventType" = 'message.received'
ORDER BY "Id" DESC
LIMIT 10
'@
foreach ($row in $recent) {
    Write-Host ("    #{0,-6} {1,-8} from {2}  occurred {3}  received {4}  key {5}" -f $row.Id, $row.MessageType, (Get-Masked $row.SenderNumber), $row.OccurredAtUtc, $row.ReceivedAtUtc, $row.IdempotencyKey)
}

# --- 2. The webhook's claims ---------------------------------------------------------------
Write-Section '2. IdempotencyRequests claimed by the webhook (scope whatsapp.webhook.receive)'
$claims = Invoke-JsonQuery @'
SELECT "IdempotencyKey", "Status", count(*) AS "Rows", min("CreatedAtUtc") AS "FirstCreated",
       max("CreatedAtUtc") AS "LastCreated", max("ExpiresAtUtc") AS "LastExpires"
FROM "IdempotencyRequests"
WHERE "Scope" = 'whatsapp.webhook.receive'
GROUP BY 1, 2
ORDER BY 3 DESC
LIMIT 20
'@
if ($claims.Count -eq 0) { Write-Host '  (none: expired claims are swept, so this only shows the last few hours)' }
foreach ($row in $claims) {
    Write-Host ("  {0,-60} status {1}  {2,4} rows  {3} .. {4}  expires {5}" -f $row.IdempotencyKey, $row.Status, $row.Rows, $row.FirstCreated, $row.LastCreated, $row.LastExpires)
}

# --- 3. What OpenWA tried to deliver -------------------------------------------------------
Write-Section ("3. POST {0} in the IIS logs since {1:yyyy-MM-dd} (UTC)" -f $WebhookPath, $Since)
# IIS writes UTC and names its files u_exYYMMDD.log. Every site is read: OpenWA posts to the public
# :5106 binding, which may be a different site from the slot that served it.
$logFiles = @(Get-ChildItem -Path $IisLogRoot -Recurse -Filter 'u_ex*.log' -ErrorAction SilentlyContinue |
    Where-Object {
        $stamp = $_.BaseName.Substring(4)
        $day = [datetime]::MinValue
        [datetime]::TryParseExact($stamp, 'yyMMdd', [Globalization.CultureInfo]::InvariantCulture, 'None', [ref]$day) -and $day -ge $Since.Date
    })
if ($logFiles.Count -eq 0) {
    Write-Host "  No IIS log files under $IisLogRoot from that date. Section skipped."
} else {
    $counts = @{}
    foreach ($file in $logFiles) {
        $fields = $null
        $reader = [IO.StreamReader]::new($file.FullName)
        try {
            while ($null -ne ($line = $reader.ReadLine())) {
                if ($line.StartsWith('#Fields:')) { $fields = $line.Substring(9).Split(' '); continue }
                if ($line.StartsWith('#') -or -not $fields) { continue }
                if ($line.IndexOf($WebhookPath, [StringComparison]::OrdinalIgnoreCase) -lt 0) { continue }
                $values = $line.Split(' ')
                $row = @{}
                for ($i = 0; $i -lt $fields.Length -and $i -lt $values.Length; $i++) { $row[$fields[$i]] = $values[$i] }
                if ($row['cs-method'] -ne 'POST' -or $row['cs-uri-stem'] -ne $WebhookPath) { continue }
                $key = '{0}|{1}|{2}' -f $row['date'], $file.Directory.Name, $row['sc-status']
                $counts[$key] = 1 + [int]$counts[$key]
            }
        } finally {
            $reader.Dispose()
        }
    }

    if ($counts.Count -eq 0) {
        Write-Host '  No webhook POSTs found.'
    } else {
        $totals = @{}
        Write-Host ("  {0,-12} {1,-10} {2,6} {3,6} {4,6} {5,6}" -f 'day', 'site', '202', '409', '401', 'other')
        $days = $counts.Keys | ForEach-Object { $p = $_.Split('|'); '{0}|{1}' -f $p[0], $p[1] } | Sort-Object -Unique
        foreach ($day in $days) {
            $p = $day.Split('|')
            $s202 = [int]$counts["$day|202"]; $s409 = [int]$counts["$day|409"]; $s401 = [int]$counts["$day|401"]
            $other = 0
            foreach ($k in $counts.Keys) {
                if ($k.StartsWith("$day|") -and @('202', '409', '401') -notcontains $k.Split('|')[2]) { $other += $counts[$k] }
            }
            foreach ($pair in @(@('202', $s202), @('409', $s409), @('401', $s401), @('other', $other))) { $totals[$pair[0]] = $pair[1] + [int]$totals[$pair[0]] }
            Write-Host ("  {0,-12} {1,-10} {2,6} {3,6} {4,6} {5,6}" -f $p[0], $p[1], $s202, $s409, $s401, $other)
        }
        Write-Host ("  {0,-12} {1,-10} {2,6} {3,6} {4,6} {5,6}" -f 'TOTAL', '', [int]$totals['202'], [int]$totals['409'], [int]$totals['401'], [int]$totals['other'])
        Write-Host ''
        Write-Host '  Reading it: each 202 is one message OpenWA delivered once. Only the first was stored, so'
        Write-Host '  every other 202 is a message lost with a success answer. A message answered 409 is'
        Write-Host '  retried, so it shows as up to the webhook''s retryCount (3 by default) 409s and is lost.'
    }
}

# --- 4. Sessions and their profiles --------------------------------------------------------
Write-Section '4. OpenWA sessions and the WhatsApp Web profiles on disk'
$profileRoot = Join-Path $OpenWaRoot 'data\sessions'
$names = @{}
$asked = $false
if ($openWaBaseUrl -and $openWaApiKey) {
    try {
        $sessions = @((Invoke-RestMethod -Method Get -Uri "$openWaBaseUrl/api/sessions" -Headers @{ 'X-API-Key' = $openWaApiKey } -TimeoutSec 20) | ForEach-Object { $_ })
        $asked = $true
        foreach ($s in $sessions) {
            $names[$s.name] = $s.id
            Write-Host ("  {0,-24} {1,-14} phone {2,-10} id {3}" -f $s.name, $s.status, (Get-Masked $s.phone), $s.id)
        }
    } catch {
        Write-Host "  Could not list sessions from $openWaBaseUrl : $($_.Exception.Message)"
    }
}
Write-Host ''
if (-not (Test-Path $profileRoot)) {
    Write-Host "  No profile folder at $profileRoot"
} else {
    foreach ($dir in Get-ChildItem -Path $profileRoot -Directory -Filter 'session-*') {
        # OpenWA names the profile after the session's name; the webhook (and the dedupe key) carry its id.
        $name = $dir.Name.Substring('session-'.Length)
        $label = if ($names[$name]) { "id $($names[$name])" } elseif ($asked) { '(not on the gateway)' } else { '(gateway not asked)' }
        Write-Host ("  {0,-40} {1,-40} last written {2}" -f $dir.Name, $label, $dir.LastWriteTime)
    }
}
Write-Host ''
Write-Host '  Messages that reached a session whose profile is gone cannot be read back by any means.'
