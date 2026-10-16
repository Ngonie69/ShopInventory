[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Low')]
param(
    # Defaults to the OpenWA folder beside this script's. Resolved below rather than here:
    # Windows PowerShell 5.1 leaves $PSScriptRoot empty in a default value under -File.
    [string]$OpenWARoot,
    [string]$NodeHome = "C:\Users\ngoni\.config\herd\bin\nvm\v20.20.2",
    [string]$ChromePath = "C:\Program Files\Google\Chrome\Application\chrome.exe",
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

if (-not $OpenWARoot) {
    $OpenWARoot = Join-Path $PSScriptRoot "..\OpenWA"
}

$startupFolder = [Environment]::GetFolderPath('Startup')
if ([string]::IsNullOrWhiteSpace($startupFolder)) {
    throw 'Could not resolve the current user Startup folder.'
}

$runScript = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot 'Run-OpenWA.ps1')).Path
$resolvedOpenWARoot = (Resolve-Path -LiteralPath $OpenWARoot).Path
$startupCommandPath = Join-Path $startupFolder 'ShopInventory-OpenWA.cmd'

$skipBuildArg = if ($SkipBuild) { ' -SkipBuild' } else { '' }

$command = @"
@echo off
powershell.exe -WindowStyle Hidden -NoProfile -ExecutionPolicy Bypass -File "$runScript" -OpenWARoot "$resolvedOpenWARoot" -NodeHome "$NodeHome" -ChromePath "$ChromePath"$skipBuildArg
"@

if ($PSCmdlet.ShouldProcess($startupCommandPath, 'Create current-user OpenWA startup entry')) {
    Set-Content -LiteralPath $startupCommandPath -Value $command -Encoding Ascii
    Write-Host "Created Startup entry: $startupCommandPath"
}