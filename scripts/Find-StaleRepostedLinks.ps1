<#
.SYNOPSIS
  Lists local rows that still hold the pre-rollback numbers of an invoice reposted after the
  September 2026 SAP update. Read-only.

.DESCRIPTION
  Runs find-stale-reposted-links.sql, which scripts/stale_reposted_links.py generates from the reposts
  SAP holds. Those old DocEntries and DocNums have since been reissued to other documents, so a row
  that still holds one names someone else's invoice.

  Read-only twice over: the session is opened with default_transaction_read_only, and the SQL runs
  inside BEGIN READ ONLY ... ROLLBACK. Takes the connection string from the API's web.config, like
  Move-VanSaleToVan009.ps1. Windows PowerShell 5.1; the SQL goes to psql with -f, never -c.

.EXAMPLE
  .\Find-StaleRepostedLinks.ps1 | Tee-Object stale-reposted-links.txt
#>
[CmdletBinding()]
param(
    [string]$WebConfig
)

$ErrorActionPreference = 'Stop'
$sql = Join-Path $PSScriptRoot 'find-stale-reposted-links.sql'
if (-not (Test-Path $sql)) { throw "find-stale-reposted-links.sql must sit beside this script ($sql)." }

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

Write-Host "Using $WebConfig -> $user@${pgHost}:$port/$db  (read-only)"
$env:PGPASSWORD = $cs['password']
$env:PGOPTIONS = '-c default_transaction_read_only=on'
try {
    & $psql -h $pgHost -p $port -U $user -d $db -P pager=off -X -v ON_ERROR_STOP=1 -f $sql
    if ($LASTEXITCODE -ne 0) { throw "psql exited $LASTEXITCODE." }
}
finally {
    Remove-Item Env:PGPASSWORD, Env:PGOPTIONS -ErrorAction SilentlyContinue
}
