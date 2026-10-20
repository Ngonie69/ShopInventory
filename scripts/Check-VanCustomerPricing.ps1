<#
.SYNOPSIS
  Why a van handset shows "Price Missing" for a route customer. Read-only.

.DESCRIPTION
  A van sale to a web-app (route) customer is not priced for the shop. The handset is handed the rep's
  van account (RouteCustomers.AssignedBusinessPartnerCode, the same code as Users.AssignedBusinessPartnerCode)
  as bp_number, and the API prices the warehouse page from that account's BusinessPartnerPriceProfiles
  row: its PriceListNum, looked up in ItemPrices. This prints each link of that chain for the customers
  matching -CustomerName, so a zero can be placed on the server (a link missing here) or on the handset
  (every link present: the handset withheld the prices, which it does offline or after a failed read).

  Read-only: the session is opened with default_transaction_read_only. Takes the connection string from
  the API's web.config, like Check-VanDailyReport.ps1. Windows PowerShell 5.1; the SQL goes to psql
  with -f, never -c.

.PARAMETER CustomerName
  Part of the route customer's name, matched case-insensitively.

.PARAMETER ItemCodes
  The items the handset showed unpriced. Defaults to the 2026-10-20 Athiens order; SAP prices all of
  them above zero on van lists 45 and 48.

.EXAMPLE
  .\Check-VanCustomerPricing.ps1 -CustomerName Athiens | Tee-Object van-customer-pricing.txt
#>
[CmdletBinding()]
param(
    [string]$CustomerName = 'Athiens',

    [string[]]$ItemCodes = @(
        'ICC025', 'ICC026', 'ICC027', 'ICS008', 'ICS011', 'ICS017', 'ICS018', 'ICT001', 'ICT003', 'ICT007',
        'ICV003', 'ICV014', 'MUE001', 'YOG017', 'YOG021', 'YOG023', 'YOG024', 'YOG025', 'YOG027', 'YOG053',
        'YOG056', 'YOG057', 'YOG058', 'YOG061', 'YOG063', 'YOG101', 'YOG102', 'YOG137', 'YOG153', 'YOG154',
        'YOG155'),

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

$pattern = '%' + $CustomerName.Replace("'", "''") + '%'
$codes = ($ItemCodes | ForEach-Object { "'" + $_.Replace("'", "''") + "'" }) -join ','

# The accounts the matching customers are priced against, used by every section below.
$accounts = "SELECT rc.""AssignedBusinessPartnerCode"" FROM ""RouteCustomers"" rc WHERE rc.""Name"" ILIKE '$pattern'"

$sql = @"
\echo '== 1. Route customers matching the name, and the van account each is priced against'
SELECT rc."Code", rc."Name", rc."IsActive", rc."AssignedBusinessPartnerCode" AS "PricedAgainst"
FROM "RouteCustomers" rc
WHERE rc."Name" ILIKE '$pattern'
ORDER BY rc."Name";

\echo '== 2. Reps on those accounts'
SELECT u."Username", u."IsActive", u."AssignedBusinessPartnerCode"
FROM "Users" u
WHERE u."AssignedBusinessPartnerCode" IN ($accounts)
ORDER BY u."Username";

\echo '== 3. The accounts'' price profiles. No row: the API has no list to price them from'
SELECT p."CardCode", p."PriceListNum", p."IsActive", p."SyncedFromSAP", p."LastSyncedAt"
FROM "BusinessPartnerPriceProfiles" p
WHERE p."CardCode" IN ($accounts);

\echo '== 4. The reported items on those lists. UsablePrices below the item count is a server-side zero'
SELECT ip."PriceList",
       COUNT(*) AS "Rows",
       COUNT(*) FILTER (WHERE ip."Price" > 0 AND ip."IsActive" AND ip."SyncedFromSAP") AS "UsablePrices",
       MIN(ip."LastSyncedAt") AS "OldestSync",
       MAX(ip."LastSyncedAt") AS "NewestSync"
FROM "ItemPrices" ip
WHERE ip."ItemCode" IN ($codes)
  AND ip."PriceList" IN (
      SELECT p."PriceListNum" FROM "BusinessPartnerPriceProfiles" p WHERE p."CardCode" IN ($accounts))
GROUP BY ip."PriceList";

\echo '== 5. Which of the reported items have no usable price on those lists'
SELECT p."CardCode", p."PriceListNum", c."ItemCode"
FROM "BusinessPartnerPriceProfiles" p
CROSS JOIN unnest(ARRAY[$codes]) AS c("ItemCode")
WHERE p."CardCode" IN ($accounts)
  AND NOT EXISTS (
      SELECT 1 FROM "ItemPrices" ip
      WHERE ip."PriceList" = p."PriceListNum" AND ip."ItemCode" = c."ItemCode"
        AND ip."Price" > 0 AND ip."IsActive" AND ip."SyncedFromSAP")
ORDER BY p."CardCode", c."ItemCode";

\echo '== 6. Size and age of the van lists as a whole (SAP prices the 31 items on both 45 and 48)'
SELECT ip."PriceList", COUNT(*) AS "Items", MAX(ip."LastSyncedAt") AS "LastSynced"
FROM "ItemPrices" ip
WHERE ip."PriceList" IN (45, 48)
GROUP BY ip."PriceList";

\echo '== 7. When the account''s profile was first stored. Every sync restamps LastSyncedAt; CreatedAt is the'
\echo '      first sync that found it, so a CreatedAt after the report means the API had no profile then'
SELECT p."CardCode", p."PriceListNum", p."CreatedAt", p."UpdatedAt"
FROM "BusinessPartnerPriceProfiles" p
WHERE p."CardCode" IN ($accounts);

\echo '== 8. When the reported items were first stored on the account''s list, to the minute'
SELECT ip."PriceList", date_trunc('minute', ip."CreatedAt") AS "FirstStored", COUNT(*) AS "Items"
FROM "ItemPrices" ip
WHERE ip."ItemCode" IN ($codes)
  AND ip."PriceList" IN (
      SELECT p."PriceListNum" FROM "BusinessPartnerPriceProfiles" p WHERE p."CardCode" IN ($accounts))
GROUP BY 1, 2
ORDER BY 2;

\echo '== 9. The whole van lists by first-stored time (a block created at the last sync was missing before it)'
SELECT ip."PriceList", date_trunc('minute', ip."CreatedAt") AS "FirstStored", COUNT(*) AS "Items"
FROM "ItemPrices" ip
WHERE ip."PriceList" IN (45, 48)
GROUP BY 1, 2
ORDER BY 1, 2 DESC
LIMIT 20;

\echo '== 10. Cache syncs in the last two days, as the Data Sync tiles read them (only the latest of each is kept)'
SELECT "CacheKey", "LastSyncedAt", "ItemCount", "LastErrorAt", left("LastError", 120) AS "LastError"
FROM "CacheSyncStates"
WHERE "LastSyncedAt" >= now() - interval '2 days' OR "LastErrorAt" >= now() - interval '2 days'
ORDER BY coalesce("LastSyncedAt", "LastErrorAt") DESC;
"@

$sqlFile = Join-Path $env:TEMP 'check-van-customer-pricing.sql'
# ASCII, not UTF8: PowerShell 5.1 writes a BOM, which psql reads as part of the first statement.
$sql | Set-Content -Path $sqlFile -Encoding ASCII

Write-Host "Using $WebConfig -> $user@${pgHost}:$port/$db  (read-only), customer like '$CustomerName'"
if ($cs['password']) { $env:PGPASSWORD = $cs['password'] }
$env:PGOPTIONS = '-c default_transaction_read_only=on'
try {
    # -w: fail rather than sit at a password prompt nobody can answer.
    & $psql -h $pgHost -p $port -U $user -d $db -w -P pager=off -X -v ON_ERROR_STOP=1 -f $sqlFile
    if ($LASTEXITCODE -ne 0) { throw "psql exited $LASTEXITCODE." }
}
finally {
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
    Remove-Item Env:PGOPTIONS -ErrorAction SilentlyContinue
    Remove-Item $sqlFile -ErrorAction SilentlyContinue
}
