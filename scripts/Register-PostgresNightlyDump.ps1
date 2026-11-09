<#
.SYNOPSIS
    Installs the nightly PostgreSQL dump and the weekly restore check as scheduled tasks on the
    database server. Safe to run again: it replaces both tasks and refreshes the copied scripts.

.DESCRIPTION
    Run once on 10.10.10.9 in an elevated Windows PowerShell 5.1 session, from a checkout of this
    repo (or from a folder holding the four scripts).

    It copies Invoke-PostgresNightlyDump.ps1, Test-PostgresDumpRestore.ps1 and
    PostgresBackup.Common.ps1 to -InstallRoot\scripts, so the tasks never depend on where a
    checkout happens to be, and registers under the task folder \ShopInventory\:

      PostgreSQL nightly dump     daily at -DumpAt
      PostgreSQL restore check    weekly on -RestoreCheckDay at -RestoreCheckAt

    The account the tasks run as must be able to read the API site's web.config (an
    administrator can) and write to -Destination. SYSTEM can reach a network share only if the
    share grants this computer's account (KFL-DNS2$) access, so for the NAS pass -Credential with
    an account that can write there. The password goes only to Task Scheduler.

    Use -WhatIf to see what would be registered without changing anything.

.EXAMPLE
    .\Register-PostgresNightlyDump.ps1 -Destination '\\10.10.10.254\svr_backup\ShopInventory-pgdump' -Credential (Get-Credential)

.EXAMPLE
    .\Register-PostgresNightlyDump.ps1 -Destination 'D:\pgdump' -RunAsSystem -WhatIf
#>
[CmdletBinding(SupportsShouldProcess, DefaultParameterSetName = 'Account')]
param(
    [Parameter(Mandatory)]
    [string]$Destination,

    [string[]]$Databases = @('ShopInventory', 'ShopInventoryWeb'),

    [int]$KeepDays = 14,

    # Local server time (CAT on production). Away from the 19:30 deploy window, and before the
    # 07:00 van stock read.
    [string]$DumpAt = '01:30',

    [ValidateSet('Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday')]
    [string]$RestoreCheckDay = 'Sunday',
    [string]$RestoreCheckAt = '03:30',

    [string]$InstallRoot = 'C:\ShopInventoryBackups',

    [Parameter(Mandatory, ParameterSetName = 'Account')]
    [pscredential]$Credential,

    [Parameter(Mandatory, ParameterSetName = 'System')]
    [switch]$RunAsSystem
)

$ErrorActionPreference = 'Stop'
$taskPath = '\ShopInventory\'
$scriptsTarget = Join-Path $InstallRoot 'scripts'
$files = @('PostgresBackup.Common.ps1', 'Invoke-PostgresNightlyDump.ps1', 'Test-PostgresDumpRestore.ps1')

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin -and -not $WhatIfPreference) {
    throw 'Run this from an elevated Windows PowerShell session: registering tasks that run whether or not anyone is signed in needs it.'
}

foreach ($file in $files) {
    if (-not (Test-Path (Join-Path $PSScriptRoot $file))) { throw "$file is missing beside this script." }
}

function Format-Argument([string]$value) { '"' + $value.Replace('"', '\"') + '"' }

$dumpArguments = @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass',
    '-File', (Format-Argument (Join-Path $scriptsTarget 'Invoke-PostgresNightlyDump.ps1')),
    '-Destination', (Format-Argument $Destination),
    '-Databases', (($Databases | ForEach-Object { Format-Argument $_ }) -join ','),
    '-KeepDays', $KeepDays,
    '-StagingRoot', (Format-Argument (Join-Path $InstallRoot 'staging')),
    '-LogRoot', (Format-Argument (Join-Path $InstallRoot 'logs'))
) -join ' '

$checkArguments = @(
    '-NoProfile', '-ExecutionPolicy', 'Bypass',
    '-File', (Format-Argument (Join-Path $scriptsTarget 'Test-PostgresDumpRestore.ps1')),
    '-Root', (Format-Argument $Destination),
    '-LogRoot', (Format-Argument (Join-Path $InstallRoot 'logs'))
) -join ' '

$powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$runAs = if ($RunAsSystem) { 'SYSTEM' } else { $Credential.UserName }

Write-Host "Install root : $InstallRoot"
Write-Host "Destination  : $Destination"
Write-Host "Databases    : $($Databases -join ', ')   (keep $KeepDays days)"
Write-Host "Runs as      : $runAs"
Write-Host ''
Write-Host "Task $($taskPath)PostgreSQL nightly dump   daily at $DumpAt"
Write-Host "  $powershell $dumpArguments"
Write-Host "Task $($taskPath)PostgreSQL restore check  $RestoreCheckDay at $RestoreCheckAt"
Write-Host "  $powershell $checkArguments"
Write-Host ''

if ($PSCmdlet.ShouldProcess($scriptsTarget, 'Copy the backup scripts')) {
    New-Item -ItemType Directory -Force -Path $scriptsTarget, (Join-Path $InstallRoot 'staging'), (Join-Path $InstallRoot 'logs') | Out-Null
    foreach ($file in $files) { Copy-Item -Path (Join-Path $PSScriptRoot $file) -Destination (Join-Path $scriptsTarget $file) -Force }
    # The folder holds dumps of every customer and sale: administrators and the task account only.
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $principals = @('BUILTIN\Administrators', 'NT AUTHORITY\SYSTEM')
    if (-not $RunAsSystem) { $principals += $Credential.UserName }
    foreach ($principal in $principals) {
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($principal, 'FullControl', $inherit, 'None', 'Allow')))
    }
    Set-Acl -Path $InstallRoot -AclObject $acl
    Write-Host "Copied scripts to $scriptsTarget; $InstallRoot is readable by $($principals -join ', ') only."
}

$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew `
    -ExecutionTimeLimit (New-TimeSpan -Hours 6) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries
$tasks = @(
    @{ Name = 'PostgreSQL nightly dump'; Arguments = $dumpArguments; Trigger = (New-ScheduledTaskTrigger -Daily -At $DumpAt)
       Description = 'Dumps the ShopInventory databases and copies them off the server. docs/operations/disaster-recovery.md' },
    @{ Name = 'PostgreSQL restore check'; Arguments = $checkArguments; Trigger = (New-ScheduledTaskTrigger -Weekly -DaysOfWeek $RestoreCheckDay -At $RestoreCheckAt)
       Description = 'Restores the newest dump into restorecheck_* databases, compares row counts with live, drops them.' }
)

foreach ($task in $tasks) {
    if (-not $PSCmdlet.ShouldProcess("$taskPath$($task.Name)", 'Register scheduled task')) { continue }
    $action = New-ScheduledTaskAction -Execute $powershell -Argument $task.Arguments -WorkingDirectory $scriptsTarget
    if ($RunAsSystem) {
        $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
        Register-ScheduledTask -TaskName $task.Name -TaskPath $taskPath -Action $action -Trigger $task.Trigger `
            -Settings $settings -Principal $principal -Description $task.Description -Force | Out-Null
    } else {
        Register-ScheduledTask -TaskName $task.Name -TaskPath $taskPath -Action $action -Trigger $task.Trigger `
            -Settings $settings -User $Credential.UserName -Password $Credential.GetNetworkCredential().Password `
            -RunLevel Highest -Description $task.Description -Force | Out-Null
    }
    Write-Host "Registered $taskPath$($task.Name)"
}

Write-Host ''
Write-Host 'Next: run both once now and read the results, rather than waiting for tonight.'
Write-Host "  Start-ScheduledTask -TaskPath '$taskPath' -TaskName 'PostgreSQL nightly dump'"
Write-Host "  Get-Content '$(Join-Path $InstallRoot 'staging\last-run.json')'"
Write-Host "  Start-ScheduledTask -TaskPath '$taskPath' -TaskName 'PostgreSQL restore check'   # after the dump finishes"
Write-Host "  Get-Content '$(Join-Path $Destination 'last-restore-check.json')'"
