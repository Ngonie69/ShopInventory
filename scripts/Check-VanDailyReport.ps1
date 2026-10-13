<#
.SYNOPSIS
  Sets the hand-kept VANSALES DAILY REPORT workbook beside what the system recorded for the same
  trucks on the same days. Read-only.

.DESCRIPTION
  Runs van-daily-report-sheet.sql (the workbook's rows) and then Check-VanDailyReport.sql, which
  explains what it measures and how to read each section, in one psql session.

  The sheet file is written on a machine that has the workbook:
    python -I scripts/VanDailyReport/export_sheet.py "VANSALES DAILY REPORT OCTOBER.xlsx"
  Copy it here with this script and Check-VanDailyReport.sql.

  Read-only twice over: the session is opened with default_transaction_read_only, and the SQL runs
  inside BEGIN READ ONLY ... ROLLBACK. Takes the connection string from the API's web.config, like
  Check-VanStockRollForward.ps1. Windows PowerShell 5.1; the SQL goes to psql with -f, never -c.

.PARAMETER Sheet
  The file export_sheet.py wrote. van-daily-report-sheet.sql beside this script by default.

.EXAMPLE
  .\Check-VanDailyReport.ps1 | Tee-Object van-daily-report.txt

.EXAMPLE
  .\Check-VanDailyReport.ps1 -Sheet .\van-daily-report-sheet-november.sql
#>
[CmdletBinding()]
param(
    [string]$Sheet,

    [string]$WebConfig
)

$ErrorActionPreference = 'Stop'
$sql = Join-Path $PSScriptRoot 'Check-VanDailyReport.sql'
if (-not (Test-Path $sql)) { throw "Check-VanDailyReport.sql must sit beside this script ($sql)." }

if (-not $Sheet) { $Sheet = Join-Path $PSScriptRoot 'van-daily-report-sheet.sql' }
if (-not (Test-Path $Sheet)) {
    throw "No sheet file at $Sheet. Write one with scripts/VanDailyReport/export_sheet.py and copy it here."
}
$Sheet = (Resolve-Path $Sheet).Path

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

Write-Host "Using $WebConfig -> $user@${pgHost}:$port/$db  (read-only), sheet $Sheet"
$env:PGPASSWORD = $cs['password']
$env:PGOPTIONS = '-c default_transaction_read_only=on'
try {
    # Two -f in one session: the first parks the sheet in a psql variable, the second reads it.
    & $psql -h $pgHost -p $port -U $user -d $db -P pager=off -X -v ON_ERROR_STOP=1 -f $Sheet -f $sql
    if ($LASTEXITCODE -ne 0) { throw "psql exited $LASTEXITCODE." }
}
finally {
    Remove-Item Env:PGPASSWORD, Env:PGOPTIONS -ErrorAction SilentlyContinue
}
