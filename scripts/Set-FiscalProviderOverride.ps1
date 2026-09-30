<#
.SYNOPSIS
    Pins the API's fiscal provider in both slots' web.config, or removes the pin.

.DESCRIPTION
    Run on KFL-DNS2 in Windows PowerShell 5.1, as an administrator.

    -Provider Revmax   adds Fiscalisation__Provider=Revmax to ShopInventory-API-Blue and -Green. This is
                       the rollback in docs/operations/revmax-to-platform-cutover.md.
    -Provider Platform removes that override, so appsettings.json (Provider=Platform) applies again. Do
                       this once Fiscalisation__ApiKey is set.

    Both slots are edited because a deploy seeds the new slot from the live slot's web.config, and the
    idle slot's app pool keeps running. Each web.config is backed up next to itself first. The live
    slot's app pool is recycled, and then the script waits for the log's "Fiscalisation provider:" line.

.PARAMETER DryRun
    Show what would change and stop. Nothing is written or recycled.
#>
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Revmax', 'Platform')]
    [string]$Provider,
    [string]$InetpubRoot = 'C:\inetpub',
    [string]$ApiSiteName = 'ShopInventory-API',
    [string]$ApiAppPoolName = 'ShopInventoryAPI',
    [int]$PublicPort = 5106,
    [switch]$DryRun,
    [switch]$NoRecycle
)

$ErrorActionPreference = 'Stop'
$variableName = 'Fiscalisation__Provider'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'

# The live slot is the one whose site holds the public port binding.
$liveSlot = $null
if (-not $NoRecycle) {
    Import-Module WebAdministration
    foreach ($slot in 'Blue', 'Green') {
        $site = "$ApiSiteName-$slot"
        if (Get-WebBinding -Name $site -Port $PublicPort -ErrorAction SilentlyContinue) { $liveSlot = $slot }
    }
    if (-not $liveSlot) { throw "Neither $ApiSiteName-Blue nor $ApiSiteName-Green is bound to port $PublicPort." }
    Write-Host "Live API slot: $liveSlot (bound to :$PublicPort)"
}

foreach ($slot in 'Blue', 'Green') {
    $configPath = Join-Path $InetpubRoot "$ApiSiteName-$slot\web.config"
    if (-not (Test-Path $configPath)) {
        Write-Host "[$slot] no web.config at $configPath - skipped" -ForegroundColor Yellow
        continue
    }

    $xml = New-Object System.Xml.XmlDocument
    $xml.PreserveWhitespace = $true
    $xml.Load($configPath)

    $envVars = $xml.SelectSingleNode('//aspNetCore/environmentVariables')
    if ($null -eq $envVars) { throw "[$slot] $configPath has no aspNetCore/environmentVariables section." }

    $existing = $envVars.SelectSingleNode("environmentVariable[@name='$variableName']")
    $before = if ($existing) { $existing.GetAttribute('value') } else { '(not set: appsettings.json, i.e. Platform)' }

    if ($Provider -eq 'Revmax') {
        if ($existing -and $existing.GetAttribute('value') -eq 'Revmax') {
            Write-Host "[$slot] $variableName is already Revmax - unchanged"
            continue
        }
        $after = 'Revmax'
    } else {
        if (-not $existing) {
            Write-Host "[$slot] no $variableName override - unchanged"
            continue
        }
        $after = '(removed: appsettings.json, i.e. Platform)'
    }

    Write-Host "[$slot] $variableName : $before -> $after"
    if ($DryRun) { continue }

    $backup = "$configPath.before-provider-$stamp"
    Copy-Item $configPath $backup
    Write-Host "[$slot] backed up to $backup"

    if ($Provider -eq 'Revmax') {
        if ($existing) {
            $existing.SetAttribute('value', 'Revmax')
        } else {
            $node = $xml.CreateElement('environmentVariable')
            $node.SetAttribute('name', $variableName)
            $node.SetAttribute('value', 'Revmax')
            [void]$envVars.AppendChild($node)
        }
    } else {
        [void]$envVars.RemoveChild($existing)
    }
    $xml.Save($configPath)

    # Read it back rather than trusting the save.
    [xml]$check = Get-Content $configPath -Raw
    $saved = $check.SelectSingleNode("//aspNetCore/environmentVariables/environmentVariable[@name='$variableName']")
    $savedValue = if ($saved) { $saved.GetAttribute('value') } else { '(not set)' }
    Write-Host "[$slot] web.config now says $variableName = $savedValue" -ForegroundColor Green
}

if ($DryRun) { Write-Host 'Dry run: nothing written.'; return }
if ($NoRecycle) { Write-Host 'NoRecycle: app pools not recycled.'; return }

$pool = "$ApiAppPoolName-$liveSlot"
$recycledAt = Get-Date
Restart-WebAppPool -Name $pool
Write-Host "Recycled $pool at $recycledAt. Waiting for the startup line..."

# Wake the app, then look for a provider line written after the recycle.
$expected = if ($Provider -eq 'Revmax') { 'Fiscalisation provider: REVMax at' } else { 'Fiscalisation provider: the platform at' }
$logDir = Join-Path $InetpubRoot "$ApiSiteName-$liveSlot\logs"
$deadline = (Get-Date).AddSeconds(180)
while ((Get-Date) -lt $deadline) {
    try { Invoke-WebRequest "http://localhost:$PublicPort/health/live" -UseBasicParsing -TimeoutSec 10 | Out-Null } catch { }
    $lines = Get-ChildItem $logDir -Filter 'shopinventory-api-*.log' |
        Where-Object { $_.LastWriteTime -ge $recycledAt } |
        ForEach-Object { Select-String -Path $_.FullName -Pattern 'Fiscalisation provider:' } |
        Where-Object {
            $when = [datetime]::MinValue
            [datetime]::TryParse($_.Line.Substring(0, 23), [ref]$when) -and $when -ge $recycledAt.AddSeconds(-2)
        }
    if ($lines) {
        $line = ($lines | Select-Object -Last 1).Line
        Write-Host $line
        if ($line -like "*$expected*") {
            Write-Host "OK: the live slot now files through $Provider." -ForegroundColor Green
        } else {
            Write-Host "WRONG: expected '$expected'." -ForegroundColor Red
        }
        return
    }
    Start-Sleep -Seconds 5
}
Write-Host "No provider line within 3 minutes. Check $logDir by hand." -ForegroundColor Red
