#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Pins the SAP Service Layer certificate thumbprints in every ShopInventory API web.config on this
    box, and shows which one SAP is serving right now.

.DESCRIPTION
    SAP's Service Layer certificate is self-signed, so the API only accepts it if its thumbprint is
    listed in SAP:AllowedServerCertificateThumbprints. When SAP starts serving a different
    certificate, every SAP call fails in the TLS handshake with "The remote certificate was rejected
    by the provided RemoteCertificateValidationCallback". The /sap page shows that as a credentials
    problem.

    Each Service Layer build brings its own certificate. The 2026-09-20 upgrade to 1000331 served
    7DFDCCEA...; the 2026-09-23 rollback to 1000250 probably brought back 7D78CEB7.... The setting
    is a list, and the API accepts a certificate if its thumbprint matches any entry. Pinning both
    means a switch between the builds is not an outage.

    This script, run on an API node:

      1. Reads the certificate SAP serves now (host and port from SAP__ServiceLayerUrl) and says
         which pinned thumbprint it matches. If it matches none, the script warns and prints the
         live thumbprint, and still writes the list.
      2. Replaces every SAP__AllowedServerCertificateThumbprints__N entry in each API web.config
         (the live site and the blue and green slots) with -Thumbprints, in that order. A file that
         already holds exactly that list and URL is left alone, so it does not recycle.
      3. Sets SAP__ServiceLayerUrl to -ServiceLayerUrl in the same files.
      4. Waits for the API to come back, then prints /health/dependencies, whose "sap" entry makes a
         real call to SAP.

    Saving a web.config restarts that app (seconds). Every file is backed up next to itself first.

    It writes to web.config and not appsettings.json because every publish overwrites
    appsettings.json, and each deployment copies the new slot's web.config from the live one.

.PARAMETER Thumbprints
    The full list to pin. Spaces are removed and the values are uppercased.

.PARAMETER ServiceLayerUrl
    Written to SAP__ServiceLayerUrl in every API web.config, so that the live site and both slots
    talk to the same Service Layer. The app is written against /b1s/v1/. On 2026-09-23 the blue and
    green slots had drifted to /b1s/v2/.

.EXAMPLE
    .\scripts\Set-SapCertThumbprints.ps1 -WhatIf

.EXAMPLE
    .\scripts\Set-SapCertThumbprints.ps1
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string[]]$Thumbprints = @(
        '7DFDCCEA849362E27527623CB05085AFF6C2722C', # 1000331 cert, served from 2026-09-20, valid to 2028-12-11
        '7D78CEB79F65851C8EA03AF0C1F8546B12EF5A3D'  # the cert served before the 2026-09-20 upgrade
    ),
    [string]$ServiceLayerUrl = 'https://10.10.10.6:50000/b1s/v1/',
    [string[]]$ApiSiteNames = @('ShopInventory-API', 'ShopInventory-API-Blue', 'ShopInventory-API-Green'),
    [int]$ApiPort = 5106
)

$ErrorActionPreference = 'Stop'
Import-Module WebAdministration -ErrorAction Stop

$SettingPrefix = 'SAP__AllowedServerCertificateThumbprints__'
$AspNetCoreXPath = '/configuration/location/system.webServer/aspNetCore'

$pins = @($Thumbprints | ForEach-Object { ($_ -replace '\s', '').ToUpperInvariant() } | Where-Object { $_ } | Select-Object -Unique)
foreach ($pin in $pins) {
    if ($pin -notmatch '^[0-9A-F]{40}$') { throw "'$pin' is not a SHA-1 thumbprint (40 hex characters)." }
}
if ($pins.Count -eq 0) { throw 'Pass at least one thumbprint.' }

function Get-SiteWebConfigPath {
    param([string]$Name)

    $site = Get-Item "IIS:\Sites\$Name" -ErrorAction SilentlyContinue
    if ($null -eq $site) { return $null }

    $physicalPath = [Environment]::ExpandEnvironmentVariables([string]$site.physicalPath)
    $path = Join-Path $physicalPath 'web.config'
    if (Test-Path -LiteralPath $path) { return $path }
    return $null
}

function Get-EnvironmentVariablesNode {
    param([xml]$Config)

    $aspNetCore = $Config.SelectSingleNode($AspNetCoreXPath)
    if ($null -eq $aspNetCore) { throw 'web.config is missing /configuration/location/system.webServer/aspNetCore.' }

    $variables = $aspNetCore.SelectSingleNode('environmentVariables')
    if ($null -eq $variables) {
        $variables = $Config.CreateElement('environmentVariables')
        [void]$aspNetCore.AppendChild($variables)
    }
    return $variables
}

function Get-PinnedThumbprints {
    param([xml]$Config)

    $variables = Get-EnvironmentVariablesNode -Config $Config
    $pinned = @()
    foreach ($node in $variables.SelectNodes('environmentVariable')) {
        $name = $node.GetAttribute('name')
        if ($name.StartsWith($SettingPrefix)) {
            $pinned += [pscustomobject]@{ Index = [int]$name.Substring($SettingPrefix.Length); Value = $node.GetAttribute('value') }
        }
    }
    return @($pinned | Sort-Object Index | ForEach-Object { $_.Value })
}

function Set-PinnedThumbprints {
    param([xml]$Config, [string[]]$Values)

    $variables = Get-EnvironmentVariablesNode -Config $Config
    $existing = @($variables.SelectNodes('environmentVariable') | Where-Object { $_.GetAttribute('name').StartsWith($SettingPrefix) })

    # Keep the entries where the first one was, so the file still reads in its old order.
    $anchor = $null
    if ($existing.Count -gt 0) { $anchor = $existing[0].PreviousSibling }
    foreach ($node in $existing) { [void]$variables.RemoveChild($node) }

    for ($i = 0; $i -lt $Values.Count; $i++) {
        $node = $Config.CreateElement('environmentVariable')
        $node.SetAttribute('name', "$SettingPrefix$i")
        $node.SetAttribute('value', $Values[$i])
        if ($null -ne $anchor) { $anchor = $variables.InsertAfter($node, $anchor) }
        elseif ($variables.HasChildNodes) { $anchor = $variables.PrependChild($node) }
        else { $anchor = $variables.AppendChild($node) }
    }
}

function Set-EnvironmentVariable {
    param([xml]$Config, [string]$Name, [string]$Value)

    $variables = Get-EnvironmentVariablesNode -Config $Config
    $node = $variables.SelectSingleNode("environmentVariable[@name='$Name']")
    if ($null -eq $node) {
        $node = $Config.CreateElement('environmentVariable')
        $node.SetAttribute('name', $Name)
        [void]$variables.AppendChild($node)
    }
    $node.SetAttribute('value', $Value)
}

function Get-LiveSapCertificate {
    param([string]$ServiceLayerUrl)

    $uri = [Uri]$ServiceLayerUrl
    $client = New-Object Net.Sockets.TcpClient
    try {
        if (-not $client.ConnectAsync($uri.Host, $uri.Port).Wait(10000)) { throw "no TCP connection to $($uri.Host):$($uri.Port) within 10 s" }

        # Accept any certificate here: the point is to see what SAP serves, not to trust it.
        $callback = [Net.Security.RemoteCertificateValidationCallback] { $true }
        $ssl = New-Object Net.Security.SslStream($client.GetStream(), $false, $callback)
        try {
            $ssl.AuthenticateAsClient($uri.Host)
            # Copy the bytes before Dispose: disposing the stream disposes its RemoteCertificate too.
            return New-Object Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList (, $ssl.RemoteCertificate.GetRawCertData())
        }
        finally {
            $ssl.Dispose()
        }
    }
    finally {
        $client.Dispose()
    }
}

# ── Where the API runs on this box ─────────────────────────────────────────────────────────────
$apiConfigs = @()
foreach ($siteName in $ApiSiteNames) {
    $path = Get-SiteWebConfigPath -Name $siteName
    if ($null -eq $path -or ($apiConfigs.Path -contains $path)) { continue }

    [xml]$config = Get-Content -LiteralPath $path -Raw
    $aspNetCore = $config.SelectSingleNode($AspNetCoreXPath)
    if ($null -eq $aspNetCore -or $aspNetCore.GetAttribute('arguments') -notlike '*ShopInventory.dll*') {
        Write-Warning "Skipping $siteName ($path): it does not run ShopInventory.dll."
        continue
    }

    $urlNode = $config.SelectSingleNode("$AspNetCoreXPath/environmentVariables/environmentVariable[@name='SAP__ServiceLayerUrl']")
    $apiConfigs += [pscustomobject]@{
        Site            = $siteName
        Path            = $path
        Config          = $config
        Pinned          = Get-PinnedThumbprints -Config $config
        ServiceLayerUrl = if ($null -ne $urlNode) { $urlNode.GetAttribute('value') } else { $null }
    }
}

if ($apiConfigs.Count -eq 0) {
    throw "No ShopInventory API web.config found under the IIS sites: $($ApiSiteNames -join ', ')."
}

# ── What SAP serves now ────────────────────────────────────────────────────────────────────────
try {
    $live = Get-LiveSapCertificate -ServiceLayerUrl $ServiceLayerUrl
    Write-Host "SAP serves  $($live.Thumbprint)  $($live.Subject)  $($live.NotBefore.ToString('yyyy-MM-dd')) to $($live.NotAfter.ToString('yyyy-MM-dd'))" -ForegroundColor Cyan
    if ($pins -contains $live.Thumbprint.ToUpperInvariant()) {
        Write-Host '            That certificate is in the new list.' -ForegroundColor Green
    }
    else {
        Write-Warning "SAP's current certificate $($live.Thumbprint) is NOT in the list. SAP calls will still fail after this runs; re-run with -Thumbprints including it."
    }
}
catch {
    Write-Warning "Could not read SAP's certificate from $ServiceLayerUrl ($($_.Exception.Message)). Writing the list anyway."
}

Write-Host ''
Write-Host "Pinning     $($pins -join ', ')" -ForegroundColor Cyan
Write-Host "URL         $ServiceLayerUrl" -ForegroundColor Cyan
foreach ($api in $apiConfigs) {
    $pinnedText = if ($api.Pinned.Count) { $api.Pinned -join ', ' } else { '(none)' }
    Write-Host ("  {0,-24} now: {1}  {2}" -f $api.Site, $pinnedText, $api.ServiceLayerUrl) -ForegroundColor DarkGray
}

# A foreach, not Where-Object: under -WhatIf, Windows PowerShell 5.1 also skips ForEach-Object's
# property form, which is how the first version of this script lost the URL.
$toWrite = @()
foreach ($api in $apiConfigs) {
    if (($api.Pinned -join ',') -ne ($pins -join ',') -or $api.ServiceLayerUrl -ne $ServiceLayerUrl) { $toWrite += $api }
}
if ($toWrite.Count -eq 0) {
    Write-Host 'Every API web.config already pins exactly this list and URL. Nothing written.' -ForegroundColor Green
    return
}

if (-not $PSCmdlet.ShouldProcess("$($toWrite.Count) API web.config file(s): $(@($toWrite | ForEach-Object { $_.Site }) -join ', ')", 'Pin the SAP certificate thumbprints and set the Service Layer URL')) {
    return
}

foreach ($api in $toWrite) {
    Set-PinnedThumbprints -Config $api.Config -Values $pins
    Set-EnvironmentVariable -Config $api.Config -Name 'SAP__ServiceLayerUrl' -Value $ServiceLayerUrl

    $backupPath = "$($api.Path).bak-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
    Copy-Item -LiteralPath $api.Path -Destination $backupPath -Force
    $api.Config.Save($api.Path)
    Write-Host "  Updated $($api.Path)" -ForegroundColor Green
    Write-Host "  Backup  $backupPath" -ForegroundColor DarkGray
}

# ── Proof: the API reaches SAP ─────────────────────────────────────────────────────────────────
# /health/dependencies is the only health endpoint that calls SAP. It answers 503 while any
# dependency is unhealthy, so read the body in both cases and look at the sap entry.
$healthUrl = "http://localhost:$ApiPort/health/dependencies"
$body = $null
$deadline = (Get-Date).AddSeconds(120)
while ((Get-Date) -lt $deadline -and $null -eq $body) {
    Start-Sleep -Seconds 5
    try {
        $body = (Invoke-WebRequest -Uri $healthUrl -UseBasicParsing -TimeoutSec 60).Content
    }
    catch {
        if ($_.Exception.Response) {
            $reader = New-Object IO.StreamReader($_.Exception.Response.GetResponseStream())
            try { $body = $reader.ReadToEnd() } finally { $reader.Dispose() }
        }
    }
}

if ($null -eq $body) {
    Write-Warning "The API did not answer on $healthUrl within two minutes. The thumbprints and URL are written; check the site is running."
    return
}

Write-Host ''
Write-Host "$healthUrl says:" -ForegroundColor Cyan
Write-Host $body
Write-Host ''
Write-Host "If the sap entry is not Healthy, search the API log for 'RemoteCertificateValidationCallback' (still the cert) or 'code' 312 / -306 (SAP itself)." -ForegroundColor DarkGray
