# Update Production Server - Automated Script
# Production Server: 10.10.10.9
# This script publishes ShopInventory API and Web apps and deploys them to the production IIS server

param(
    [string]$ProductionServer = "10.10.10.9",
    [string[]]$AdditionalProductionServers = @(),
    [string[]]$AdditionalSerializedCredentialPaths = @(),
    [string]$ApiAppPoolName = "ShopInventoryAPI",
    [string]$WebAppPoolName = "ShopInventoryWeb",
    [string]$ApiSiteName = "ShopInventory-API",
    [string]$WebSiteName = "ShopInventory-Web",
    [string]$ApiRemotePath = "C:\inetpub\ShopInventory-API",
    [string]$WebRemotePath = "C:\inetpub\ShopInventory-Web",
    [ValidateSet("Both", "API", "Web")]
    [string]$DeployTarget = "Both",
    [switch]$SkipBackup,
    [switch]$IncludeRuntimeDataInBackup,
    [switch]$RestartOnly,
    [switch]$FirstTimeSetup,
    [string]$ApiDbConnectionString,
    [string]$WebDbConnectionString,
    [switch]$SuppressExitPrompt,
    [PSCredential]$Credential,
    [string]$SerializedCredentialPath
)

function Export-SerializedCredential {
    param(
        [Parameter(Mandatory = $true)]
        [PSCredential]$Credential
    )

    $path = Join-Path ([System.IO.Path]::GetTempPath()) ("shopinventory-deploy-" + [Guid]::NewGuid().ToString("N") + ".credential.xml")
    $Credential | Export-Clixml -LiteralPath $path
    return $path
}

function Import-SerializedCredential {
    param(
        [string]$Path
    )

    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -LiteralPath $Path)) {
        return $null
    }

    try {
        return Import-Clixml -LiteralPath $Path
    }
    finally {
        Remove-Item -LiteralPath $Path -Force -ErrorAction SilentlyContinue
    }
}

function Get-DeploymentCredential {
    param(
        [string]$Server
    )

    return Get-Credential -Message "Enter credentials with administrator access to \\$Server\C`$ and PowerShell remoting."
}

function Get-TargetProductionServers {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PrimaryServer,
        [string[]]$AdditionalServers
    )

    $orderedServers = [System.Collections.Generic.List[string]]::new()
    foreach ($server in @($PrimaryServer) + @($AdditionalServers)) {
        if ([string]::IsNullOrWhiteSpace($server)) {
            continue
        }

        $normalizedServer = $server.Trim()
        if (-not $orderedServers.Contains($normalizedServer)) {
            [void]$orderedServers.Add($normalizedServer)
        }
    }

    return $orderedServers.ToArray()
}

function Get-AdditionalCredentialPathByServer {
    param(
        [string[]]$AdditionalServers,
        [string[]]$CredentialPaths
    )

    $credentialPathByServer = @{}
    for ($index = 0; $index -lt $AdditionalServers.Count; $index++) {
        if ($index -ge $CredentialPaths.Count) {
            break
        }

        $server = $AdditionalServers[$index]
        $credentialPath = $CredentialPaths[$index]
        if ([string]::IsNullOrWhiteSpace($server) -or [string]::IsNullOrWhiteSpace($credentialPath)) {
            continue
        }

        $credentialPathByServer[$server.Trim()] = $credentialPath.Trim()
    }

    return $credentialPathByServer
}

function Wait-ForExitPrompt {
    if (-not $SuppressExitPrompt) {
        Read-Host "Press Enter to exit"
    }
}

function Get-BlueGreenDeploymentDefinitions {
    param(
        [string]$ApiPool,
        [string]$WebPool,
        [string]$ApiSite,
        [string]$WebSite,
        [string]$ApiPath,
        [string]$WebPath
    )

    return @(
        [pscustomobject]@{
            Name                 = 'API'
            PublicSiteName       = $ApiSite
            PublicAppPoolName    = $ApiPool
            PublicPort           = 5106
            LegacyPath           = $ApiPath
            ReadyPath            = '/health/ready'
            WarmupTimeoutSeconds = 180
            Slots                = @(
                [pscustomobject]@{
                    Name        = 'Blue'
                    SiteName    = "$ApiSite-Blue"
                    AppPoolName = "$ApiPool-Blue"
                    RemotePath  = "$ApiPath-Blue"
                    Port        = 15106
                },
                [pscustomobject]@{
                    Name        = 'Green'
                    SiteName    = "$ApiSite-Green"
                    AppPoolName = "$ApiPool-Green"
                    RemotePath  = "$ApiPath-Green"
                    Port        = 15116
                }
            )
        },
        [pscustomobject]@{
            Name                 = 'Web'
            PublicSiteName       = $WebSite
            PublicAppPoolName    = $WebPool
            PublicPort           = 5107
            LegacyPath           = $WebPath
            ReadyPath            = '/health/ready'
            WarmupTimeoutSeconds = 240
            Slots                = @(
                [pscustomobject]@{
                    Name        = 'Blue'
                    SiteName    = "$WebSite-Blue"
                    AppPoolName = "$WebPool-Blue"
                    RemotePath  = "$WebPath-Blue"
                    Port        = 15107
                },
                [pscustomobject]@{
                    Name        = 'Green'
                    SiteName    = "$WebSite-Green"
                    AppPoolName = "$WebPool-Green"
                    RemotePath  = "$WebPath-Green"
                    Port        = 15117
                }
            )
        }
    )
}

function Get-SelectedDeploymentDefinitions {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$Definitions,
        [Parameter(Mandatory = $true)]
        [string]$Target
    )

    switch ($Target) {
        'API' { return @($Definitions | Where-Object Name -eq 'API') }
        'Web' { return @($Definitions | Where-Object Name -eq 'Web') }
        default { return $Definitions }
    }
}

$targetServers = Get-TargetProductionServers -PrimaryServer $ProductionServer -AdditionalServers $AdditionalProductionServers
$additionalCredentialPathByServer = Get-AdditionalCredentialPathByServer -AdditionalServers $AdditionalProductionServers -CredentialPaths $AdditionalSerializedCredentialPaths

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
if ($targetServers.Count -gt 1) {
    Write-Host "ShopInventory - Multi-Server Deployment" -ForegroundColor Cyan
}
else {
    Write-Host "ShopInventory - Production Deployment" -ForegroundColor Cyan
}
Write-Host "========================================" -ForegroundColor Cyan
if ($targetServers.Count -gt 1) {
    Write-Host "Servers: $($targetServers -join ', ')" -ForegroundColor White
}
else {
    Write-Host "Server: $ProductionServer" -ForegroundColor White
}
Write-Host "Target: $DeployTarget" -ForegroundColor White
Write-Host ""

if (-not $Credential -and $SerializedCredentialPath) {
    $Credential = Import-SerializedCredential -Path $SerializedCredentialPath
}

if ($targetServers.Count -gt 1) {
    if ($FirstTimeSetup) {
        Write-Host "ERROR: Multi-server deployment is not supported with -FirstTimeSetup. Run first-time setup once per server." -ForegroundColor Red
        Wait-ForExitPrompt
        exit 1
    }

    if ($AdditionalSerializedCredentialPaths.Count -gt $AdditionalProductionServers.Count) {
        Write-Host "ERROR: AdditionalSerializedCredentialPaths cannot contain more entries than AdditionalProductionServers." -ForegroundColor Red
        Wait-ForExitPrompt
        exit 1
    }

    if (-not $Credential) {
        $Credential = Get-DeploymentCredential -Server $targetServers[0]

        if (-not $Credential) {
            Write-Host "ERROR: Deployment credential was not provided." -ForegroundColor Red
            Wait-ForExitPrompt
            exit 1
        }

        Write-Host "Deployment credentials acquired securely." -ForegroundColor Green
        Write-Host ""
    }

    foreach ($targetServer in $targetServers) {
        Write-Host "Deploying to $targetServer..." -ForegroundColor Yellow

        $serializedCredentialForTarget = $null
        $childCredentialPath = $null
        try {
            if ($additionalCredentialPathByServer.ContainsKey($targetServer)) {
                $childCredentialPath = $additionalCredentialPathByServer[$targetServer]
                if (-not (Test-Path -LiteralPath $childCredentialPath)) {
                    Write-Host "ERROR: Credential file not found for $targetServer at $childCredentialPath" -ForegroundColor Red
                    Wait-ForExitPrompt
                    exit 1
                }
            }
            else {
                $serializedCredentialForTarget = Export-SerializedCredential -Credential $Credential
                $childCredentialPath = $serializedCredentialForTarget
            }

            $argumentList = @(
                '-NoProfile'
                '-ExecutionPolicy'
                'Bypass'
                '-File'
                $PSCommandPath
                '-ProductionServer'
                $targetServer
                '-ApiAppPoolName'
                $ApiAppPoolName
                '-WebAppPoolName'
                $WebAppPoolName
                '-ApiSiteName'
                $ApiSiteName
                '-WebSiteName'
                $WebSiteName
                '-ApiRemotePath'
                $ApiRemotePath
                '-WebRemotePath'
                $WebRemotePath
                '-DeployTarget'
                $DeployTarget
                '-SerializedCredentialPath'
                $childCredentialPath
                '-SuppressExitPrompt'
            )

            if ($SkipBackup) { $argumentList += '-SkipBackup' }
            if ($IncludeRuntimeDataInBackup) { $argumentList += '-IncludeRuntimeDataInBackup' }
            if ($RestartOnly) { $argumentList += '-RestartOnly' }
            if (-not [string]::IsNullOrWhiteSpace($ApiDbConnectionString)) {
                $argumentList += @('-ApiDbConnectionString', $ApiDbConnectionString)
            }
            if (-not [string]::IsNullOrWhiteSpace($WebDbConnectionString)) {
                $argumentList += @('-WebDbConnectionString', $WebDbConnectionString)
            }

            & powershell.exe @argumentList
            $childExitCode = $LASTEXITCODE
        }
        finally {
            if ($serializedCredentialForTarget -and (Test-Path -LiteralPath $serializedCredentialForTarget)) {
                Remove-Item -LiteralPath $serializedCredentialForTarget -Force -ErrorAction SilentlyContinue
            }
        }

        if ($childExitCode -ne 0) {
            Write-Host "ERROR: Deployment failed for $targetServer with exit code $childExitCode." -ForegroundColor Red
            Wait-ForExitPrompt
            exit $childExitCode
        }

        Write-Host "Completed deployment for $targetServer." -ForegroundColor Green
        Write-Host ""
    }

    Write-Host "========================================" -ForegroundColor Green
    Write-Host "Multi-server deployment completed!" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Green
    Write-Host ""
    Wait-ForExitPrompt
    exit 0
}

# First-time IIS bootstrap needs local elevation for the remote setup workflow.
# Normal deployments only publish locally and operate remotely, so they can run
# without forcing a new elevated PowerShell process.
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($FirstTimeSetup -and -not $isAdmin) {
    Write-Host "Not running as Administrator. Elevating..." -ForegroundColor Yellow
    
    # Build argument list to pass parameters to elevated script
    $argList = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    if ($ProductionServer -ne "10.10.10.9") { $argList += " -ProductionServer `"$ProductionServer`"" }
    if ($ApiAppPoolName -ne "ShopInventoryAPI") { $argList += " -ApiAppPoolName `"$ApiAppPoolName`"" }
    if ($WebAppPoolName -ne "ShopInventoryWeb") { $argList += " -WebAppPoolName `"$WebAppPoolName`"" }
    if ($DeployTarget -ne "Both") { $argList += " -DeployTarget `"$DeployTarget`"" }
    if ($SkipBackup) { $argList += " -SkipBackup" }
    if ($IncludeRuntimeDataInBackup) { $argList += " -IncludeRuntimeDataInBackup" }
    if ($RestartOnly) { $argList += " -RestartOnly" }
    if ($FirstTimeSetup) { $argList += " -FirstTimeSetup" }
    if (-not [string]::IsNullOrWhiteSpace($ApiDbConnectionString)) { $argList += " -ApiDbConnectionString `"$ApiDbConnectionString`"" }
    if (-not [string]::IsNullOrWhiteSpace($WebDbConnectionString)) { $argList += " -WebDbConnectionString `"$WebDbConnectionString`"" }

    $credentialPath = $null
    try {
        if ($Credential) {
            $credentialPath = Export-SerializedCredential -Credential $Credential
            $argList += " -SerializedCredentialPath `"$credentialPath`""
        }

        Start-Process powershell.exe -Verb RunAs -ArgumentList $argList
    }
    catch {
        if ($credentialPath) {
            Remove-Item -LiteralPath $credentialPath -Force -ErrorAction SilentlyContinue
        }

        throw
    }

    exit
}

# Get credentials for production server if not provided
if (-not $Credential) {
    $Credential = Get-DeploymentCredential -Server $ProductionServer

    if (-not $Credential) {
        Write-Host "ERROR: Deployment credential was not provided." -ForegroundColor Red
        exit 1
    }

    Write-Host "Deployment credentials acquired securely." -ForegroundColor Green
    Write-Host ""
}

$deploymentDefinitions = Get-SelectedDeploymentDefinitions -Definitions (Get-BlueGreenDeploymentDefinitions `
        -ApiPool $ApiAppPoolName `
        -WebPool $WebAppPoolName `
        -ApiSite $ApiSiteName `
        -WebSite $WebSiteName `
        -ApiPath $ApiRemotePath `
        -WebPath $WebRemotePath) -Target $DeployTarget

$databaseConnectionOverrides = @{
    API = $ApiDbConnectionString
    Web = $WebDbConnectionString
}

# Test connection to production server
Write-Host "Testing connection to production server..." -ForegroundColor Yellow
$pingResult = Test-Connection -ComputerName $ProductionServer -Count 2 -Quiet
if (-not $pingResult) {
    Write-Host "ERROR: Cannot reach production server at $ProductionServer" -ForegroundColor Red
    Write-Host "Please check network connectivity and server address." -ForegroundColor Yellow
    Wait-ForExitPrompt
    exit 1
}
Write-Host "Connection successful!" -ForegroundColor Green
Write-Host ""

# Validate remoting access up front because deployment packages are transferred over WinRM.
Write-Host "Validating deployment remoting access..." -ForegroundColor Yellow
try {
    $remoteComputerName = Invoke-Command -ComputerName $ProductionServer -Credential $Credential -Authentication Negotiate -ScriptBlock {
        $env:COMPUTERNAME
    } -ErrorAction Stop

    Write-Host "Remoting access confirmed on $remoteComputerName!" -ForegroundColor Green
}
catch {
    Write-Host "ERROR: Could not establish a deployment session to $ProductionServer" -ForegroundColor Red
    Write-Host "Error: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ""
    Write-Host "Make sure:" -ForegroundColor Yellow
    Write-Host "  1. WinRM is enabled on the server" -ForegroundColor White
    Write-Host "  2. Your account has administrator access on the server" -ForegroundColor White
    Write-Host "  3. PowerShell remoting is allowed through the firewall" -ForegroundColor White
    Wait-ForExitPrompt
    exit 1
}
Write-Host ""

# If RestartOnly flag is set, just restart and exit
if ($RestartOnly) {
    Write-Host "Restart-only mode - no deployment" -ForegroundColor Yellow
    Write-Host ""
    
    try {
        $restartResults = Invoke-Command -ComputerName $ProductionServer -Credential $Credential -Authentication Negotiate -ScriptBlock {
            param($Definitions)

            Import-Module WebAdministration

            function Test-SiteHasPublicPortBinding {
                param(
                    [string]$SiteName,
                    [int]$Port
                )

                $binding = Get-WebBinding -Name $SiteName -Protocol 'http' -ErrorAction SilentlyContinue |
                Where-Object { $_.bindingInformation -match "^[^:]*:${Port}:" } |
                Select-Object -First 1

                return $null -ne $binding
            }

            $results = foreach ($definition in $Definitions) {
                $siteToRestart = $definition.PublicSiteName
                $poolToRestart = $definition.PublicAppPoolName

                foreach ($slot in $definition.Slots) {
                    if (Test-SiteHasPublicPortBinding -SiteName $slot.SiteName -Port $definition.PublicPort) {
                        $siteToRestart = $slot.SiteName
                        $poolToRestart = $slot.AppPoolName
                        break
                    }
                }

                $sitePath = "IIS:\Sites\$siteToRestart"

                if (Test-Path $sitePath) {
                    $site = Get-Item $sitePath
                    if (-not [string]::IsNullOrWhiteSpace($site.applicationPool)) {
                        $poolToRestart = $site.applicationPool
                    }

                    $siteState = Get-WebsiteState -Name $siteToRestart -ErrorAction SilentlyContinue
                    if ($siteState.Value -ne 'Started') {
                        Start-Website -Name $siteToRestart -ErrorAction SilentlyContinue | Out-Null
                    }
                }

                if (-not (Test-Path "IIS:\AppPools\$poolToRestart")) {
                    throw "App pool $poolToRestart was not found on the production server."
                }

                Restart-WebAppPool -Name $poolToRestart -ErrorAction Stop

                [pscustomobject]@{
                    Name    = $definition.Name
                    Site    = $siteToRestart
                    AppPool = $poolToRestart
                }
            }

            return $results
        } -ArgumentList (, $deploymentDefinitions) -ErrorAction Stop

        foreach ($result in $restartResults) {
            Write-Host "  $($result.Name): recycled $($result.AppPool) on $($result.Site)" -ForegroundColor Green
        }
        
        Write-Host ""
        Write-Host "Application(s) restarted successfully!" -ForegroundColor Green
    }
    catch {
        Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "Try using IIS Manager on the production server manually." -ForegroundColor Yellow
    }
    
    Wait-ForExitPrompt
    exit 0
}

# First-time setup on production server
if ($FirstTimeSetup) {
    Write-Host "First-time setup mode - configuring IIS on production server..." -ForegroundColor Yellow
    Write-Host ""
    
    try {
        Invoke-Command -ComputerName $ProductionServer -Credential $Credential -Authentication Negotiate -ScriptBlock {
            param($ApiPool, $WebPool, $ApiSite, $WebSite, $ApiPath, $WebPath, $ApiPort, $WebPort)
            
            Import-Module WebAdministration
            
            # Create directories
            Write-Host "Creating deployment directories..." -ForegroundColor Yellow
            @($ApiPath, $WebPath, "$ApiPath\logs", "$WebPath\logs") | ForEach-Object {
                if (-not (Test-Path $_)) {
                    New-Item -Path $_ -ItemType Directory -Force | Out-Null
                    Write-Host "  Created: $_" -ForegroundColor Green
                }
            }
            
            # Create API App Pool
            if (-not (Test-Path "IIS:\AppPools\$ApiPool")) {
                Write-Host "Creating App Pool: $ApiPool..." -ForegroundColor Yellow
                New-WebAppPool -Name $ApiPool
            }
            Set-ItemProperty "IIS:\AppPools\$ApiPool" -Name "managedRuntimeVersion" -Value ""
            Set-ItemProperty "IIS:\AppPools\$ApiPool" -Name "startMode" -Value "AlwaysRunning"
            Set-ItemProperty "IIS:\AppPools\$ApiPool" -Name "processModel.idleTimeout" -Value ([TimeSpan]::FromMinutes(0))
            Write-Host "  $ApiPool configured" -ForegroundColor Green
            
            # Create Web App Pool
            if (-not (Test-Path "IIS:\AppPools\$WebPool")) {
                Write-Host "Creating App Pool: $WebPool..." -ForegroundColor Yellow
                New-WebAppPool -Name $WebPool
            }
            Set-ItemProperty "IIS:\AppPools\$WebPool" -Name "managedRuntimeVersion" -Value ""
            Set-ItemProperty "IIS:\AppPools\$WebPool" -Name "startMode" -Value "AlwaysRunning"
            Set-ItemProperty "IIS:\AppPools\$WebPool" -Name "processModel.idleTimeout" -Value ([TimeSpan]::FromMinutes(0))
            Write-Host "  $WebPool configured" -ForegroundColor Green
            
            # Create API Website
            if (-not (Test-Path "IIS:\Sites\$ApiSite")) {
                Write-Host "Creating Website: $ApiSite on port 5106..." -ForegroundColor Yellow
                New-Website -Name $ApiSite -PhysicalPath $ApiPath -ApplicationPool $ApiPool -Port 5106 -Force
            }
            Write-Host "  $ApiSite created" -ForegroundColor Green
            
            # Create Web Website
            if (-not (Test-Path "IIS:\Sites\$WebSite")) {
                Write-Host "Creating Website: $WebSite on port 5107..." -ForegroundColor Yellow
                New-Website -Name $WebSite -PhysicalPath $WebPath -ApplicationPool $WebPool -Port 5107 -Force
            }
            Write-Host "  $WebSite created" -ForegroundColor Green
            
            # Set permissions
            Write-Host "Setting folder permissions..." -ForegroundColor Yellow
            $acl = Get-Acl $ApiPath
            $rule = New-Object System.Security.AccessControl.FileSystemAccessRule("IIS_IUSRS", "Modify", "ContainerInherit,ObjectInherit", "None", "Allow")
            $acl.AddAccessRule($rule)
            Set-Acl -Path $ApiPath -AclObject $acl
            
            $acl = Get-Acl $WebPath
            $acl.AddAccessRule($rule)
            Set-Acl -Path $WebPath -AclObject $acl
            Write-Host "  Permissions set" -ForegroundColor Green
            
            # Configure firewall
            Write-Host "Configuring firewall rules..." -ForegroundColor Yellow
            New-NetFirewallRule -DisplayName "ShopInventory API" -Direction Inbound -Protocol TCP -LocalPort 5106 -Action Allow -ErrorAction SilentlyContinue
            New-NetFirewallRule -DisplayName "ShopInventory Web" -Direction Inbound -Protocol TCP -LocalPort 5107 -Action Allow -ErrorAction SilentlyContinue
            Write-Host "  Firewall rules configured" -ForegroundColor Green
            
        } -ArgumentList $ApiAppPoolName, $WebAppPoolName, $ApiSiteName, $WebSiteName, $ApiRemotePath, $WebRemotePath, 5106, 5107 -ErrorAction Stop
        
        Write-Host ""
        Write-Host "========================================" -ForegroundColor Green
        Write-Host "First-time setup completed!" -ForegroundColor Green
        Write-Host "========================================" -ForegroundColor Green
        Write-Host ""
        Write-Host "Next: Run this script without -FirstTimeSetup to deploy the application." -ForegroundColor Yellow
    }
    catch {
        Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    }
    
    Wait-ForExitPrompt
    exit 0
}

# ============================================
# MAIN DEPLOYMENT PROCESS
# ============================================

$RootDir = $PSScriptRoot
$ApiProjectPath = Join-Path $RootDir "ShopInventory\ShopInventory.csproj"
$WebProjectPath = Join-Path $RootDir "ShopInventory.Web\ShopInventory.Web.csproj"
$PublishPath = Join-Path $RootDir "publish"

# Step 1: Publish the applications locally
Write-Host "Step 1: Publishing applications..." -ForegroundColor Cyan
Write-Host "----------------------------------------" -ForegroundColor Gray

# Clean previous publish (handle locked files)
if (Test-Path $PublishPath) {
    Write-Host "Cleaning previous publish folder..." -ForegroundColor Gray
    $retryCount = 0
    $maxRetries = 3
    while ((Test-Path $PublishPath) -and $retryCount -lt $maxRetries) {
        try {
            # Try to release any handles
            [System.GC]::Collect()
            [System.GC]::WaitForPendingFinalizers()
            Start-Sleep -Milliseconds 500
            Remove-Item -Path $PublishPath -Recurse -Force -ErrorAction Stop
        }
        catch {
            $retryCount++
            if ($retryCount -lt $maxRetries) {
                Write-Host "  Retry $retryCount/$maxRetries - waiting for file locks to release..." -ForegroundColor Yellow
                Start-Sleep -Seconds 2
            }
            else {
                Write-Host "  WARNING: Could not fully clean publish folder. Continuing anyway..." -ForegroundColor Yellow
                # Delete what we can
                Get-ChildItem -Path $PublishPath -Recurse -ErrorAction SilentlyContinue | 
                Where-Object { -not $_.PSIsContainer } | 
                Remove-Item -Force -ErrorAction SilentlyContinue
            }
        }
    }
}
New-Item -Path $PublishPath -ItemType Directory -Force | Out-Null

$publishedApps = @()

if ($DeployTarget -eq "Both" -or $DeployTarget -eq "API") {
    Write-Host "Publishing API..." -ForegroundColor White
    dotnet publish $ApiProjectPath -c Release -o "$PublishPath\api" --no-self-contained
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: API publish failed!" -ForegroundColor Red
        Wait-ForExitPrompt
        exit 1
    }
    Write-Host "  API published successfully!" -ForegroundColor Green

    # Copy Firebase service account key to publish output
    $firebaseKeySource = Join-Path $PSScriptRoot "ShopInventory\firebase-service-account.json"
    if (Test-Path $firebaseKeySource) {
        Copy-Item $firebaseKeySource "$PublishPath\api\firebase-service-account.json"
        Write-Host "  Firebase service account key included." -ForegroundColor Green
    }
    else {
        Write-Host "  WARNING: firebase-service-account.json not found - push notifications will be disabled on deploy." -ForegroundColor Yellow
    }

    $publishedApps += "API"
}

if ($DeployTarget -eq "Both" -or $DeployTarget -eq "Web") {
    Write-Host "Publishing Web app..." -ForegroundColor White
    dotnet publish $WebProjectPath -c Release -o "$PublishPath\web" --no-self-contained
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: Web publish failed!" -ForegroundColor Red
        Wait-ForExitPrompt
        exit 1
    }
    Write-Host "  Web app published successfully!" -ForegroundColor Green
    $publishedApps += "Web"
}

Write-Host ""

# Step 2: Prepare blue/green slot infrastructure and deployment plan
Write-Host "Step 2: Preparing blue/green deployment slots..." -ForegroundColor Cyan
Write-Host "----------------------------------------" -ForegroundColor Gray

try {
    $deploymentPlans = Invoke-Command -ComputerName $ProductionServer -Credential $Credential -Authentication Negotiate -ScriptBlock {
        param($Definitions)

        Import-Module WebAdministration

        function Ensure-Directory {
            param([string]$Path)

            if (-not (Test-Path $Path)) {
                New-Item -Path $Path -ItemType Directory -Force | Out-Null
                return $true
            }

            return $false
        }

        function Ensure-ModifyPermission {
            param([string]$Path)

            if (-not (Test-Path $Path)) {
                return
            }

            $null = & icacls $Path /grant 'IIS_IUSRS:(OI)(CI)M' /C
            $icaclsExitCode = $LASTEXITCODE
            if ($icaclsExitCode -ge 8) {
                throw "icacls failed for $Path with exit code $icaclsExitCode."
            }
        }

        function Ensure-AppPool {
            param([string]$Name)

            if (-not (Test-Path "IIS:\AppPools\$Name")) {
                New-WebAppPool -Name $Name | Out-Null
            }

            Set-ItemProperty "IIS:\AppPools\$Name" -Name managedRuntimeVersion -Value ""
            Set-ItemProperty "IIS:\AppPools\$Name" -Name startMode -Value "AlwaysRunning"
            Set-ItemProperty "IIS:\AppPools\$Name" -Name processModel.idleTimeout -Value ([TimeSpan]::FromMinutes(0))
        }

        function Ensure-Site {
            param(
                [string]$Name,
                [string]$PhysicalPath,
                [string]$AppPool,
                [int]$Port
            )

            if (-not (Test-Path "IIS:\Sites\$Name")) {
                New-Website -Name $Name -PhysicalPath $PhysicalPath -ApplicationPool $AppPool -Port $Port -Force | Out-Null
            }
            else {
                Set-ItemProperty "IIS:\Sites\$Name" -Name physicalPath -Value $PhysicalPath
                Set-ItemProperty "IIS:\Sites\$Name" -Name applicationPool -Value $AppPool
            }

            $state = Get-WebsiteState -Name $Name -ErrorAction SilentlyContinue
            if ($state.Value -ne 'Started') {
                Start-Website -Name $Name -ErrorAction SilentlyContinue | Out-Null
            }
        }

        function Test-SiteHasPublicPortBinding {
            param(
                [string]$SiteName,
                [int]$Port
            )

            $binding = Get-WebBinding -Name $SiteName -Protocol 'http' -ErrorAction SilentlyContinue |
            Where-Object { $_.bindingInformation -match "^[^:]*:${Port}:" } |
            Select-Object -First 1

            return $null -ne $binding
        }

        $plans = foreach ($definition in $Definitions) {
            Write-Host "  [$($definition.Name)] Checking public site and slot infrastructure..." -ForegroundColor Gray

            $createdPaths = @()

            if (Ensure-Directory -Path $definition.LegacyPath) {
                $createdPaths += $definition.LegacyPath
            }

            $legacyLogsPath = Join-Path $definition.LegacyPath 'logs'
            if (Ensure-Directory -Path $legacyLogsPath) {
                $createdPaths += $legacyLogsPath
            }

            if ($definition.Name -eq 'API') {
                $legacyUploadsPath = Join-Path $definition.LegacyPath 'uploads'
                if (Ensure-Directory -Path $legacyUploadsPath) {
                    $createdPaths += $legacyUploadsPath
                }
            }

            foreach ($path in $createdPaths) {
                Ensure-ModifyPermission -Path $path
            }

            Ensure-AppPool -Name $definition.PublicAppPoolName

            if (-not (Test-Path "IIS:\Sites\$($definition.PublicSiteName)")) {
                Ensure-Site -Name $definition.PublicSiteName -PhysicalPath $definition.LegacyPath -AppPool $definition.PublicAppPoolName -Port $definition.PublicPort
            }

            foreach ($slot in $definition.Slots) {
                Write-Host "    [$($definition.Name)] Ensuring slot $($slot.Name)..." -ForegroundColor DarkGray

                $slotCreatedPaths = @()

                if (Ensure-Directory -Path $slot.RemotePath) {
                    $slotCreatedPaths += $slot.RemotePath
                }

                $slotLogsPath = Join-Path $slot.RemotePath 'logs'
                if (Ensure-Directory -Path $slotLogsPath) {
                    $slotCreatedPaths += $slotLogsPath
                }

                if ($definition.Name -eq 'API') {
                    $slotUploadsPath = Join-Path $slot.RemotePath 'uploads'
                    if (Ensure-Directory -Path $slotUploadsPath) {
                        $slotCreatedPaths += $slotUploadsPath
                    }
                }

                foreach ($path in $slotCreatedPaths) {
                    Ensure-ModifyPermission -Path $path
                }

                Ensure-AppPool -Name $slot.AppPoolName
                Ensure-Site -Name $slot.SiteName -PhysicalPath $slot.RemotePath -AppPool $slot.AppPoolName -Port $slot.Port
            }

            $publicSite = Get-Item "IIS:\Sites\$($definition.PublicSiteName)"
            $currentSiteName = $definition.PublicSiteName
            $currentPath = [string]$publicSite.physicalPath
            $currentAppPoolName = [string]$publicSite.applicationPool
            $activeSlot = 'Legacy'

            foreach ($slot in $definition.Slots) {
                if (Test-SiteHasPublicPortBinding -SiteName $slot.SiteName -Port $definition.PublicPort) {
                    $activeSlot = $slot.Name
                    $currentSiteName = $slot.SiteName
                    $currentPath = $slot.RemotePath
                    $currentAppPoolName = $slot.AppPoolName
                    break
                }
            }

            $targetSlot = if ($activeSlot -eq 'Blue') { 'Green' } else { 'Blue' }
            $targetConfig = $definition.Slots | Where-Object Name -eq $targetSlot | Select-Object -First 1

            [pscustomobject]@{
                Name                 = $definition.Name
                PublicSiteName       = $definition.PublicSiteName
                PublicPort           = [int]$definition.PublicPort
                ReadyPath            = $definition.ReadyPath
                WarmupTimeoutSeconds = [int]$definition.WarmupTimeoutSeconds
                ActiveSlot           = $activeSlot
                CurrentSiteName      = $currentSiteName
                CurrentPath          = $currentPath
                CurrentAppPoolName   = $currentAppPoolName
                TargetSlot           = $targetSlot
                TargetPath           = $targetConfig.RemotePath
                TargetAppPoolName    = $targetConfig.AppPoolName
                TargetSiteName       = $targetConfig.SiteName
                TargetPort           = [int]$targetConfig.Port
                TargetReadyUrl       = "http://localhost:$($targetConfig.Port)$($definition.ReadyPath)"
                PublicReadyUrl       = "http://localhost:$($definition.PublicPort)$($definition.ReadyPath)"
            }
        }

        return $plans
    } -ArgumentList (, $deploymentDefinitions) -ErrorAction Stop

    foreach ($plan in $deploymentPlans) {
        Write-Host "  $($plan.Name): active slot $($plan.ActiveSlot), deploying to $($plan.TargetSlot)" -ForegroundColor Green
    }
}
catch {
    Write-Host "ERROR: Could not prepare blue/green deployment slots." -ForegroundColor Red
    Write-Host "Error: $($_.Exception.Message)" -ForegroundColor Red
    Wait-ForExitPrompt
    exit 1
}

Write-Host ""

# Step 3: Create backup
if (-not $SkipBackup) {
    Write-Host "Step 3: Creating backup from the active slot..." -ForegroundColor Cyan
    Write-Host "----------------------------------------" -ForegroundColor Gray

    try {
        Invoke-Command -ComputerName $ProductionServer -Credential $Credential -Authentication Negotiate -ScriptBlock {
            param($Plans, $IncludeRuntimeDataInBackup)

            function Invoke-AppBackup {
                param(
                    [string]$SourcePath,
                    [string]$BackupPath,
                    [string]$AppName,
                    [bool]$IncludeRuntimeData
                )

                if (-not (Test-Path $SourcePath)) {
                    return
                }

                $robocopyArgs = @(
                    $SourcePath,
                    $BackupPath,
                    '/MIR',
                    '/MT:8',
                    '/R:1',
                    '/W:1',
                    '/XJ',
                    '/NP',
                    '/NJH',
                    '/NJS',
                    '/NFL',
                    '/NDL'
                )

                $excludedDirectories = @()
                if (-not $IncludeRuntimeData) {
                    foreach ($folderName in @('logs', 'uploads')) {
                        $folderPath = Join-Path $SourcePath $folderName
                        if (Test-Path $folderPath) {
                            $excludedDirectories += $folderPath
                        }
                    }
                }

                if ($excludedDirectories.Count -gt 0) {
                    Write-Host "    Excluding runtime folders: $($excludedDirectories.ForEach({ Split-Path $_ -Leaf }) -join ', ')" -ForegroundColor DarkGray
                    $robocopyArgs += '/XD'
                    $robocopyArgs += $excludedDirectories
                }

                $appOfflinePath = Join-Path $SourcePath 'app_offline.htm'
                if (Test-Path $appOfflinePath) {
                    $robocopyArgs += '/XF'
                    $robocopyArgs += $appOfflinePath
                }

                Write-Host "  Backing up $AppName from $SourcePath..." -ForegroundColor Gray
                $null = & robocopy @robocopyArgs
                $robocopyExitCode = $LASTEXITCODE

                if ($robocopyExitCode -ge 8) {
                    throw "Robocopy backup failed for $AppName with exit code $robocopyExitCode."
                }

                Write-Host "  $AppName backup created" -ForegroundColor Green
            }

            $BackupBase = "C:\inetpub\ShopInventory-backup-latest"

            foreach ($plan in $Plans) {
                if (Test-Path $plan.CurrentPath) {
                    Invoke-AppBackup -SourcePath $plan.CurrentPath -BackupPath "$BackupBase-$($plan.Name)" -AppName $plan.Name -IncludeRuntimeData:$IncludeRuntimeDataInBackup
                }
            }
        } -ArgumentList (, $deploymentPlans), $IncludeRuntimeDataInBackup.IsPresent -ErrorAction Stop
    }
    catch {
        Write-Host "WARNING: Backup failed: $($_.Exception.Message)" -ForegroundColor Yellow
    }

    Write-Host ""
}
else {
    Write-Host "Step 3: Skipping backup (SkipBackup flag set)..." -ForegroundColor Yellow
    Write-Host ""
}

# Step 4: Package, deploy to inactive slot, warm, and cut over
Write-Host "Step 4: Deploying to the inactive slot and cutting over on readiness..." -ForegroundColor Cyan
Write-Host "----------------------------------------" -ForegroundColor Gray

$cutoverResults = @()

try {
    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'

    foreach ($app in $publishedApps) {
        $deploymentPlan = $deploymentPlans | Where-Object Name -eq $app | Select-Object -First 1
        if ($null -eq $deploymentPlan) {
            throw "No deployment plan was generated for $app."
        }

        $appLower = $app.ToLower()
        $sourcePath = "$PublishPath\$appLower"
        $zipFileName = "ShopInventory-$app-$timestamp.zip"
        $zipPath = "$PublishPath\$zipFileName"

        Write-Host "Deploying $app to slot $($deploymentPlan.TargetSlot)..." -ForegroundColor White

        Write-Host "  Creating deployment package..." -ForegroundColor Gray
        Compress-Archive -Path "$sourcePath\*" -DestinationPath $zipPath -Force
        $zipSize = [math]::Round((Get-Item $zipPath).Length / 1MB, 2)
        Write-Host "  Package created: $zipFileName - $zipSize MB" -ForegroundColor Green

        Write-Host "  Uploading to production server..." -ForegroundColor Gray
        $transferSession = $null
        try {
            $transferSession = New-PSSession -ComputerName $ProductionServer -Credential $Credential -Authentication Negotiate -ErrorAction Stop
            Copy-Item -Path $zipPath -Destination "C:\inetpub\$zipFileName" -ToSession $transferSession -Force -ErrorAction Stop
        }
        finally {
            if ($null -ne $transferSession) {
                Remove-PSSession -Session $transferSession -ErrorAction SilentlyContinue
            }
        }

        Write-Host "  Upload complete!" -ForegroundColor Green

        Write-Host "  Deploying to inactive slot and warming it up..." -ForegroundColor Gray
        $cutoverResult = Invoke-Command -ComputerName $ProductionServer -Credential $Credential -Authentication Negotiate -ScriptBlock {
            param($ZipFile, $Plan, $DatabaseConnectionOverrides)

            Import-Module WebAdministration

            function Initialize-SlotWebConfig {
                param(
                    [object]$DeploymentPlan,
                    [string]$ExtractedWebConfigPath
                )

                $targetWebConfigPath = Join-Path $DeploymentPlan.TargetPath 'web.config'
                $currentWebConfigPath = if ([string]::IsNullOrWhiteSpace($DeploymentPlan.CurrentPath)) {
                    $null
                }
                else {
                    Join-Path $DeploymentPlan.CurrentPath 'web.config'
                }

                if (-not [string]::IsNullOrWhiteSpace($currentWebConfigPath) -and (Test-Path $currentWebConfigPath)) {
                    Copy-Item $currentWebConfigPath $targetWebConfigPath -Force
                    Write-Host "  Seeded slot web.config from active site" -ForegroundColor Green
                    return
                }

                if (Test-Path $targetWebConfigPath) {
                    Write-Host "  Retaining existing slot web.config" -ForegroundColor Yellow
                    return
                }

                if (-not [string]::IsNullOrWhiteSpace($ExtractedWebConfigPath) -and (Test-Path $ExtractedWebConfigPath)) {
                    Copy-Item $ExtractedWebConfigPath $targetWebConfigPath -Force
                    Write-Host "  Seeded slot web.config from deployment package template" -ForegroundColor Yellow
                    return
                }

                throw "No source web.config was available for $($DeploymentPlan.Name) slot $($DeploymentPlan.TargetSlot). Copy the active site's production web.config into $targetWebConfigPath and rerun the deployment."
            }

            function Set-WebConfigEnvironmentVariableValue {
                param(
                    [string]$WebConfigPath,
                    [string]$Name,
                    [string]$Value
                )

                if (-not (Test-Path $WebConfigPath)) {
                    throw "web.config not found at $WebConfigPath."
                }

                [xml]$config = Get-Content $WebConfigPath

                $aspNetCoreNode = $config.SelectSingleNode("/configuration/location/system.webServer/aspNetCore")
                if ($null -eq $aspNetCoreNode) {
                    throw "Target web.config is missing /configuration/location/system.webServer/aspNetCore."
                }

                $environmentVariablesNode = $config.SelectSingleNode("/configuration/location/system.webServer/aspNetCore/environmentVariables")
                if ($null -eq $environmentVariablesNode) {
                    $environmentVariablesNode = $config.CreateElement("environmentVariables")
                    [void]$aspNetCoreNode.AppendChild($environmentVariablesNode)
                }

                $environmentVariableNode = $config.SelectSingleNode("/configuration/location/system.webServer/aspNetCore/environmentVariables/environmentVariable[@name='$Name']")
                if ($null -eq $environmentVariableNode) {
                    $environmentVariableNode = $config.CreateElement("environmentVariable")
                    $nameAttribute = $config.CreateAttribute("name")
                    $nameAttribute.Value = $Name
                    [void]$environmentVariableNode.Attributes.Append($nameAttribute)

                    $valueAttribute = $config.CreateAttribute("value")
                    $valueAttribute.Value = $Value
                    [void]$environmentVariableNode.Attributes.Append($valueAttribute)

                    [void]$environmentVariablesNode.AppendChild($environmentVariableNode)
                }
                else {
                    $environmentVariableNode.SetAttribute("value", $Value)
                }

                $config.Save($WebConfigPath)
            }

            function Wait-ForHealthyEndpoint {
                param(
                    [string]$Url,
                    [int]$TimeoutSeconds,
                    [string]$AppName,
                    [string]$Phase,
                    [string]$AppPath,
                    [int]$RequiredConsecutiveSuccesses = 2
                )

                Write-Host "  Waiting for $AppName $Phase readiness: $Url" -ForegroundColor Gray

                $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
                $lastError = $null
                $attempt = 0
                $consecutiveSuccesses = 0
                $lastLogTail = $null

                while ((Get-Date) -lt $deadline) {
                    $attempt++
                    $remainingSeconds = [math]::Max([int][math]::Ceiling(($deadline - (Get-Date)).TotalSeconds), 0)

                    try {
                        $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 15
                        if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 300) {
                            $consecutiveSuccesses++

                            if ($consecutiveSuccesses -ge $RequiredConsecutiveSuccesses) {
                                Write-Host "  $AppName $Phase readiness confirmed after $attempt checks." -ForegroundColor Green
                                return [pscustomobject]@{ Success = $true; Message = "HTTP $($response.StatusCode)" }
                            }

                            Write-Host "    [$AppName][$Phase] check $attempt succeeded ($consecutiveSuccesses/$RequiredConsecutiveSuccesses), verifying stability..." -ForegroundColor DarkGray
                            Start-Sleep -Seconds 2
                            continue
                        }

                        $consecutiveSuccesses = 0
                        $lastError = "HTTP $($response.StatusCode)"
                    }
                    catch {
                        $consecutiveSuccesses = 0
                        $lastError = $_.Exception.Message
                    }

                    Write-Host "    [$AppName][$Phase] check $attempt failed, retrying. Remaining: ${remainingSeconds}s. Last result: $lastError" -ForegroundColor DarkYellow

                    if (($attempt % 3) -eq 0) {
                        $stdoutLogTail = Get-LatestStdoutLogTail -AppPath $AppPath
                        if (-not [string]::IsNullOrWhiteSpace($stdoutLogTail) -and $stdoutLogTail -ne $lastLogTail) {
                            Write-Host "    [$AppName][$Phase] latest stdout log:" -ForegroundColor DarkGray
                            Write-Host $stdoutLogTail -ForegroundColor DarkGray
                            $lastLogTail = $stdoutLogTail
                        }
                    }

                    Start-Sleep -Seconds 2
                }

                return [pscustomobject]@{ Success = $false; Message = $lastError }
            }

            function Get-LatestStdoutLogTail {
                param([string]$AppPath)

                $logsPath = Join-Path $AppPath 'logs'
                if (-not (Test-Path $logsPath)) {
                    return $null
                }

                $latestLog = Get-ChildItem -Path $logsPath -Filter 'stdout*' -File -ErrorAction SilentlyContinue |
                Sort-Object LastWriteTimeUtc -Descending |
                Select-Object -First 1

                if ($null -eq $latestLog) {
                    return $null
                }

                $tail = Get-Content -Path $latestLog.FullName -Tail 40 -ErrorAction SilentlyContinue
                if ($null -eq $tail -or $tail.Count -eq 0) {
                    return $latestLog.Name
                }

                return ($latestLog.Name + [Environment]::NewLine + ($tail -join [Environment]::NewLine))
            }

            function Remove-PublicPortBinding {
                param(
                    [string]$SiteName,
                    [int]$Port
                )

                $bindings = Get-WebBinding -Name $SiteName -Protocol 'http' -ErrorAction SilentlyContinue |
                Where-Object { $_.bindingInformation -match "^[^:]*:${Port}:" }

                foreach ($binding in $bindings) {
                    $parts = $binding.bindingInformation.Split(':', 3)
                    $ipAddress = if ([string]::IsNullOrWhiteSpace($parts[0])) { '*' } else { $parts[0] }
                    $hostHeader = if ($parts.Length -gt 2) { $parts[2] } else { '' }

                    Remove-WebBinding -Name $SiteName -Protocol 'http' -Port $Port -IPAddress $ipAddress -HostHeader $hostHeader -ErrorAction SilentlyContinue
                }
            }

            function Ensure-PublicPortBinding {
                param(
                    [string]$SiteName,
                    [int]$Port
                )

                $existingBinding = Get-WebBinding -Name $SiteName -Protocol 'http' -ErrorAction SilentlyContinue |
                Where-Object { $_.bindingInformation -match "^[^:]*:${Port}:" } |
                Select-Object -First 1

                if ($null -eq $existingBinding) {
                    New-WebBinding -Name $SiteName -Protocol 'http' -Port $Port -IPAddress '*' -HostHeader '' | Out-Null
                }
            }

            function Ensure-SiteOnline {
                param(
                    [string]$SiteName,
                    [string]$AppPoolName
                )

                $appPoolState = Get-WebAppPoolState -Name $AppPoolName -ErrorAction SilentlyContinue
                if ($appPoolState.Value -eq 'Stopped') {
                    Start-WebAppPool -Name $AppPoolName -ErrorAction SilentlyContinue | Out-Null
                }

                $siteState = Get-WebsiteState -Name $SiteName -ErrorAction SilentlyContinue
                if ($siteState.Value -ne 'Started') {
                    Start-Website -Name $SiteName -ErrorAction SilentlyContinue | Out-Null
                }
            }

            function Switch-PublicTrafficToSite {
                param(
                    [object]$DeploymentPlan,
                    [string]$DestinationSiteName,
                    [string]$DestinationAppPoolName
                )

                $sitesToClean = @(
                    $DeploymentPlan.CurrentSiteName,
                    $DeploymentPlan.PublicSiteName,
                    $DeploymentPlan.TargetSiteName
                ) |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and $_ -ne $DestinationSiteName } |
                Select-Object -Unique

                foreach ($siteName in $sitesToClean) {
                    if (Test-Path "IIS:\Sites\$siteName") {
                        Remove-PublicPortBinding -SiteName $siteName -Port $DeploymentPlan.PublicPort
                    }
                }

                Ensure-PublicPortBinding -SiteName $DestinationSiteName -Port $DeploymentPlan.PublicPort
                Ensure-SiteOnline -SiteName $DestinationSiteName -AppPoolName $DestinationAppPoolName
            }

            $zipFullPath = "C:\inetpub\$ZipFile"
            $tempPath = "C:\inetpub\ShopInventory-temp-extract-$($Plan.Name.ToLower())"

            try {
                if (Test-Path $tempPath) {
                    Remove-Item -Path $tempPath -Recurse -Force -ErrorAction SilentlyContinue
                }

                $targetSiteState = Get-WebsiteState -Name $Plan.TargetSiteName -ErrorAction SilentlyContinue
                if ($targetSiteState.Value -eq 'Started') {
                    Stop-Website -Name $Plan.TargetSiteName -ErrorAction SilentlyContinue | Out-Null
                }

                $targetPoolStateBeforeDeploy = Get-WebAppPoolState -Name $Plan.TargetAppPoolName -ErrorAction SilentlyContinue
                if ($targetPoolStateBeforeDeploy.Value -ne 'Stopped') {
                    Stop-WebAppPool -Name $Plan.TargetAppPoolName -ErrorAction SilentlyContinue | Out-Null
                }

                New-Item -Path $tempPath -ItemType Directory -Force | Out-Null
                Expand-Archive -Path $zipFullPath -DestinationPath $tempPath -Force

                $null = robocopy $tempPath $Plan.TargetPath /E /MT:8 /IS /IT /XF "web.config" /NFL /NDL /NJH /NJS /R:1 /W:1
                if ($LASTEXITCODE -ge 8) {
                    throw "Robocopy deployment failed with exit code $LASTEXITCODE."
                }

                Initialize-SlotWebConfig -DeploymentPlan $Plan -ExtractedWebConfigPath "$tempPath\web.config"

                $databaseConnectionString = $DatabaseConnectionOverrides[$Plan.Name]
                if (-not [string]::IsNullOrWhiteSpace($databaseConnectionString)) {
                    Set-WebConfigEnvironmentVariableValue -WebConfigPath "$($Plan.TargetPath)\web.config" -Name 'ConnectionStrings__DefaultConnection' -Value $databaseConnectionString
                    Write-Host "  Applied database connection override for $($Plan.Name)" -ForegroundColor Green
                }

                if ((Test-Path "$($Plan.TargetPath)\web.config") -and (Test-Path "$tempPath\web.config")) {
                    try {
                        [xml]$sourceConfig = Get-Content "$tempPath\web.config"
                        [xml]$targetConfig = Get-Content "$($Plan.TargetPath)\web.config"

                        $sourceRule = $sourceConfig.SelectSingleNode("/configuration/location/system.webServer/security/requestFiltering/filteringRules/filteringRule[@name='BlockCrawlerUserAgents']")
                        if ($null -ne $sourceRule) {
                            $systemWebServer = $targetConfig.SelectSingleNode("/configuration/location/system.webServer")
                            if ($null -eq $systemWebServer) {
                                throw "Target web.config is missing /configuration/location/system.webServer."
                            }

                            $securityNode = $targetConfig.SelectSingleNode("/configuration/location/system.webServer/security")
                            if ($null -eq $securityNode) {
                                $securityNode = $targetConfig.CreateElement("security")
                                $handlersNode = $systemWebServer.SelectSingleNode("handlers")
                                if ($null -ne $handlersNode) {
                                    [void]$systemWebServer.InsertBefore($securityNode, $handlersNode)
                                }
                                else {
                                    [void]$systemWebServer.AppendChild($securityNode)
                                }
                            }

                            $requestFilteringNode = $targetConfig.SelectSingleNode("/configuration/location/system.webServer/security/requestFiltering")
                            if ($null -eq $requestFilteringNode) {
                                $requestFilteringNode = $targetConfig.CreateElement("requestFiltering")
                                [void]$securityNode.AppendChild($requestFilteringNode)
                            }

                            $rulesNode = $targetConfig.SelectSingleNode("/configuration/location/system.webServer/security/requestFiltering/filteringRules")
                            if ($null -eq $rulesNode) {
                                $rulesNode = $targetConfig.CreateElement("filteringRules")
                                [void]$requestFilteringNode.AppendChild($rulesNode)
                            }

                            $existingRule = $targetConfig.SelectSingleNode("/configuration/location/system.webServer/security/requestFiltering/filteringRules/filteringRule[@name='BlockCrawlerUserAgents']")
                            if ($null -ne $existingRule) {
                                [void]$rulesNode.RemoveChild($existingRule)
                            }

                            $importedRule = $targetConfig.ImportNode($sourceRule, $true)
                            [void]$rulesNode.AppendChild($importedRule)
                            $targetConfig.Save("$($Plan.TargetPath)\web.config")
                        }
                    }
                    catch {
                        Write-Host "  WARNING: Could not sync managed IIS request filtering rules: $($_.Exception.Message)" -ForegroundColor Yellow
                    }
                }

                $targetPoolState = Get-WebAppPoolState -Name $Plan.TargetAppPoolName -ErrorAction SilentlyContinue
                if ($targetPoolState.Value -eq 'Stopped') {
                    Start-WebAppPool -Name $Plan.TargetAppPoolName -ErrorAction SilentlyContinue | Out-Null
                }

                $targetSiteState = Get-WebsiteState -Name $Plan.TargetSiteName -ErrorAction SilentlyContinue
                if ($targetSiteState.Value -ne 'Started') {
                    Start-Website -Name $Plan.TargetSiteName -ErrorAction SilentlyContinue | Out-Null
                }

                $slotWarmup = Wait-ForHealthyEndpoint -Url $Plan.TargetReadyUrl -TimeoutSeconds $Plan.WarmupTimeoutSeconds -AppName $Plan.Name -Phase 'slot' -AppPath $Plan.TargetPath
                if (-not $slotWarmup.Success) {
                    $stdoutLogTail = Get-LatestStdoutLogTail -AppPath $Plan.TargetPath
                    if ([string]::IsNullOrWhiteSpace($stdoutLogTail)) {
                        throw "Slot warm-up failed for $($Plan.Name): $($slotWarmup.Message)"
                    }

                    throw "Slot warm-up failed for $($Plan.Name): $($slotWarmup.Message)$([Environment]::NewLine)$([Environment]::NewLine)Latest stdout log:$([Environment]::NewLine)$stdoutLogTail"
                }

                Write-Host "  Switching public binding to the healthy slot site..." -ForegroundColor Gray
                Switch-PublicTrafficToSite -DeploymentPlan $Plan -DestinationSiteName $Plan.TargetSiteName -DestinationAppPoolName $Plan.TargetAppPoolName

                $publicWarmup = Wait-ForHealthyEndpoint -Url $Plan.PublicReadyUrl -TimeoutSeconds $Plan.WarmupTimeoutSeconds -AppName $Plan.Name -Phase 'public' -AppPath $Plan.TargetPath
                if (-not $publicWarmup.Success) {
                    Write-Host "  Public cutover failed, rolling back to the previous slot..." -ForegroundColor Yellow

                    if (-not [string]::IsNullOrWhiteSpace($Plan.CurrentAppPoolName) -and (Test-Path "IIS:\AppPools\$($Plan.CurrentAppPoolName)")) {
                        $currentPoolState = Get-WebAppPoolState -Name $Plan.CurrentAppPoolName -ErrorAction SilentlyContinue
                        if ($currentPoolState.Value -eq 'Stopped') {
                            Start-WebAppPool -Name $Plan.CurrentAppPoolName -ErrorAction SilentlyContinue | Out-Null
                        }
                    }

                    if (-not [string]::IsNullOrWhiteSpace($Plan.CurrentSiteName) -and -not [string]::IsNullOrWhiteSpace($Plan.CurrentAppPoolName)) {
                        Switch-PublicTrafficToSite -DeploymentPlan $Plan -DestinationSiteName $Plan.CurrentSiteName -DestinationAppPoolName $Plan.CurrentAppPoolName
                        $null = Wait-ForHealthyEndpoint -Url $Plan.PublicReadyUrl -TimeoutSeconds 60 -AppName $Plan.Name -Phase 'rollback' -AppPath $Plan.CurrentPath
                    }

                    throw "Public cutover failed for $($Plan.Name): $($publicWarmup.Message)"
                }

                $fileCount = (Get-ChildItem -Path $Plan.TargetPath -Recurse -File -ErrorAction SilentlyContinue).Count

                [pscustomobject]@{
                    Name       = $Plan.Name
                    ActiveSlot = $Plan.TargetSlot
                    FileCount  = $fileCount
                    TargetPath = $Plan.TargetPath
                }
            }
            finally {
                Remove-Item -Path $tempPath -Recurse -Force -ErrorAction SilentlyContinue
                Remove-Item -Path $zipFullPath -Force -ErrorAction SilentlyContinue
            }
        } -ArgumentList $zipFileName, $deploymentPlan, $databaseConnectionOverrides -ErrorAction Stop

        $cutoverResults += $cutoverResult

        Write-Host "  $app deployed successfully to slot $($cutoverResult.ActiveSlot)!" -ForegroundColor Green
        Write-Host "  Active path: $($cutoverResult.TargetPath)" -ForegroundColor DarkGray
        Write-Host ""
    }

    Write-Host "Cleaning up local files..." -ForegroundColor Yellow
    Remove-Item -Path $PublishPath -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "  Local cleanup completed!" -ForegroundColor Green
}
catch {
    Write-Host ""
    Write-Host "ERROR: Deployment failed!" -ForegroundColor Red
    Write-Host "Error: $($_.Exception.Message)" -ForegroundColor Red
    Wait-ForExitPrompt
    exit 1
}

Write-Host ""

# Step 5: Verify the public endpoints after cutover
Write-Host "Step 5: Verifying public readiness endpoints..." -ForegroundColor Cyan
Write-Host "----------------------------------------" -ForegroundColor Gray

try {
    $verificationResults = Invoke-Command -ComputerName $ProductionServer -Credential $Credential -Authentication Negotiate -ScriptBlock {
        param($Plans)

        function Test-EndpointStability {
            param(
                [string]$Url,
                [int]$TimeoutSeconds = 45,
                [int]$RequiredConsecutiveSuccesses = 2
            )

            $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
            $attempt = 0
            $consecutiveSuccesses = 0
            $lastError = $null

            while ((Get-Date) -lt $deadline) {
                $attempt++

                try {
                    $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 15
                    if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 300) {
                        $consecutiveSuccesses++
                        if ($consecutiveSuccesses -ge $RequiredConsecutiveSuccesses) {
                            return [pscustomobject]@{
                                Healthy = $true
                                Message = "HTTP $($response.StatusCode) after $attempt checks"
                            }
                        }

                        Start-Sleep -Seconds 2
                        continue
                    }

                    $consecutiveSuccesses = 0
                    $lastError = "HTTP $($response.StatusCode)"
                }
                catch {
                    $consecutiveSuccesses = 0
                    $lastError = $_.Exception.Message
                }

                Start-Sleep -Seconds 2
            }

            return [pscustomobject]@{
                Healthy = $false
                Message = $lastError
            }
        }

        $results = foreach ($plan in $Plans) {
            $status = Test-EndpointStability -Url $plan.PublicReadyUrl
            [pscustomobject]@{
                Name    = $plan.Name
                Healthy = $status.Healthy
                Message = $status.Message
            }
        }

        return $results
    } -ArgumentList (, $deploymentPlans) -ErrorAction Stop

    foreach ($result in $verificationResults) {
        if (-not $result.Healthy) {
            throw "$($result.Name) verification failed: $($result.Message)"
        }

        Write-Host "  $($result.Name): $($result.Message)" -ForegroundColor Green
    }
}
catch {
    Write-Host "WARNING: Post-cutover verification reported a problem." -ForegroundColor Yellow
    Write-Host "Error: $($_.Exception.Message)" -ForegroundColor Red
}

Write-Host ""

# Step 6: Summary
Write-Host "========================================" -ForegroundColor Green
Write-Host "Deployment Completed!" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "Production Server: $ProductionServer" -ForegroundColor White
Write-Host ""
Write-Host "Deployed applications:" -ForegroundColor Cyan
foreach ($result in $cutoverResults) {
    if ($result.Name -eq 'API') {
        Write-Host "  - API: http://$ProductionServer`:5106/swagger (active slot: $($result.ActiveSlot))" -ForegroundColor White
    }
    else {
        Write-Host "  - Web: http://$ProductionServer`:5107/ (active slot: $($result.ActiveSlot))" -ForegroundColor White
    }
}
Write-Host ""
Write-Host "Quick commands:" -ForegroundColor Yellow
Write-Host "  - Restart only: .\Update-Production.ps1 -RestartOnly" -ForegroundColor White
Write-Host "  - Deploy API only: .\Update-Production.ps1 -DeployTarget API" -ForegroundColor White
Write-Host "  - Deploy Web only: .\Update-Production.ps1 -DeployTarget Web" -ForegroundColor White
Write-Host "  - Deploy both IIS nodes: .\Update-Production.ps1 -AdditionalProductionServers 10.10.10.58" -ForegroundColor White
Write-Host "  - Deploy both IIS nodes with separate creds: .\Update-Production.ps1 -AdditionalProductionServers 10.10.10.58 -AdditionalSerializedCredentialPaths <10.10.10.58-cred.xml>" -ForegroundColor White
Write-Host "  - Skip backup: .\Update-Production.ps1 -SkipBackup" -ForegroundColor White
Write-Host "  - Include uploads/logs in backup: .\Update-Production.ps1 -IncludeRuntimeDataInBackup" -ForegroundColor White
Write-Host ""
Write-Host "Logs location:" -ForegroundColor Yellow
foreach ($result in $cutoverResults) {
    Write-Host "  - $($result.Name): \\$ProductionServer\C`$\$($result.TargetPath.Substring(3))\logs" -ForegroundColor White
}
Write-Host ""

Wait-ForExitPrompt
