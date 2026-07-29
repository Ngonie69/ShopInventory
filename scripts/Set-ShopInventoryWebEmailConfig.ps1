#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Updates the ShopInventory Web IIS web.config SMTP settings.

.DESCRIPTION
    Use this on the production server when the web.config file cannot be edited
    through Explorer/Notepad because of IIS folder permissions.
#>

param(
    [string]$SiteName = "ShopInventory-Web",
    [string]$AppPoolName = "ShopInventoryWeb",
    [string]$WebConfigPath,
    [string]$SmtpHost = "mail.kefaloscheese.com",
    [int]$SmtpPort = 587,
    [string]$SmtpUsername = "alerts@kefaloscheese.com",
    [Parameter(Mandatory = $true)]
    [string]$SmtpPassword,
    [string]$FromEmail = "alerts@kefaloscheese.com",
    [string]$FromName = "Kefalos Cheese - POD Reports",
    [string]$ApplicationUrl = "https://sis.kefaloscheese.com",
    [int]$ConnectTimeoutSeconds = 30,
    [int]$OperationTimeoutSeconds = 300,
    [switch]$IncludeSlots,
    [switch]$RestartAppPool
)

$ErrorActionPreference = "Stop"

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
        $path = switch -Regex ($Name) {
            "-Blue$" { "C:\inetpub\ShopInventory-Web-Blue\web.config"; break }
            "-Green$" { "C:\inetpub\ShopInventory-Web-Green\web.config"; break }
            default { "C:\inetpub\ShopInventory-Web\web.config" }
        }

        if (Test-Path $path) {
            return $path
        }

        throw "WebAdministration is unavailable and the fallback path '$path' does not exist. Re-run with -WebConfigPath 'C:\path\to\web.config'."
    }

    $site = Get-Item "IIS:\Sites\$Name" -ErrorAction Stop
    $physicalPath = [Environment]::ExpandEnvironmentVariables([string]$site.physicalPath)
    if (-not [System.IO.Path]::IsPathRooted($physicalPath)) {
        throw "Site '$Name' has a non-rooted physical path: $physicalPath"
    }

    return Join-Path $physicalPath "web.config"
}

function Set-WebConfigEnvironmentVariable {
    param(
        [xml]$Config,
        [string]$Name,
        [string]$Value
    )

    $aspNetCoreNode = $Config.SelectSingleNode("/configuration/location/system.webServer/aspNetCore")
    if ($null -eq $aspNetCoreNode) {
        throw "web.config is missing /configuration/location/system.webServer/aspNetCore."
    }

    $environmentVariablesNode = $Config.SelectSingleNode("/configuration/location/system.webServer/aspNetCore/environmentVariables")
    if ($null -eq $environmentVariablesNode) {
        $environmentVariablesNode = $Config.CreateElement("environmentVariables")
        [void]$aspNetCoreNode.AppendChild($environmentVariablesNode)
    }

    $node = $Config.SelectSingleNode("/configuration/location/system.webServer/aspNetCore/environmentVariables/environmentVariable[@name='$Name']")
    if ($null -eq $node) {
        $node = $Config.CreateElement("environmentVariable")

        $nameAttribute = $Config.CreateAttribute("name")
        $nameAttribute.Value = $Name
        [void]$node.Attributes.Append($nameAttribute)

        $valueAttribute = $Config.CreateAttribute("value")
        $valueAttribute.Value = $Value
        [void]$node.Attributes.Append($valueAttribute)

        [void]$environmentVariablesNode.AppendChild($node)
        return
    }

    $node.SetAttribute("value", $Value)
}

function Update-WebConfig {
    param([string]$Path)

    if (-not (Test-Path $Path)) {
        throw "web.config not found at $Path"
    }

    [xml]$config = Get-Content $Path
    $arguments = $config.SelectSingleNode("/configuration/location/system.webServer/aspNetCore").GetAttribute("arguments")
    if ($arguments -notlike "*ShopInventory.Web.dll*") {
        Write-Warning "Skipping '$Path' because it does not appear to run ShopInventory.Web.dll. arguments='$arguments'"
        return $false
    }

    Set-WebConfigEnvironmentVariable -Config $config -Name "Email__Enabled" -Value "true"
    Set-WebConfigEnvironmentVariable -Config $config -Name "Email__SmtpHost" -Value $SmtpHost
    Set-WebConfigEnvironmentVariable -Config $config -Name "Email__SmtpPort" -Value ([string]$SmtpPort)
    Set-WebConfigEnvironmentVariable -Config $config -Name "Email__SmtpUsername" -Value $SmtpUsername
    Set-WebConfigEnvironmentVariable -Config $config -Name "Email__SmtpPassword" -Value $SmtpPassword
    Set-WebConfigEnvironmentVariable -Config $config -Name "Email__FromEmail" -Value $FromEmail
    Set-WebConfigEnvironmentVariable -Config $config -Name "Email__FromName" -Value $FromName
    Set-WebConfigEnvironmentVariable -Config $config -Name "Email__EnableSsl" -Value "true"
    Set-WebConfigEnvironmentVariable -Config $config -Name "Email__SmtpSecurityMode" -Value "StartTls"
    Set-WebConfigEnvironmentVariable -Config $config -Name "Email__SmtpConnectTimeoutSeconds" -Value ([string]$ConnectTimeoutSeconds)
    Set-WebConfigEnvironmentVariable -Config $config -Name "Email__SmtpOperationTimeoutSeconds" -Value ([string]$OperationTimeoutSeconds)
    Set-WebConfigEnvironmentVariable -Config $config -Name "Email__ApplicationUrl" -Value $ApplicationUrl

    $backupPath = "$Path.bak-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
    Copy-Item $Path $backupPath -Force
    $config.Save($Path)

    Write-Host "Updated $Path" -ForegroundColor Green
    Write-Host "Backup: $backupPath" -ForegroundColor DarkGray
    return $true
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
                foreach ($slotPath in @(
                    "C:\inetpub\ShopInventory-Web-Blue\web.config",
                    "C:\inetpub\ShopInventory-Web-Green\web.config"
                )) {
                    if (Test-Path $slotPath) {
                        $paths.Add($slotPath)
                    }
                }
                break
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

if ($RestartAppPool) {
    if (-not $hasWebAdministration) {
        $appcmd = Join-Path $env:SystemRoot "System32\inetsrv\appcmd.exe"
        if (-not (Test-Path $appcmd)) {
            Write-Warning "appcmd.exe was not found. Restart the ShopInventory Web app pool manually in IIS Manager."
            return
        }

        $candidateAppPools = New-Object System.Collections.Generic.List[string]
        $candidateAppPools.Add($AppPoolName)

        if ($IncludeSlots) {
            $candidateAppPools.Add("$AppPoolName-Blue")
            $candidateAppPools.Add("$AppPoolName-Green")
        }

        foreach ($pool in $candidateAppPools | Select-Object -Unique) {
            $listOutput = & $appcmd list apppool /name:$pool 2>$null
            if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($listOutput)) {
                Write-Warning "App pool '$pool' was not found; skipping restart."
                continue
            }

            & $appcmd recycle apppool /apppool.name:$pool | Out-Host
            Write-Host "Recycled app pool $pool" -ForegroundColor Green
        }

        return
    }

    $restartSiteNames = New-Object System.Collections.Generic.List[string]
    $restartSiteNames.Add($SiteName)

    if ($IncludeSlots) {
        foreach ($slotName in @("$SiteName-Blue", "$SiteName-Green")) {
            if (Test-Path "IIS:\Sites\$slotName") {
                $restartSiteNames.Add($slotName)
            }
        }
    }

    $appPoolNames = $restartSiteNames |
        ForEach-Object { (Get-Item "IIS:\Sites\$_" -ErrorAction Stop).applicationPool } |
        Select-Object -Unique

    foreach ($appPoolName in $appPoolNames) {
        Restart-WebAppPool -Name ([string]$appPoolName)
        Write-Host "Restarted app pool $appPoolName" -ForegroundColor Green
    }
}
elseif ($updatedAny) {
    Write-Host "Restart the ShopInventory Web app pool for the changes to take effect." -ForegroundColor Yellow
}
