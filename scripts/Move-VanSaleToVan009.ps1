<#
.SYNOPSIS
  Runs move-van-sale-to-van009.sql against the production database on the API box.

.DESCRIPTION
  Takes the connection string from the API's web.config (ConnectionStrings__DefaultConnection), the
  same place the API reads it, so nothing has to be typed or pasted. Run it first with -DryRun: it
  prints the rows before and after and rolls back. Without -DryRun it commits, and still rolls back
  on its own unless each of the three updates hits exactly one row.

  Windows PowerShell 5.1. The SQL goes to psql with -f, never -c: 5.1 strips the double quotes this
  schema's PascalCase names need.

.EXAMPLE
  .\Move-VanSaleToVan009.ps1 -DryRun
  .\Move-VanSaleToVan009.ps1
#>
[CmdletBinding()]
param(
    [switch]$DryRun,
    [string]$WebConfig
)

$ErrorActionPreference = 'Stop'
$sql = Join-Path $PSScriptRoot 'move-van-sale-to-van009.sql'
if (-not (Test-Path $sql)) { throw "move-van-sale-to-van009.sql must sit beside this script ($sql)." }

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

Write-Host "Using $WebConfig -> $user@${pgHost}:$port/$db$(if ($DryRun) { '  (DRY RUN)' })"
$env:PGPASSWORD = $cs['password']
try {
    $psqlArgs = @('-h', $pgHost, '-p', $port, '-U', $user, '-d', $db, '-P', 'pager=off', '-X', '-f', $sql)
    if ($DryRun) { $psqlArgs += @('-v', 'dryrun=1') }
    & $psql @psqlArgs
    if ($LASTEXITCODE -ne 0) { throw "psql exited $LASTEXITCODE; the transaction was rolled back." }
}
finally {
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
}
