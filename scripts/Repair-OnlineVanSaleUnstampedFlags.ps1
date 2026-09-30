<#
.SYNOPSIS
    Clears the false "Never stamped" flag from online van sales the server fiscalised after the
    REVMax -> platform cut-over.

.DESCRIPTION
    From the cut-over (PR #629) until the fix that ships with this script, every online van sale the
    server fiscalised was written with ReceiptIngestStatus = Unstamped (6). The code assumed that under
    the platform a handset signs for itself, but the platform's live devices are Online, so no van
    holds a device of its own. Each of those sales already has its receipt, yet the /fiscalisation work
    queue listed it as "Never stamped - do not retry" and counted it as not with ZIMRA.

    This sets those rows to NotApplicable (0), which is what the fixed code now writes. It touches a
    row only when all of these hold:
      - SourceSystem = 'KefalosVanSalesOnline'
      - ReceiptIngestStatus = 6 (Unstamped) and FiscalizationStatus = 1 (Success)
      - the capturing user (CreatedBy) holds no FiscalDeviceId, so its handset was never meant to sign
      - CreatedAt is on or after -Since

    Windows PowerShell 5.1, on the API box. It takes the connection string from the API's web.config.
    The default is a dry run: it prints the console's outstanding sales before and after, then rolls
    back. Pass -Apply to commit. The SQL goes to psql with -f, never -c, because 5.1 strips the double
    quotes this schema's PascalCase names need.

.PARAMETER Since
    UTC start of the window. Defaults to the cut-over deploy (run 36657204440).

.EXAMPLE
    .\Repair-OnlineVanSaleUnstampedFlags.ps1
    .\Repair-OnlineVanSaleUnstampedFlags.ps1 -Apply
#>
[CmdletBinding()]
param(
    [switch]$Apply,
    [string]$Since = '2026-09-30 02:07:00+00',
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

# The same set the work queue's "Everything outstanding" filter reads, decoded. Enums are stored as ints.
$outstanding = @'
SELECT "SourceSystem",
       (ARRAY['Pending','Success','Failed','Skipped'])["FiscalizationStatus" + 1] AS fiscal,
       "FiscalizationRequiresReconciliation" AS needs_recon,
       (ARRAY['NotApplicable','Pending','Ingested','Failed','ChainBroken','Unsignable','Unstamped'])
           ["ReceiptIngestStatus" + 1] AS receipt_ingest,
       count(*), min("CreatedAt") AS first_created, max("CreatedAt") AS last_created
FROM "DesktopSales"
WHERE "FiscalizationStatus" IN (0, 2) OR "ReceiptIngestStatus" IN (3, 4, 5, 6)
GROUP BY 1, 2, 3, 4
ORDER BY 1, 2, 3, 4;
'@

$target = @'
FROM "DesktopSales" s
LEFT JOIN "Users" u ON u."Id"::text = lower(s."CreatedBy")
WHERE s."SourceSystem" = 'KefalosVanSalesOnline'
  AND s."ReceiptIngestStatus" = 6
  AND s."FiscalizationStatus" = 1
  AND COALESCE(u."FiscalDeviceId", 0) <= 0
  AND s."CreatedAt" >= timestamptz ':since'
'@

$sql = @"
\set ON_ERROR_STOP on
BEGIN;

\echo '== Outstanding desktop sales on /fiscalisation, before =='
$outstanding

\echo '== Rows to clear: online van sales fiscalised by the server and flagged Unstamped =='
SELECT s."WarehouseCode", count(*), min(s."CreatedAt") AS first_created, max(s."CreatedAt") AS last_created
$target
GROUP BY 1 ORDER BY 1;

\echo '== Left alone: Unstamped online sales outside that rule (older, unfiscalised, or a handset with a device) =='
SELECT (ARRAY['Pending','Success','Failed','Skipped'])[s."FiscalizationStatus" + 1] AS fiscal,
       u."FiscalDeviceId", s."CreatedAt" >= timestamptz ':since' AS since_cutover, count(*)
FROM "DesktopSales" s
LEFT JOIN "Users" u ON u."Id"::text = lower(s."CreatedBy")
WHERE s."SourceSystem" = 'KefalosVanSalesOnline' AND s."ReceiptIngestStatus" = 6
  AND NOT (s."FiscalizationStatus" = 1 AND COALESCE(u."FiscalDeviceId", 0) <= 0
           AND s."CreatedAt" >= timestamptz ':since')
GROUP BY 1, 2, 3 ORDER BY 1, 2, 3;

UPDATE "DesktopSales" SET "ReceiptIngestStatus" = 0
WHERE "Id" IN (SELECT s."Id" $target);

\echo '== Outstanding desktop sales on /fiscalisation, after =='
$outstanding

\if :{?apply}
COMMIT;
\echo '== Committed =='
\else
ROLLBACK;
\echo '== Dry run: rolled back, nothing was changed. Re-run with -Apply to commit. =='
\endif
"@
$sql = $sql.Replace(':since', $Since)

$sqlFile = Join-Path $env:TEMP 'repair-online-van-sale-unstamped-flags.sql'
$sql | Set-Content -Path $sqlFile -Encoding ASCII

Write-Host "Using $WebConfig -> $user@${pgHost}:$port/$db$(if (-not $Apply) { '  (DRY RUN)' })"
$env:PGPASSWORD = $cs['password']
try {
    $psqlArgs = @('-w', '-h', $pgHost, '-p', $port, '-U', $user, '-d', $db, '-P', 'pager=off', '-X', '-f', $sqlFile)
    if ($Apply) { $psqlArgs += @('-v', 'apply=1') }
    & $psql @psqlArgs
    if ($LASTEXITCODE -ne 0) { throw "psql exited $LASTEXITCODE; the transaction was rolled back." }
}
finally {
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
    Remove-Item $sqlFile -ErrorAction SilentlyContinue
}
