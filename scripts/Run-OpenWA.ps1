[CmdletBinding()]
param(
    # Defaults to the OpenWA folder beside this script's. Resolved below rather than here:
    # Windows PowerShell 5.1 leaves $PSScriptRoot empty in a default value under -File.
    [string]$OpenWARoot,
    [string]$NodeHome = "C:\Users\ngoni\.config\herd\bin\nvm\v20.20.2",
    [string]$ChromePath = "C:\Program Files\Google\Chrome\Application\chrome.exe",
    [switch]$SkipBuild,
    [switch]$ValidateOnly,
    # node is restarted after any non-zero exit, first after this many seconds, doubling each time.
    [int]$RestartDelaySeconds = 5,
    [int]$MaxRestartDelaySeconds = 300,
    # A run that lasted this long was healthy, so the next restart starts the backoff again.
    [int]$HealthyRunSeconds = 600,
    # The port node serves on, for its health check. 0 takes it from where OpenWA does: the PORT
    # environment variable, then OpenWA\.env, then data\.env.generated, then OpenWA's own 2785.
    [int]$Port = 0,
    # Once node has answered /api/health it is asked again this often (0 turns the check off). A node
    # that is still running but fails this many checks in a row is ended, and restarted like a crash.
    [int]$HealthCheckSeconds = 30,
    [int]$HealthCheckFailures = 4,
    [int]$HealthCheckTimeoutSeconds = 10
)

$ErrorActionPreference = 'Stop'

if (-not $OpenWARoot) {
    $OpenWARoot = Join-Path $PSScriptRoot "..\OpenWA"
}

. (Join-Path $PSScriptRoot 'OpenWAProcesses.ps1')

function Resolve-RequiredPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "$Description not found: $Path"
    }

    return (Resolve-Path -LiteralPath $Path).Path
}

function Get-OpenWAProcess {
    # This install's gateway node. Another folder's gateway, such as a test's fake one, is left alone.
    $snapshot = @(Get-CimInstance Win32_Process -Filter "name = 'node.exe' or name = 'powershell.exe' or name = 'pwsh.exe'" -ErrorAction SilentlyContinue)
    @(Select-OpenWAGateway -Snapshot $snapshot -Root $resolvedOpenWARoot -Name 'node.exe')
}

function Format-OpenWAProcess {
    param([object]$Process)

    if ($Process.OpenWARoot) {
        return "PID $($Process.ProcessId)"
    }

    return "PID $($Process.ProcessId), started as $($Process.CommandLine), whose folder cannot be told and so is taken for this one"
}

function Write-SupervisorLog {
    param([string]$Message)

    $line = '{0}  {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message
    Write-Host $line
    # Anyone reading the log at that moment (Get-Content, an editor) can hold it open long enough for
    # Add-Content to fail, and the line would be lost.
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        try {
            Add-Content -LiteralPath $supervisorLog -Value $line -Encoding UTF8
            return
        }
        catch {
            Start-Sleep -Milliseconds 100
        }
    }
}

function Test-StopRequested {
    Test-Path -LiteralPath $stopMarker
}

function Get-OrphanedChrome {
    param([object[]]$Chromes = @(Get-CimInstance Win32_Process -Filter "name = 'chrome.exe'" -ErrorAction SilentlyContinue))

    # whatsapp-web.js keeps each session's Chrome profile under OpenWA's data directory, and passes it
    # to Chrome as an absolute --user-data-dir. No gateway is running when this is called, so any
    # Chrome still holding one of those profiles is orphaned.
    @($Chromes | Where-Object {
            $commandLine = $_.CommandLine
            $commandLine -and $commandLine.IndexOf('--user-data-dir=', [StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            (Test-OpenWAPathInCommandLine -CommandLine $commandLine -Root $resolvedOpenWARoot)
        })
}

function Stop-OrphanedChrome {
    # A crash on a Chrome protocol timeout can leave a hung Chrome behind, and while it holds a
    # session's profile that session cannot start again.
    $chromes = @(Get-CimInstance Win32_Process -Filter "name = 'chrome.exe'" -ErrorAction SilentlyContinue)

    # Matching is by command line, which only an elevated or SYSTEM runner (the boot task) can read
    # for another account's process. Say so rather than skip what it cannot see.
    # One of this account's own processes is unreadable for a moment while it starts or exits.
    $me = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $unreadable = @($chromes | Where-Object {
            $owner = $null
            if (-not $_.CommandLine) {
                try {
                    $owner = Invoke-CimMethod -InputObject $_ -MethodName GetOwner -ErrorAction Stop
                }
                catch { }
            }
            -not $_.CommandLine -and -not ($owner -and $owner.ReturnValue -eq 0 -and "$($owner.Domain)\$($owner.User)" -eq $me)
        })
    if ($unreadable.Count -gt 0) {
        Write-SupervisorLog ("WARNING: cannot read the command line of $($unreadable.Count) chrome.exe process(es) " +
            "(PID $(($unreadable | ForEach-Object { $_.ProcessId }) -join ', ')), so any of them holding an OpenWA session " +
            "profile is not ended. This runner is not elevated. If a session then fails with 'The browser is already " +
            "running', run Stop-OpenWA.ps1 from an elevated PowerShell.")
    }

    $orphans = @(Get-OrphanedChrome -Chromes $chromes)
    if ($orphans.Count -eq 0) {
        return
    }

    Write-SupervisorLog "Ending $($orphans.Count) Chrome process(es) left holding OpenWA session profiles: PID $(($orphans | ForEach-Object { $_.ProcessId }) -join ', ')."

    # Matched again by command line each time rather than by PID, which Windows soon reuses.
    $deadline = (Get-Date).AddSeconds(15)
    while ($orphans.Count -gt 0) {
        foreach ($orphan in $orphans) {
            # Ending the browser process takes its renderers with it, so some of these are already gone.
            Stop-Process -Id $orphan.ProcessId -Force -ErrorAction SilentlyContinue
        }

        if ((Get-Date) -ge $deadline) {
            Write-SupervisorLog "Chrome PID $(($orphans | ForEach-Object { $_.ProcessId }) -join ', ') still running 15s later; starting node anyway."
            return
        }

        Start-Sleep -Milliseconds 500
        $orphans = @(Get-OrphanedChrome)
    }
}

function Wait-RestartDelay {
    param([int]$Seconds)

    # Returns false when a stop is requested during the wait.
    $until = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $until) {
        if (Test-StopRequested) {
            return $false
        }
        Start-Sleep -Milliseconds 500
    }

    return -not (Test-StopRequested)
}

function Resolve-OpenWAPort {
    # OpenWA's main.ts lets the process environment win over .env, and .env over data\.env.generated.
    if ($Port -gt 0) {
        return $Port
    }

    if ($env:PORT -match '^\s*(\d+)\s*$') {
        return [int]$Matches[1]
    }

    foreach ($file in @((Join-Path $resolvedOpenWARoot '.env'), (Join-Path $resolvedOpenWARoot 'data\.env.generated'))) {
        if (-not (Test-Path -LiteralPath $file)) {
            continue
        }

        $value = 0
        foreach ($line in @(Get-Content -LiteralPath $file)) {
            # dotenv keeps the last assignment in a file.
            if ($line -match '^\s*PORT\s*=\s*["'']?(\d+)["'']?\s*(#.*)?$') {
                $value = [int]$Matches[1]
            }
        }

        if ($value -gt 0) {
            return $value
        }
    }

    return 2785
}

function Get-HealthFailure {
    # $null when node answers its health check with 200, otherwise what went wrong.
    $ProgressPreference = 'SilentlyContinue'
    try {
        $response = Invoke-WebRequest -Uri $healthUrl -UseBasicParsing -TimeoutSec $HealthCheckTimeoutSeconds
        if ($response.StatusCode -eq 200) {
            return $null
        }

        return "HTTP $($response.StatusCode)"
    }
    catch {
        return $_.Exception.Message
    }
}

function Wait-NodeExit {
    param([System.Diagnostics.Process]$Process, [int]$Run, [datetime]$Started)

    # Returns $true when node was ended here for not answering, $false when it exited by itself.
    #
    # A crash ends node, but a wedged event loop or a puppeteer deadlock leaves it running and silent,
    # which no exit code reports. So node is asked for /api/health, and held to it only once it has
    # answered: a restart on 10.10.10.9 took over a minute to answer, and a gateway that is still
    # starting must not be ended for being slow.
    $answered = $false
    $failures = 0
    $nextCheck = (Get-Date).AddSeconds($HealthCheckSeconds)

    while (-not $Process.WaitForExit(500)) {
        if ($HealthCheckSeconds -le 0 -or (Get-Date) -lt $nextCheck -or (Test-StopRequested)) {
            continue
        }

        $failure = Get-HealthFailure
        $nextCheck = (Get-Date).AddSeconds($HealthCheckSeconds)

        if ($null -eq $failure) {
            if (-not $answered) {
                Write-SupervisorLog "node answered $healthUrl $([int]((Get-Date) - $Started).TotalSeconds)s after it started (run $Run). Health checks count from now."
                $answered = $true
            }
            elseif ($failures -gt 0) {
                Write-SupervisorLog "node answered $healthUrl again after $failures failed check(s)."
            }

            $failures = 0
            continue
        }

        if (-not $answered) {
            continue
        }

        $failures++
        Write-SupervisorLog "Health check $failures of $HealthCheckFailures failed: $failure"
        if ($failures -lt $HealthCheckFailures) {
            continue
        }

        Write-SupervisorLog "node PID $($Process.Id) is running but failed $failures health checks in a row. Ending it."
        try {
            # The handle held on $Process keeps Windows from reusing this PID, so this ends node and nothing else.
            $Process.Kill()
        }
        catch { }

        if (-not $Process.WaitForExit(15000)) {
            Write-SupervisorLog "node PID $($Process.Id) was still running 15s after it was ended."
        }

        # node's Chrome outlives it, and would keep holding the session profiles.
        Stop-OrphanedChrome
        return $true
    }

    return $false
}

$resolvedOpenWARoot = Resolve-RequiredPath -Path $OpenWARoot -Description 'OpenWA root directory'
$nodeExe = Resolve-RequiredPath -Path (Join-Path $NodeHome 'node.exe') -Description 'Node executable'
$npmCmd = Resolve-RequiredPath -Path (Join-Path $NodeHome 'npm.cmd') -Description 'npm executable'
$resolvedChromePath = Resolve-RequiredPath -Path $ChromePath -Description 'Chrome executable'
$distMainPath = Join-Path $resolvedOpenWARoot 'dist\main.js'

if (-not (Test-Path -LiteralPath $distMainPath)) {
    if ($SkipBuild) {
        throw "Compiled OpenWA entrypoint not found and -SkipBuild was supplied: $distMainPath"
    }

    Push-Location $resolvedOpenWARoot
    try {
        & $npmCmd run build
        if ($LASTEXITCODE -ne 0) {
            throw "OpenWA build failed with exit code $LASTEXITCODE"
        }
    }
    finally {
        Pop-Location
    }

    if (-not (Test-Path -LiteralPath $distMainPath)) {
        throw "Compiled OpenWA entrypoint still not found after build: $distMainPath"
    }
}

$existingProcess = Get-OpenWAProcess | Select-Object -First 1
if ($existingProcess) {
    Write-Host "OpenWA is already running from $resolvedOpenWARoot ($(Format-OpenWAProcess $existingProcess))."
    if ($ValidateOnly) {
        return
    }

    return
}

if ($ValidateOnly) {
    Write-Host "OpenWA prerequisites validated."
    Write-Host "OpenWA root: $resolvedOpenWARoot"
    Write-Host "Node executable: $nodeExe"
    Write-Host "Chrome executable: $resolvedChromePath"
    Write-Host "Entrypoint: $distMainPath"
    return
}

$logsDir = Join-Path $resolvedOpenWARoot 'logs'
New-Item -ItemType Directory -Path $logsDir -Force | Out-Null

$supervisorLog = Join-Path $logsDir 'openwa-supervisor.txt'

# Stop-OpenWA.ps1 writes this before it ends node, so that the exit it causes is not taken for a
# crash. A start is a request to run, so a marker left from an earlier stop no longer applies.
$stopMarker = Join-Path $logsDir 'stop-requested'
Remove-Item -LiteralPath $stopMarker -Force -ErrorAction SilentlyContinue

$healthUrl = "http://127.0.0.1:$(Resolve-OpenWAPort)/api/health"

$env:PATH = "$NodeHome;$env:PATH"
$env:PUPPETEER_SKIP_CHROMIUM_DOWNLOAD = 'true'
$env:PUPPETEER_EXECUTABLE_PATH = $resolvedChromePath

Write-Host "Starting OpenWA from $resolvedOpenWARoot"
Write-Host "Supervisor log: $supervisorLog"
if ($HealthCheckSeconds -gt 0) {
    Write-SupervisorLog "Supervisor started. Once node answers $healthUrl it is checked every ${HealthCheckSeconds}s, and restarted after $HealthCheckFailures failed checks in a row."
}
else {
    Write-SupervisorLog "Supervisor started with health checks off (-HealthCheckSeconds 0): node is restarted only when it exits."
}

# Task Scheduler's restart settings only cover a task that fails to launch, not an action that exits
# non-zero, so this loop is what brings the gateway back after a crash. On 14 Oct 2026 an unhandled
# Chrome protocol timeout ended node and WhatsApp delivery stayed down for two days.
$restartDelay = $RestartDelaySeconds
$run = 0

Push-Location $resolvedOpenWARoot
try {
    while ($true) {
        $run++
        Stop-OrphanedChrome

        if (Test-StopRequested) {
            Write-SupervisorLog "Stop requested before run $run started. Not starting node."
            exit 0
        }

        $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $stdoutLog = Join-Path $logsDir "openwa-$timestamp.out.log"
        $stderrLog = Join-Path $logsDir "openwa-$timestamp.err.log"
        if ((Test-Path -LiteralPath $stdoutLog) -or (Test-Path -LiteralPath $stderrLog)) {
            $stdoutLog = Join-Path $logsDir "openwa-$timestamp-$run.out.log"
            $stderrLog = Join-Path $logsDir "openwa-$timestamp-$run.err.log"
        }

        Write-SupervisorLog "Starting node (run $run). Stdout log: $stdoutLog  Stderr log: $stderrLog"
        $runStarted = Get-Date

        # node runs as a child this loop can watch, not under &, which blocks until it exits. Start-Process
        # gives node the two log files as its own stdout and stderr, so PowerShell never reads its output.
        # That keeps what the old 2>> redirect needed 'Continue' for: Windows PowerShell 5.1 turns each
        # stderr line it reads from a native command into an error record, and under 'Stop' the first one
        # ended this script and node with it. node writes a punycode deprecation warning to stderr at every
        # start, so the boot task exited 1 within seconds. node's exit code is what reports failure.
        #
        # The entry point goes in by its absolute path, so node's command line names this folder and
        # Stop-OpenWA.ps1 and the tests can tell this gateway from another folder's. OpenWA reads .env
        # and data\ from its working directory, which is still this folder.
        $node = Start-Process -FilePath $nodeExe -ArgumentList "`"$distMainPath`"" -WorkingDirectory $resolvedOpenWARoot `
            -RedirectStandardOutput $stdoutLog -RedirectStandardError $stderrLog -NoNewWindow -PassThru
        # Windows PowerShell 5.1 reports no ExitCode for a process whose handle was not read while it ran.
        $null = $node.Handle

        $endedUnresponsive = Wait-NodeExit -Process $node -Run $run -Started $runStarted
        $exitCode = if ($node.HasExited) { $node.ExitCode } else { 'none' }

        $ranSeconds = [int]((Get-Date) - $runStarted).TotalSeconds

        if (Test-StopRequested) {
            Write-SupervisorLog "node exited with code $exitCode after ${ranSeconds}s because a stop was requested. Not restarting."
            exit 0
        }

        if (-not $endedUnresponsive -and $exitCode -eq 0) {
            Write-SupervisorLog "node exited cleanly after ${ranSeconds}s. Not restarting."
            exit 0
        }

        if ($ranSeconds -ge $HealthyRunSeconds) {
            $restartDelay = $RestartDelaySeconds
        }

        if ($endedUnresponsive) {
            Write-SupervisorLog "node was ended after ${ranSeconds}s for not answering $healthUrl. Restarting in ${restartDelay}s; its last output is in $stdoutLog and $stderrLog."
        }
        else {
            Write-SupervisorLog "node exited with code $exitCode after ${ranSeconds}s. Restarting in ${restartDelay}s; the reason is in $stderrLog."
        }

        if (-not (Wait-RestartDelay -Seconds $restartDelay)) {
            Write-SupervisorLog "Stop requested while waiting to restart. Not restarting."
            exit 0
        }

        # Someone may have started a gateway by hand during the wait. Two would fight for the port and
        # for the session profiles.
        $existingProcess = Get-OpenWAProcess | Select-Object -First 1
        if ($existingProcess) {
            Write-SupervisorLog "Another OpenWA gateway started from this folder during the wait ($(Format-OpenWAProcess $existingProcess)). Leaving it running."
            exit 0
        }

        $restartDelay = [Math]::Min($restartDelay * 2, $MaxRestartDelaySeconds)
    }
}
finally {
    Pop-Location
}
