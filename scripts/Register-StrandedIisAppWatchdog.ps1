# Installs Watch-StrandedIisApps.ps1 on the IIS server as a scheduled task that runs every minute as SYSTEM.
#
# Run it on the server itself, as an administrator, from a copy of this scripts folder. It copies the
# watchdog to C:\ProgramData\StrandedIisAppWatchdog so the task does not depend on a repo checkout,
# registers the 'StrandedIisAppWatchdog' event source (recycles show as Application event 1000, failures
# as 1001), and registers the task. -DryRun installs it in report-only mode; re-run without it to arm.
# -Unregister removes the task and leaves the log behind.

[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [string]$TaskName = 'StrandedIisAppWatchdog',
    [string]$InstallDirectory = 'C:\ProgramData\StrandedIisAppWatchdog',
    [switch]$DryRun,
    [switch]$Unregister
)

$ErrorActionPreference = 'Stop'

if ($Unregister) {
    if ($PSCmdlet.ShouldProcess($TaskName, 'Unregister scheduled task')) {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
        Write-Host "Removed scheduled task '$TaskName'. Logs stay in $InstallDirectory."
    }
    return
}

$principalCheck = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principalCheck.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this as an administrator on the IIS server.'
}

$source = Join-Path $PSScriptRoot 'Watch-StrandedIisApps.ps1'
if (-not (Test-Path -LiteralPath $source)) { throw "Watch-StrandedIisApps.ps1 was not found next to this script ($PSScriptRoot)." }

if ($PSCmdlet.ShouldProcess($InstallDirectory, 'Copy the watchdog script')) {
    New-Item -ItemType Directory -Path $InstallDirectory -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination (Join-Path $InstallDirectory 'Watch-StrandedIisApps.ps1') -Force
}

if (-not [System.Diagnostics.EventLog]::SourceExists('StrandedIisAppWatchdog')) {
    if ($PSCmdlet.ShouldProcess('StrandedIisAppWatchdog', 'Register Application event source')) {
        New-EventLog -LogName Application -Source 'StrandedIisAppWatchdog'
    }
}

$arguments = @(
    '-NoProfile',
    '-ExecutionPolicy', 'Bypass',
    '-File', ('"{0}"' -f (Join-Path $InstallDirectory 'Watch-StrandedIisApps.ps1')),
    '-StateDirectory', ('"{0}"' -f $InstallDirectory)
)
if ($DryRun) { $arguments += '-DryRun' }

$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ($arguments -join ' ')
# No RepetitionDuration: Server 2016 and later repeat indefinitely when it is left out.
$every = New-ScheduledTaskTrigger -Once -At (Get-Date).Date -RepetitionInterval (New-TimeSpan -Minutes 1)
$atBoot = New-ScheduledTaskTrigger -AtStartup
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -MultipleInstances IgnoreNew `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 5)
$principal = New-ScheduledTaskPrincipal -UserId 'NT AUTHORITY\SYSTEM' -LogonType ServiceAccount -RunLevel Highest

$mode = if ($DryRun) { 'report-only (-DryRun)' } else { 'armed' }
if ($PSCmdlet.ShouldProcess($TaskName, "Register the watchdog task, $mode")) {
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger @($every, $atBoot) -Settings $settings -Principal $principal `
        -Description 'Recycles an IIS app pool whose in-process ASP.NET Core app shut down inside a worker that never exited (503 0 1255). See scripts/Watch-StrandedIisApps.ps1 in ShopInventory.' -Force | Out-Null
    Write-Host "Registered '$TaskName' every minute as SYSTEM, $mode. Log: $(Join-Path $InstallDirectory 'watchdog.log')"
}

Write-Host "Remove it with: .\Register-StrandedIisAppWatchdog.ps1 -Unregister"
