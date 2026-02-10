#Requires -RunAsAdministrator
<#
.SYNOPSIS
    Deploys ShopInventory API and Web applications to IIS.

.DESCRIPTION
    This script automates the deployment of ShopInventory applications to IIS:
    - Publishes both projects
    - Creates IIS Application Pools
    - Creates IIS Websites
    - Sets up folder permissions
    - Configures environment variables

.PARAMETER Action
    The action to perform: Install, Update, or Uninstall

.PARAMETER ApiPort
    Port for the API (default: 5106)

.PARAMETER WebPort
    Port for the Web app (default: 80)

.PARAMETER DeployPath
    Base path for deployment (default: C:\inetpub)

.EXAMPLE
    .\Deploy-IIS.ps1 -Action Install
    
.EXAMPLE
    .\Deploy-IIS.ps1 -Action Update

.NOTES
    Run this script as Administrator
#>

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("Install", "Update", "Uninstall")]
    [string]$Action,
    
    [int]$ApiPort = 5106,
    [int]$WebPort = 80,
    [string]$DeployPath = "C:\inetpub"
)

# ============================================
# Configuration
# ============================================
$ErrorActionPreference = "Stop"

$Config = @{
    SolutionPath = $PSScriptRoot
    ApiProject = "ShopInventory\ShopInventory.csproj"
    WebProject = "ShopInventory.Web\ShopInventory.Web.csproj"
    
    ApiSiteName = "ShopInventory-API"
    WebSiteName = "ShopInventory-Web"
    
    ApiPoolName = "ShopInventoryAPI"
    WebPoolName = "ShopInventoryWeb"
    
    ApiDeployPath = "$DeployPath\ShopInventory-API"
    WebDeployPath = "$DeployPath\ShopInventory-Web"
    
    ApiPort = $ApiPort
    WebPort = $WebPort
}

# ============================================
# Helper Functions
# ============================================

function Write-Status {
    param([string]$Message, [string]$Type = "Info")
    
    $color = switch ($Type) {
        "Info"    { "Cyan" }
        "Success" { "Green" }
        "Warning" { "Yellow" }
        "Error"   { "Red" }
        default   { "White" }
    }
    
    $prefix = switch ($Type) {
        "Info"    { "[*]" }
        "Success" { "[+]" }
        "Warning" { "[!]" }
        "Error"   { "[-]" }
        default   { "[*]" }
    }
    
    Write-Host "$prefix $Message" -ForegroundColor $color
}

function Test-Prerequisites {
    Write-Status "Checking prerequisites..."
    
    # Check if running as admin
    $isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        throw "This script must be run as Administrator"
    }
    
    # Check IIS
    $iis = Get-WindowsFeature -Name Web-Server
    if (-not $iis.Installed) {
        throw "IIS is not installed. Run: Install-WindowsFeature -Name Web-Server -IncludeManagementTools"
    }
    
    # Check .NET SDK
    $dotnetVersion = dotnet --version 2>$null
    if (-not $dotnetVersion) {
        throw ".NET SDK is not installed"
    }
    Write-Status ".NET SDK version: $dotnetVersion" "Success"
    
    # Check ASP.NET Core Module
    $ancmPath = "$env:ProgramFiles\IIS\Asp.Net Core Module\V2\aspnetcorev2.dll"
    if (-not (Test-Path $ancmPath)) {
        Write-Status "ASP.NET Core Hosting Bundle may not be installed" "Warning"
        Write-Status "Download from: https://dotnet.microsoft.com/download/dotnet/10.0" "Warning"
    }
    
    Write-Status "Prerequisites check completed" "Success"
}

function Publish-Applications {
    Write-Status "Publishing applications..."
    
    $publishPath = "$($Config.SolutionPath)\publish"
    
    # Clean publish directory
    if (Test-Path $publishPath) {
        Remove-Item -Path $publishPath -Recurse -Force
    }
    
    # Publish API
    Write-Status "Publishing API..."
    $apiProjectPath = Join-Path $Config.SolutionPath $Config.ApiProject
    dotnet publish $apiProjectPath -c Release -o "$publishPath\api" --no-self-contained
    if ($LASTEXITCODE -ne 0) { throw "Failed to publish API" }
    Write-Status "API published successfully" "Success"
    
    # Publish Web
    Write-Status "Publishing Web app..."
    $webProjectPath = Join-Path $Config.SolutionPath $Config.WebProject
    dotnet publish $webProjectPath -c Release -o "$publishPath\web" --no-self-contained
    if ($LASTEXITCODE -ne 0) { throw "Failed to publish Web app" }
    Write-Status "Web app published successfully" "Success"
    
    return $publishPath
}

function New-IISAppPool {
    param([string]$Name)
    
    Import-Module WebAdministration
    
    if (Test-Path "IIS:\AppPools\$Name") {
        Write-Status "App Pool '$Name' already exists, updating..." "Warning"
        Stop-WebAppPool -Name $Name -ErrorAction SilentlyContinue
    }
    else {
        Write-Status "Creating App Pool: $Name"
        New-WebAppPool -Name $Name
    }
    
    # Configure for .NET Core (No Managed Code)
    Set-ItemProperty "IIS:\AppPools\$Name" -Name "managedRuntimeVersion" -Value ""
    Set-ItemProperty "IIS:\AppPools\$Name" -Name "startMode" -Value "AlwaysRunning"
    Set-ItemProperty "IIS:\AppPools\$Name" -Name "processModel.idleTimeout" -Value ([TimeSpan]::FromMinutes(0))
    
    Start-WebAppPool -Name $Name
    Write-Status "App Pool '$Name' configured" "Success"
}

function New-IISWebsite {
    param(
        [string]$Name,
        [string]$PhysicalPath,
        [string]$AppPoolName,
        [int]$Port
    )
    
    Import-Module WebAdministration
    
    # Remove existing site if exists
    if (Test-Path "IIS:\Sites\$Name") {
        Write-Status "Website '$Name' already exists, removing..." "Warning"
        Remove-Website -Name $Name
    }
    
    # Create website
    Write-Status "Creating Website: $Name on port $Port"
    New-Website -Name $Name `
        -PhysicalPath $PhysicalPath `
        -ApplicationPool $AppPoolName `
        -Port $Port `
        -Force
    
    Write-Status "Website '$Name' created" "Success"
}

function Set-FolderPermissions {
    param([string]$Path)
    
    Write-Status "Setting permissions for: $Path"
    
    # Create logs directory
    $logsPath = Join-Path $Path "logs"
    if (-not (Test-Path $logsPath)) {
        New-Item -Path $logsPath -ItemType Directory -Force | Out-Null
    }
    
    # Grant IIS_IUSRS permissions
    $acl = Get-Acl $Path
    $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
        "IIS_IUSRS",
        "Modify",
        "ContainerInherit,ObjectInherit",
        "None",
        "Allow"
    )
    $acl.AddAccessRule($rule)
    Set-Acl -Path $Path -AclObject $acl
    
    Write-Status "Permissions set for '$Path'" "Success"
}

function Deploy-Files {
    param(
        [string]$SourcePath,
        [string]$DestinationPath
    )
    
    Write-Status "Deploying files to: $DestinationPath"
    
    # Create destination if not exists
    if (-not (Test-Path $DestinationPath)) {
        New-Item -Path $DestinationPath -ItemType Directory -Force | Out-Null
    }
    
    # Copy files
    Copy-Item -Path "$SourcePath\*" -Destination $DestinationPath -Recurse -Force
    
    Write-Status "Files deployed to '$DestinationPath'" "Success"
}

function Install-Application {
    Write-Status "Starting installation..." "Info"
    
    Test-Prerequisites
    
    # Publish applications
    $publishPath = Publish-Applications
    
    # Import IIS module
    Import-Module WebAdministration
    
    # Create App Pools
    New-IISAppPool -Name $Config.ApiPoolName
    New-IISAppPool -Name $Config.WebPoolName
    
    # Deploy files
    Deploy-Files -SourcePath "$publishPath\api" -DestinationPath $Config.ApiDeployPath
    Deploy-Files -SourcePath "$publishPath\web" -DestinationPath $Config.WebDeployPath
    
    # Set permissions
    Set-FolderPermissions -Path $Config.ApiDeployPath
    Set-FolderPermissions -Path $Config.WebDeployPath
    
    # Create websites
    New-IISWebsite -Name $Config.ApiSiteName `
        -PhysicalPath $Config.ApiDeployPath `
        -AppPoolName $Config.ApiPoolName `
        -Port $Config.ApiPort
    
    New-IISWebsite -Name $Config.WebSiteName `
        -PhysicalPath $Config.WebDeployPath `
        -AppPoolName $Config.WebPoolName `
        -Port $Config.WebPort
    
    # Configure firewall
    Write-Status "Configuring firewall rules..."
    New-NetFirewallRule -DisplayName "ShopInventory API" -Direction Inbound -Protocol TCP -LocalPort $Config.ApiPort -Action Allow -ErrorAction SilentlyContinue
    New-NetFirewallRule -DisplayName "ShopInventory Web" -Direction Inbound -Protocol TCP -LocalPort $Config.WebPort -Action Allow -ErrorAction SilentlyContinue
    
    Write-Status "========================================" "Success"
    Write-Status "Installation completed successfully!" "Success"
    Write-Status "========================================" "Success"
    Write-Status ""
    Write-Status "IMPORTANT: Update web.config files with your settings:"
    Write-Status "  - $($Config.ApiDeployPath)\web.config"
    Write-Status "  - $($Config.WebDeployPath)\web.config"
    Write-Status ""
    Write-Status "Access your applications:"
    Write-Status "  - API: http://localhost:$($Config.ApiPort)/swagger"
    Write-Status "  - Web: http://localhost:$($Config.WebPort)/"
}

function Update-Application {
    Write-Status "Starting update..." "Info"
    
    Test-Prerequisites
    
    # Publish applications
    $publishPath = Publish-Applications
    
    Import-Module WebAdministration
    
    # Stop app pools
    Write-Status "Stopping application pools..."
    Stop-WebAppPool -Name $Config.ApiPoolName -ErrorAction SilentlyContinue
    Stop-WebAppPool -Name $Config.WebPoolName -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    
    # Backup web.config files
    $apiWebConfig = "$($Config.ApiDeployPath)\web.config"
    $webWebConfig = "$($Config.WebDeployPath)\web.config"
    
    $backupDir = "$($Config.SolutionPath)\backup_$(Get-Date -Format 'yyyyMMdd_HHmmss')"
    New-Item -Path $backupDir -ItemType Directory -Force | Out-Null
    
    if (Test-Path $apiWebConfig) {
        Copy-Item $apiWebConfig "$backupDir\api_web.config"
        Write-Status "Backed up API web.config" "Success"
    }
    if (Test-Path $webWebConfig) {
        Copy-Item $webWebConfig "$backupDir\web_web.config"
        Write-Status "Backed up Web web.config" "Success"
    }
    
    # Deploy new files (excluding web.config)
    Write-Status "Deploying updated files..."
    
    # API
    Get-ChildItem "$publishPath\api" -Exclude "web.config" | ForEach-Object {
        Copy-Item $_.FullName -Destination $Config.ApiDeployPath -Recurse -Force
    }
    
    # Web
    Get-ChildItem "$publishPath\web" -Exclude "web.config" | ForEach-Object {
        Copy-Item $_.FullName -Destination $Config.WebDeployPath -Recurse -Force
    }
    
    # Start app pools
    Write-Status "Starting application pools..."
    Start-WebAppPool -Name $Config.ApiPoolName
    Start-WebAppPool -Name $Config.WebPoolName
    
    Write-Status "========================================" "Success"
    Write-Status "Update completed successfully!" "Success"
    Write-Status "========================================" "Success"
    Write-Status "Backup saved to: $backupDir"
}

function Uninstall-Application {
    Write-Status "Starting uninstallation..." "Warning"
    
    $confirm = Read-Host "Are you sure you want to uninstall? (yes/no)"
    if ($confirm -ne "yes") {
        Write-Status "Uninstall cancelled" "Info"
        return
    }
    
    Import-Module WebAdministration
    
    # Stop and remove websites
    if (Test-Path "IIS:\Sites\$($Config.ApiSiteName)") {
        Stop-Website -Name $Config.ApiSiteName -ErrorAction SilentlyContinue
        Remove-Website -Name $Config.ApiSiteName
        Write-Status "Removed website: $($Config.ApiSiteName)" "Success"
    }
    
    if (Test-Path "IIS:\Sites\$($Config.WebSiteName)") {
        Stop-Website -Name $Config.WebSiteName -ErrorAction SilentlyContinue
        Remove-Website -Name $Config.WebSiteName
        Write-Status "Removed website: $($Config.WebSiteName)" "Success"
    }
    
    # Stop and remove app pools
    if (Test-Path "IIS:\AppPools\$($Config.ApiPoolName)") {
        Stop-WebAppPool -Name $Config.ApiPoolName -ErrorAction SilentlyContinue
        Remove-WebAppPool -Name $Config.ApiPoolName
        Write-Status "Removed app pool: $($Config.ApiPoolName)" "Success"
    }
    
    if (Test-Path "IIS:\AppPools\$($Config.WebPoolName)") {
        Stop-WebAppPool -Name $Config.WebPoolName -ErrorAction SilentlyContinue
        Remove-WebAppPool -Name $Config.WebPoolName
        Write-Status "Removed app pool: $($Config.WebPoolName)" "Success"
    }
    
    # Ask about file removal
    $removeFiles = Read-Host "Remove deployed files? (yes/no)"
    if ($removeFiles -eq "yes") {
        if (Test-Path $Config.ApiDeployPath) {
            Remove-Item -Path $Config.ApiDeployPath -Recurse -Force
            Write-Status "Removed: $($Config.ApiDeployPath)" "Success"
        }
        if (Test-Path $Config.WebDeployPath) {
            Remove-Item -Path $Config.WebDeployPath -Recurse -Force
            Write-Status "Removed: $($Config.WebDeployPath)" "Success"
        }
    }
    
    # Remove firewall rules
    Remove-NetFirewallRule -DisplayName "ShopInventory API" -ErrorAction SilentlyContinue
    Remove-NetFirewallRule -DisplayName "ShopInventory Web" -ErrorAction SilentlyContinue
    
    Write-Status "========================================" "Success"
    Write-Status "Uninstallation completed!" "Success"
    Write-Status "========================================" "Success"
}

# ============================================
# Main Execution
# ============================================

Write-Host ""
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  ShopInventory IIS Deployment Script" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""

try {
    switch ($Action) {
        "Install"   { Install-Application }
        "Update"    { Update-Application }
        "Uninstall" { Uninstall-Application }
    }
}
catch {
    Write-Status "Error: $($_.Exception.Message)" "Error"
    Write-Status "Stack trace: $($_.ScriptStackTrace)" "Error"
    exit 1
}
