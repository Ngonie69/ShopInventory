<#
.SYNOPSIS
  Loads the UPC route workbooks into the four UPC vans' customer lists. Dry run unless -Apply.

.DESCRIPTION
  Step 3 of loading the office's UPC route workbooks. Runs upc-apply.sql, written by
  scripts/UpcCustomerImport/build_apply.py from the workbooks and the step-1 export, in one
  transaction:

    - shops already on a van: phone and address filled where blank (never overwritten), reactivated
      if removed
    - shops not yet on a van: created on the van account (VAN014-VAN017) with the app's code
    - every calling day on the four vans replaced by the workbook's

  Without -Apply it does all of that, prints the result, and rolls back. It refuses to run at all if
  a matched customer has moved or been deleted since the export, or if a shop it would create is
  already on its van (the file was applied before, or a rep added the shop since the export): export
  again and rebuild.

  Rows go in directly, not through the API, so the handsets get no "customer added" notification;
  they see the new shops on their next customer refresh.

  Takes the connection string from the API's web.config, like Export-UpcRouteCustomers.ps1.
  Windows PowerShell 5.1; the SQL goes to psql with -f.

.EXAMPLE
  .\Apply-UpcRouteCustomers.ps1                 # dry run
  .\Apply-UpcRouteCustomers.ps1 -Apply          # keep it
#>
[CmdletBinding()]
param(
    [string]$SqlFile = (Join-Path (Get-Location).Path 'upc-apply.sql'),
    [switch]$Apply,
    [string]$WebConfig
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $SqlFile)) { throw "$SqlFile not found. Copy upc-apply.sql next to this script or pass -SqlFile." }

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

$mode = if ($Apply) { 'APPLY' } else { 'DRY RUN' }
Write-Host "$mode  $SqlFile -> $user@${pgHost}:$port/$db"
if ($cs['password']) { $env:PGPASSWORD = $cs['password'] }
try {
    $applyValue = if ($Apply) { 'true' } else { 'false' }
    & $psql -h $pgHost -p $port -U $user -d $db -w -X -P pager=off -v ON_ERROR_STOP=1 -v "apply=$applyValue" -f $SqlFile
    if ($LASTEXITCODE -ne 0) { throw "psql exited $LASTEXITCODE; the transaction was rolled back." }
}
finally {
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
}
