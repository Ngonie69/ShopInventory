<#
.SYNOPSIS
  Points local rows of reposted sales at the invoices SAP holds today. Run -DryRun first.

.DESCRIPTION
  Runs repair-stale-reposted-links.sql, which scripts/repair_stale_reposted_links.py generates from
  SAP. With -DryRun it prints exactly what it would change and rolls back. Without it, it commits only
  if, afterwards, no sale row of a reposted sale still holds the repost's old DocNum; otherwise it
  raises and nothing is committed.

  Run Find-StaleRepostedLinks.ps1 first and keep its output: it is the record of the old values.

  Takes the connection string from the API's web.config, like Move-VanSaleToVan009.ps1. Windows
  PowerShell 5.1; the SQL goes to psql with -f, never -c.

.EXAMPLE
  .\Repair-StaleRepostedLinks.ps1 -DryRun | Tee-Object repair-dry-run.txt
  .\Repair-StaleRepostedLinks.ps1 | Tee-Object repair.txt
#>
[CmdletBinding()]
param(
    [switch]$DryRun,
    [string]$WebConfig
)

$ErrorActionPreference = 'Stop'
$sql = Join-Path $PSScriptRoot 'repair-stale-reposted-links.sql'
if (-not (Test-Path $sql)) { throw "repair-stale-reposted-links.sql must sit beside this script ($sql)." }

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
