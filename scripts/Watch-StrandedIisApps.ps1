# Recycles an IIS app pool whose ASP.NET Core app has shut down inside a worker process that never exited.
#
# Every site on the production IIS box runs in-process. When IIS stops an app without recycling its pool,
# the in-process module cannot start the app again in the same worker, so the pool shows Started while
# every request gets "503 Service Unavailable" (IIS log: 503 0 1255) until someone recycles the pool.
# On 3 November 2026 this took five apps down for half an hour (an SAP installer rewrote machine.config)
# and then the ShopInventory Web and API (trigger unknown). Usually the module reports itself unhealthy
# right after such a stop (WAS event 5078) and IIS replaces the worker; a stranded app is one where that
# never happened. A site stop/start does not cause it: it leaves the app running.
#
# The ASP.NET Core Module logs event 1033 ("has shutdown") or 1005 ("failed to gracefully shutdown") with
# the worker's process id. An app is stranded when that worker is still alive, is still the newest worker
# of its pool, and nothing else explains it: the pool and site are Started, there is no app_offline.htm,
# no deploy is running, and the site answers 503 (or has no http binding to ask). A normal recycle never
# matches, because it either ends the old worker or starts a newer one.
#
# Run every minute by the scheduled task Register-StrandedIisAppWatchdog.ps1 installs. -DryRun reports
# what it would do and changes nothing. Windows PowerShell 5.1: no pwsh-only syntax.

[CmdletBinding()]
param(
    [int]$LookbackMinutes = 30,
    [int]$GraceSeconds = 60,
    [int]$CooldownMinutes = 15,
    [string]$StateDirectory = 'C:\ProgramData\StrandedIisAppWatchdog',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

$script:EventSource = 'StrandedIisAppWatchdog'
$script:AncmProvider = 'IIS AspNetCore Module V2'

# Turns one ASP.NET Core Module event into the site it names and the worker that logged it.
# 1033: "Application 'MACHINE/WEBROOT/APPHOST/SITE' has shutdown." / 1005: "Failed to gracefully
# shutdown application 'MACHINE/WEBROOT/APPHOST/SITE'." $Message is the event's insertion strings
# joined; the second one is "Process Id: N." (the rendered Message leaves it out).
function ConvertFrom-AncmStopEvent {
    param(
        [int]$Id,
        [datetime]$TimeCreated,
        [string]$Message
    )

    if ($Id -ne 1033 -and $Id -ne 1005) { return $null }

    $siteMatch = [regex]::Match($Message, "MACHINE/WEBROOT/APPHOST/([^'/]+)'", 'IgnoreCase')
    if (-not $siteMatch.Success) { return $null }

    $processId = $null
    $pidMatch = [regex]::Match($Message, 'Process Id:\s*(\d+)', 'IgnoreCase')
    if ($pidMatch.Success) { $processId = [int]$pidMatch.Groups[1].Value }

    [pscustomobject]@{
        EventId     = $Id
        TimeCreated = $TimeCreated
        SiteName    = $siteMatch.Groups[1].Value
        ProcessId   = $processId
    }
}

# The whole decision, with no IIS calls, so the tests can drive it.
#   $StopEvent   - from ConvertFrom-AncmStopEvent
#   $Site        - @{ Name; State; PoolName; PoolState; HasAppOffline; DeployInProgress } or $null
#   $Workers     - the pool's w3wp processes: @(@{ ProcessId; StartTime })
#   $LastRecycledAt - when this script last recycled the pool, or $null
#   $ProbeStatus - HTTP status from the site's http binding, or $null when there is none or no answer
function Get-StrandedAppDecision {
    param(
        [object]$StopEvent,
        [object]$Site,
        [object[]]$Workers,
        [datetime]$Now,
        [object]$LastRecycledAt,
        [object]$ProbeStatus,
        [int]$GraceSeconds = 60,
        [int]$CooldownMinutes = 15
    )

    function Decide([string]$Action, [string]$Reason) {
        [pscustomobject]@{ Action = $Action; Reason = $Reason }
    }

    $age = ($Now - $StopEvent.TimeCreated).TotalSeconds
    if ($age -lt $GraceSeconds) { return Decide 'Skip' "shutdown is $([int]$age)s old; waiting $GraceSeconds s for IIS to settle" }
    if ($null -eq $Site) { return Decide 'Skip' "no IIS site is named '$($StopEvent.SiteName)'" }
    if ($Site.PoolState -ne 'Started') { return Decide 'Skip' "pool '$($Site.PoolName)' is $($Site.PoolState) (a deploy or a deliberate stop)" }
    if ($Site.State -ne 'Started') { return Decide 'Skip' "site is $($Site.State) on purpose" }
    if ($Site.HasAppOffline) { return Decide 'Skip' 'app_offline.htm is present (a deploy is taking the app offline)' }
    if ($Site.DeployInProgress) { return Decide 'Skip' 'a ShopInventory deploy is extracting files' }

    # Without the logging worker's id there is no telling a stranded app from a normal recycle: IIS starts
    # the new worker a few seconds before the old one logs its shutdown (StockSheets, 10:57 on 3 Nov).
    if ($null -eq $StopEvent.ProcessId) { return Decide 'Skip' 'the event does not say which worker logged it' }

    $workers = @($Workers | Where-Object { $null -ne $_ })
    $stuck = @($workers | Where-Object { $_.ProcessId -eq $StopEvent.ProcessId })
    if ($stuck.Count -eq 0) { return Decide 'Skip' "worker $($StopEvent.ProcessId) has exited (a normal recycle)" }

    $newer = @($workers | Where-Object { $_.StartTime -gt $StopEvent.TimeCreated })
    if ($newer.Count -gt 0) { return Decide 'Skip' "worker $($newer[0].ProcessId) started after the shutdown and is serving" }

    if ($null -ne $LastRecycledAt -and ($Now - [datetime]$LastRecycledAt).TotalMinutes -lt $CooldownMinutes) {
        return Decide 'Skip' "recycled it at $(([datetime]$LastRecycledAt).ToString('HH:mm:ss')); cooling down for $CooldownMinutes min"
    }

    if ($null -ne $ProbeStatus -and [int]$ProbeStatus -ne 503) {
        return Decide 'Skip' "the site answers HTTP $ProbeStatus, so it is not stranded"
    }

    $evidence = if ($null -ne $ProbeStatus) { 'answers 503' } else { 'has no http binding to ask' }
    return Decide 'Recycle' "app shut down at $($StopEvent.TimeCreated.ToString('HH:mm:ss')) (event $($StopEvent.EventId)) inside worker $($StopEvent.ProcessId), which is still the pool's newest worker, and the site $evidence"
}

function Write-WatchdogLog {
    param([string]$Line)

    $logPath = Join-Path $StateDirectory 'watchdog.log'
    if ((Test-Path -LiteralPath $logPath) -and (Get-Item -LiteralPath $logPath).Length -gt 5MB) {
        Move-Item -LiteralPath $logPath -Destination "$logPath.1" -Force
    }
    $stamped = '{0} {1}' -f (Get-Date).ToString('yyyy-MM-dd HH:mm:ss'), $Line
    for ($i = 0; $i -lt 3; $i++) {
        try { Add-Content -LiteralPath $logPath -Value $stamped -Encoding UTF8; break }
        catch { Start-Sleep -Milliseconds 200 }
    }
    Write-Host $stamped
}

function Write-WatchdogEvent {
    param([int]$EventId, [string]$Message, [string]$EntryType = 'Warning')

    try {
        if ([System.Diagnostics.EventLog]::SourceExists($script:EventSource)) {
            Write-EventLog -LogName Application -Source $script:EventSource -EventId $EventId -EntryType $EntryType -Message $Message
        }
    }
    catch { Write-WatchdogLog "could not write event $EventId : $($_.Exception.Message)" }
}

function Read-RecycleState {
    $path = Join-Path $StateDirectory 'last-recycles.json'
    $state = @{}
    if (Test-Path -LiteralPath $path) {
        try {
            $json = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
            foreach ($p in $json.PSObject.Properties) { $state[$p.Name] = [datetime]$p.Value }
        }
        catch { Write-WatchdogLog "ignoring unreadable $path : $($_.Exception.Message)" }
    }
    return $state
}

function Save-RecycleState {
    param([hashtable]$State)

    $out = @{}
    foreach ($k in $State.Keys) { $out[$k] = $State[$k].ToString('o') }
    $out | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $StateDirectory 'last-recycles.json') -Encoding UTF8
}

function Get-PoolWorkers {
    param([string]$PoolName)

    $pattern = '-ap "' + $PoolName + '"'
    @(Get-CimInstance Win32_Process -Filter "Name='w3wp.exe'" |
        Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf($pattern, [StringComparison]::OrdinalIgnoreCase) -ge 0 } |
        ForEach-Object { [pscustomobject]@{ ProcessId = [int]$_.ProcessId; StartTime = $_.CreationDate } })
}

# Asks the site's first http binding for "/" on localhost. Returns the status code, or $null when the
# site has no http binding or nothing answered in time.
function Get-SiteProbeStatus {
    param([object]$IisSite)

    $binding = @($IisSite.bindings.Collection | Where-Object { $_.protocol -eq 'http' }) | Select-Object -First 1
    if (-not $binding) { return $null }

    $parts = $binding.bindingInformation -split ':'
    $port = $parts[1]
    $hostName = if ($parts.Count -gt 2 -and $parts[2]) { $parts[2] } else { "localhost:$port" }

    try {
        $request = [System.Net.HttpWebRequest]::Create("http://localhost:$port/")
        $request.Host = $hostName
        $request.Timeout = 10000
        $request.AllowAutoRedirect = $false
        $response = $request.GetResponse()
        $code = [int]$response.StatusCode
        $response.Close()
        return $code
    }
    catch [System.Net.WebException] {
        if ($_.Exception.Response) {
            $code = [int]$_.Exception.Response.StatusCode
            $_.Exception.Response.Close()
            return $code
        }
        return $null
    }
}

function Invoke-Watchdog {
    if (-not (Test-Path -LiteralPath $StateDirectory)) { New-Item -ItemType Directory -Path $StateDirectory -Force | Out-Null }
    Import-Module WebAdministration -ErrorAction Stop

    $now = Get-Date
    $events = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = $script:AncmProvider; Id = 1033, 1005; StartTime = $now.AddMinutes(-$LookbackMinutes) } -ErrorAction SilentlyContinue |
        # The rendered Message holds only the first insertion string; "Process Id: N." is the second.
        ForEach-Object { ConvertFrom-AncmStopEvent -Id $_.Id -TimeCreated $_.TimeCreated -Message ((@($_.Properties) | ForEach-Object { [string]$_.Value }) -join "`r`n") } |
        Where-Object { $null -ne $_ })
    if ($events.Count -eq 0) { return }

    $sitesByName = @{}
    foreach ($s in Get-Website) { $sitesByName[$s.Name.ToUpperInvariant()] = $s }

    $deployInProgress = @(Get-ChildItem -Path 'C:\inetpub' -Directory -Filter 'ShopInventory-temp-extract-*' -ErrorAction SilentlyContinue).Count -gt 0
    $recycled = Read-RecycleState
    $changed = $false

    # One decision per pool, on its latest shutdown.
    $latestPerSite = $events | Group-Object { $_.SiteName.ToUpperInvariant() } | ForEach-Object { $_.Group | Sort-Object TimeCreated | Select-Object -Last 1 }

    foreach ($stop in $latestPerSite) {
        $iisSite = $sitesByName[$stop.SiteName.ToUpperInvariant()]
        $site = $null
        $workers = @()
        $probe = $null
        $poolName = $null

        if ($iisSite) {
            $poolName = $iisSite.applicationPool
            $physicalPath = [Environment]::ExpandEnvironmentVariables($iisSite.physicalPath)
            $site = [pscustomobject]@{
                Name             = $iisSite.Name
                State            = (Get-WebsiteState -Name $iisSite.Name).Value
                PoolName         = $poolName
                PoolState        = (Get-WebAppPoolState -Name $poolName).Value
                HasAppOffline    = Test-Path -LiteralPath (Join-Path $physicalPath 'app_offline.htm')
                DeployInProgress = $deployInProgress -and $iisSite.Name -like 'ShopInventory-*'
            }
            $workers = Get-PoolWorkers -PoolName $poolName
        }

        $last = if ($poolName -and $recycled.ContainsKey($poolName)) { $recycled[$poolName] } else { $null }

        # Probe only when everything else already says stranded: it is the slowest check.
        $pre = Get-StrandedAppDecision -StopEvent $stop -Site $site -Workers $workers -Now $now -LastRecycledAt $last -ProbeStatus $null -GraceSeconds $GraceSeconds -CooldownMinutes $CooldownMinutes
        if ($pre.Action -eq 'Recycle') {
            $probe = Get-SiteProbeStatus -IisSite $iisSite
        }
        $decision = Get-StrandedAppDecision -StopEvent $stop -Site $site -Workers $workers -Now $now -LastRecycledAt $last -ProbeStatus $probe -GraceSeconds $GraceSeconds -CooldownMinutes $CooldownMinutes

        if ($decision.Action -ne 'Recycle') {
            Write-Verbose "$($stop.SiteName): skip - $($decision.Reason)"
            continue
        }

        if ($DryRun) {
            Write-WatchdogLog "DRY RUN would recycle '$poolName' for site '$($site.Name)': $($decision.Reason)"
            continue
        }

        Write-WatchdogLog "Recycling '$poolName' for site '$($site.Name)': $($decision.Reason)"
        try {
            Restart-WebAppPool -Name $poolName -ErrorAction Stop
            $recycled[$poolName] = Get-Date
            $changed = $true
            Start-Sleep -Seconds 15
            $after = Get-SiteProbeStatus -IisSite $iisSite
            $afterText = if ($null -ne $after) { "HTTP $after" } else { 'no http binding to check' }
            Write-WatchdogLog "Recycled '$poolName'; the site now gives $afterText"
            Write-WatchdogEvent -EventId 1000 -Message "Recycled app pool '$poolName' for site '$($site.Name)': $($decision.Reason). Afterwards the site gives $afterText."
        }
        catch {
            Write-WatchdogLog "FAILED to recycle '$poolName': $($_.Exception.Message)"
            Write-WatchdogEvent -EventId 1001 -EntryType Error -Message "Could not recycle app pool '$poolName' for stranded site '$($site.Name)': $($_.Exception.Message)"
        }
    }

    if ($changed) { Save-RecycleState -State $recycled }
}

# Dot-sourcing (the tests) loads the functions without running anything.
if ($MyInvocation.InvocationName -ne '.') {
    try { Invoke-Watchdog }
    catch {
        Write-WatchdogLog "watchdog run failed: $($_.Exception.Message)"
        Write-WatchdogEvent -EventId 1001 -EntryType Error -Message "Stranded IIS app watchdog failed: $($_.Exception.Message)"
        exit 1
    }
}
