[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    # Defaults to the OpenWA folder beside this script's. Resolved below rather than here: Windows
    # PowerShell 5.1 leaves $PSScriptRoot empty in a default value when the script is run with -File.
    [string]$OpenWARoot,
    # How long Run-OpenWA.ps1 and the gateway's Chrome are given to go before this gives up waiting.
    [int]$SupervisorTimeoutSeconds = 15
)

$ErrorActionPreference = 'Stop'

if (-not $OpenWARoot) {
    $OpenWARoot = Join-Path $PSScriptRoot '..\OpenWA'
}

. (Join-Path $PSScriptRoot 'OpenWAProcesses.ps1')

function Test-Elevated {
    ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-ProcessSnapshot {
    @(Get-CimInstance Win32_Process -Filter "name = 'node.exe' or name = 'chrome.exe' or name = 'powershell.exe' or name = 'pwsh.exe'" -ErrorAction SilentlyContinue)
}

function Select-OpenWAProcesses {
    # Sorts one process snapshot into what belongs to this OpenWA install. Pure, so it can be tested
    # with made-up processes.
    param(
        [object[]]$Snapshot,
        [string]$Root,
        # node processes already ended in an earlier pass. Their Chrome still points at them.
        [int[]]$EndedNodePids = @(),
        [int]$ExcludePid
    )

    $Snapshot = @($Snapshot | Where-Object { $_ })

    # Only this install's: another folder's gateway, such as a test's fake one, is left running. See
    # OpenWAProcesses.ps1 for how a process is tied to its folder.
    $gateway = @(Select-OpenWAGateway -Snapshot $Snapshot -Root $Root)
    $node = @($gateway | Where-Object { $_.Name -eq 'node.exe' })

    # Run-OpenWA.ps1, the boot task's action, restarts node whenever it exits non-zero.
    $supervisors = @($gateway | Where-Object { $_.Name -ne 'node.exe' -and [int]$_.ProcessId -ne $ExcludePid })

    # The Chrome the gateway launched, found by parent id down from node. The parent id is readable
    # even where the command line is not, and a child keeps its parent's id after the parent exits.
    $chrome = @{}
    $queue = New-Object System.Collections.Queue
    foreach ($seed in @($node | ForEach-Object { [int]$_.ProcessId }) + $EndedNodePids) {
        $queue.Enqueue([int]$seed)
    }
    while ($queue.Count -gt 0) {
        $parent = $queue.Dequeue()
        foreach ($child in @($Snapshot | Where-Object { $_.Name -eq 'chrome.exe' -and [int]$_.ParentProcessId -eq $parent })) {
            if (-not $chrome.ContainsKey([int]$child.ProcessId)) {
                $chrome[[int]$child.ProcessId] = $child
                $queue.Enqueue([int]$child.ProcessId)
            }
        }
    }

    # And any Chrome still holding a session profile under this install, whose node is long gone.
    # whatsapp-web.js passes the profile as an absolute --user-data-dir.
    foreach ($process in @($Snapshot | Where-Object { $_.Name -eq 'chrome.exe' -and $_.CommandLine })) {
        $commandLine = $process.CommandLine
        if ($commandLine.IndexOf('--user-data-dir=', [StringComparison]::OrdinalIgnoreCase) -ge 0 -and
            (Test-OpenWAPathInCommandLine -CommandLine $commandLine -Root $Root)) {
            $chrome[[int]$process.ProcessId] = $process
        }
    }

    $known = @{}
    foreach ($process in $node + $supervisors + @($chrome.Values)) {
        $known[[int]$process.ProcessId] = $true
    }

    # node or Chrome whose command line this console cannot read: other accounts' processes, such as
    # the SYSTEM boot task's gateway when this console is not elevated.
    $hidden = @($Snapshot | Where-Object {
            ($_.Name -eq 'node.exe' -or $_.Name -eq 'chrome.exe') -and -not $_.CommandLine -and -not $known.ContainsKey([int]$_.ProcessId)
        })

    [pscustomobject]@{
        Node       = $node
        Supervisor = $supervisors
        Chrome     = @($chrome.Values)
        Hidden     = $hidden
    }
}

function Select-OtherAccountProcess {
    # Of the processes whose command line could not be read, keeps the ones that belong to another
    # account. A process of this account is unreadable for a moment while it starts or exits, as
    # Chrome's helpers did when a test started a browser, so it is not counted, and neither is one
    # that has gone by a second look.
    param([object[]]$Processes)

    $me = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    $candidates = @($Processes | Where-Object {
            $owner = $null
            try {
                $owner = Invoke-CimMethod -InputObject $_ -MethodName GetOwner -ErrorAction Stop
            }
            catch { }
            -not ($owner -and $owner.ReturnValue -eq 0 -and "$($owner.Domain)\$($owner.User)" -eq $me)
        })
    if ($candidates.Count -eq 0) {
        return @()
    }

    Start-Sleep -Seconds 1
    $ids = @($candidates | ForEach-Object { [int]$_.ProcessId })
    @(Get-ProcessSnapshot | Where-Object { $ids -contains [int]$_.ProcessId -and -not $_.CommandLine })
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

function Format-Pids {
    param([object[]]$Processes)
    ($Processes | ForEach-Object { "$($_.ProcessId) ($($_.Name))" }) -join ', '
}

$resolvedRoot = [IO.Path]::GetFullPath($OpenWARoot)

if (-not $PSCmdlet.ShouldProcess($resolvedRoot, 'Stop the OpenWA gateway, its Chrome and its supervisor')) {
    return
}

$found = Select-OpenWAProcesses -Snapshot (Get-ProcessSnapshot) -Root $resolvedRoot -ExcludePid $PID

# On 16 Oct 2026 a stop from an unelevated console on 10.10.10.9 ended nothing of the gateway's
# Chrome: the boot task runs it as SYSTEM, and an unelevated console reads no command line of a
# SYSTEM process, so nothing matched. The next start failed on the locked session profile.
$hidden = @()
if ($found.Hidden.Count -gt 0) {
    $hidden = @(Select-OtherAccountProcess -Processes $found.Hidden)
}

if ($hidden.Count -gt 0 -and -not (Test-Elevated)) {
    throw @"
This console is not elevated and cannot read the command line of $($hidden.Count) node/Chrome process(es) of another account: $(Format-Pids $hidden).
The boot task runs the gateway as SYSTEM, so its node and Chrome may be among them, and a stop from here would leave them running and holding the session profiles. Nothing was stopped. Run this again from an elevated PowerShell.
"@
}

if ($hidden.Count -gt 0) {
    Write-Warning "Cannot read the command line of $(Format-Pids $hidden) even elevated. Any of them left by the gateway is not ended."
}

foreach ($process in @(@($found.Node) + @($found.Supervisor) | Where-Object { -not $_.OpenWARoot })) {
    Write-Warning "Cannot tell which OpenWA folder PID $($process.ProcessId) ($($process.Name)) runs from: $($process.CommandLine). Ending it as this install's."
}

# Ending node looks like a crash to the supervisor, which would restart it. The marker tells it the
# exit was asked for, so it exits instead - including when it is waiting out a restart delay. Left
# behind when nothing is running, it is harmless: Run-OpenWA.ps1 clears it when it starts.
$logsDir = Join-Path $resolvedRoot 'logs'
New-Item -ItemType Directory -Path $logsDir -Force | Out-Null
Set-Content -LiteralPath (Join-Path $logsDir 'stop-requested') -Value "Stop requested by $env:USERDOMAIN\$env:USERNAME at $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" -Encoding UTF8

$hadSupervisor = $found.Supervisor.Count -gt 0
if ($found.Node.Count -eq 0 -and $found.Supervisor.Count -eq 0 -and $found.Chrome.Count -eq 0) {
    Write-Host "OpenWA is not running from $resolvedRoot."
    return
}

# A Chrome that outlives node keeps its session's profile locked, and every later start of that
# session fails with "The browser is already running". So the stop ends it too.
$endedNodePids = @()
$endedChrome = @()
$deadline = (Get-Date).AddSeconds($SupervisorTimeoutSeconds)
while ($true) {
    foreach ($process in $found.Node) {
        if (Stop-ProcessIfRunning -ProcessId $process.ProcessId) {
            Write-Host "Stopped OpenWA process PID $($process.ProcessId)."
        }
        $endedNodePids += [int]$process.ProcessId
    }

    foreach ($process in $found.Chrome) {
        if (Stop-ProcessIfRunning -ProcessId $process.ProcessId) {
            $endedChrome += $process
        }
    }

    $found = Select-OpenWAProcesses -Snapshot (Get-ProcessSnapshot) -Root $resolvedRoot -EndedNodePids $endedNodePids -ExcludePid $PID
    $remaining = $found.Node.Count + $found.Supervisor.Count + $found.Chrome.Count
    if ($remaining -eq 0 -or (Get-Date) -ge $deadline) {
        break
    }

    Start-Sleep -Milliseconds 500
}

if ($found.Supervisor.Count -gt 0) {
    foreach ($supervisor in $found.Supervisor) {
        if (Stop-ProcessIfRunning -ProcessId $supervisor.ProcessId) {
            Write-Warning "The OpenWA supervisor PID $($supervisor.ProcessId) did not exit within ${SupervisorTimeoutSeconds}s of the stop, so it was ended."
        }
    }

    foreach ($process in $found.Node) {
        if (Stop-ProcessIfRunning -ProcessId $process.ProcessId) {
            Write-Host "Stopped OpenWA process PID $($process.ProcessId)."
        }
        $endedNodePids += [int]$process.ProcessId
    }
    $found = Select-OpenWAProcesses -Snapshot (Get-ProcessSnapshot) -Root $resolvedRoot -EndedNodePids $endedNodePids -ExcludePid $PID
}
elseif ($hadSupervisor) {
    Write-Host 'The OpenWA supervisor exited without restarting node.'
}

if ($endedChrome.Count -gt 0) {
    Write-Host "Ended $($endedChrome.Count) Chrome process(es) of the gateway: PID $(($endedChrome | ForEach-Object { $_.ProcessId }) -join ', ')."
}

if ($found.Chrome.Count -gt 0) {
    Write-Warning "Chrome $(Format-Pids $found.Chrome) is still running ${SupervisorTimeoutSeconds}s after the stop. While it holds a session's profile that session cannot start: end it by hand."
}
