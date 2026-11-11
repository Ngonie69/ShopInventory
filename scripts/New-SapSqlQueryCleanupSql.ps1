<#
.SYNOPSIS
    Turns a SQLQueries census CSV into a HANA SQL script that backs up, deletes and verifies the
    leaked query objects. Writes a .sql file; connects to nothing.

.DESCRIPTION
    The Service Layer cannot delete a SQLQueries object (DELETE never finishes, see
    docs/operations/sap-sqlqueries-census.md), so the leaked ones are removed in HANA Studio instead.
    SAP keeps them in the company schema's OSQL table. ASQL beside it is SAP's change history (one row
    per version) and is left alone.

    Input is the CSV that Get-SapSqlQueryCensus.ps1 writes. Only codes the census put in
    'legacy random' are taken, and each one is checked again here rather than trusted:
      - its shape must be a random suffix: 5-8 digits, or 12 hex with a lowercase letter (a GUID).
      - 12 uppercase hex is taken only when created before 2026-07-27, the day content-addressed
        codes shipped (8235dcb). The census calls uppercase hex legacy up to 2026-08-01 whenever the
        stored text does not hash to the code, but SAP rewrites stored text (it quotes table names),
        so real content-addressed codes fail that hash too. Those from 2026-07-27 on are left alone
        and counted.
      - it must have been created before -CreatedBefore.
      - it must not appear as a string literal anywhere in the API's C# source, when the source is
        here to read (it is not on the API server; then this check is skipped and says so).
    A code that the census calls legacy but has no random-suffix shape at all stops the script: the
    two disagree, and that needs a person.

    The generated SQL repeats the date guard in the database, so a code that SAP created or changed
    after the census cannot be deleted even if it somehow ended up on the list. Every row is copied to
    a backup table before the delete, and the delete removes only rows that are in that backup table,
    so nothing can be deleted that was not backed up first.

.EXAMPLE
    .\New-SapSqlQueryCleanupSql.ps1 -CensusCsv .\sap-sqlqueries-census\sqlqueries-KEFALOS_USD_NEW2-20261111-120000Z.csv
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CensusCsv,
    # The company schema. Taken from the census file name (sqlqueries-<CompanyDB>-<stamp>.csv).
    [string]$Schema,
    # No current build mints a legacy code, and an older node might still be mid-request on a new one,
    # so only codes created before this date are taken.
    [DateTime]$CreatedBefore = '2026-08-01',
    [string]$BackupSchema = 'SQLQ_CLEANUP',
    [string]$OutputPath,
    [int]$BatchSize = 500,
    # The API source to search for literal codes. Skipped when the folder does not exist.
    [string]$SourceRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) 'ShopInventory')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

# Content-addressed codes shipped in 8235dcb, merged 2026-07-27 12:28 CAT. Uppercase-hex codes from
# that day or later could be real ones.
$contentAddressedSince = [DateTime]'2026-07-27'

# The Production Plan app (repo production-plan) saves its queries in the same company as 'PP' + 12
# uppercase hex of SHA-256 over the text. Some end in 5-8 digits by chance, which the census reads as
# a random suffix, and several of those are in use. Never ours to delete.
$productionPlanCode = '^PP[0-9A-F]{12}$'

$hexTwelve = New-Object Text.RegularExpressions.Regex '^(?<family>.+)_(?<fp>[0-9A-Fa-f]{12})$'
$legacyDigits = New-Object Text.RegularExpressions.Regex '^(?<family>.+?)_?(?<suffix>\d{5,8})$'
$safeCode = New-Object Text.RegularExpressions.Regex '^[A-Za-z0-9_\-\.]+$'

# 'legacy', 'maybe content-addressed' (uppercase hex from the day the fix shipped on), or 'not random'.
function Get-LegacyShape([string]$code, $created) {
    $m = $hexTwelve.Match($code)
    if ($m.Success) {
        if ($m.Groups['fp'].Value -cmatch '[a-f]') { return 'legacy' }
        if ($null -ne $created -and $created -lt $contentAddressedSince) { return 'legacy' }
        return 'maybe content-addressed'
    }
    if ($legacyDigits.IsMatch($code)) { return 'legacy' }
    return 'not random'
}

function ConvertTo-Day($value) {
    if (-not $value) { return $null }
    $parsed = [DateTime]::MinValue
    $styles = [Globalization.DateTimeStyles]::AssumeLocal
    if ([DateTime]::TryParseExact("$value", 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture, $styles, [ref]$parsed)) { return $parsed }
    return $null
}

if (-not (Test-Path -LiteralPath $CensusCsv)) { throw "No census CSV at '$CensusCsv'." }
$csvItem = Get-Item -LiteralPath $CensusCsv
if (-not $Schema) {
    $m = [regex]::Match($csvItem.Name, '^sqlqueries-(?<db>.+)-\d{8}-\d{6}Z\.csv$')
    if (-not $m.Success) { throw "Cannot read the company from '$($csvItem.Name)'. Pass -Schema." }
    $Schema = $m.Groups['db'].Value
}
foreach ($name in @($Schema, $BackupSchema)) {
    if ($name -notmatch '^[A-Za-z0-9_]+$') { throw "Schema name '$name' has characters this script will not quote." }
}

$rows = @(Import-Csv -LiteralPath $CensusCsv)
if ($rows.Count -eq 0) { throw 'The census CSV is empty.' }
foreach ($column in 'SqlCode', 'Kind', 'CreateDate', 'UpdateDate') {
    if (-not $rows[0].PSObject.Properties[$column]) { throw "The census CSV has no '$column' column. Is it from Get-SapSqlQueryCensus.ps1?" }
}

$seen = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
$targets = New-Object Collections.Generic.List[string]
$tooRecent = 0
$maybeLive = 0
$otherApp = 0
$disagree = @()
$unsafe = @()
foreach ($row in $rows) {
    if ($row.Kind -ne 'legacy random') { continue }
    $code = "$($row.SqlCode)"
    $created = ConvertTo-Day $row.CreateDate
    if (-not $safeCode.IsMatch($code)) { $unsafe += $code; continue }
    if ($code -cmatch $productionPlanCode) { $otherApp++; continue }
    $shape = Get-LegacyShape $code $created
    if ($shape -eq 'not random') { $disagree += $code; continue }
    if ($shape -eq 'maybe content-addressed') { $maybeLive++; continue }
    if ($null -eq $created -or $created -ge $CreatedBefore) { $tooRecent++; continue }
    if (-not $seen.Add($code)) { throw "Code '$code' appears twice in the census." }
    $targets.Add($code)
}

if ($unsafe.Count) {
    throw "The census lists $($unsafe.Count) legacy code(s) with characters outside A-Z, 0-9, _ - . : $($unsafe[0..([math]::Min(9, $unsafe.Count - 1))] -join ', '). Look at them by hand."
}
if ($disagree.Count) {
    throw "The census calls $($disagree.Count) code(s) legacy that do not have a random-suffix shape here: $($disagree[0..([math]::Min(9, $disagree.Count - 1))] -join ', '). Nothing was written."
}
if ($targets.Count -eq 0) { throw 'No legacy codes to remove.' }

# A code the API names literally is in use, whatever its shape.
$sourceNote = "skipped: no source at $SourceRoot"
if (Test-Path -LiteralPath $SourceRoot) {
    $literals = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    Get-ChildItem -LiteralPath $SourceRoot -Recurse -Filter *.cs -File |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
        ForEach-Object {
            foreach ($m in [regex]::Matches([IO.File]::ReadAllText($_.FullName), '"([A-Za-z0-9_\-\.]{4,})"')) { [void]$literals.Add($m.Groups[1].Value) }
        }
    $named = @($targets | Where-Object { $literals.Contains($_) })
    if ($named.Count) { throw "The API source names $($named.Count) of these codes literally: $($named -join ', '). Nothing was written." }
    $sourceNote = "none of them appears as a literal in $SourceRoot"
}

$stamp = [regex]::Match($csvItem.Name, '\d{8}-\d{6}').Value
if (-not $stamp) { $stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') }
$backupTable = '"{0}"."OSQL_{1}"' -f $BackupSchema, $stamp.Replace('-', '_')
$osql = '"{0}"."OSQL"' -f $Schema
$asql = '"{0}"."ASQL"' -f $Schema
$cutoff = $CreatedBefore.ToString('yyyy-MM-dd')
$guard = """CreateDate"" < '$cutoff' AND (""UpdateDate"" IS NULL OR ""UpdateDate"" < '$cutoff')"
$n = $targets.Count

$kinds = ($rows | Group-Object Kind | Sort-Object Count -Descending | ForEach-Object { '{0:N0} {1}' -f $_.Count, $_.Name }) -join ', '

$sql = New-Object Text.StringBuilder
function Add([string]$line = '') { [void]$sql.AppendLine($line) }

Add "-- Remove leaked SAP SQLQueries objects (table OSQL) from $Schema."
Add "-- Generated $([DateTime]::UtcNow.ToString('yyyy-MM-dd HH:mm')) UTC by scripts/New-SapSqlQueryCleanupSql.ps1"
Add "-- from $($csvItem.Name): $($rows.Count.ToString('N0')) codes ($kinds)."
Add "-- Deletes $($n.ToString('N0')) 'legacy random' codes created before $cutoff."
if ($otherApp) { Add "-- Leaves $($otherApp.ToString('N0')) Production Plan app codes (PP + 12 hex) alone: not ShopInventory's." }
if ($maybeLive) { Add "-- Leaves $($maybeLive.ToString('N0')) uppercase-hex codes from $($contentAddressedSince.ToString('yyyy-MM-dd')) on alone: they may be content-addressed." }
if ($tooRecent) { Add "-- Leaves $($tooRecent.ToString('N0')) legacy codes created on or after $cutoff alone." }
Add "-- Literal check against the API source: $sourceNote."
Add '--'
Add '-- Run in HANA Studio, SQL console on tenant KLS, outside trading hours.'
Add '-- Run ONE PART AT A TIME and read its CHECK column before starting the next.'
Add '-- Take a tenant backup before PART 2. ASQL (change history) is not touched.'
Add ''
Add '-- ============================================================================================='
Add '-- PART 1. Read-only. Write down OSQL_ROWS: PART 4 compares against it.'
Add '-- ============================================================================================='
Add "SELECT COUNT(*) AS ""OSQL_ROWS"" FROM $osql;"
Add "SELECT COUNT(*) AS ""ASQL_ROWS"" FROM $asql;"
Add "SELECT TABLE_NAME, RECORD_COUNT, TABLE_SIZE FROM M_TABLES WHERE SCHEMA_NAME = '$Schema' AND TABLE_NAME IN ('OSQL', 'ASQL');"
Add "SELECT TOP 20 ""SqlCode"", COUNT(*) AS ""VERSIONS"" FROM $asql GROUP BY ""SqlCode"" ORDER BY COUNT(*) DESC;"
Add '-- Expect no rows. Anything here is a lock someone holds; find out whose before going on.'
Add 'SELECT * FROM M_BLOCKED_TRANSACTIONS;'
Add ''
Add '-- ============================================================================================='
Add "-- PART 2. Copy the $($n.ToString('N0')) rows to $backupTable. Changes nothing in $Schema."
Add '-- ============================================================================================='
Add "-- If $BackupSchema already exists from an earlier run, skip the CREATE SCHEMA line."
Add "CREATE SCHEMA ""$BackupSchema"";"
Add "CREATE COLUMN TABLE $backupTable LIKE $osql WITH NO DATA;"
for ($i = 0; $i -lt $n; $i += $BatchSize) {
    $chunk = $targets.GetRange($i, [math]::Min($BatchSize, $n - $i))
    Add "INSERT INTO $backupTable SELECT * FROM $osql WHERE $guard AND ""SqlCode"" IN ("
    $lines = for ($j = 0; $j -lt $chunk.Count; $j += 8) {
        '  ' + (($chunk.GetRange($j, [math]::Min(8, $chunk.Count - $j)) | ForEach-Object { "'" + $_.Replace("'", "''") + "'" }) -join ', ')
    }
    Add ($lines -join (",`r`n"))
    Add ');'
}
Add "-- OK means every code on the list exists, is old, and is now in the backup."
Add "-- Fewer means some codes are gone or were changed since ${cutoff}: stop and look at which."
Add "SELECT COUNT(*) AS ""BACKED_UP"", CASE WHEN COUNT(*) = $n THEN 'OK' ELSE 'STOP: expected $n' END AS ""CHECK"" FROM $backupTable;"
Add ''
Add '-- ============================================================================================='
Add '-- PART 3. The delete. One statement, so it commits whole or not at all.'
Add '-- It removes only rows that are in the backup table and still pass the date guard.'
Add '-- If it has not returned within a minute, cancel it and run SELECT * FROM M_BLOCKED_TRANSACTIONS;'
Add '-- ============================================================================================='
Add "DELETE FROM $osql WHERE ""SqlCode"" IN (SELECT ""SqlCode"" FROM $backupTable) AND $guard;"
Add ''
Add '-- ============================================================================================='
Add '-- PART 4. Verify. STILL_THERE must be 0; OSQL_ROWS_NOW must be PART 1''s OSQL_ROWS minus BACKED_UP.'
Add '-- Then rerun Get-SapSqlQueryCensus.ps1 on the API server: legacy random should read 0'
Add '-- (or the number this file says it left alone), and the Service Layer must agree with OSQL_ROWS_NOW.'
Add '-- ============================================================================================='
Add "SELECT COUNT(*) AS ""STILL_THERE"", CASE WHEN COUNT(*) = 0 THEN 'OK' ELSE 'STOP' END AS ""CHECK"" FROM $osql WHERE ""SqlCode"" IN (SELECT ""SqlCode"" FROM $backupTable);"
Add "SELECT COUNT(*) AS ""OSQL_ROWS_NOW"" FROM $osql;"
Add ''
Add '-- ============================================================================================='
Add '-- UNDO. Puts back every deleted row that has not been recreated since. Keep the backup table'
Add '-- until the census rerun looks right, then drop it.'
Add '-- ============================================================================================='
Add "-- INSERT INTO $osql SELECT * FROM $backupTable WHERE ""SqlCode"" NOT IN (SELECT ""SqlCode"" FROM $osql);"
Add "-- DROP TABLE $backupTable;"

if (-not $OutputPath) { $OutputPath = Join-Path $csvItem.DirectoryName ("cleanup-{0}-{1}.sql" -f $Schema, $stamp) }
[IO.File]::WriteAllText($OutputPath, $sql.ToString(), (New-Object Text.UTF8Encoding($false)))

Write-Host "Company schema : $Schema"
Write-Host "Census         : $($csvItem.Name) ($($rows.Count.ToString('N0')) codes: $kinds)"
Write-Host "To delete      : $($n.ToString('N0')) legacy codes created before $cutoff"
if ($otherApp) { Write-Host "Left alone     : $($otherApp.ToString('N0')) Production Plan app codes" }
if ($maybeLive) { Write-Host "Left alone     : $($maybeLive.ToString('N0')) uppercase-hex codes from $($contentAddressedSince.ToString('yyyy-MM-dd')) on (may be content-addressed)" }
if ($tooRecent) { Write-Host "Left alone     : $($tooRecent.ToString('N0')) legacy codes created on or after $cutoff" }
Write-Host "Source check   : $sourceNote"
Write-Host "Backup table   : $backupTable"
Write-Host "SQL            : $OutputPath"
