[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    # Defaults to the OpenWA folder beside this script's. Resolved below rather than here: Windows
    # PowerShell 5.1 leaves $PSScriptRoot empty in a default value when the script is run with -File.
    [string]$OpenWARoot,
    # How long Run-OpenWA.ps1 is given to notice the stop and exit before it is ended.
    [int]$SupervisorTimeoutSeconds = 15
)

$ErrorActionPreference = 'Stop'

if (-not $OpenWARoot) {
    $OpenWARoot = Join-Path $PSScriptRoot '..\OpenWA'
}

function Get-OpenWANodeProcess {
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

function Get-OpenWASupervisor {
    # Run-OpenWA.ps1, the boot task's action, restarts node whenever it exits non-zero.
    Get-CimInstance Win32_Process -Filter "name = 'powershell.exe' or name = 'pwsh.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.ProcessId -ne $PID -and $_.CommandLine -and $_.CommandLine -like '*Run-OpenWA.ps1*' }
}

function Stop-ProcessIfRunning {
    param([int]$ProcessId)

    try {
        Stop-Process -Id $ProcessId -Force
        return $true
    }
    catch {
        # Already gone is fine. Anything else, such as access denied from an unelevated console, is not.
        if (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) {
            throw
        }
        return $false
    }
}

if (-not $PSCmdlet.ShouldProcess($OpenWARoot, 'Stop the OpenWA gateway and its supervisor')) {
    return
}

# Ending node looks like a crash to the supervisor, which would restart it. The marker tells it the
# exit was asked for, so it exits instead - including when it is waiting out a restart delay. It is
# written before the process queries below, which took seconds on a loaded laptop. Left behind
# when nothing is running, it is harmless: Run-OpenWA.ps1 clears it when it starts.
$logsDir = Join-Path $OpenWARoot 'logs'
New-Item -ItemType Directory -Path $logsDir -Force | Out-Null
Set-Content -LiteralPath (Join-Path $logsDir 'stop-requested') -Value "Stop requested by $env:USERDOMAIN\$env:USERNAME at $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" -Encoding UTF8

$processes = @(Get-OpenWANodeProcess)
$supervisors = @(Get-OpenWASupervisor)
$hadSupervisor = $supervisors.Count -gt 0

if ($processes.Count -eq 0 -and $supervisors.Count -eq 0) {
    Write-Host 'OpenWA is not running.'
    return
}

$deadline = (Get-Date).AddSeconds($SupervisorTimeoutSeconds)
while ($true) {
    foreach ($process in @(Get-OpenWANodeProcess)) {
        if (Stop-ProcessIfRunning -ProcessId $process.ProcessId) {
            Write-Host "Stopped OpenWA process PID $($process.ProcessId)."
        }
    }

    $supervisors = @(Get-OpenWASupervisor)
    if ($supervisors.Count -eq 0 -or (Get-Date) -ge $deadline) {
        break
    }

    Start-Sleep -Milliseconds 500
}

if ($supervisors.Count -gt 0) {
    foreach ($supervisor in $supervisors) {
        if (Stop-ProcessIfRunning -ProcessId $supervisor.ProcessId) {
            Write-Warning "The OpenWA supervisor PID $($supervisor.ProcessId) did not exit within ${SupervisorTimeoutSeconds}s of the stop, so it was ended."
        }
    }

    foreach ($process in @(Get-OpenWANodeProcess)) {
        if (Stop-ProcessIfRunning -ProcessId $process.ProcessId) {
            Write-Host "Stopped OpenWA process PID $($process.ProcessId)."
        }
    }
}
elseif ($hadSupervisor) {
    Write-Host 'The OpenWA supervisor exited without restarting node.'
}
