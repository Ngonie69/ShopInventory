# Update Production Server - Automated Script
# Production Server: 10.10.10.9
# This script publishes ShopInventory API and Web apps and deploys them to the production IIS server

param(
    [string]$ProductionServer = "10.10.10.9",
    [string]$ApiAppPoolName = "ShopInventoryAPI",
    [string]$WebAppPoolName = "ShopInventoryWeb",
    [string]$ApiSiteName = "ShopInventory-API",
    [string]$WebSiteName = "ShopInventory-Web",
    [string]$ApiRemotePath = "C:\inetpub\ShopInventory-API",
    [string]$WebRemotePath = "C:\inetpub\ShopInventory-Web",
    [ValidateSet("Both", "API", "Web")]
    [string]$DeployTarget = "Both",
    [switch]$SkipBackup,
    [switch]$RestartOnly,
    [switch]$FirstTimeSetup,
    [PSCredential]$Credential
)

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "ShopInventory - Production Deployment" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Server: $ProductionServer" -ForegroundColor White
Write-Host "Target: $DeployTarget" -ForegroundColor White
Write-Host ""

# Get credentials for production server if not provided
if (-not $Credential) {
    # Use stored credentials for production server
    $securePassword = ConvertTo-SecureString "C@llofduty69?!" -AsPlainText -Force
    $Credential = New-Object System.Management.Automation.PSCredential("KEFALOS\Ngoni.Mutambirwa", $securePassword)
    Write-Host "Using stored credentials for KEFALOS\Ngoni.Mutambirwa" -ForegroundColor Green
    Write-Host ""
}

# Check if running as Administrator and elevate if needed
$isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host "Not running as Administrator. Elevating..." -ForegroundColor Yellow
    
    # Build argument list to pass parameters to elevated script
    $argList = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    if ($ProductionServer -ne "10.10.10.9") { $argList += " -ProductionServer `"$ProductionServer`"" }
    if ($ApiAppPoolName -ne "ShopInventoryAPI") { $argList += " -ApiAppPoolName `"$ApiAppPoolName`"" }
    if ($WebAppPoolName -ne "ShopInventoryWeb") { $argList += " -WebAppPoolName `"$WebAppPoolName`"" }
    if ($DeployTarget -ne "Both") { $argList += " -DeployTarget `"$DeployTarget`"" }
    if ($SkipBackup) { $argList += " -SkipBackup" }
    if ($RestartOnly) { $argList += " -RestartOnly" }
    if ($FirstTimeSetup) { $argList += " -FirstTimeSetup" }
    
    Start-Process powershell.exe -Verb RunAs -ArgumentList $argList
    exit
}

# Test connection to production server
Write-Host "Testing connection to production server..." -ForegroundColor Yellow
$pingResult = Test-Connection -ComputerName $ProductionServer -Count 2 -Quiet
if (-not $pingResult) {
    Write-Host "ERROR: Cannot reach production server at $ProductionServer" -ForegroundColor Red
    Write-Host "Please check network connectivity and server address." -ForegroundColor Yellow
    Read-Host "Press Enter to exit"
    exit 1
}
Write-Host "Connection successful!" -ForegroundColor Green
Write-Host ""

# Establish network connection to admin share using net use (more reliable for cross-domain)
Write-Host "Establishing file share connection..." -ForegroundColor Yellow
$netUser = $Credential.UserName
$netPass = $Credential.GetNetworkCredential().Password
$null = net use "\\$ProductionServer\C`$" /delete /y 2>$null
$netResult = net use "\\$ProductionServer\C`$" /user:$netUser $netPass 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Could not connect to \\$ProductionServer\C`$" -ForegroundColor Red
    Write-Host "Error: $netResult" -ForegroundColor Red
    Write-Host ""
    Write-Host "Make sure:" -ForegroundColor Yellow
    Write-Host "  1. Admin shares (C`$) are enabled on the server" -ForegroundColor White
    Write-Host "  2. Your account has admin rights on the server" -ForegroundColor White
    Write-Host "  3. Windows Firewall allows File and Printer Sharing" -ForegroundColor White
    Read-Host "Press Enter to exit"
    exit 1
}
Write-Host "File share connection established!" -ForegroundColor Green
Write-Host ""

# Maintenance page HTML content
$MaintenancePageHtml = @"
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>Shop Inventory - System Maintenance</title>
    <style>
        * {
            margin: 0;
            padding: 0;
            box-sizing: border-box;
        }
        
        body { 
            font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif;
            display: flex; 
            justify-content: center; 
            align-items: center; 
            min-height: 100vh;
            background: linear-gradient(135deg, #1e3c72 0%, #2a5298 50%, #1e3c72 100%);
            background-size: 400% 400%;
            animation: gradientShift 15s ease infinite;
            padding: 20px;
            overflow: hidden;
        }
        
        @keyframes gradientShift {
            0%, 100% { background-position: 0% 50%; }
            50% { background-position: 100% 50%; }
        }
        
        .background-shapes {
            position: fixed;
            top: 0;
            left: 0;
            width: 100%;
            height: 100%;
            overflow: hidden;
            z-index: 0;
        }
        
        .shape {
            position: absolute;
            border-radius: 50%;
            background: rgba(255, 255, 255, 0.05);
            animation: float 20s infinite ease-in-out;
        }
        
        .shape:nth-child(1) {
            width: 300px;
            height: 300px;
            top: -150px;
            left: -150px;
            animation-delay: 0s;
        }
        
        .shape:nth-child(2) {
            width: 200px;
            height: 200px;
            bottom: -100px;
            right: -100px;
            animation-delay: 5s;
        }
        
        .shape:nth-child(3) {
            width: 250px;
            height: 250px;
            top: 50%;
            right: -125px;
            animation-delay: 10s;
        }
        
        @keyframes float {
            0%, 100% { transform: translate(0, 0) rotate(0deg); }
            33% { transform: translate(30px, -50px) rotate(120deg); }
            66% { transform: translate(-20px, 20px) rotate(240deg); }
        }
        
        .container { 
            position: relative;
            z-index: 1;
            text-align: center; 
            padding: 60px 50px;
            background: rgba(255, 255, 255, 0.15);
            border-radius: 24px;
            backdrop-filter: blur(20px);
            box-shadow: 0 8px 32px rgba(0, 0, 0, 0.1),
                        inset 0 1px 0 rgba(255, 255, 255, 0.2);
            border: 1px solid rgba(255, 255, 255, 0.18);
            max-width: 550px;
            animation: fadeIn 0.6s ease-out;
        }
        
        @keyframes fadeIn {
            from { 
                opacity: 0; 
                transform: translateY(20px); 
            }
            to { 
                opacity: 1; 
                transform: translateY(0); 
            }
        }
        
        .logo {
            font-size: 4rem;
            margin-bottom: 20px;
            animation: pulse 2s ease-in-out infinite;
        }
        
        @keyframes pulse {
            0%, 100% { transform: scale(1); }
            50% { transform: scale(1.05); }
        }
        
        h1 { 
            font-size: 2.2rem;
            margin-bottom: 12px;
            color: white;
            font-weight: 600;
            text-shadow: 0 2px 10px rgba(0, 0, 0, 0.2);
        }
        
        .subtitle {
            font-size: 1.1rem;
            color: rgba(255, 255, 255, 0.9);
            margin-bottom: 35px;
            font-weight: 400;
        }
        
        .spinner-container {
            margin: 35px 0;
            display: flex;
            justify-content: center;
            align-items: center;
            gap: 12px;
        }
        
        .spinner {
            width: 14px;
            height: 14px;
            background: white;
            border-radius: 50%;
            animation: bounce 1.4s infinite ease-in-out both;
        }
        
        .spinner:nth-child(1) { animation-delay: -0.32s; }
        .spinner:nth-child(2) { animation-delay: -0.16s; }
        
        @keyframes bounce {
            0%, 80%, 100% { 
                transform: scale(0);
                opacity: 0.5;
            }
            40% { 
                transform: scale(1);
                opacity: 1;
            }
        }
        
        .message { 
            font-size: 1rem;
            line-height: 1.6;
            color: rgba(255, 255, 255, 0.95);
            margin-bottom: 25px;
        }
        
        .refresh-btn {
            display: inline-block;
            padding: 14px 32px;
            background: rgba(255, 255, 255, 0.25);
            color: white;
            text-decoration: none;
            border-radius: 50px;
            font-weight: 600;
            font-size: 0.95rem;
            transition: all 0.3s ease;
            border: 1px solid rgba(255, 255, 255, 0.3);
            cursor: pointer;
            margin-top: 10px;
        }
        
        .refresh-btn:hover {
            background: rgba(255, 255, 255, 0.35);
            transform: translateY(-2px);
            box-shadow: 0 6px 20px rgba(0, 0, 0, 0.2);
        }
        
        .footer {
            margin-top: 30px;
            font-size: 0.85rem;
            color: rgba(255, 255, 255, 0.7);
        }
        
        @media (max-width: 600px) {
            .container {
                padding: 40px 30px;
            }
            h1 {
                font-size: 1.8rem;
            }
            .logo {
                font-size: 3rem;
            }
        }
    </style>
</head>
<body>
    <div class="background-shapes">
        <div class="shape"></div>
        <div class="shape"></div>
        <div class="shape"></div>
    </div>
    
    <div class="container">
        <div class="logo">&#128230;</div>
        <h1>System Maintenance</h1>
        <p class="subtitle">We're making improvements</p>
        
        <div class="spinner-container">
            <div class="spinner"></div>
            <div class="spinner"></div>
            <div class="spinner"></div>
        </div>
        
        <p class="message">
            Shop Inventory is currently being updated with new features and improvements.<br>
            This will only take a moment.
        </p>
        
        <a href="javascript:location.reload()" class="refresh-btn">
            &#8635; Refresh Page
        </a>
        
        <div class="footer">
            Expected downtime: less than 2 minutes
        </div>
    </div>
    
    <script>
        // Auto-refresh every 10 seconds
        setTimeout(function() {
            location.reload();
        }, 10000);
    </script>
</body>
</html>
"@

# Function to create maintenance page
function Set-MaintenancePage {
    param(
        [string]$Path,
        [string]$AppName
    )
    
    try {
        $appOfflinePath = "\\$ProductionServer\C`$\$($Path.Substring(3))\app_offline.htm"
        [System.IO.File]::WriteAllText($appOfflinePath, $MaintenancePageHtml, [System.Text.UTF8Encoding]::new($false))
        Write-Host "  Maintenance page created for $AppName" -ForegroundColor Green
        return $true
    }
    catch {
        Write-Host "  WARNING: Could not create maintenance page for $AppName" -ForegroundColor Yellow
        return $false
    }
}

# Function to remove maintenance page
function Remove-MaintenancePage {
    param(
        [string]$Path,
        [string]$AppName
    )
    
    try {
        $appOfflinePath = "\\$ProductionServer\C`$\$($Path.Substring(3))\app_offline.htm"
        if (Test-Path $appOfflinePath) {
            Remove-Item -Path $appOfflinePath -Force
            Write-Host "  Maintenance page removed for $AppName" -ForegroundColor Green
        }
    }
    catch {
        Write-Host "  WARNING: Could not remove maintenance page for $AppName" -ForegroundColor Yellow
    }
}

# If RestartOnly flag is set, just restart and exit
if ($RestartOnly) {
    Write-Host "Restart-only mode - no deployment" -ForegroundColor Yellow
    Write-Host ""
    
    try {
        $appPools = @()
        if ($DeployTarget -eq "Both" -or $DeployTarget -eq "API") { $appPools += $ApiAppPoolName }
        if ($DeployTarget -eq "Both" -or $DeployTarget -eq "Web") { $appPools += $WebAppPoolName }
        
        foreach ($pool in $appPools) {
            Write-Host "Recycling application pool: $pool..." -ForegroundColor Yellow
            Invoke-Command -ComputerName $ProductionServer -Credential $Credential -ScriptBlock {
                param($AppPool)
                Import-Module WebAdministration
                Restart-WebAppPool -Name $AppPool -ErrorAction Stop
            } -ArgumentList $pool -ErrorAction Stop
            Write-Host "  $pool recycled successfully!" -ForegroundColor Green
        }
        
        Write-Host ""
        Write-Host "Application(s) restarted successfully!" -ForegroundColor Green
    }
    catch {
        Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
        Write-Host "Try using IIS Manager on the production server manually." -ForegroundColor Yellow
    }
    
    Read-Host "Press Enter to exit"
    exit 0
}

# First-time setup on production server
if ($FirstTimeSetup) {
    Write-Host "First-time setup mode - configuring IIS on production server..." -ForegroundColor Yellow
    Write-Host ""
    
    try {
        Invoke-Command -ComputerName $ProductionServer -Credential $Credential -ScriptBlock {
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
    
    Read-Host "Press Enter to exit"
    exit 0
}

# ============================================
# MAIN DEPLOYMENT PROCESS
# ============================================

$ScriptDir = $PSScriptRoot
$RootDir = $ScriptDir
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
        Read-Host "Press Enter to exit"
        exit 1
    }
    Write-Host "  API published successfully!" -ForegroundColor Green
    $publishedApps += "API"
}

if ($DeployTarget -eq "Both" -or $DeployTarget -eq "Web") {
    Write-Host "Publishing Web app..." -ForegroundColor White
    dotnet publish $WebProjectPath -c Release -o "$PublishPath\web" --no-self-contained
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: Web publish failed!" -ForegroundColor Red
        Read-Host "Press Enter to exit"
        exit 1
    }
    Write-Host "  Web app published successfully!" -ForegroundColor Green
    $publishedApps += "Web"
}

Write-Host ""

# Step 2: Create maintenance pages and take apps offline
Write-Host "Step 2: Taking application(s) offline..." -ForegroundColor Cyan
Write-Host "----------------------------------------" -ForegroundColor Gray

try {
    # Network connection already established with net use, use direct UNC paths
    if ($DeployTarget -eq "Both" -or $DeployTarget -eq "API") {
        Set-MaintenancePage -Path $ApiRemotePath -AppName "API"
    }
    if ($DeployTarget -eq "Both" -or $DeployTarget -eq "Web") {
        Set-MaintenancePage -Path $WebRemotePath -AppName "Web"
    }
    
    Write-Host "Waiting for IIS to detect maintenance pages..." -ForegroundColor Yellow
    Start-Sleep -Seconds 5
    Write-Host "Applications are now offline." -ForegroundColor Green
}
catch {
    Write-Host "WARNING: Could not create maintenance pages: $($_.Exception.Message)" -ForegroundColor Yellow
}

Write-Host ""

# Step 3: Create backup
if (-not $SkipBackup) {
    Write-Host "Step 3: Creating backup..." -ForegroundColor Cyan
    Write-Host "----------------------------------------" -ForegroundColor Gray
    
    try {
        Invoke-Command -ComputerName $ProductionServer -Credential $Credential -ScriptBlock {
            param($ApiPath, $WebPath, $DeployTarget)
            
            $BackupBase = "C:\inetpub\ShopInventory-backup-latest"
            
            if ($DeployTarget -eq "Both" -or $DeployTarget -eq "API") {
                if (Test-Path $ApiPath) {
                    $ApiBackup = "$BackupBase-API"
                    if (Test-Path $ApiBackup) { Remove-Item -Path $ApiBackup -Recurse -Force -ErrorAction SilentlyContinue }
                    Write-Host "  Backing up API..." -ForegroundColor Gray
                    $null = robocopy $ApiPath $ApiBackup /MIR /MT:8 /NFL /NDL /NJH /NJS /R:1 /W:1
                    Write-Host "  API backup created" -ForegroundColor Green
                }
            }
            
            if ($DeployTarget -eq "Both" -or $DeployTarget -eq "Web") {
                if (Test-Path $WebPath) {
                    $WebBackup = "$BackupBase-Web"
                    if (Test-Path $WebBackup) { Remove-Item -Path $WebBackup -Recurse -Force -ErrorAction SilentlyContinue }
                    Write-Host "  Backing up Web..." -ForegroundColor Gray
                    $null = robocopy $WebPath $WebBackup /MIR /MT:8 /NFL /NDL /NJH /NJS /R:1 /W:1
                    Write-Host "  Web backup created" -ForegroundColor Green
                }
            }
        } -ArgumentList $ApiRemotePath, $WebRemotePath, $DeployTarget -ErrorAction Stop
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

# Step 4: Package and deploy
Write-Host "Step 4: Packaging and deploying to production..." -ForegroundColor Cyan
Write-Host "----------------------------------------" -ForegroundColor Gray

try {
    $timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    
    foreach ($app in $publishedApps) {
        $appLower = $app.ToLower()
        $sourcePath = "$PublishPath\$appLower"
        $remotePath = if ($app -eq "API") { $ApiRemotePath } else { $WebRemotePath }
        $zipFileName = "ShopInventory-$app-$timestamp.zip"
        $zipPath = "$PublishPath\$zipFileName"
        
        Write-Host "Deploying $app..." -ForegroundColor White
        
        # Create zip
        Write-Host "  Creating deployment package..." -ForegroundColor Gray
        Compress-Archive -Path "$sourcePath\*" -DestinationPath $zipPath -Force
        $zipSize = [math]::Round((Get-Item $zipPath).Length / 1MB, 2)
        Write-Host "  Package created: $zipFileName ($zipSize MB)" -ForegroundColor Green
        
        # Upload with progress
        Write-Host "  Uploading to production server..." -ForegroundColor Gray
        $sourceStream = [System.IO.File]::OpenRead($zipPath)
        $destStream = [System.IO.File]::Create("\\$ProductionServer\C`$\inetpub\$zipFileName")
        
        $buffer = New-Object byte[] 1048576  # 1MB buffer
        $totalBytes = $sourceStream.Length
        $copiedBytes = 0
        
        while (($bytesRead = $sourceStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            $destStream.Write($buffer, 0, $bytesRead)
            $copiedBytes += $bytesRead
            
            $percentComplete = [math]::Round(($copiedBytes / $totalBytes) * 100, 0)
            $progressBar = "[" + ("=" * [math]::Floor($percentComplete / 2)) + (" " * (50 - [math]::Floor($percentComplete / 2))) + "]"
            $copiedMB = [math]::Round($copiedBytes / 1MB, 1)
            
            Write-Host "`r    $progressBar $percentComplete% ($copiedMB MB / $zipSize MB)" -NoNewline -ForegroundColor Cyan
        }
        
        $sourceStream.Close()
        $destStream.Close()
        Write-Host ""
        Write-Host "  Upload complete!" -ForegroundColor Green
        
        # Extract on remote server
        Write-Host "  Extracting and deploying files..." -ForegroundColor Gray
        Invoke-Command -ComputerName $ProductionServer -Credential $Credential -ScriptBlock {
            param($ZipFile, $TargetPath)
            
            $zipFullPath = "C:\inetpub\$ZipFile"
            $tempPath = "C:\inetpub\ShopInventory-temp-extract"
            
            # Extract to temp
            if (Test-Path $tempPath) { Remove-Item -Path $tempPath -Recurse -Force -ErrorAction SilentlyContinue }
            New-Item -Path $tempPath -ItemType Directory -Force | Out-Null
            Expand-Archive -Path $zipFullPath -DestinationPath $tempPath -Force
            
            # Deploy with robocopy (preserve web.config in target)
            $null = robocopy $tempPath $TargetPath /E /MT:8 /IS /IT /XF "web.config" /NFL /NDL /NJH /NJS /R:1 /W:1
            
            # If web.config doesn't exist in target, copy it
            if (-not (Test-Path "$TargetPath\web.config") -and (Test-Path "$tempPath\web.config")) {
                Copy-Item "$tempPath\web.config" "$TargetPath\web.config"
            }
            
            $fileCount = (Get-ChildItem -Path $TargetPath -Recurse -File -ErrorAction SilentlyContinue).Count
            Write-Host "  Deployed $fileCount files" -ForegroundColor Green
            
            # Cleanup
            Remove-Item -Path $tempPath -Recurse -Force -ErrorAction SilentlyContinue
            Remove-Item -Path $zipFullPath -Force -ErrorAction SilentlyContinue
        } -ArgumentList $zipFileName, $remotePath -ErrorAction Stop
        
        Write-Host "  $app deployed successfully!" -ForegroundColor Green
        Write-Host ""
    }
    
    # Cleanup local files
    Write-Host "Cleaning up local files..." -ForegroundColor Yellow
    Remove-Item -Path $PublishPath -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "  Local cleanup completed!" -ForegroundColor Green
}
catch {
    Write-Host ""
    Write-Host "ERROR: Deployment failed!" -ForegroundColor Red
    Write-Host "Error: $($_.Exception.Message)" -ForegroundColor Red
    
    # Cleanup on error
    try { Remove-PSDrive -Name "ProdDeploy" -Force -ErrorAction SilentlyContinue } catch {}
    
    Read-Host "Press Enter to exit"
    exit 1
}

Write-Host ""

# Step 5: Bring applications back online
Write-Host "Step 5: Bringing applications online..." -ForegroundColor Cyan
Write-Host "----------------------------------------" -ForegroundColor Gray

try {
    # Remove maintenance pages (network connection already established)
    if ($DeployTarget -eq "Both" -or $DeployTarget -eq "API") {
        Remove-MaintenancePage -Path $ApiRemotePath -AppName "API"
    }
    if ($DeployTarget -eq "Both" -or $DeployTarget -eq "Web") {
        Remove-MaintenancePage -Path $WebRemotePath -AppName "Web"
    }
    
    # Recycle app pools
    Write-Host "Recycling application pools..." -ForegroundColor Yellow
    Invoke-Command -ComputerName $ProductionServer -Credential $Credential -ScriptBlock {
        param($ApiPool, $WebPool, $DeployTarget)
        
        Import-Module WebAdministration
        
        if ($DeployTarget -eq "Both" -or $DeployTarget -eq "API") {
            $state = Get-WebAppPoolState -Name $ApiPool -ErrorAction SilentlyContinue
            if ($state.Value -eq "Stopped") {
                Start-WebAppPool -Name $ApiPool -ErrorAction SilentlyContinue
            }
            else {
                Restart-WebAppPool -Name $ApiPool -ErrorAction SilentlyContinue
            }
            Write-Host "  $ApiPool recycled" -ForegroundColor Green
        }
        
        if ($DeployTarget -eq "Both" -or $DeployTarget -eq "Web") {
            $state = Get-WebAppPoolState -Name $WebPool -ErrorAction SilentlyContinue
            if ($state.Value -eq "Stopped") {
                Start-WebAppPool -Name $WebPool -ErrorAction SilentlyContinue
            }
            else {
                Restart-WebAppPool -Name $WebPool -ErrorAction SilentlyContinue
            }
            Write-Host "  $WebPool recycled" -ForegroundColor Green
        }
    } -ArgumentList $ApiAppPoolName, $WebAppPoolName, $DeployTarget -ErrorAction Stop
    
    Write-Host "Waiting for applications to initialize..." -ForegroundColor Yellow
    Start-Sleep -Seconds 5
    Write-Host "Applications are now live!" -ForegroundColor Green
}
catch {
    Write-Host "WARNING: Could not restart applications automatically." -ForegroundColor Yellow
    Write-Host "Error: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ""
    Write-Host "Please restart manually via IIS Manager on $ProductionServer" -ForegroundColor Yellow
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
foreach ($app in $publishedApps) {
    if ($app -eq "API") {
        Write-Host "  - API: http://$ProductionServer`:5106/swagger" -ForegroundColor White
    }
    else {
        Write-Host "  - Web: http://$ProductionServer`:5107/" -ForegroundColor White
    }
}
Write-Host ""
Write-Host "Quick commands:" -ForegroundColor Yellow
Write-Host "  - Restart only: .\Update-Production.ps1 -RestartOnly" -ForegroundColor White
Write-Host "  - Deploy API only: .\Update-Production.ps1 -DeployTarget API" -ForegroundColor White
Write-Host "  - Deploy Web only: .\Update-Production.ps1 -DeployTarget Web" -ForegroundColor White
Write-Host "  - Skip backup: .\Update-Production.ps1 -SkipBackup" -ForegroundColor White
Write-Host ""
Write-Host "Logs location:" -ForegroundColor Yellow
Write-Host "  - API: \\$ProductionServer\C`$\inetpub\ShopInventory-API\logs\" -ForegroundColor White
Write-Host "  - Web: \\$ProductionServer\C`$\inetpub\ShopInventory-Web\logs\" -ForegroundColor White
Write-Host ""

Read-Host "Press Enter to exit"
