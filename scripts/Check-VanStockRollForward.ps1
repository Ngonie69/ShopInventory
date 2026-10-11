<#
.SYNOPSIS
  Measures how far a van's stock, rolled forward from yesterday's morning read, was from what SAP held
  at the next morning's read. Read-only.

.DESCRIPTION
  Runs Check-VanStockRollForward.sql, which explains what it measures and how to read each section.
  It answers one question: is yesterday's position, less the sales since, safe to check a van sale
  against when SAP gives no figure and the 07:00 read has not happened or did not finish?

  Read-only twice over: the session is opened with default_transaction_read_only, and the SQL runs
  inside BEGIN READ ONLY ... ROLLBACK. Takes the connection string from the API's web.config, like
  Find-StaleRepostedLinks.ps1. Windows PowerShell 5.1; the SQL goes to psql with -f, never -c.

.PARAMETER Days
  How many mornings back to judge, counting today. 31 by default.

.PARAMETER VanLike
  Which warehouses are vans, as a LIKE pattern. 'VAN%' by default.

.EXAMPLE
  .\Check-VanStockRollForward.ps1 | Tee-Object van-stock-roll-forward.txt

.EXAMPLE
  .\Check-VanStockRollForward.ps1 -Days 14 -VanLike 'VAN004'
#>
[CmdletBinding()]
param(
    [ValidateRange(2, 366)]
    [int]$Days = 31,

    [ValidatePattern("^[A-Za-z0-9_%]+$")]
    [string]$VanLike = 'VAN%',

    [string]$WebConfig
)

$ErrorActionPreference = 'Stop'
$sql = Join-Path $PSScriptRoot 'Check-VanStockRollForward.sql'
if (-not (Test-Path $sql)) { throw "Check-VanStockRollForward.sql must sit beside this script ($sql)." }

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

Write-Host "Using $WebConfig -> $user@${pgHost}:$port/$db  (read-only), $Days mornings, vans like $VanLike"
$env:PGPASSWORD = $cs['password']
$env:PGOPTIONS = '-c default_transaction_read_only=on'
try {
    & $psql -h $pgHost -p $port -U $user -d $db -P pager=off -X -v ON_ERROR_STOP=1 `
        -v "days=$Days" -v "van_like=$VanLike" -f $sql
    if ($LASTEXITCODE -ne 0) { throw "psql exited $LASTEXITCODE." }
}
finally {
    Remove-Item Env:PGPASSWORD, Env:PGOPTIONS -ErrorAction SilentlyContinue
}
