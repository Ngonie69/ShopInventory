<#
.SYNOPSIS
  Exports the shops already on the four UPC vans, so the UPC workbooks can be matched to them. Read-only.

.DESCRIPTION
  Step 1 of loading the office's UPC route workbooks. Writes three CSV files next to where it is run:

    upc-reps.csv       every user on a UPC route or a UPC van account, with the account they sell on
    upc-customers.csv  every route customer (active or removed) on those accounts, with its calling days
    upc-summary.txt    counts per account

  Send the three files back; scripts/UpcCustomerImport/build_apply.py matches the workbooks against
  them and writes the reviewed apply script (step 2).

  Read-only: the session is opened with default_transaction_read_only. Takes the connection string from
  the API's web.config, like Check-VanCustomerPricing.ps1. Windows PowerShell 5.1; the SQL goes to psql
  with -f, never -c. Needs psql 12 or later (--csv).

.EXAMPLE
  .\Export-UpcRouteCustomers.ps1
#>
[CmdletBinding()]
param(
    [string[]]$Accounts = @('VAN014', 'VAN015', 'VAN016', 'VAN017'),
    [string[]]$RouteCodes = @('UPC1', 'UPC2', 'UPC3', 'UPC4'),
    [string]$OutDir = (Get-Location).Path,
    [string]$WebConfig
)

$ErrorActionPreference = 'Stop'

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

$accountList = ($Accounts | ForEach-Object { "'" + $_.Replace("'", "''").ToUpperInvariant() + "'" }) -join ','
$routeList = ($RouteCodes | ForEach-Object { "'" + $_.Replace("'", "''").ToUpperInvariant() + "'" }) -join ','

# The UPC accounts as named, plus any account a rep on a UPC route sells on, in case the pairing moved.
$accountsSql = @"
SELECT upper(btrim(a)) FROM unnest(ARRAY[$accountList]) AS a
UNION
SELECT upper(btrim(u."AssignedBusinessPartnerCode"))
FROM "Users" u JOIN "Routes" r ON r."Id" = u."RouteId"
WHERE upper(r."Code") IN ($routeList) AND coalesce(u."AssignedBusinessPartnerCode", '') <> ''
"@

$queries = [ordered]@{
    'upc-reps.csv' = @"
SELECT u."Username", u."FirstName", u."LastName", u."Role", u."IsActive",
       upper(btrim(u."AssignedBusinessPartnerCode")) AS "Account", r."Code" AS "RouteCode", r."Name" AS "RouteName"
FROM "Users" u
LEFT JOIN "Routes" r ON r."Id" = u."RouteId"
WHERE upper(btrim(coalesce(u."AssignedBusinessPartnerCode", ''))) IN ($accountsSql)
   OR upper(coalesce(r."Code", '')) IN ($routeList)
ORDER BY 6, 1;
"@
    'upc-customers.csv' = @"
SELECT rc."Id", upper(btrim(rc."AssignedBusinessPartnerCode")) AS "Account", rc."Code", rc."Name", rc."Surname",
       rc."Phone", rc."Address", rc."IsActive", rc."CreatedAt", rc."UpdatedAt",
       (SELECT string_agg(v."DayOfWeek"::text, '+' ORDER BY v."DayOfWeek")
          FROM "RouteCustomerVisitDays" v WHERE v."RouteCustomerId" = rc."Id") AS "VisitDays"
FROM "RouteCustomers" rc
WHERE upper(btrim(rc."AssignedBusinessPartnerCode")) IN ($accountsSql)
ORDER BY 2, 4;
"@
    'upc-summary.txt' = @"
SELECT upper(btrim(rc."AssignedBusinessPartnerCode")) AS "Account",
       count(*) AS "Customers",
       count(*) FILTER (WHERE rc."IsActive") AS "Active",
       count(*) FILTER (WHERE coalesce(rc."Phone", '') <> '') AS "WithPhone",
       count(DISTINCT v."RouteCustomerId") AS "WithCallingDays"
FROM "RouteCustomers" rc
LEFT JOIN "RouteCustomerVisitDays" v ON v."RouteCustomerId" = rc."Id"
WHERE upper(btrim(rc."AssignedBusinessPartnerCode")) IN ($accountsSql)
GROUP BY 1
ORDER BY 1;
"@
}

Write-Host "Using $WebConfig -> $user@${pgHost}:$port/$db  (read-only)"
if ($cs['password']) { $env:PGPASSWORD = $cs['password'] }
$env:PGOPTIONS = '-c default_transaction_read_only=on'
$sqlFile = Join-Path $env:TEMP 'export-upc-route-customers.sql'
try {
    foreach ($name in $queries.Keys) {
        # ASCII, not UTF8: PowerShell 5.1 writes a BOM, which psql reads as part of the first statement.
        $queries[$name] | Set-Content -Path $sqlFile -Encoding ASCII
        $target = Join-Path $OutDir $name
        # @(...) around the whole if: a one-element array returned from an if is unwrapped to a string,
        # and splatting a string hands psql its characters one by one.
        $format = @(if ($name -like '*.csv') { '--csv' } else { '-P', 'pager=off' })
        # -w: fail rather than sit at a password prompt nobody can answer. -o: psql writes the file
        # itself, as UTF-8, rather than PowerShell redirection, which would write UTF-16.
        & $psql -h $pgHost -p $port -U $user -d $db -w -X -v ON_ERROR_STOP=1 @format -f $sqlFile -o $target
        if ($LASTEXITCODE -ne 0) { throw "psql exited $LASTEXITCODE on $name." }
        Write-Host "Wrote $target"
    }
    Get-Content (Join-Path $OutDir 'upc-summary.txt')
}
finally {
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
    Remove-Item Env:PGOPTIONS -ErrorAction SilentlyContinue
    Remove-Item $sqlFile -ErrorAction SilentlyContinue
}
