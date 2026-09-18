#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Gives TransferEventListener its own ShopInventory API key, or rotates it, and proves the API
    accepts it.

.DESCRIPTION
    The listener posts every stock-transfer line it finds in SAP to the API's
    /api/DesktopIntegration/webhook/transfer-event with an X-API-Key header. If the API does not
    recognise that key it answers 401, the listener keeps the line queued, and the movement never
    reaches local stock or the tills. /health/dependencies then reports transfer-listener Unhealthy
    with "Last answer: HTTP 401".

    This script, run on the API node the listener posts to (10.10.10.9):

      1. Mints a new random key. The value is never printed.
      2. Writes it into every ShopInventory API web.config on this box - the live site and the
         blue/green slots - as a Security:ApiKeys entry named TransferEventListener with role
         ApiUser (enough for the "ApiAccess" policy on the webhook, and nothing more) and an
         ExpiresAt no more than 90 days out, which AuthService requires.
      3. Writes the same value into the listener's web.config as DesktopIntegration__ApiKey.
      4. Waits for the API to come back, then POSTs an empty line to the webhook with the key:
         400 means the key was accepted and the empty line refused by validation (nothing is
         written); 401 means it was not.
      5. Watches the listener's /api/Transfer/health until its queued lines start to drain.

    Saving a web.config restarts that app, so the API restarts once (seconds) and so does the
    listener. The listener keeps its queued lines across a restart in its state file.

    ASP.NET Core binds configuration arrays by position, so the entry lives at a fixed index. The
    script reuses the index already named TransferEventListener in either appsettings.json or
    web.config, and otherwise takes the first index past both - never one appsettings.json already
    uses for another key, which would silently merge the two.

    Re-run it to rotate the key before it expires. The api-keys check on /health/dependencies goes
    Degraded 14 days before ExpiresAt.

    web.config rather than appsettings.json: every publish overwrites appsettings.json, while each
    deployment seeds the new slot's web.config from the live one.

.PARAMETER LifetimeDays
    Days until the key expires. AuthService rejects nothing longer than 90.

.EXAMPLE
    .\scripts\Set-TransferListenerApiKey.ps1

.EXAMPLE
    .\scripts\Set-TransferListenerApiKey.ps1 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string[]]$ApiSiteNames = @('ShopInventory-API', 'ShopInventory-API-Blue', 'ShopInventory-API-Green'),
    [string]$ListenerSiteName = 'TransferEventListener',
    [string]$KeyName = 'TransferEventListener',
    [ValidateRange(1, 90)][int]$LifetimeDays = 90,
    [int]$ApiPort = 5106,
    [int]$ListenerPort = 5050,
    [int]$DrainWaitSeconds = 300
)

$ErrorActionPreference = 'Stop'
Import-Module WebAdministration -ErrorAction Stop

$EnvironmentVariablesXPath = '/configuration/location/system.webServer/aspNetCore/environmentVariables'

function Get-SiteWebConfigPath {
    param([string]$Name)

    $site = Get-Item "IIS:\Sites\$Name" -ErrorAction SilentlyContinue
    if ($null -eq $site) { return $null }

    $physicalPath = [Environment]::ExpandEnvironmentVariables([string]$site.physicalPath)
    $path = Join-Path $physicalPath 'web.config'
    if (Test-Path -LiteralPath $path) { return $path }
    return $null
}

function Get-AspNetCoreNode {
    param([xml]$Config)

    # The API's web.config nests under <location>; a plain publish (the listener's) does not.
    foreach ($xpath in @('/configuration/location/system.webServer/aspNetCore', '/configuration/system.webServer/aspNetCore')) {
        $node = $Config.SelectSingleNode($xpath)
        if ($null -ne $node) { return $node }
    }

    throw 'web.config has no system.webServer/aspNetCore element.'
}

function Get-EnvironmentVariables {
    param([xml]$Config)

    $values = @{}
    $aspNetCore = Get-AspNetCoreNode -Config $Config
    foreach ($node in $aspNetCore.SelectNodes('environmentVariables/environmentVariable')) {
        $values[$node.GetAttribute('name')] = $node.GetAttribute('value')
    }

    return $values
}

function Set-EnvironmentVariable {
    param([xml]$Config, [string]$Name, [string]$Value)

    $aspNetCore = Get-AspNetCoreNode -Config $Config
    $variables = $aspNetCore.SelectSingleNode('environmentVariables')
    if ($null -eq $variables) {
        $variables = $Config.CreateElement('environmentVariables')
        [void]$aspNetCore.AppendChild($variables)
    }

    $node = $variables.SelectSingleNode("environmentVariable[@name='$Name']")
    if ($null -eq $node) {
        $node = $Config.CreateElement('environmentVariable')
        $node.SetAttribute('name', $Name)
        [void]$variables.AppendChild($node)
    }

    $node.SetAttribute('value', $Value)
}

function Save-WebConfig {
    param([xml]$Config, [string]$Path)

    $backupPath = "$Path.bak-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
    Copy-Item -LiteralPath $Path -Destination $backupPath -Force
    $Config.Save($Path)
    Write-Host "  Updated $Path" -ForegroundColor Green
    Write-Host "  Backup  $backupPath" -ForegroundColor DarkGray
}

# ── Where the API runs on this box ─────────────────────────────────────────────────────────────
$apiConfigs = @()
foreach ($siteName in $ApiSiteNames) {
    $path = Get-SiteWebConfigPath -Name $siteName
    if ($null -eq $path -or ($apiConfigs.Path -contains $path)) { continue }

    [xml]$config = Get-Content -LiteralPath $path -Raw
    $arguments = (Get-AspNetCoreNode -Config $config).GetAttribute('arguments')
    if ($arguments -notlike '*ShopInventory.dll*') {
        Write-Warning "Skipping $siteName ($path): it does not run ShopInventory.dll."
        continue
    }

    $appSettingsNames = @()
    $appSettingsPath = Join-Path (Split-Path $path) 'appsettings.json'
    if (Test-Path -LiteralPath $appSettingsPath) {
        $appSettingsNames = @((Get-Content -LiteralPath $appSettingsPath -Raw | ConvertFrom-Json).Security.ApiKeys | ForEach-Object { [string]$_.Name })
    }

    $apiConfigs += [pscustomobject]@{
        Site             = $siteName
        Path             = $path
        Config           = $config
        Variables        = Get-EnvironmentVariables -Config $config
        AppSettingsNames = $appSettingsNames
    }
}

if ($apiConfigs.Count -eq 0) {
    throw "No ShopInventory API web.config found under the IIS sites: $($ApiSiteNames -join ', ')."
}

$listenerPath = Get-SiteWebConfigPath -Name $ListenerSiteName
if ($null -eq $listenerPath) {
    throw "No web.config for the IIS site '$ListenerSiteName'. Run this on the box that hosts TransferEventListener."
}
[xml]$listenerConfig = Get-Content -LiteralPath $listenerPath -Raw

# ── Which Security:ApiKeys index the entry takes ───────────────────────────────────────────────
$index = $null
$nextFree = 0
foreach ($api in $apiConfigs) {
    for ($i = 0; $i -lt $api.AppSettingsNames.Count; $i++) {
        if ($api.AppSettingsNames[$i] -eq $KeyName) { $index = $i }
    }
    $nextFree = [Math]::Max($nextFree, $api.AppSettingsNames.Count)

    foreach ($name in $api.Variables.Keys) {
        if ($name -match '^Security__ApiKeys__(\d+)__') {
            $n = [int]$Matches[1]
            $nextFree = [Math]::Max($nextFree, $n + 1)
            if ($name -eq "Security__ApiKeys__${n}__Name" -and $api.Variables[$name] -eq $KeyName) { $index = $n }
        }
    }
}

if ($null -eq $index) { $index = $nextFree }

foreach ($api in $apiConfigs) {
    $taken = $api.Variables["Security__ApiKeys__${index}__Name"]
    $appSettingsName = if ($index -lt $api.AppSettingsNames.Count) { $api.AppSettingsNames[$index] } else { $null }
    foreach ($name in @($taken, $appSettingsName)) {
        if (-not [string]::IsNullOrWhiteSpace($name) -and $name -ne $KeyName) {
            throw "Security:ApiKeys index $index already belongs to '$name' in $($api.Path). Refusing to overwrite it."
        }
    }
}

# ── The key ────────────────────────────────────────────────────────────────────────────────────
# RandomNumberGenerator.Fill is .NET Core only; the server's Windows PowerShell 5.1 runs on .NET Framework.
$bytes = New-Object byte[] 32
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
$key = -join ($bytes | ForEach-Object { $_.ToString('x2') })
$expiresAt = [DateTime]::UtcNow.Date.AddDays($LifetimeDays).AddSeconds(-1).ToString("yyyy-MM-ddTHH:mm:ss'Z'")

Write-Host "Key name   $KeyName (Security:ApiKeys index $index, role ApiUser)" -ForegroundColor Cyan
Write-Host "Expires    $expiresAt" -ForegroundColor Cyan
Write-Host "API        $(@($apiConfigs | ForEach-Object Site) -join ', ')" -ForegroundColor Cyan
Write-Host "Listener   $listenerPath" -ForegroundColor Cyan

if (-not $PSCmdlet.ShouldProcess("$($apiConfigs.Count) API web.config file(s) and $listenerPath", 'Write the TransferEventListener API key')) {
    return
}

# The API first, so the listener - which restarts when its web.config is saved - comes up with a
# key the API already accepts.
foreach ($api in $apiConfigs) {
    Set-EnvironmentVariable -Config $api.Config -Name "Security__ApiKeys__${index}__Name" -Value $KeyName
    Set-EnvironmentVariable -Config $api.Config -Name "Security__ApiKeys__${index}__Key" -Value $key
    Set-EnvironmentVariable -Config $api.Config -Name "Security__ApiKeys__${index}__Roles__0" -Value 'ApiUser'
    Set-EnvironmentVariable -Config $api.Config -Name "Security__ApiKeys__${index}__IsActive" -Value 'true'
    Set-EnvironmentVariable -Config $api.Config -Name "Security__ApiKeys__${index}__ExpiresAt" -Value $expiresAt
    Save-WebConfig -Config $api.Config -Path $api.Path
}

# ── Proof: the API accepts the key ─────────────────────────────────────────────────────────────
$webhookUrl = "http://localhost:$ApiPort/api/desktopintegration/webhook/transfer-event"
$status = $null
$deadline = (Get-Date).AddSeconds(120)
while ((Get-Date) -lt $deadline) {
    try {
        $response = Invoke-WebRequest -Uri $webhookUrl -Method Post -Body '{}' -ContentType 'application/json' `
            -Headers @{ 'X-API-Key' = $key } -UseBasicParsing -TimeoutSec 15
        $status = [int]$response.StatusCode
    }
    catch {
        $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { $null }
    }

    # 400 is the answer we want: authenticated, and the empty line refused by validation.
    if ($status -eq 400 -or $status -eq 401 -or $status -eq 403) { break }
    Start-Sleep -Seconds 3
}

switch ($status) {
    400 { Write-Host "API accepts the key: an empty line to the webhook was refused by validation (400), not by auth." -ForegroundColor Green }
    401 { throw "The API still answers 401 with the new key. Check that $($apiConfigs[0].Site) is the site on port $ApiPort, and read the API log for 'Invalid API key'." }
    403 { throw 'The API authenticated the key but refused the role (403). The webhook needs a role in ApplicationRoles.ApiAccessRoles.' }
    default { throw "The API did not answer on $webhookUrl within two minutes (last status: $status). The key is written; check the site is running." }
}

$switchedAtUtc = [DateTime]::UtcNow
Set-EnvironmentVariable -Config $listenerConfig -Name 'DesktopIntegration__ApiKey' -Value $key
Save-WebConfig -Config $listenerConfig -Path $listenerPath

# ── Proof: the listener's queue drains ─────────────────────────────────────────────────────────
# Judged by a delivery after the switch, not by lastDeliveryError, which can keep the old 401 text
# after deliveries resume. The listener replays its queue once per poll cycle (120 s).
$healthUrl = "http://localhost:$ListenerPort/api/Transfer/health"
$first = $null
$deadline = (Get-Date).AddSeconds($DrainWaitSeconds)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 15
    try { $health = Invoke-RestMethod -Uri $healthUrl -TimeoutSec 15 } catch { continue }

    $poll = $health.poll
    $pending = [int]$poll.pendingNotifications
    if ($null -eq $first) { $first = $pending }
    Write-Host ("  pending {0,5}   lastDelivered {1}   lastDeliveryError {2}" -f $pending, $poll.lastDeliveredUtc, $poll.lastDeliveryError) -ForegroundColor DarkGray

    $deliveredSinceSwitch = $poll.lastDeliveredUtc -and ([DateTime]$poll.lastDeliveredUtc).ToUniversalTime() -gt $switchedAtUtc
    if ($deliveredSinceSwitch) {
        Write-Host "Listener is delivering again: $pending line(s) still queued (was $first)." -ForegroundColor Green
        return
    }
}

Write-Warning "The listener did not show its queue draining within $DrainWaitSeconds s. Read $healthUrl - its lastDeliveryError says why."
