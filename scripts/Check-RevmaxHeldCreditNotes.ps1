<#
.SYNOPSIS
    Read-only: why credit notes against REVMax invoices are held back with "REVMax could not be asked".

.DESCRIPTION
    Run on the production API box in Windows PowerShell 5.1. It changes nothing:
      1. Runs Check-RevmaxHeldCreditNotes.sql (kept beside this script) against Postgres with
         default_transaction_read_only=on, using the connection string from the API web.config.
      2. Prints the credit-note fiscal lines from each API slot's logs of the last 7 days: REVMax
         hold-backs, credits filed on REVMax, and every warning or error about fiscalising a credit note.

    The end of each error says which cause it is:
      "...not being asked again until HH:mm:ss UTC"  another REVMax call timed out moments before
      "...HttpClient.Timeout..." / "No connection..." REVMax was slow or unreachable at that moment
      "REVMax did not say whether it holds N (Code"   REVMax answered in words the code does not expect

.EXAMPLE
    .\Check-RevmaxHeldCreditNotes.ps1
#>
param(
    [string]$InetpubRoot = 'C:\inetpub',
    [int]$LogLines = 40,
    [int]$LogDays = 7,
    # Another read-only query to run in place of the default one, also kept beside this script.
    [string]$SqlFile = 'Check-RevmaxHeldCreditNotes.sql'
)

$ErrorActionPreference = 'Stop'

function Write-Section([string]$Title) {
    Write-Host ''
    Write-Host ('=' * 78)
    Write-Host $Title
    Write-Host ('=' * 78)
}

$sqlFile = Join-Path $PSScriptRoot $SqlFile
if (-not (Test-Path $sqlFile)) { throw "Put $SqlFile in the same folder as this script ($PSScriptRoot)." }

# --- Connection string from the API web.config ----------------------------------------------
$apiDirs = Get-ChildItem -Path $InetpubRoot -Directory -Filter 'ShopInventory-API*'
$connectionString = $null
foreach ($dir in $apiDirs) {
    $configPath = Join-Path $dir.FullName 'web.config'
    if (-not (Test-Path $configPath)) { continue }
    [xml]$config = Get-Content -Path $configPath -Raw
    foreach ($var in $config.SelectNodes('//environmentVariable')) {
        if ($var.GetAttribute('name') -eq 'ConnectionStrings__DefaultConnection') {
            $connectionString = $var.GetAttribute('value')
            break
        }
    }
    if ($connectionString) { break }
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

# --- 1. Database ----------------------------------------------------------------------------
Write-Section '1. Held-back credit notes in the database (read-only)'
$env:PGPASSWORD = $parts['password']
$env:PGOPTIONS = '-c default_transaction_read_only=on'
try {
    & $psql -w -h $pgHost -p $pgPort -U $pgUser -d $pgDb -P pager=off -P format=wrapped -P columns=200 -f $sqlFile
} finally {
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
    Remove-Item Env:PGOPTIONS -ErrorAction SilentlyContinue
}

# --- 2. API logs ----------------------------------------------------------------------------
Write-Section "2. Credit-note fiscal lines in each API slot's logs, last $LogDays days"
# The REVMax hold-backs, plus every warning or error that names a credit note's fiscalisation.
$pattern = 'REVMax could not be asked|did not answer a request moments ago|REVMax did not say whether|REVMax returned no answer|held back because REVMax|Crediting .* on REVMax|(WRN|ERR).*(fiscalis|fiscaliz).*credit note|(WRN|ERR).*credit note.*(fiscalis|fiscaliz)'
foreach ($dir in $apiDirs) {
    $logDir = Join-Path $dir.FullName 'logs'
    if (-not (Test-Path $logDir)) { continue }
    $logs = Get-ChildItem -Path $logDir -Filter 'shopinventory-api-*.log' |
        Where-Object { $_.LastWriteTime -ge (Get-Date).AddDays(-$LogDays) } |
        Sort-Object LastWriteTime
    if (-not $logs) { continue }
    Write-Host ''
    Write-Host "[$($dir.Name)] $(@($logs).Count) log file(s), newest $($logs[-1].Name) (last written $($logs[-1].LastWriteTime))"
    $hits = Select-String -Path ($logs | ForEach-Object FullName) -Pattern $pattern | Select-Object -Last $LogLines
    if (-not $hits) { Write-Host '  (none)'; continue }
    foreach ($hit in $hits) {
        $line = $hit.Line
        if ($line.Length -gt 400) { $line = $line.Substring(0, 400) + '...' }
        Write-Host "  $line"
    }
}
