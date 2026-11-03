<#
.SYNOPSIS
  Exports two months of Shop & Vending sales as CSV files and zips them. Read-only.

.DESCRIPTION
  Runs Export-CounterSalesMonths.sql, which writes five CSVs: shops, sales by tender, item lines,
  credits and trading days. Counters only (shop tills and vending), never the vans.

  Read-only twice over: the session is opened with default_transaction_read_only, and the SQL runs
  inside BEGIN READ ONLY ... ROLLBACK. Takes the connection string from the API's web.config, like
  Check-VanRosterGrowth.ps1. Windows PowerShell 5.1; the SQL goes to psql with -f, never -c.

  Writes counter-sales-<from>-<to>.zip beside this script. Bring that one file back.

.PARAMETER From
  First day, yyyy-MM-dd. 2026-09-01 by default.

.PARAMETER To
  Last day (inclusive), yyyy-MM-dd. 2026-10-31 by default.

.EXAMPLE
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Export-CounterSalesMonths.ps1
#>
[CmdletBinding()]
param(
    [ValidatePattern("^\d{4}-\d{2}-\d{2}$")]
    [string]$From = '2026-09-01',

    [ValidatePattern("^\d{4}-\d{2}-\d{2}$")]
    [string]$To = '2026-10-31',

    [string]$WebConfig
)

$ErrorActionPreference = 'Stop'
$sql = Join-Path $PSScriptRoot 'Export-CounterSalesMonths.sql'
if (-not (Test-Path $sql)) { throw "Export-CounterSalesMonths.sql must sit beside this script ($sql)." }

$toExclusive = ([datetime]::ParseExact($To, 'yyyy-MM-dd', $null)).AddDays(1).ToString('yyyy-MM-dd')

if (-not $WebConfig) {
    $WebConfig = @(
        'C:\inetpub\ShopInventory-API\web.config',
        'C:\inetpub\ShopInventory-API-Blue\web.config',
        'C:\inetpub\ShopInventory-API-Green\web.config'
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $WebConfig) { throw 'No API web.config found. Pass -WebConfig <path>.' }

$node = ([xml](Get-Content -Raw $WebConfig)).SelectSingleNode(
    "//environmentVariable[@name='ConnectionStrings__DefaultConnection']")
if (-not $node) { throw "$WebConfig has no ConnectionStrings__DefaultConnection." }

$cs = @{}
foreach ($part in $node.value.Split(';')) {
    $kv = $part.Split('=', 2)
    if ($kv.Count -eq 2) { $cs[$kv[0].Trim().ToLowerInvariant()] = $kv[1].Trim() }
}
$pgHost = if ($cs['host']) { $cs['host'] } else { $cs['server'] }
$port = if ($cs['port']) { $cs['port'] } else { '5432' }
$db = $cs['database']
$user = if ($cs['username']) { $cs['username'] } elseif ($cs['user id']) { $cs['user id'] } else { $cs['user'] }

$psql = (Get-Command psql -ErrorAction SilentlyContinue).Source
if (-not $psql) {
    $psql = Get-ChildItem 'C:\Program Files\PostgreSQL\*\bin\psql.exe' -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $psql) { throw 'psql.exe not found.' }

$name = "counter-sales-$From-$To"
$out = Join-Path $PSScriptRoot $name
New-Item -ItemType Directory -Force -Path $out | Out-Null

Write-Host "Using $WebConfig -> $user@${pgHost}:$port/$db  (read-only), $From to $To"
$env:PGPASSWORD = $cs['password']
$env:PGOPTIONS = '-c default_transaction_read_only=on'
$files = foreach ($f in 'shops', 'sales_by_tender', 'lines_by_item', 'credits', 'trading_days') {
    '-v'
    "f_$f=" + (Join-Path $out "$f.csv").Replace('\', '/')
}
try {
    & $psql -h $pgHost -p $port -U $user -d $db -P pager=off -X -q -v ON_ERROR_STOP=1 `
        -v "from=$From" -v "to_excl=$toExclusive" @files -f $sql
    if ($LASTEXITCODE -ne 0) { throw "psql exited $LASTEXITCODE." }
}
finally {
    Remove-Item Env:PGPASSWORD, Env:PGOPTIONS -ErrorAction SilentlyContinue
}

Get-ChildItem $out -Filter *.csv | ForEach-Object {
    $rows = (Get-Content $_.FullName | Measure-Object -Line).Lines - 1
    Write-Host ("  {0,-22} {1,7} rows" -f $_.Name, $rows)
}

$zip = Join-Path $PSScriptRoot "$name.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $out '*.csv') -DestinationPath $zip
Write-Host "Done: $zip"
