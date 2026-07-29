[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Low')]
param()

$ErrorActionPreference = 'Stop'

$startupFolder = [Environment]::GetFolderPath('Startup')
if ([string]::IsNullOrWhiteSpace($startupFolder)) {
    throw 'Could not resolve the current user Startup folder.'
}

$startupCommandPath = Join-Path $startupFolder 'ShopInventory-OpenWA.cmd'

if (-not (Test-Path -LiteralPath $startupCommandPath)) {
    Write-Host 'No current-user OpenWA startup entry was found.'
    return
}

if ($PSCmdlet.ShouldProcess($startupCommandPath, 'Remove current-user OpenWA startup entry')) {
    Remove-Item -LiteralPath $startupCommandPath -Force
    Write-Host "Removed Startup entry: $startupCommandPath"
}