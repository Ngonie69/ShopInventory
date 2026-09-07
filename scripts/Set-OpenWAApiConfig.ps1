#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Points the ShopInventory API at the OpenWA gateway, by writing OpenWA__* into its web.config.

.DESCRIPTION
    The API reads these four settings, and the WhatsApp console is dead without all four:

      OpenWA__Enabled           false is what makes the console report "WhatsApp integration is
                                disabled" for every operator action.
      OpenWA__BaseUrl           where the gateway is. One gateway serves both API nodes, so this is
                                the same 10.x address on both - never localhost on one of them.
      OpenWA__ApiKey            an OpenWA key with at least the operator role.
      OpenWA__WebhookSecret     the HMAC secret OpenWA signs deliveries with. It MUST be identical
                                on both nodes: the load balancer sends a delivery to either one, and
                                the node that gets it verifies the signature with its own copy.
      OpenWA__WebhookPublicUrl  where OpenWA should deliver to. Derived per node from the node's own
                                address unless -WebhookPublicUrl is given.

    Run this on each API node. It writes the live slot and, with -IncludeSlots, the blue and green
    slots too - which is what stops the next deployment cutting over to a slot that has never been
    told about the gateway.

    Values are stored in web.config rather than appsettings.json for the same reason the SMTP
    password is: appsettings.json is overwritten by every publish, and web.config is not.

.PARAMETER OpenWaBaseUrl
    The gateway, e.g. http://10.10.10.9:2785.

.PARAMETER OpenWaApiKey
    From data\.api-key on the OpenWA host, or minted from its dashboard.

.PARAMETER WebhookSecret
    Any high-entropy string. The same value on every node. Changing it here is not enough on its
    own - the sessions already registered with OpenWA still carry the old one, so restart each
    session (or press Repair delivery) afterwards and re-run Test-WhatsAppDeliveryPath.ps1.

.PARAMETER WebhookPublicUrl
    Overrides the derived value. OpenWA validates this with class-validator's IsUrl, which rejects
    the bare hostname "localhost" - use 127.0.0.1 or the node's address.

.PARAMETER Disable
    Writes OpenWA__Enabled=false and leaves everything else alone. The kill switch: the console
    then refuses operator actions and says why, rather than timing out against a dead gateway.

.EXAMPLE
    .\scripts\Set-OpenWAApiConfig.ps1 -OpenWaBaseUrl 'http://10.10.10.9:2785' `
        -OpenWaApiKey 'owa_k1_...' -WebhookSecret '...' -IncludeSlots -RestartAppPool

.EXAMPLE
    .\scripts\Set-OpenWAApiConfig.ps1 -Disable -IncludeSlots -RestartAppPool
#>
[CmdletBinding(DefaultParameterSetName = 'Enable')]
param(
    [Parameter(ParameterSetName = 'Enable', Mandatory = $true)][string]$OpenWaBaseUrl,
    [Parameter(ParameterSetName = 'Enable', Mandatory = $true)][string]$OpenWaApiKey,
    [Parameter(ParameterSetName = 'Enable', Mandatory = $true)][string]$WebhookSecret,
    [Parameter(ParameterSetName = 'Enable')][string]$WebhookPublicUrl,
    [Parameter(ParameterSetName = 'Disable', Mandatory = $true)][switch]$Disable,

    [string]$SiteName = 'ShopInventory-API',
    [string]$AppPoolName = 'ShopInventoryAPI',
    [string]$WebConfigPath,
    [int]$ApiPort = 5106,
    [switch]$IncludeSlots,
    [switch]$RestartAppPool
)

$ErrorActionPreference = 'Stop'

$hasWebAdministration = $false
try {
    Import-Module WebAdministration -ErrorAction Stop
    $hasWebAdministration = $true
}
catch {
    Write-Warning "WebAdministration module is not available. Falling back to direct C:\inetpub paths and appcmd."
}

function Get-SiteWebConfigPath {
    param([string]$Name)

    if (-not $hasWebAdministration) {
        $path = "C:\inetpub\$Name\web.config"
        if (Test-Path $path) { return $path }
        throw "WebAdministration is unavailable and the fallback path '$path' does not exist. Re-run with -WebConfigPath."
    }

    $site = Get-Item "IIS:\Sites\$Name" -ErrorAction Stop
    $physicalPath = [Environment]::ExpandEnvironmentVariables([string]$site.physicalPath)
    if (-not [System.IO.Path]::IsPathRooted($physicalPath)) {
        throw "Site '$Name' has a non-rooted physical path: $physicalPath"
    }

    return Join-Path $physicalPath 'web.config'
}

function Set-WebConfigEnvironmentVariable {
    param([xml]$Config, [string]$Name, [string]$Value)

    $aspNetCoreNode = $Config.SelectSingleNode('/configuration/location/system.webServer/aspNetCore')
    if ($null -eq $aspNetCoreNode) {
        throw 'web.config is missing /configuration/location/system.webServer/aspNetCore.'
    }

    $environmentVariablesNode = $Config.SelectSingleNode('/configuration/location/system.webServer/aspNetCore/environmentVariables')
    if ($null -eq $environmentVariablesNode) {
        $environmentVariablesNode = $Config.CreateElement('environmentVariables')
        [void]$aspNetCoreNode.AppendChild($environmentVariablesNode)
    }

    $node = $Config.SelectSingleNode("/configuration/location/system.webServer/aspNetCore/environmentVariables/environmentVariable[@name='$Name']")
    if ($null -eq $node) {
        $node = $Config.CreateElement('environmentVariable')

        $nameAttribute = $Config.CreateAttribute('name')
        $nameAttribute.Value = $Name
        [void]$node.Attributes.Append($nameAttribute)

        $valueAttribute = $Config.CreateAttribute('value')
        $valueAttribute.Value = $Value
        [void]$node.Attributes.Append($valueAttribute)

        [void]$environmentVariablesNode.AppendChild($node)
        return
    }

    $node.SetAttribute('value', $Value)
}

function Get-NodeAddress {
    $candidate = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -like '10.*' } |
        Select-Object -First 1

    if ($candidate) { return $candidate.IPAddress }
    return '127.0.0.1'
}

function Update-WebConfig {
    param([string]$Path)

    if (-not (Test-Path $Path)) {
        throw "web.config not found at $Path"
    }

    [xml]$config = Get-Content $Path
    $aspNetCore = $config.SelectSingleNode('/configuration/location/system.webServer/aspNetCore')
    $arguments = if ($aspNetCore) { $aspNetCore.GetAttribute('arguments') } else { '' }

    # The API and the Web are different applications with the same web.config shape, and pointing
    # the Web at OpenWA would do nothing at all while looking like it had worked.
    if ($arguments -notlike '*ShopInventory.dll*') {
        Write-Warning "Skipping '$Path': it does not run ShopInventory.dll. arguments='$arguments'"
        return $false
    }

    if ($Disable) {
        Set-WebConfigEnvironmentVariable -Config $config -Name 'OpenWA__Enabled' -Value 'false'
    }
    else {
        Set-WebConfigEnvironmentVariable -Config $config -Name 'OpenWA__Enabled' -Value 'true'
        Set-WebConfigEnvironmentVariable -Config $config -Name 'OpenWA__BaseUrl' -Value $OpenWaBaseUrl
        Set-WebConfigEnvironmentVariable -Config $config -Name 'OpenWA__ApiKey' -Value $OpenWaApiKey
        Set-WebConfigEnvironmentVariable -Config $config -Name 'OpenWA__WebhookSecret' -Value $WebhookSecret
        Set-WebConfigEnvironmentVariable -Config $config -Name 'OpenWA__WebhookPublicUrl' -Value $script:ResolvedWebhookUrl
    }

    $backupPath = "$Path.bak-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
    Copy-Item $Path $backupPath -Force
    $config.Save($Path)

    Write-Host "Updated $Path" -ForegroundColor Green
    Write-Host "Backup: $backupPath" -ForegroundColor DarkGray
    return $true
}

if (-not $Disable) {
    if ([string]::IsNullOrWhiteSpace($WebhookPublicUrl)) {
        $script:ResolvedWebhookUrl = "http://$(Get-NodeAddress):$ApiPort/api/whatsapp/webhook/openwa"
    }
    else {
        $script:ResolvedWebhookUrl = $WebhookPublicUrl
    }

    $parsed = $null
    if (-not [Uri]::TryCreate($script:ResolvedWebhookUrl, [UriKind]::Absolute, [ref]$parsed)) {
        throw "The webhook URL '$($script:ResolvedWebhookUrl)' is not an absolute URL."
    }

    if ($parsed.Host -eq 'localhost') {
        throw @"
OpenWA refuses webhook URLs on 'localhost' - its validator rejects the bare hostname. Use
127.0.0.1 or this node's address instead:

    -WebhookPublicUrl 'http://$(Get-NodeAddress):$ApiPort/api/whatsapp/webhook/openwa'
"@
    }

    Write-Host "Gateway  $OpenWaBaseUrl" -ForegroundColor Cyan
    Write-Host "Webhook  $($script:ResolvedWebhookUrl)" -ForegroundColor Cyan
}
else {
    Write-Host "Disabling the WhatsApp integration on this node." -ForegroundColor Yellow
}

$paths = New-Object System.Collections.Generic.List[string]
if (-not [string]::IsNullOrWhiteSpace($WebConfigPath)) {
    $paths.Add($WebConfigPath)
}
else {
    $paths.Add((Get-SiteWebConfigPath -Name $SiteName))

    if ($IncludeSlots) {
        foreach ($slotName in @("$SiteName-Blue", "$SiteName-Green")) {
            if (-not $hasWebAdministration) {
                $slotPath = "C:\inetpub\$slotName\web.config"
                if (Test-Path $slotPath) { $paths.Add($slotPath) }
                continue
            }

            if (Test-Path "IIS:\Sites\$slotName") {
                $paths.Add((Get-SiteWebConfigPath -Name $slotName))
            }
        }
    }
}

$updatedAny = $false
foreach ($path in $paths | Select-Object -Unique) {
    $updatedAny = (Update-WebConfig -Path $path) -or $updatedAny
}

if (-not $updatedAny) {
    throw "No web.config was updated. Check -SiteName and -WebConfigPath."
}

if ($RestartAppPool) {
    $poolNames = New-Object System.Collections.Generic.List[string]
    $poolNames.Add($AppPoolName)
    if ($IncludeSlots) {
        $poolNames.Add("$AppPoolName-Blue")
        $poolNames.Add("$AppPoolName-Green")
    }

    foreach ($pool in $poolNames | Select-Object -Unique) {
        try {
            if ($hasWebAdministration) {
                if (Test-Path "IIS:\AppPools\$pool") {
                    Restart-WebAppPool -Name $pool
                    Write-Host "Restarted app pool $pool" -ForegroundColor Green
                }
            }
            else {
                $appcmd = Join-Path $env:SystemRoot 'System32\inetsrv\appcmd.exe'
                & $appcmd recycle apppool /apppool.name:$pool | Out-Host
                Write-Host "Recycled app pool $pool" -ForegroundColor Green
            }
        }
        catch {
            Write-Warning "Could not restart '$pool': $($_.Exception.Message)"
        }
    }
}
else {
    Write-Host "Restart the ShopInventory API app pool for the changes to take effect." -ForegroundColor Yellow
}

if (-not $Disable) {
    Write-Host ""
    Write-Host "Then confirm the whole path with scripts/Test-WhatsAppDeliveryPath.ps1 - the console" -ForegroundColor White
    Write-Host "showing a green gateway does not by itself mean a message would reach the inbox." -ForegroundColor White
}
