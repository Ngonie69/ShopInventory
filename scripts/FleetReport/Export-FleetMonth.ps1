<#
.SYNOPSIS
  Exports one month of van fleet data (reps, Start/End Day odometers and GPS, shop check-ins,
  sales order positions, fuel issued) as CSV files and zips them. Read-only.

.DESCRIPTION
  This is the van half of the monthly fleet report. The Cartrack half is read straight from the
  Cartrack API. Run this on the API box (KFL-DNS2) in Windows PowerShell 5.1, with
  Export-FleetMonth.sql in the same folder.

  It is read-only twice over. The session opens with default_transaction_read_only, and the SQL
  runs inside BEGIN READ ONLY ... ROLLBACK. The connection string comes from the API's web.config,
  the same way Check-VanDailyReport.ps1 gets it.

.PARAMETER Month
  The month to export, as yyyy-MM. Defaults to last month.

.EXAMPLE
  .\Export-FleetMonth.ps1 -Month 2026-10
  Writes fleet-2026-10.zip next to this script. Send that file back.
#>
[CmdletBinding()]
param(
    [string]$Month,

    [string]$WebConfig
)

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$sql = Join-Path $here 'Export-FleetMonth.sql'
if (-not (Test-Path $sql)) { throw "Export-FleetMonth.sql must sit beside this script ($sql)." }

if (-not $Month) { $Month = (Get-Date).AddMonths(-1).ToString('yyyy-MM') }
$first = [datetime]::ParseExact("$Month-01", 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
$fromDay = $first.ToString('yyyy-MM-dd')
$toDay = $first.AddMonths(1).AddDays(-1).ToString('yyyy-MM-dd')

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

$out = Join-Path $here "fleet-$Month"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Path $out | Out-Null

Write-Host "Using $WebConfig -> $user@${pgHost}:$port/$db (read-only), $fromDay to $toDay"
$env:PGPASSWORD = $cs['password']
$env:PGOPTIONS = '-c default_transaction_read_only=on'
# Without this psql writes the CSVs in the console's code page, and accented names come out as cp1252.
$env:PGCLIENTENCODING = 'UTF8'
Push-Location $out
try {
    & $psql -h $pgHost -p $port -U $user -d $db -X -q -v ON_ERROR_STOP=1 `
        -v "from_day=$fromDay" -v "to_day=$toDay" -f $sql
    if ($LASTEXITCODE -ne 0) { throw "psql exited $LASTEXITCODE." }
}
finally {
    Pop-Location
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
    Remove-Item Env:PGOPTIONS -ErrorAction SilentlyContinue
    Remove-Item Env:PGCLIENTENCODING -ErrorAction SilentlyContinue
}

Get-ChildItem $out -Filter *.csv | ForEach-Object {
    $rows = (Get-Content $_.FullName | Measure-Object -Line).Lines - 1
    Write-Host ("  {0,-26} {1,7} rows" -f $_.Name, $rows)
}

$zip = Join-Path $here "fleet-$Month.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path (Join-Path $out '*.csv') -DestinationPath $zip
Write-Host "Wrote $zip - send this file back."
