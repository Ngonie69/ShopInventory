<#
.SYNOPSIS
    Read-only check that invoices queued for WhatsApp actually left OpenWA.

.DESCRIPTION
    Run on KFL-DNS2 (10.10.10.9) in Windows PowerShell 5.1. It changes nothing:
      1. Prints the OpenWA__* settings in each API slot's web.config. Secrets print as
         "set (N chars)", never their value.
      2. Reads the CustomerDocuments.* runtime settings from SystemConfigs (read-only Postgres).
      3. Lists OpenWA's sessions and marks the one documents are sent from.
      4. Lists the CustomerDocumentDeliveries rows created in the window.
      5. For every row that was handed to OpenWA, looks for the outgoing message in OpenWA's own
         log for that chat: same file name, sent between two minutes before the send was issued
         and five minutes after the document timeout. This is the runbook's "Proving it works"
         step 3 (docs/operations/whatsapp-production.md). Judge the phone first, then this.

    Recipient numbers print as their last four digits only.

.PARAMETER Hours
    How far back to look at deliveries. Default 24.

.PARAMETER OpenWaBaseUrl
    Overrides OpenWA__BaseUrl from web.config.

.PARAMETER OpenWaApiKey
    Overrides OpenWA__ApiKey from web.config.
#>
param(
    [int]$Hours = 24,
    [string]$InetpubRoot = 'C:\inetpub',
    [string]$OpenWaBaseUrl,
    [string]$OpenWaApiKey,
    [int]$DocumentTimeoutSeconds = 60
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

# --- 1. Settings per slot ----------------------------------------------------------------
Write-Section '1. OpenWA settings in each API web.config'
$apiDirs = Get-ChildItem -Path $InetpubRoot -Directory -Filter 'ShopInventory-API*'
$connectionString = $null
$configBaseUrl = $null
$configApiKey = $null
foreach ($dir in $apiDirs) {
    $configPath = Join-Path $dir.FullName 'web.config'
    if (-not (Test-Path $configPath)) { continue }
    Write-Host ''
    Write-Host "[$($dir.Name)]  (web.config modified $((Get-Item $configPath).LastWriteTime))"
    [xml]$config = Get-Content -Path $configPath -Raw
    $found = $false
    foreach ($var in $config.SelectNodes('//environmentVariable')) {
        $name = $var.GetAttribute('name')
        $value = $var.GetAttribute('value')
        if ($name -eq 'ConnectionStrings__DefaultConnection' -and -not $connectionString) { $connectionString = $value }
        if ($name -notmatch '^OpenWA__') { continue }
        $found = $true
        if ($name -eq 'OpenWA__BaseUrl' -and -not $configBaseUrl) { $configBaseUrl = $value }
        if ($name -eq 'OpenWA__ApiKey' -and -not $configApiKey) { $configApiKey = $value }
        if ($name -eq 'OpenWA__DocumentTimeoutSeconds' -and $value -match '^\d+$') { $DocumentTimeoutSeconds = [int]$value }
        if ($name -match '(ApiKey|Secret)$') {
            $shown = if ([string]::IsNullOrWhiteSpace($value)) { '(EMPTY)' } else { "set ($($value.Length) chars)" }
        } else {
            $shown = $value
        }
        Write-Host ("  {0} = {1}" -f $name, $shown)
    }
    if (-not $found) { Write-Host '  (no OpenWA__ settings: this slot sends nothing and claims nothing)' }
}

if (-not $OpenWaBaseUrl) { $OpenWaBaseUrl = $configBaseUrl }
if (-not $OpenWaApiKey) { $OpenWaApiKey = $configApiKey }
if ($OpenWaBaseUrl) { $OpenWaBaseUrl = $OpenWaBaseUrl.TrimEnd('/') }

# --- Postgres -----------------------------------------------------------------------------
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
    $sqlFile = Join-Path $env:TEMP ("check-whatsapp-sends-{0}.sql" -f [guid]::NewGuid().ToString('N'))
    "SELECT coalesce(json_agg(t), '[]'::json) FROM ($Sql) t;" | Set-Content -Path $sqlFile -Encoding ASCII
    $env:PGPASSWORD = $parts['password']
    $env:PGOPTIONS = '-c default_transaction_read_only=on'
    try {
        $raw = & $psql -w -h $pgHost -p $pgPort -U $pgUser -d $pgDb -A -t -q -P pager=off -f $sqlFile
        if ($LASTEXITCODE -ne 0) { throw "psql exited with $LASTEXITCODE" }
        return @(($raw -join "`n") | ConvertFrom-Json)
    } finally {
        Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
        Remove-Item Env:PGOPTIONS -ErrorAction SilentlyContinue
        Remove-Item $sqlFile -ErrorAction SilentlyContinue
    }
}

# --- 2. Runtime settings ------------------------------------------------------------------
Write-Section '2. Runtime settings (SystemConfigs, set on /whatsapp-deliveries)'
$settings = Invoke-JsonQuery @'
SELECT "Key", "Value" FROM "SystemConfigs" WHERE "Key" LIKE 'CustomerDocuments.%' ORDER BY "Key"
'@
$selectedSession = $null
if ($settings.Count -eq 0) { Write-Host '  (none saved: no session is chosen, so nothing is sent)' }
foreach ($row in $settings) {
    Write-Host ("  {0} = {1}" -f $row.Key, $row.Value)
    if ($row.Key -eq 'CustomerDocuments.WhatsAppSessionId' -and -not [string]::IsNullOrWhiteSpace($row.Value)) {
        $selectedSession = $row.Value.Trim()
    }
}

# --- 3. OpenWA sessions -------------------------------------------------------------------
Write-Section "3. OpenWA sessions at $OpenWaBaseUrl"
$openWaReachable = $false
if (-not $OpenWaBaseUrl -or -not $OpenWaApiKey) {
    Write-Host '  OpenWA__BaseUrl or OpenWA__ApiKey is not set; skipping every gateway check.'
} else {
    $headers = @{ 'X-API-Key' = $OpenWaApiKey }
    try {
        # PowerShell 5.1 hands a JSON array back as ONE object; the pipe unrolls it.
        $sessions = @((Invoke-RestMethod -Method Get -Uri "$OpenWaBaseUrl/api/sessions" -Headers $headers -TimeoutSec 20) | ForEach-Object { $_ })
        $openWaReachable = $true
        foreach ($s in $sessions) {
            $mark = if ($s.id -eq $selectedSession) { '  <- sends documents' } else { '' }
            Write-Host ("  {0,-24} {1,-16} phone {2,-10} id {3}{4}" -f $s.name, $s.status, (Get-Masked $s.phone), $s.id, $mark)
        }
        if ($selectedSession -and -not ($sessions | Where-Object { $_.id -eq $selectedSession })) {
            Write-Host "  !! The chosen session $selectedSession is not on this gateway."
        }
    } catch {
        Write-Host "  !! Could not list sessions: $($_.Exception.Message)"
    }
}

# --- 4. Deliveries ------------------------------------------------------------------------
Write-Section "4. Deliveries created in the last $Hours hour(s)"
$deliveries = Invoke-JsonQuery (@'
SELECT "Id", "Status", "Trigger", "DocumentNumber", "CardCode", "RecipientE164", "RequestedBy",
       "DispatchAttempts", "SessionId", "FileName", "FileBytes", "MessageId",
       left("StatusReason", 160) AS "StatusReason", left("LastError", 160) AS "LastError",
       "FiscalEvidenceSource", "CreatedAtUtc", "SendIssuedAtUtc", "SentAtUtc"
FROM "CustomerDocumentDeliveries"
WHERE "CreatedAtUtc" >= now() - interval '{0} hours'
ORDER BY "Id" DESC
LIMIT 50
'@ -f $Hours)

if ($deliveries.Count -eq 0) { Write-Host '  (none)' }
foreach ($d in $deliveries) {
    Write-Host ''
    Write-Host ("  #{0}  {1}  invoice {2}  {3}  to {4}  by {5}  ({6})" -f `
        $d.Id, $d.Status, $d.DocumentNumber, $d.CardCode, (Get-Masked $d.RecipientE164), $d.RequestedBy, $d.Trigger)
    Write-Host ("      created {0}  issued {1}  sent {2}  attempts {3}" -f $d.CreatedAtUtc, $d.SendIssuedAtUtc, $d.SentAtUtc, $d.DispatchAttempts)
    if ($d.FileName) { Write-Host ("      file {0} ({1} bytes)  fiscal evidence: {2}" -f $d.FileName, $d.FileBytes, $d.FiscalEvidenceSource) }
    if ($d.StatusReason) { Write-Host "      reason: $($d.StatusReason)" }
    if ($d.LastError) { Write-Host "      last error: $($d.LastError)" }
}

# --- 5. Cross-check against OpenWA's log --------------------------------------------------
Write-Section "5. Does OpenWA's own log hold each send? (document timeout ${DocumentTimeoutSeconds}s)"
$issued = @($deliveries | Where-Object { $_.SendIssuedAtUtc })
if ($issued.Count -eq 0) {
    Write-Host '  No delivery in the window was handed to OpenWA yet.'
} elseif (-not $openWaReachable) {
    Write-Host '  Skipped: the gateway could not be asked.'
} else {
    $agree = 0
    $disagree = 0
    foreach ($d in $issued) {
        $session = if ($d.SessionId) { $d.SessionId } else { $selectedSession }
        $chatId = ($d.RecipientE164 -replace '\D', '') + '@c.us'
        $issuedAt = ([datetime]$d.SendIssuedAtUtc).ToUniversalTime()
        $from = $issuedAt.AddMinutes(-2)
        $to = $issuedAt.AddSeconds($DocumentTimeoutSeconds).AddMinutes(5)
        try {
            $history = Invoke-RestMethod -Method Get -Headers $headers -TimeoutSec 30 `
                -Uri ("{0}/api/sessions/{1}/messages?chatId={2}&limit=200" -f $OpenWaBaseUrl, [uri]::EscapeDataString($session), [uri]::EscapeDataString($chatId))
        } catch {
            Write-Host ("  #{0}  ?? could not read OpenWA's log: {1}" -f $d.Id, $_.Exception.Message)
            $disagree++
            continue
        }
        $logged = @($history.messages | Where-Object {
            $_.direction -eq 'outgoing' -and $_.body -ceq $d.FileName -and $_.createdAt -and
            ([datetime]$_.createdAt).ToUniversalTime() -ge $from -and ([datetime]$_.createdAt).ToUniversalTime() -le $to
        })
        $gatewayStatus = if ($logged.Count -gt 0) { ($logged | ForEach-Object { $_.status }) -join ',' } else { 'no message' }
        $rowSaysSent = @('Sent', 'SentUnconfirmed') -contains $d.Status
        $logSaysSent = @($logged | Where-Object { @('sent', 'delivered', 'read', 'pending') -contains $_.status }).Count -gt 0
        $verdict = if ($rowSaysSent -eq $logSaysSent) { 'AGREE   ' } else { 'DISAGREE' }
        if ($rowSaysSent -eq $logSaysSent) { $agree++ } else { $disagree++ }
        Write-Host ("  {0} #{1}  row {2,-16} OpenWA {3,-20} {4} to {5}" -f `
            $verdict, $d.Id, $d.Status, $gatewayStatus, $d.FileName, (Get-Masked $d.RecipientE164))
    }
    Write-Host ''
    Write-Host ("  {0} agree, {1} disagree or could not be checked." -f $agree, $disagree)
}
