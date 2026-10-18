# Read-only. Run on the production API box (KFL-DNS2) in Windows PowerShell 5.1:
#   powershell -ExecutionPolicy Bypass -File .\diagnose-sale-margin-read.ps1
# Shows why the sales breakdowns' gross profit reads "unavailable": every SAP call the margin read
# made today with its time, and the API log lines around it. Writes nothing anywhere but this
# script's own folder (the .sql file it runs).

param([string]$Day = (Get-Date -Format 'yyyy-MM-dd'))

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

# ── 1. The API log ─────────────────────────────────────────────────────────────────────────────
$stamp = $Day.Replace('-', '')
$patterns = 'invoice cost line', 'margin deadline', 'invoice cost from SAP', 'provision SQL query', 'booked cost of',
            'DSK_INV', 'SalesAnalysisQuery', 'concurrency slot'
foreach ($slot in 'ShopInventory-API-Green', 'ShopInventory-API-Blue') {
    # Any roll of the day's file, not only the first.
    $logs = Get-ChildItem "C:\inetpub\$slot\logs\shopinventory-api-$stamp*.log" -ErrorAction SilentlyContinue
    if (-not $logs) { continue }
    Write-Host "`n=== $slot ($(@($logs).Count) file(s)) ===" -ForegroundColor Cyan
    Select-String -Path $logs.FullName -Pattern $patterns -SimpleMatch |
        ForEach-Object { $_.Line.Substring(0, [Math]::Min(300, $_.Line.Length)) }
}

# ── 2. SAP request timings (SapConnectionLogs; times are UTC) ─────────────────────────────────
$config = 'C:\inetpub\ShopInventory-API-Green\web.config', 'C:\inetpub\ShopInventory-API-Blue\web.config',
          'C:\inetpub\ShopInventory-API\web.config' | Where-Object { Test-Path $_ } | Select-Object -First 1
[xml]$xml = Get-Content $config
$conn = ($xml.SelectNodes('//environmentVariable') |
    Where-Object { $_.name -eq 'ConnectionStrings__DefaultConnection' }).value
if (-not $conn) { throw "No ConnectionStrings__DefaultConnection in $config" }
$parts = @{}
foreach ($pair in $conn.Split(';')) {
    $kv = $pair.Split('=', 2)
    if ($kv.Count -eq 2) { $parts[$kv[0].Trim().ToLowerInvariant().Replace(' ', '')] = $kv[1].Trim() }
}
$user = @($parts['username'], $parts['userid'], $parts['user'], 'postgres') | Where-Object { $_ } | Select-Object -First 1
$database = @($parts['database'], 'ShopInventory') | Where-Object { $_ } | Select-Object -First 1

@"
\echo '--- Every margin SQL query call and every SQLQueries create/repair today ---'
SELECT "CheckedAt", round("ResponseTimeMs") AS ms, "IsSuccess", "Endpoint", left("ErrorMessage", 120) AS error
FROM "SapConnectionLogs"
WHERE "CheckedAt" >= '$Day'
  AND ("Endpoint" LIKE '%DSK_INV%' OR "Endpoint" LIKE 'POST SQLQueries%' OR "Endpoint" LIKE 'PATCH SQLQueries%')
ORDER BY "CheckedAt";

\echo '--- SQLQueries reads today, by query: how slow SAP is answering overall ---'
SELECT split_part("Endpoint", '/', 1) AS query, count(*) AS calls,
       round(avg("ResponseTimeMs")) AS avg_ms, round(max("ResponseTimeMs")) AS max_ms,
       sum(CASE WHEN "IsSuccess" THEN 0 ELSE 1 END) AS failed
FROM "SapConnectionLogs"
WHERE "CheckedAt" >= '$Day' AND "Endpoint" LIKE 'GET SQLQueries(%'
GROUP BY 1 ORDER BY max_ms DESC LIMIT 25;

\echo '--- The stored copy the reports read GP from (absent before the sale-invoice-cost-sync release) ---'
SELECT count(*) AS invoices_copied,
       count(*) FILTER (WHERE NOT EXISTS (SELECT 1 FROM "SaleInvoiceCostLines" l WHERE l."SaleInvoiceCostId" = c."Id")) AS without_lines,
       min(c."DocEntry") AS oldest_doc, max(c."DocEntry") AS newest_doc, max(c."ReadAt") AS last_copied
FROM "SaleInvoiceCosts" c;
SELECT count(*) AS posted_sales_not_copied_yet
FROM "DesktopSales" s
WHERE s."SapDocEntry" IS NOT NULL AND NOT EXISTS (SELECT 1 FROM "SaleInvoiceCosts" c WHERE c."DocEntry" = s."SapDocEntry");
"@ | Set-Content -Path "$here\diagnose-sale-margin-read.sql" -Encoding ASCII

$psql = Get-ChildItem 'C:\Program Files\PostgreSQL\*\bin\psql.exe' | Sort-Object FullName -Descending | Select-Object -First 1
$env:PGPASSWORD = $parts['password']
$env:PGOPTIONS = '-c default_transaction_read_only=on'
try {
    & $psql.FullName -h localhost -p 5432 -U $user -d $database -P pager=off -f "$here\diagnose-sale-margin-read.sql"
}
finally {
    Remove-Item Env:PGPASSWORD, Env:PGOPTIONS -ErrorAction SilentlyContinue
}
