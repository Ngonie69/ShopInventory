# Exercises New-SapSqlQueryCleanupSql.ps1 against made-up census CSVs.
#
# The generated SQL deletes rows from SAP's OSQL table in production, so what matters is that it
# can only ever name dead codes: the census's 'legacy random' bucket, re-checked by shape and date,
# never a code the API source names, never one created after the cutoff. Each check below builds a
# census, runs the generator, and reads the .sql it wrote. Nothing here touches SAP or HANA.

param(
    [string]$ScriptPath = (Join-Path $PSScriptRoot 'New-SapSqlQueryCleanupSql.ps1'),
    [string]$RealSourceRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) 'ShopInventory')
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $ScriptPath)) { throw "No generator at '$ScriptPath'." }

$work = Join-Path ([IO.Path]::GetTempPath()) ("sqlq-cleanup-test-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$noSource = Join-Path $work 'no-source-here'

$script:pass = 0
$script:fail = 0
function Check([string]$Name, [scriptblock]$Body) {
    try {
        & $Body
        $script:pass++
        Write-Host "  PASS  $Name" -ForegroundColor Green
    } catch {
        $script:fail++
        Write-Host "  FAIL  $Name : $($_.Exception.Message)" -ForegroundColor Red
    }
}
function Assert([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }

function New-Census([string]$Name, [object[]]$Rows) {
    $path = Join-Path $work $Name
    $Rows | ForEach-Object {
        [pscustomobject]@{ SqlCode = $_[0]; SqlName = "name of $($_[0])"; Kind = $_[1]; Family = 'f'; TextLength = 10; CreateDate = $_[2]; UpdateDate = $_[2] }
    } | Export-Csv -Path $path -NoTypeInformation -Encoding UTF8
    return $path
}

function Invoke-Generator([string]$Csv, [hashtable]$Extra = @{}) {
    $out = [IO.Path]::ChangeExtension($Csv, '.sql')
    $arguments = @{ CensusCsv = $Csv; OutputPath = $out; SourceRoot = $noSource }
    foreach ($key in $Extra.Keys) { $arguments[$key] = $Extra[$key] }
    & $ScriptPath @arguments 6>$null | Out-Null
    return [IO.File]::ReadAllText($out)
}

# The SqlCode literals inside the backup INSERTs' IN ( ... ) lists, in order. Those lists open with a
# line break; the DELETE's and PART 4's IN (SELECT ...) do not.
function Get-ListedCodes([string]$Sql) {
    $codes = @()
    foreach ($block in [regex]::Matches($Sql, '(?s)"SqlCode" IN \(\r?\n(.*?)\r?\n\);')) {
        foreach ($m in [regex]::Matches($block.Groups[1].Value, "'((?:[^']|'')*)'")) { $codes += $m.Groups[1].Value.Replace("''", "'") }
    }
    return , $codes
}

$mixed = New-Census 'sqlqueries-TESTCO-20261111-120000Z.csv' @(
    , @('WHS_BATCHES_KEFBYC_123456', 'legacy random', '2026-07-01')
    , @('STK_ITEMS_3f2a9c1b7d4e', 'legacy random', '2026-07-15')
    , @('POD_SO_ABCDEF123456', 'legacy random', '2026-07-20')
    , @('PKG_STK_12345678', 'legacy random', '2026-06-01')
    , @('WHS_BATCHES_KEFHRE_654321', 'legacy random', '2026-09-01')
    , @('STOCK_QTY_KEFBYC', 'fixed', '2026-05-01')
    , @('PODCRA_RANGE', 'fixed', '2026-09-18')
    , @('STK_ITEMS_ABCDEF012345', 'content-addressed', '2026-07-28')
    , @('SH_PL1_0123456789AB', 'unverified hex', '2026-08-05')
)
$expected = @('WHS_BATCHES_KEFBYC_123456', 'STK_ITEMS_3f2a9c1b7d4e', 'POD_SO_ABCDEF123456', 'PKG_STK_12345678')

Write-Host 'Choosing codes'
$sql = Invoke-Generator $mixed
Check 'takes exactly the old legacy codes, each once' {
    $listed = Get-ListedCodes $sql
    Assert ($listed.Count -eq 4) "listed $($listed.Count): $($listed -join ', ')"
    foreach ($code in $expected) { Assert ($listed -ccontains $code) "missing $code" }
}
Check 'never names a fixed, content-addressed, unverified or recent code anywhere in the file' {
    foreach ($code in 'STOCK_QTY_KEFBYC', 'PODCRA_RANGE', 'STK_ITEMS_ABCDEF012345', 'SH_PL1_0123456789AB', 'WHS_BATCHES_KEFHRE_654321') {
        Assert (-not $sql.Contains($code)) "$code appears in the SQL"
    }
}
Check 'says how many recent legacy codes it left alone' {
    Assert ($sql -match 'Leaves 1 legacy codes created on or after 2026-08-01') 'no left-alone line'
}
Check 'reads the company schema from the file name' {
    Assert ($sql.Contains('"TESTCO"."OSQL"')) 'schema not TESTCO'
    Assert ($sql.Contains('"SQLQ_CLEANUP"."OSQL_20261111_120000"')) 'backup table not named from the census stamp'
}

Write-Host 'The SQL it writes'
Check 'backup check expects the target count' {
    Assert ($sql.Contains("CASE WHEN COUNT(*) = 4 THEN 'OK' ELSE 'STOP: expected 4' END")) 'wrong expected count'
}
Check 'the delete is one statement, bounded by the backup table and the date guard' {
    $deletes = [regex]::Matches($sql, '(?m)^DELETE FROM .*$')
    Assert ($deletes.Count -eq 1) "found $($deletes.Count) DELETE lines"
    $line = $deletes[0].Value
    Assert ($line.Contains('"SqlCode" IN (SELECT "SqlCode" FROM "SQLQ_CLEANUP"."OSQL_20261111_120000")')) 'delete is not limited to the backup table'
    Assert ($line.Contains("""CreateDate"" < '2026-08-01' AND (""UpdateDate"" IS NULL OR ""UpdateDate"" < '2026-08-01')")) 'delete has no date guard'
}
Check 'every backup INSERT carries the date guard too' {
    $inserts = [regex]::Matches($sql, '(?m)^INSERT INTO .*$')
    Assert ($inserts.Count -ge 1) 'no INSERT'
    foreach ($i in $inserts) { Assert ($i.Value.Contains("""CreateDate"" < '2026-08-01'")) 'INSERT without the guard' }
}
Check 'the backup comes before the delete, and nothing deletes from ASQL or drops anything live' {
    Assert ($sql.IndexOf('INSERT INTO "SQLQ_CLEANUP"') -lt $sql.IndexOf('DELETE FROM')) 'delete before backup'
    Assert (-not ($sql -match '(?m)^\s*DELETE FROM "TESTCO"."ASQL"')) 'deletes history'
    Assert (-not ($sql -match '(?m)^\s*DROP ')) 'an uncommented DROP'
    Assert (-not ($sql -match '(?m)^\s*INSERT INTO "TESTCO"')) 'the undo is not commented out'
}
Check 'splits the list into batches' {
    $small = Invoke-Generator $mixed @{ BatchSize = 3 }
    $inserts = [regex]::Matches($small, '(?m)^INSERT INTO ')
    Assert ($inserts.Count -eq 2) "expected 2 INSERTs for 4 codes at 3, got $($inserts.Count)"
    $listed = Get-ListedCodes $small
    Assert ($listed.Count -eq 4) "batched list holds $($listed.Count)"
}
Check '-CreatedBefore moves the cutoff in the guard and the selection' {
    $later = Invoke-Generator $mixed @{ CreatedBefore = [DateTime]'2026-10-01' }
    Assert ((Get-ListedCodes $later) -ccontains 'WHS_BATCHES_KEFHRE_654321') 'recent legacy code not taken'
    Assert ($later.Contains("""CreateDate"" < '2026-10-01'")) 'guard not moved'
}

Write-Host 'Refusals'
function Assert-Refused([string]$CsvName, [object[]]$Rows, [string]$Pattern, [hashtable]$Extra = @{}) {
    $csv = New-Census $CsvName $Rows
    $out = [IO.Path]::ChangeExtension($csv, '.sql')
    $refused = $null
    try { Invoke-Generator $csv $Extra | Out-Null } catch { $refused = $_.Exception.Message }
    Assert ($null -ne $refused) 'it did not refuse'
    Assert ($refused -match $Pattern) "refused for another reason: $refused"
    Assert (-not (Test-Path -LiteralPath $out)) 'it wrote a .sql anyway'
}
Check 'leaves an uppercase-hex code from the day content-addressing shipped alone, and counts it' {
    $csv = New-Census 'sqlqueries-TESTCO-20261111-120001Z.csv' @(
        , @('POD_SO_ABCDEF123456', 'legacy random', '2026-07-27')
        , @('POD_SO_FEDCBA654321', 'legacy random', '2026-07-26')
    )
    $out = Invoke-Generator $csv
    $listed = Get-ListedCodes $out
    Assert ($listed.Count -eq 1 -and $listed[0] -eq 'POD_SO_FEDCBA654321') "listed: $($listed -join ', ')"
    Assert (-not $out.Contains('POD_SO_ABCDEF123456')) 'the 2026-07-27 code is in the SQL'
    Assert ($out -match 'Leaves 1 uppercase-hex codes from 2026-07-27 on alone') 'not counted'
}
Check 'leaves Production Plan codes alone even when the census calls them legacy and they are old' {
    $csv = New-Census 'sqlqueries-TESTCO-20261111-120007Z.csv' @(
        , @('PP49101CB47880', 'legacy random', '2026-07-01')
        , @('PKG_STK_12345678', 'legacy random', '2026-06-01')
    )
    $out = Invoke-Generator $csv @{ CreatedBefore = [DateTime]'2026-12-31' }
    Assert (-not $out.Contains('PP49101CB47880')) 'the Production Plan code is in the SQL'
    Assert ((Get-ListedCodes $out).Count -eq 1) 'the ShopInventory code was not taken'
    Assert ($out -match 'Leaves 1 Production Plan app codes') 'not counted'
}
Check 'refuses a census that calls a fixed-looking code legacy' {
    Assert-Refused 'sqlqueries-TESTCO-20261111-120002Z.csv' @(
        , @('ITEM_BATCHES_V1', 'legacy random', '2026-05-01')
    ) 'do not have a random-suffix shape'
}
Check 'refuses a code with a quote or other unsafe character' {
    Assert-Refused 'sqlqueries-TESTCO-20261111-120003Z.csv' @(
        , @("X'); DROP TABLE OSQL; --_123456", 'legacy random', '2026-05-01')
    ) 'characters outside'
}
Check 'refuses a code the API source names literally' {
    $src = Join-Path $work 'src'
    New-Item -ItemType Directory -Path $src | Out-Null
    Set-Content -Path (Join-Path $src 'Client.cs') -Value 'const string Code = "PKG_STK_12345678";'
    Assert-Refused 'sqlqueries-TESTCO-20261111-120004Z.csv' @(
        , @('PKG_STK_12345678', 'legacy random', '2026-06-01')
    ) 'names 1 of these codes literally' @{ SourceRoot = $src }
}
Check 'refuses a census with no legacy codes' {
    Assert-Refused 'sqlqueries-TESTCO-20261111-120005Z.csv' @(
        , @('STOCK_QTY_KEFBYC', 'fixed', '2026-05-01')
    ) 'No legacy codes'
}
Check 'refuses a file that is not a census' {
    $path = Join-Path $work 'sqlqueries-TESTCO-20261111-120006Z.csv'
    [pscustomobject]@{ Code = 'A_123456'; Kind = 'legacy random' } | Export-Csv -Path $path -NoTypeInformation
    $refused = $null
    try { Invoke-Generator $path | Out-Null } catch { $refused = $_.Exception.Message }
    Assert ($refused -match "no 'SqlCode' column") "got: $refused"
}

Write-Host 'Against the real API source'
if (Test-Path -LiteralPath $RealSourceRoot) {
    Check 'the literal scan runs over the real source and lets the sample codes through' {
        $real = Invoke-Generator $mixed @{ SourceRoot = $RealSourceRoot }
        Assert ($real.Contains("none of them appears as a literal in $RealSourceRoot")) 'source check did not run'
        Assert ((Get-ListedCodes $real).Count -eq 4) 'sample codes not all taken'
    }
} else {
    Write-Host "  SKIP  no source at $RealSourceRoot"
}

Remove-Item -LiteralPath $work -Recurse -Force
Write-Host ''
Write-Host "$($script:pass) passed, $($script:fail) failed"
if ($script:fail) { exit 1 }
