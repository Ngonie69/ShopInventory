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
    [int]$HealthyRunSeconds = 600
)

$ErrorActionPreference = 'Stop'

if (-not $OpenWARoot) {
    $OpenWARoot = Join-Path $PSScriptRoot "..\OpenWA"
}

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
    Get-CimInstance Win32_Process -Filter "name = 'node.exe'" -ErrorAction SilentlyContinue |
    Where-Object {
        $commandLine = $_.CommandLine
        $commandLine -and (
            $commandLine -like "*\OpenWA\dist\main*" -or
            $commandLine -like "* .\dist\main.js*" -or
            $commandLine -like "*\OpenWA\node_modules\.bin\*nest.js*" -or
            $commandLine -like "*npm-cli.js*run start*OpenWA*"
        )
    }
}

function Write-SupervisorLog {
    param([string]$Message)

    $line = '{0}  {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $Message
    Write-Host $line
    try {
        Add-Content -LiteralPath $supervisorLog -Value $line -Encoding UTF8
    }
    catch { }
}

function Test-StopRequested {
    Test-Path -LiteralPath $stopMarker
}

function Get-OrphanedChrome {
    param([object[]]$Chromes = @(Get-CimInstance Win32_Process -Filter "name = 'chrome.exe'" -ErrorAction SilentlyContinue))

    # whatsapp-web.js keeps each session's Chrome profile under OpenWA's data directory, and passes it
    # to Chrome as an absolute --user-data-dir. No gateway is running when this is called, so any
    # Chrome still holding one of those profiles is orphaned.
    $roots = @($resolvedOpenWARoot, ($resolvedOpenWARoot -replace '\\', '/'))
    @($Chromes | Where-Object {
            $commandLine = $_.CommandLine
            $commandLine -and $commandLine.IndexOf('--user-data-dir=', [StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            ($roots | Where-Object { $commandLine.IndexOf($_, [StringComparison]::OrdinalIgnoreCase) -ge 0 })
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
    Write-Host "OpenWA is already running (PID $($existingProcess.ProcessId))."
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

$env:PATH = "$NodeHome;$env:PATH"
$env:PUPPETEER_SKIP_CHROMIUM_DOWNLOAD = 'true'
$env:PUPPETEER_EXECUTABLE_PATH = $resolvedChromePath

Write-Host "Starting OpenWA from $resolvedOpenWARoot"
Write-Host "Supervisor log: $supervisorLog"

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

        # Windows PowerShell 5.1 turns each stderr line of a redirected native command into an error
        # record, and under 'Stop' the first one ends this script and takes Node with it. Node writes a
        # punycode deprecation warning to stderr at every start, so the boot task exited 1 within
        # seconds of starting. Node's stderr is log output here; its exit code is what reports failure.
        $ErrorActionPreference = 'Continue'
        & $nodeExe '.\dist\main.js' 1>> $stdoutLog 2>> $stderrLog
        $exitCode = $LASTEXITCODE
        $ErrorActionPreference = 'Stop'

        $ranSeconds = [int]((Get-Date) - $runStarted).TotalSeconds

        if (Test-StopRequested) {
            Write-SupervisorLog "node exited with code $exitCode after ${ranSeconds}s because a stop was requested. Not restarting."
            exit 0
        }

        if ($exitCode -eq 0) {
            Write-SupervisorLog "node exited cleanly after ${ranSeconds}s. Not restarting."
            exit 0
        }

        if ($ranSeconds -ge $HealthyRunSeconds) {
            $restartDelay = $RestartDelaySeconds
        }

        Write-SupervisorLog "node exited with code $exitCode after ${ranSeconds}s. Restarting in ${restartDelay}s; the reason is in $stderrLog."

        if (-not (Wait-RestartDelay -Seconds $restartDelay)) {
            Write-SupervisorLog "Stop requested while waiting to restart. Not restarting."
            exit 0
        }

        # Someone may have started a gateway by hand during the wait. Two would fight for the port and
        # for the session profiles.
        $existingProcess = Get-OpenWAProcess | Select-Object -First 1
        if ($existingProcess) {
            Write-SupervisorLog "Another OpenWA gateway started during the wait (PID $($existingProcess.ProcessId)). Leaving it running."
            exit 0
        }

        $restartDelay = [Math]::Min($restartDelay * 2, $MaxRestartDelaySeconds)
    }
}
finally {
    Pop-Location
}
