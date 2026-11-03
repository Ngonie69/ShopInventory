# Exercises the decision Watch-StrandedIisApps.ps1 makes before it recycles a production app pool.
#
# A watchdog that recycles too eagerly is worse than none: it would cut a deploy's warm-up short, bounce
# a pool someone stopped on purpose, or loop on a site that is fine. So most cases here are ones it must
# leave alone. The stranded cases replay the events from the 3 November 2026 outage verbatim.
#
# The two functions are lifted out of the real script by AST, as Test-DeploySlotSeeding.ps1 does.
# Nothing here touches IIS, the event log or production.

param(
    [string]$ScriptPath
)

$ErrorActionPreference = 'Stop'

if (-not $ScriptPath) { $ScriptPath = Join-Path $PSScriptRoot 'Watch-StrandedIisApps.ps1' }

$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$null, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) {
    $parseErrors | ForEach-Object { Write-Host "  line $($_.Extent.StartLineNumber): $($_.Message)" -ForegroundColor Yellow }
    throw 'Watch-StrandedIisApps.ps1 does not parse.'
}

foreach ($name in 'ConvertFrom-AncmStopEvent', 'Get-StrandedAppDecision') {
    $definition = $ast.Find({
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
        }, $false)
    if (-not $definition) { throw "Could not extract $name from $ScriptPath - it was renamed or removed." }
    . ([scriptblock]::Create($definition.Extent.Text))
}

$script:pass = 0
$script:fail = 0

function Check {
    param([string]$Name, [scriptblock]$Body)
    try {
        & $Body
        $script:pass++
        Write-Host "  PASS  $Name" -ForegroundColor Green
    }
    catch {
        $script:fail++
        Write-Host "  FAIL  $Name - $($_.Exception.Message)" -ForegroundColor Red
    }
}

function Assert-Equal($Expected, $Actual, [string]$What) {
    if ($Expected -ne $Actual) { throw "$What : expected '$Expected', got '$Actual'" }
}

# The API's stop on 3 November 2026, as the event log recorded it.
$apiStopMessage = "Application 'MACHINE/WEBROOT/APPHOST/SHOPINVENTORY-API-GREEN' has shutdown.`r`nProcess Id: 19072.`r`nFile Version: 20.0.26296.14. Description: IIS ASP.NET Core Module V2 Request Handler. Commit: "
$apiStopAt = [datetime]'2026-11-03 10:36:30'

function New-Site([hashtable]$Override) {
    $s = @{ Name = 'ShopInventory-API-Green'; State = 'Started'; PoolName = 'ShopInventoryAPI-Green'; PoolState = 'Started'; HasAppOffline = $false; DeployInProgress = $false }
    if ($Override) { foreach ($k in $Override.Keys) { $s[$k] = $Override[$k] } }
    [pscustomobject]$s
}

$stuckWorker = [pscustomobject]@{ ProcessId = 19072; StartTime = [datetime]'2026-11-03 07:14:27' }

function Decide([hashtable]$With) {
    $p = @{
        StopEvent      = (ConvertFrom-AncmStopEvent -Id 1033 -TimeCreated $apiStopAt -Message $apiStopMessage)
        Site           = (New-Site)
        Workers        = @($stuckWorker)
        Now            = $apiStopAt.AddMinutes(2)
        LastRecycledAt = $null
        ProbeStatus    = 503
    }
    if ($With) { foreach ($k in $With.Keys) { $p[$k] = $With[$k] } }
    Get-StrandedAppDecision @p
}

Write-Host 'Reading the module''s events'

Check 'a 1033 names the site and the worker' {
    $e = ConvertFrom-AncmStopEvent -Id 1033 -TimeCreated $apiStopAt -Message $apiStopMessage
    Assert-Equal 'SHOPINVENTORY-API-GREEN' $e.SiteName 'site'
    Assert-Equal 19072 $e.ProcessId 'process id'
}

Check 'a 1005 (failed graceful shutdown) is read the same way' {
    $m = "Failed to gracefully shutdown application 'MACHINE/WEBROOT/APPHOST/SHOPINVENTORY-WEB-GREEN'.`r`nProcess Id: 13356.`r`nFile Version: 20.0.26296.14."
    $e = ConvertFrom-AncmStopEvent -Id 1005 -TimeCreated ([datetime]'2026-11-03 10:28:59') -Message $m
    Assert-Equal 'SHOPINVENTORY-WEB-GREEN' $e.SiteName 'site'
    Assert-Equal 13356 $e.ProcessId 'process id'
}

Check 'a 1005 without a process id still names the site' {
    $e = ConvertFrom-AncmStopEvent -Id 1005 -TimeCreated $apiStopAt -Message "Failed to gracefully shutdown application 'MACHINE/WEBROOT/APPHOST/WORKSHOP'."
    Assert-Equal 'WORKSHOP' $e.SiteName 'site'
    Assert-Equal $null $e.ProcessId 'process id'
}

Check 'a start event (1032) is not a stop' {
    $e = ConvertFrom-AncmStopEvent -Id 1032 -TimeCreated $apiStopAt -Message "Application 'C:\inetpub\ShopInventory-Web-Green\' started successfully.`r`nProcess Id: 22016."
    Assert-Equal $null $e 'result'
}

Write-Host 'Stranded apps it must recycle'

Check 'the API on 3 November: worker alive, newest, site answers 503' {
    Assert-Equal 'Recycle' (Decide).Action 'action'
}

Check 'the five apps at 08:59, replayed: TransferEventListener' {
    $m = "Application 'MACHINE/WEBROOT/APPHOST/TRANSFEREVENTLISTENER' has shutdown.`r`nProcess Id: 18240.`r`nFile Version: 20.0.26296.14."
    $at = [datetime]'2026-11-03 08:59:25'
    $d = Get-StrandedAppDecision -StopEvent (ConvertFrom-AncmStopEvent -Id 1033 -TimeCreated $at -Message $m) `
        -Site (New-Site @{ Name = 'TransferEventListener'; PoolName = 'TransferEventListener' }) `
        -Workers @([pscustomobject]@{ ProcessId = 18240; StartTime = [datetime]'2026-11-02 19:40:00' }) `
        -Now $at.AddMinutes(5) -LastRecycledAt $null -ProbeStatus 503
    Assert-Equal 'Recycle' $d.Action 'action'
}

Check 'an https-only site (no probe answer) is recycled on the worker evidence' {
    Assert-Equal 'Recycle' (Decide @{ ProbeStatus = $null }).Action 'action'
}

Check 'the cooldown has passed' {
    Assert-Equal 'Recycle' (Decide @{ LastRecycledAt = $apiStopAt.AddMinutes(-30) }).Action 'action'
}

Write-Host 'Cases it must leave alone'

Check 'too soon after the shutdown' {
    Assert-Equal 'Skip' (Decide @{ Now = $apiStopAt.AddSeconds(30) }).Action 'action'
}

Check 'a normal recycle: the old worker has exited' {
    Assert-Equal 'Skip' (Decide @{ Workers = @([pscustomobject]@{ ProcessId = 5988; StartTime = $apiStopAt.AddSeconds(1) }) }).Action 'action'
}

Check 'an overlapping recycle: old worker alive, a newer one is serving' {
    $newer = [pscustomobject]@{ ProcessId = 5988; StartTime = $apiStopAt.AddSeconds(-2) }
    $d = Decide @{ Workers = @($stuckWorker, $newer); StopEvent = (ConvertFrom-AncmStopEvent -Id 1033 -TimeCreated $apiStopAt.AddSeconds(-5) -Message $apiStopMessage) }
    Assert-Equal 'Skip' $d.Action 'action'
}

Check 'a deploy stopped the target slot''s pool' {
    Assert-Equal 'Skip' (Decide @{ Site = (New-Site @{ PoolState = 'Stopped' }) }).Action 'action'
}

Check 'a pool that is still stopping' {
    Assert-Equal 'Skip' (Decide @{ Site = (New-Site @{ PoolState = 'Stopping' }) }).Action 'action'
}

Check 'a site someone stopped on purpose' {
    Assert-Equal 'Skip' (Decide @{ Site = (New-Site @{ State = 'Stopped' }) }).Action 'action'
}

Check 'app_offline.htm is in place' {
    Assert-Equal 'Skip' (Decide @{ Site = (New-Site @{ HasAppOffline = $true }) }).Action 'action'
}

Check 'a ShopInventory deploy is extracting files' {
    Assert-Equal 'Skip' (Decide @{ Site = (New-Site @{ DeployInProgress = $true }) }).Action 'action'
}

Check 'the site answers 200 after all' {
    Assert-Equal 'Skip' (Decide @{ ProbeStatus = 200 }).Action 'action'
}

Check 'the site answers 302 (a login redirect)' {
    Assert-Equal 'Skip' (Decide @{ ProbeStatus = 302 }).Action 'action'
}

Check 'recycled it five minutes ago' {
    Assert-Equal 'Skip' (Decide @{ LastRecycledAt = $apiStopAt.AddMinutes(-3) }).Action 'action'
}

Check 'the event names a site IIS does not have' {
    Assert-Equal 'Skip' (Decide @{ Site = $null }).Action 'action'
}

Check 'no process id and no workers left' {
    $e = ConvertFrom-AncmStopEvent -Id 1005 -TimeCreated $apiStopAt -Message "Failed to gracefully shutdown application 'MACHINE/WEBROOT/APPHOST/SHOPINVENTORY-API-GREEN'."
    Assert-Equal 'Skip' (Decide @{ StopEvent = $e; Workers = @() }).Action 'action'
}

Check 'no process id, only a worker from before the shutdown: still skip' {
    $e = ConvertFrom-AncmStopEvent -Id 1005 -TimeCreated $apiStopAt -Message "Failed to gracefully shutdown application 'MACHINE/WEBROOT/APPHOST/SHOPINVENTORY-API-GREEN'."
    Assert-Equal 'Skip' (Decide @{ StopEvent = $e }).Action 'action'
}

Check 'StockSheets at 10:57: the new worker started 4 s before the old one logged its shutdown' {
    $m = "Application 'MACHINE/WEBROOT/APPHOST/STOCKSHEETS' has shutdown.`r`nProcess Id: 9812.`r`nFile Version: 20.0.26296.14."
    $at = [datetime]'2026-11-03 10:57:09'
    $d = Get-StrandedAppDecision -StopEvent (ConvertFrom-AncmStopEvent -Id 1033 -TimeCreated $at -Message $m) `
        -Site (New-Site @{ Name = 'StockSheets'; PoolName = 'StockSheets' }) `
        -Workers @([pscustomobject]@{ ProcessId = 11036; StartTime = [datetime]'2026-11-03 10:57:05' }) `
        -Now $at.AddMinutes(3) -LastRecycledAt $null -ProbeStatus $null
    Assert-Equal 'Skip' $d.Action 'action'
}

Write-Host ''
Write-Host "$script:pass passed, $script:fail failed"
if ($script:fail -gt 0) { exit 1 }
