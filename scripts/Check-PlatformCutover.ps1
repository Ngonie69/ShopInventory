<#
.SYNOPSIS
    Read-only check that the REVMax -> platform cut-over (PR #629) is live on this API box.

.DESCRIPTION
    Run on KFL-DNS2 in Windows PowerShell 5.1. It changes nothing:
      1. Prints the Fiscalisation__* and Revmax__* settings from each API slot's web.config.
         Secrets print as "set (N chars)", never their value.
      2. Prints the "Fiscalisation provider:" startup line and fiscal warnings from the newest API log.
      3. Queries Postgres with default_transaction_read_only=on for what has been filed since the
         cut-over, and on which device: 22862 is REVMax, 46668-46670 are the platform.

.PARAMETER Since
    UTC start of the window. Defaults to the deploy cut-over of run 36657204440.
#>
param(
    [string]$Since = '2026-09-30 02:07:00+00',
    [string]$InetpubRoot = 'C:\inetpub'
)

$ErrorActionPreference = 'Stop'

function Write-Section([string]$Title) {
    Write-Host ''
    Write-Host ('=' * 78)
    Write-Host $Title
    Write-Host ('=' * 78)
}

# --- 1. Settings per slot ----------------------------------------------------------------
Write-Section '1. Fiscal settings in each API web.config'
$apiDirs = Get-ChildItem -Path $InetpubRoot -Directory -Filter 'ShopInventory-API*'
$connectionString = $null
foreach ($dir in $apiDirs) {
    $configPath = Join-Path $dir.FullName 'web.config'
    if (-not (Test-Path $configPath)) { continue }
    Write-Host ''
    Write-Host "[$($dir.Name)]  (web.config modified $((Get-Item $configPath).LastWriteTime))"
    [xml]$config = Get-Content -Path $configPath -Raw
    $vars = $config.SelectNodes('//environmentVariable')
    $found = $false
    foreach ($var in $vars) {
        $name = $var.GetAttribute('name')
        $value = $var.GetAttribute('value')
        if ($name -eq 'ConnectionStrings__DefaultConnection' -and -not $connectionString) {
            $connectionString = $value
        }
        if ($name -notmatch '^(Fiscalisation|Revmax)__') { continue }
        $found = $true
        if ($name -match '(ApiKey|Password|Secret)$') {
            $shown = if ([string]::IsNullOrWhiteSpace($value)) { '(EMPTY)' } else { "set ($($value.Length) chars)" }
        } else {
            $shown = $value
        }
        Write-Host ("  {0} = {1}" -f $name, $shown)
    }
    if (-not $found) {
        Write-Host '  (no Fiscalisation__/Revmax__ overrides: appsettings.json applies, i.e. Provider=Platform, ApiKey empty, LastFilingDate 2026-09-30)'
    }
}

# --- 2. Startup log -----------------------------------------------------------------------
Write-Section '2. Startup provider line and fiscal warnings (newest log per slot)'
foreach ($dir in $apiDirs) {
    $logDir = Join-Path $dir.FullName 'logs'
    if (-not (Test-Path $logDir)) { continue }
    $log = Get-ChildItem -Path $logDir -Filter 'shopinventory-api-*.log' |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $log) { continue }
    Write-Host ''
    Write-Host "[$($dir.Name)] $($log.Name)  (last written $($log.LastWriteTime))"
    Select-String -Path $log.FullName -Pattern 'Fiscalisation provider:' |
        Select-Object -Last 3 | ForEach-Object { Write-Host "  $($_.Line)" }
    Select-String -Path $log.FullName -Pattern '\[(WRN|ERR|FTL)\].*(fiscal|revmax|platform)' |
        Select-Object -Last 15 | ForEach-Object {
            $line = $_.Line
            if ($line.Length -gt 300) { $line = $line.Substring(0, 300) + '...' }
            Write-Host "  $line"
        }
}

# --- 3. Database --------------------------------------------------------------------------
Write-Section "3. What has been filed since $Since (read-only)"
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

$sql = @'
\echo '--- Till, vending and van sales created since the cut-over, by device (22862 = REVMax, 4666x = platform)'
SELECT "SourceSystem",
       (ARRAY['Pending','Success','Failed','Skipped'])["FiscalizationStatus" + 1] AS fiscal_status,
       "FiscalDeviceId", count(*), min("CreatedAt"), max("CreatedAt")
FROM "DesktopSales"
WHERE "CreatedAt" >= timestamptz ':since'
GROUP BY 1, 2, 3 ORDER BY 1, 2, 3;

\echo '--- Latest 15 sale fiscal errors since the cut-over'
SELECT "Id", "SourceSystem", "CreatedAt", "FiscalizationAttempts", left("FiscalError", 200) AS error
FROM "DesktopSales"
WHERE "CreatedAt" >= timestamptz ':since' AND "FiscalError" IS NOT NULL AND "FiscalError" <> ''
ORDER BY "CreatedAt" DESC LIMIT 15;

\echo '--- Fiscal transactions (invoices, credit notes, sales) since the cut-over, by device'
SELECT "SourceSystem", "DocumentType", "Status", "DeviceId", count(*), min("TimestampUtc"), max("TimestampUtc")
FROM "DesktopFiscalTransactions"
WHERE "TimestampUtc" >= timestamptz ':since'
GROUP BY 1, 2, 3, 4 ORDER BY 1, 2, 3, 4;

\echo '--- Latest 15 fiscal transaction messages that are not a success'
SELECT "TimestampUtc", "DocumentType", "DocNum", "Status", "DeviceId", left("Message", 200) AS message
FROM "DesktopFiscalTransactions"
WHERE "TimestampUtc" >= timestamptz ':since' AND "Status" NOT ILIKE '%success%' AND "Status" NOT ILIKE 'fiscalised'
ORDER BY "TimestampUtc" DESC LIMIT 15;

\echo '--- Exception Center incidents raised since the cut-over'
SELECT "Category", "Provider", "Status", left("Title", 80) AS title, count(*), max("CreatedAtUtc")
FROM "ExceptionCenterIncidents"
WHERE "CreatedAtUtc" >= timestamptz ':since'
GROUP BY 1, 2, 3, 4 ORDER BY count(*) DESC LIMIT 20;

\echo '--- Runbook step 5: sales still Pending or Failed (any date)'
SELECT "SourceSystem",
       (ARRAY['Pending','Success','Failed','Skipped'])["FiscalizationStatus" + 1] AS fiscal_status,
       count(*), min("CreatedAt"), max("CreatedAt")
FROM "DesktopSales"
WHERE "FiscalizationStatus" IN (0, 2)
GROUP BY 1, 2 ORDER BY 1, 2;
'@
$sql = $sql.Replace(':since', $Since)
$sqlFile = Join-Path $env:TEMP 'check-platform-cutover.sql'
$sql | Set-Content -Path $sqlFile -Encoding ASCII

$env:PGPASSWORD = $parts['password']
$env:PGOPTIONS = '-c default_transaction_read_only=on'
try {
    & $psql -w -h $pgHost -p $pgPort -U $pgUser -d $pgDb -P pager=off -f $sqlFile
} finally {
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
    Remove-Item Env:PGOPTIONS -ErrorAction SilentlyContinue
    Remove-Item $sqlFile -ErrorAction SilentlyContinue
}
