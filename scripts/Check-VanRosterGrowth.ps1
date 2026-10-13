<#
.SYNOPSIS
  Answers two questions the van daily report reconciliation left open: what the surge of new route
  customers is, and where one truck's account sales are invoiced. Read-only.

.DESCRIPTION
  Runs Check-VanRosterGrowth.sql, which explains what it measures and how to read each section.

  Read-only twice over: the session is opened with default_transaction_read_only, and the SQL runs
  inside BEGIN READ ONLY ... ROLLBACK. Takes the connection string from the API's web.config, like
  Check-VanDailyReport.ps1. Windows PowerShell 5.1; the SQL goes to psql with -f, never -c.

.PARAMETER Since
  The first day of the surge, yyyy-MM-dd. 2026-10-01 by default.

.PARAMETER Days
  How many days from Since the account-sales half looks at. 7 by default.

.PARAMETER Account
  The van account whose account sales are looked for. VAN018 (Bulawayo Local) by default.

.PARAMETER Amounts
  The sheet's account-customer amounts to look for, comma separated.

.EXAMPLE
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Check-VanRosterGrowth.ps1 | Tee-Object van-roster-growth.txt
#>
[CmdletBinding()]
param(
    [ValidatePattern("^\d{4}-\d{2}-\d{2}$")]
    [string]$Since = '2026-10-01',

    [ValidateRange(1, 62)]
    [int]$Days = 7,

    [ValidatePattern("^[A-Za-z0-9_]+$")]
    [string]$Account = 'VAN018',

    [ValidatePattern("^[0-9.,]+$")]
    [string]$Amounts = '388,1724,3297.39,435',

    [string]$WebConfig
)

$ErrorActionPreference = 'Stop'
$sql = Join-Path $PSScriptRoot 'Check-VanRosterGrowth.sql'
if (-not (Test-Path $sql)) { throw "Check-VanRosterGrowth.sql must sit beside this script ($sql)." }

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

Write-Host "Using $WebConfig -> $user@${pgHost}:$port/$db  (read-only), since $Since, $Days days, account $Account"
$env:PGPASSWORD = $cs['password']
$env:PGOPTIONS = '-c default_transaction_read_only=on'
try {
    & $psql -h $pgHost -p $port -U $user -d $db -P pager=off -X -v ON_ERROR_STOP=1 `
        -v "since=$Since" -v "days=$Days" -v "account=$Account" -v "amounts=$Amounts" -f $sql
    if ($LASTEXITCODE -ne 0) { throw "psql exited $LASTEXITCODE." }
}
finally {
    Remove-Item Env:PGPASSWORD, Env:PGOPTIONS -ErrorAction SilentlyContinue
}
