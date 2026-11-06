<#
.SYNOPSIS
    Read-only summary of how long customer statements take to build, and which SAP read the time
    goes to.

.DESCRIPTION
    Run on the API box in Windows PowerShell 5.1. It changes nothing: it only reads the API and Web
    log files under each IIS slot.

    The statement handler logs one line per SAP read ("Statement leg <leg> for <card> finished in
    Nms") and one per finished build. This script turns those into:
      1. Per leg: count, median, 90th percentile and slowest.
      2. Whole builds: how many started and finished, and how long the finished ones took, split
         by how many linked accounts the customer has. A build that never finished has no time of
         its own, so its SAP reads (section 1 and 7) are the only record of where it stuck.
      3. Cache hits, joined builds, failures and builds that ran out of time.
      4. Portal requests that gave up at the Web's 300-second limit.
      5. SAP slot waits of 2 seconds or more (any caller, not only statements).
      6. The slowest finished builds, with the account and line counts.
      7. Every SAP read slower than -SlowReadSeconds, with its time, so stuck reads can be lined
         up against deploys, restarts and the portal timeouts.

    Card codes are printed in sections 6 and 7. Do not paste those into a public PR or issue.

.PARAMETER Days
    How many days of logs to read, counting today. Logs are kept for 31 days.

.PARAMETER InetpubRoot
    Where the ShopInventory-API* and ShopInventory-Web* site folders live.

.PARAMETER Top
    How many of the slowest builds to list.

.PARAMETER SlowReadSeconds
    A SAP read at least this slow is listed in section 7.
#>
param(
    [int]$Days = 14,
    [string]$InetpubRoot = 'C:\inetpub',
    [int]$Top = 15,
    [int]$SlowReadSeconds = 20
)

$ErrorActionPreference = 'Stop'

function Write-Section([string]$Title) {
    Write-Host ''
    Write-Host ('=' * 78)
    Write-Host $Title
    Write-Host ('=' * 78)
}

function Get-Percentile([double[]]$Sorted, [double]$P) {
    if ($Sorted.Count -eq 0) { return 0 }
    $index = [Math]::Ceiling($P * $Sorted.Count) - 1
    if ($index -lt 0) { $index = 0 }
    return $Sorted[[int]$index]
}

function Format-Seconds([double]$Ms) {
    return ('{0,8:N1}s' -f ($Ms / 1000))
}

function Write-TimingRow([string]$Label, [double[]]$Values) {
    $sorted = [double[]]($Values | Sort-Object)
    Write-Host ('{0,-28} {1,6}  median {2}  p90 {3}  max {4}' -f `
        $Label, $sorted.Count, `
        (Format-Seconds (Get-Percentile $sorted 0.5)), `
        (Format-Seconds (Get-Percentile $sorted 0.9)), `
        (Format-Seconds (Get-Percentile $sorted 1.0)))
}

function Get-LogFiles([string]$SiteFilter, [string]$NameFilter) {
    $since = (Get-Date).Date.AddDays(1 - $Days)
    $dirs = Get-ChildItem -Path $InetpubRoot -Directory -Filter $SiteFilter -ErrorAction SilentlyContinue
    foreach ($dir in $dirs) {
        $logDir = Join-Path $dir.FullName 'logs'
        if (-not (Test-Path $logDir)) { continue }
        Get-ChildItem -Path $logDir -Filter $NameFilter |
            Where-Object { $_.LastWriteTime -ge $since }
    }
}

$apiLogs = @(Get-LogFiles 'ShopInventory-API*' 'shopinventory-api-*.log')
$webLogs = @(Get-LogFiles 'ShopInventory-Web*' 'shopinventory-web-*.log')

Write-Host "Reading $($apiLogs.Count) API log file(s) and $($webLogs.Count) Web log file(s) from the last $Days day(s)."
if ($apiLogs.Count -eq 0) { throw "No API logs found under $InetpubRoot\ShopInventory-API*\logs." }

$legPattern = 'Statement leg (?<leg>.+?) for (?<card>.+?) finished in (?<ms>\d+)ms'
$buildPattern = 'Customer statement for (?<card>.+?) built in (?<ms>\d+)ms across (?<accounts>\d+) account\(s\) with (?<lines>\d+) line\(s\)'
# Cache keys embed the card codes, which can contain spaces ("ABC001 FCA"), so these match any
# run of characters rather than one word.
$cacheHitPattern = 'Serving statement statement:.+? from cache'
$joinPattern = 'Joining the statement build already running for'
$failedPattern = 'Statement statement:.+? failed after (?<ms>\d+)ms'
# The build's 10-minute ceiling. Before the November 2026 read budgets this also fired when one SAP
# read hit the 5-minute SAP client timeout, so in older logs it means either.
$budgetPattern = 'Customer statement for .+? exceeded its \d+-minute budget'
# A build that throws (rather than returning an error) is logged here and nowhere else.
$threwPattern = 'Statement build statement:.+? threw'
$agingFailedPattern = 'Aging could not be built for'
$slotPattern = 'SAP request waited (?<ms>\d+)ms for a concurrency slot'
$webTimeoutPattern = 'Statement request for (?<card>.+?) exceeded the \d+s API budget'
# Since the November 2026 read budgets: each statement SQL read gets 30 s and one retry.
$retryPattern = 'Statement leg (?<leg>.+?) for (?<card>.+?) got no answer from SAP within (?<s>\d+)s \(attempt \d+ of \d+\); retrying'
$gaveUpPattern = 'Statement leg (?<leg>.+?) for (?<card>.+?) got no answer from SAP in \d+ attempt\(s\) of \d+s; giving up'
$sapNotAnsweringPattern = 'Customer statement for (?<card>.+?) gave up: '

$legs = @{}
$builds = New-Object System.Collections.Generic.List[object]
$cacheHits = 0; $joins = 0; $failed = 0; $overBudget = 0; $threw = 0; $agingFailed = 0
$retries = 0; $gaveUp = 0; $sapNotAnswering = 0
$slotWaits = New-Object System.Collections.Generic.List[double]
# Section 7: slow SAP reads and portal give-ups on one timeline.
$timeline = New-Object System.Collections.Generic.List[object]
$firstSeen = $null; $lastSeen = $null

foreach ($log in $apiLogs) {
    # Select-String reads a file the API still has open for writing; Get-Content would too, but
    # Select-String streams it rather than loading a whole day into memory.
    $hits = Select-String -Path $log.FullName -Pattern @(
        $legPattern, $buildPattern, $cacheHitPattern, $joinPattern,
        $failedPattern, $budgetPattern, $threwPattern, $agingFailedPattern, $slotPattern,
        $retryPattern, $gaveUpPattern, $sapNotAnsweringPattern)

    foreach ($hit in $hits) {
        $line = $hit.Line
        $timestamp = if ($line.Length -ge 19) { $line.Substring(0, 19) } else { '' }

        $m = [regex]::Match($line, $legPattern)
        if ($m.Success) {
            $leg = $m.Groups['leg'].Value
            $ms = [double]$m.Groups['ms'].Value
            if (-not $legs.ContainsKey($leg)) { $legs[$leg] = New-Object System.Collections.Generic.List[double] }
            $legs[$leg].Add($ms)
            if ($null -eq $firstSeen -or $timestamp -lt $firstSeen) { $firstSeen = $timestamp }
            if ($null -eq $lastSeen -or $timestamp -gt $lastSeen) { $lastSeen = $timestamp }
            if ($ms -ge $SlowReadSeconds * 1000) {
                $timeline.Add([pscustomobject]@{
                    At   = $timestamp
                    What = ('{0} {1}' -f (Format-Seconds $ms), $leg)
                    Card = $m.Groups['card'].Value
                })
            }
            continue
        }

        $m = [regex]::Match($line, $buildPattern)
        if ($m.Success) {
            $builds.Add([pscustomobject]@{
                At       = $timestamp
                Card     = $m.Groups['card'].Value
                Ms       = [double]$m.Groups['ms'].Value
                Accounts = [int]$m.Groups['accounts'].Value
                Lines    = [int]$m.Groups['lines'].Value
            })
            continue
        }

        if ($line -match $cacheHitPattern) { $cacheHits++; continue }
        if ($line -match $joinPattern) { $joins++; continue }
        if ($line -match $failedPattern) { $failed++; continue }
        if ($line -match $budgetPattern) { $overBudget++; continue }
        if ($line -match $threwPattern) { $threw++; continue }
        if ($line -match $agingFailedPattern) { $agingFailed++; continue }
        if ($line -match $sapNotAnsweringPattern) { $sapNotAnswering++; continue }

        foreach ($pair in @(@($retryPattern, 'retrying'), @($gaveUpPattern, 'gave up'))) {
            $m = [regex]::Match($line, $pair[0])
            if ($m.Success) {
                if ($pair[1] -eq 'retrying') { $retries++ } else { $gaveUp++ }
                $timeline.Add([pscustomobject]@{
                    At   = $timestamp
                    What = ('   {0} {1}' -f $m.Groups['leg'].Value, $pair[1])
                    Card = $m.Groups['card'].Value
                })
                break
            }
        }
        if ($m.Success) { continue }

        $m = [regex]::Match($line, $slotPattern)
        if ($m.Success) { $slotWaits.Add([double]$m.Groups['ms'].Value) }
    }
}

if ($null -ne $firstSeen) {
    Write-Host "Statement activity found from $firstSeen to $lastSeen."
}

Write-Section '1. Time per SAP read (leg)'
if ($legs.Count -eq 0) {
    Write-Host '  No "Statement leg" lines found.'
} else {
    foreach ($leg in ($legs.Keys | Sort-Object)) {
        Write-TimingRow "  $leg" $legs[$leg].ToArray()
    }
    Write-Host ''
    Write-Host '  Legs overlap: opening balance, ledger rows, open items and payment terms run'
    Write-Host '  together, two SAP reads at a time. The slowest leg sets the build time.'
}

Write-Section '2. Whole builds'
# Every build that finds the customer starts with the business partner read, so its count is the
# number of builds that started. The two numbers differing is the headline, not a detail.
$started = if ($legs.ContainsKey('business partner')) { $legs['business partner'].Count } else { 0 }
Write-Host ('  started {0}, finished {1}, never finished {2}' -f $started, $builds.Count, ($started - $builds.Count))
Write-Host '  The times below are for finished builds only.'
Write-Host ''
if ($builds.Count -eq 0) {
    Write-Host '  No finished builds found.'
} else {
    Write-TimingRow '  finished builds' ($builds | ForEach-Object { $_.Ms })
    foreach ($group in ($builds | Group-Object Accounts | Sort-Object { [int]$_.Name })) {
        Write-TimingRow ("  {0} account(s)" -f $group.Name) ($group.Group | ForEach-Object { $_.Ms })
    }
    $lineCounts = [double[]]($builds | ForEach-Object { $_.Lines } | Sort-Object)
    Write-Host ('  ledger lines per statement: median {0}, p90 {1}, max {2}' -f `
        (Get-Percentile $lineCounts 0.5), (Get-Percentile $lineCounts 0.9), (Get-Percentile $lineCounts 1.0))
}

Write-Section '3. Cache and failures (API)'
Write-Host ('  served from the 5-minute cache : {0}' -f $cacheHits)
Write-Host ('  joined a build already running : {0}' -f $joins)
Write-Host ('  builds that returned an error  : {0}' -f $failed)
Write-Host ('  builds that threw             : {0}' -f $threw)
Write-Host ('  builds that timed out         : {0}  (the 10-minute ceiling; in older logs also a 5-minute SAP read)' -f $overBudget)
Write-Host ('  statements shown without aging: {0}' -f $agingFailed)
Write-Host ''
Write-Host '  Since the November 2026 read budgets (30 s a read, one retry):'
Write-Host ('  reads retried after no answer : {0}' -f $retries)
Write-Host ('  reads given up after retrying : {0}' -f $gaveUp)
Write-Host ('  statements refused as "SAP did not answer": {0}' -f $sapNotAnswering)
Write-Host ''
Write-Host '  A retry that worked shows as "retrying" with no "gave up" for the same read. Before the'
Write-Host '  budgets, a timed-out build was often not logged at all: when the portal gave up at the'
Write-Host '  same moment, the API treated it as the caller leaving. Compare section 2 instead.'

Write-Section '4. Portal requests that gave up at 300 seconds (Web)'
if ($webLogs.Count -eq 0) {
    Write-Host '  No Web logs found on this box.'
} else {
    $webTimeouts = @(Select-String -Path ($webLogs | ForEach-Object { $_.FullName }) -Pattern $webTimeoutPattern)
    Write-Host ('  {0} request(s)' -f $webTimeouts.Count)
    foreach ($hit in $webTimeouts) {
        $timeline.Add([pscustomobject]@{
            At   = $hit.Line.Substring(0, [Math]::Min(19, $hit.Line.Length))
            What = '   portal gave up'
            Card = ([regex]::Match($hit.Line, $webTimeoutPattern)).Groups['card'].Value
        })
    }
}

Write-Section '5. SAP slot waits of 2s or more (all callers)'
if ($slotWaits.Count -eq 0) {
    Write-Host '  None.'
} else {
    Write-TimingRow '  slot waits' $slotWaits.ToArray()
}

Write-Section ("6. Slowest {0} finished builds (contains card codes: keep private)" -f $Top)
$builds | Sort-Object Ms -Descending | Select-Object -First $Top | ForEach-Object {
    Write-Host ('  {0}  {1}  {2,2} account(s)  {3,5} line(s)  {4}' -f `
        $_.At, (Format-Seconds $_.Ms), $_.Accounts, $_.Lines, $_.Card)
}

Write-Section ("7. SAP reads of {0}s or more, and portal give-ups, in time order (card codes: keep private)" -f $SlowReadSeconds)
Write-Host '  A read is logged when it ends, so a 300s read started 5 minutes before its time here.'
Write-Host ''
if ($timeline.Count -eq 0) {
    Write-Host '  None.'
} else {
    $timeline | Sort-Object At | ForEach-Object {
        Write-Host ('  {0}  {1,-28}  {2}' -f $_.At, $_.What, $_.Card)
    }
}
