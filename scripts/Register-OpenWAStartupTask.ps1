[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [string]$TaskName = 'ShopInventory-OpenWA',
    [string]$OpenWARoot = (Join-Path $PSScriptRoot "..\OpenWA"),
    [string]$NodeHome = "C:\Users\ngoni\.config\herd\bin\nvm\v20.20.2",
    [string]$ChromePath = "C:\Program Files\Google\Chrome\Application\chrome.exe",
    [switch]$SkipBuild,
    [switch]$StartNow
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command Register-ScheduledTask -ErrorAction SilentlyContinue)) {
    throw 'The ScheduledTasks module is not available on this machine.'
}

$runScript = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot 'Run-OpenWA.ps1')).Path
$resolvedOpenWARoot = (Resolve-Path -LiteralPath $OpenWARoot).Path

$arguments = @(
    '-NoProfile',
    '-ExecutionPolicy', 'Bypass',
    '-File', ('"{0}"' -f $runScript),
    '-OpenWARoot', ('"{0}"' -f $resolvedOpenWARoot),
    '-NodeHome', ('"{0}"' -f $NodeHome),
    '-ChromePath', ('"{0}"' -f $ChromePath)
)

if ($SkipBuild) {
    $arguments += '-SkipBuild'
}

$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ($arguments -join ' ')
$trigger = New-ScheduledTaskTrigger -AtStartup
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -ExecutionTimeLimit (New-TimeSpan -Days 3650)
$principal = New-ScheduledTaskPrincipal -UserId 'NT AUTHORITY\SYSTEM' -LogonType ServiceAccount -RunLevel Highest

if ($PSCmdlet.ShouldProcess($TaskName, 'Register OpenWA startup task')) {
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null
    Write-Host "Registered scheduled task '$TaskName'."
}

if ($StartNow -and $PSCmdlet.ShouldProcess($TaskName, 'Start OpenWA startup task')) {
    Start-ScheduledTask -TaskName $TaskName
    Write-Host "Started scheduled task '$TaskName'."
}

Write-Host "Run Unregister-ScheduledTask -TaskName '$TaskName' -Confirm:`$false to remove it later."