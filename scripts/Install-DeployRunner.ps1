<#
.SYNOPSIS
    Installs the self-hosted GitHub Actions runner that deploys ShopInventory to production.

.DESCRIPTION
    Provisions everything .github/workflows/deploy-production.yml needs on this machine:

      1. Verifies prerequisites - .NET 10 SDK, git, WinRM to both production nodes, GitHub.
      2. Downloads the runner, checking its SHA256 against the published release.
      3. Registers it against the repository with the labels the workflow selects on.
      4. Installs it as a Windows service under a dedicated account.
      5. Creates the DPAPI-sealed deploy credential as that same account.
      6. Reads the credential back, as that account, to prove the seal works.

    You are prompted for exactly two secrets, both typed straight into Windows:
    the service account's password, and the production deploy account's credentials.
    Neither is written to disk in the clear, echoed, or passed through a file.

    Run with -ValidateOnly first. It performs step 1 and nothing else, so it is safe
    to run on any machine you are merely considering.

.PARAMETER ServiceAccount
    The account the runner service runs as, e.g. "KEFALOS\svc-shopinventory-runner".

    Deliberately not LocalSystem, unlike the Fiscalisation runner on this same pattern.
    That runner holds no secrets; this one holds an administrator credential for
    production, and Export-Clixml seals it under the account that wrote it. A named
    account is both the thing that can create that seal and the thing that scopes who
    can open it. It needs no rights on production - the sealed credential supplies those.

.PARAMETER DeployCredentialPath
    Where the sealed production credential is written. The workflow finds it through the
    machine-scoped SHOPINVENTORY_DEPLOY_CREDENTIAL variable, which this script sets.

.PARAMETER ValidateOnly
    Check prerequisites and report. Changes nothing.

.EXAMPLE
    .\scripts\Install-DeployRunner.ps1 -ValidateOnly

.EXAMPLE
    .\scripts\Install-DeployRunner.ps1 -ServiceAccount "KEFALOS\svc-shopinventory-runner"

.NOTES
    Run as Administrator. Full context in docs/operations/github-actions-deploy-runner.md.
#>

[CmdletBinding()]
param(
    [string]$ServiceAccount,
    [string]$Repository = "Ngonie69/ShopInventory",
    [string]$RunnerDirectory = "C:\actions-runner-shopinventory",
    [string]$RunnerName = "$env:COMPUTERNAME-shopinventory-deploy",
    [string[]]$Labels = @("shopinventory-deploy"),
    [string]$DeployCredentialPath = "C:\ProgramData\ShopInventory\deploy.credential.xml",
    [string[]]$ProductionNodes = @("10.10.10.9", "10.10.10.58"),
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'

function Write-Step { param([string]$Message) Write-Host "`n=== $Message ===" -ForegroundColor Cyan }
function Write-Ok { param([string]$Message) Write-Host "  [+] $Message" -ForegroundColor Green }
function Write-Warn { param([string]$Message) Write-Host "  [!] $Message" -ForegroundColor Yellow }
function Write-Bad { param([string]$Message) Write-Host "  [-] $Message" -ForegroundColor Red }

# ============================================================================
# Step 1: prerequisites
# ============================================================================

function Test-Prerequisites {
    Write-Step "Checking prerequisites"

    $problems = [System.Collections.Generic.List[string]]::new()

    # The workflow publishes on this machine, so the SDK has to be here rather than on
    # production. Anchored to the start of the line: "9.0.110" contains no "10." but a
    # substring match on other versions eventually will.
    try {
        $sdks = & dotnet --list-sdks 2>$null
        if ($LASTEXITCODE -ne 0) { throw "dotnet exited $LASTEXITCODE" }
        if ($sdks -match '^10\.') {
            Write-Ok ".NET 10 SDK present"
        }
        else {
            $problems.Add("No .NET 10 SDK. Installed: $($sdks -join '; ')")
        }
    }
    catch {
        $problems.Add("dotnet is not on PATH for this session.")
    }

    try {
        $null = & git --version 2>$null
        if ($LASTEXITCODE -ne 0) { throw }
        Write-Ok "git present"
    }
    catch {
        $problems.Add("git is not on PATH for this session.")
    }

    # Deployment packages travel over WinRM, not SMB, so 5985 is the port that matters.
    foreach ($node in $ProductionNodes) {
        $reachable = Test-NetConnection -ComputerName $node -Port 5985 -WarningAction SilentlyContinue
        if ($reachable.TcpTestSucceeded) {
            Write-Ok "WinRM reachable on $node"
        }
        else {
            $problems.Add("Cannot reach WinRM (5985) on $node. This machine cannot deploy that node.")
        }
    }

    $github = Test-NetConnection -ComputerName "github.com" -Port 443 -WarningAction SilentlyContinue
    if ($github.TcpTestSucceeded) { Write-Ok "github.com reachable" }
    else { $problems.Add("Cannot reach github.com:443. The runner could not collect jobs.") }

    # A laptop makes a poor deployment host: a merge parks a deployment that can only run
    # when the machine happens to be awake and on the corporate network, and the sealed
    # production credential travels in a bag. Worth saying out loud, not worth blocking.
    $chassis = (Get-CimInstance Win32_SystemEnclosure).ChassisTypes
    if ($chassis | Where-Object { $_ -in 8, 9, 10, 14 }) {
        Write-Warn "$env:COMPUTERNAME is a portable machine. Deployments will only run while it is awake and on the LAN."
    }

    if (Get-Service actions.runner.* -ErrorAction SilentlyContinue) {
        $existing = (Get-Service actions.runner.*).Name -join ', '
        Write-Warn "Other runners already on this machine: $existing"
        Write-Warn "That is fine - this one installs alongside them in its own directory."
    }

    return $problems
}

$problems = Test-Prerequisites

if ($problems.Count -gt 0) {
    Write-Host ""
    Write-Bad "Not ready:"
    $problems | ForEach-Object { Write-Bad "    $_" }
    Write-Host ""
    exit 1
}

Write-Host ""
Write-Ok "All prerequisites satisfied."

if ($ValidateOnly) {
    Write-Host ""
    Write-Host "Validation only - nothing was changed." -ForegroundColor Cyan
    Write-Host "Re-run with -ServiceAccount '<DOMAIN\\account>' to install." -ForegroundColor White
    exit 0
}

if ([string]::IsNullOrWhiteSpace($ServiceAccount)) {
    Write-Host ""
    Write-Bad "-ServiceAccount is required to install. See the notes in this script's help."
    exit 1
}

# Checked here rather than through #Requires so that -ValidateOnly stays runnable by anyone.
# Assessing whether a machine is a suitable candidate should not itself need elevation.
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host ""
    Write-Bad "Installing needs Administrator - it creates a service and sets a machine-wide variable."
    Write-Bad "Re-run this from an elevated PowerShell. (-ValidateOnly does not need elevation.)"
    exit 1
}

# ============================================================================
# Step 2: refuse to clobber
# ============================================================================

Write-Step "Checking the target directory"

if (Test-Path -LiteralPath (Join-Path $RunnerDirectory ".runner")) {
    Write-Bad "A configured runner already exists at $RunnerDirectory."
    Write-Bad "Remove it first (.\config.cmd remove --token <token>) or choose another -RunnerDirectory."
    exit 1
}

if (Test-Path -LiteralPath $DeployCredentialPath) {
    Write-Warn "A deploy credential already exists at $DeployCredentialPath."
    Write-Warn "It will be replaced. The old one is unrecoverable once overwritten."
    $answer = Read-Host "Continue? (yes/no)"
    if ($answer -ne 'yes') { Write-Host "Stopped."; exit 1 }
}

New-Item -ItemType Directory -Force -Path $RunnerDirectory | Out-Null
Write-Ok "Using $RunnerDirectory"

# ============================================================================
# Step 3: download, hash-checked
# ============================================================================

Write-Step "Downloading the runner"

$release = Invoke-RestMethod -Uri "https://api.github.com/repos/actions/runner/releases/latest" -Headers @{ 'User-Agent' = 'ShopInventory-Setup' }
$version = $release.tag_name.TrimStart('v')
$archive = "actions-runner-win-x64-$version.zip"
$assetUrl = "https://github.com/actions/runner/releases/download/v$version/$archive"
$archivePath = Join-Path $RunnerDirectory $archive

Write-Host "  version : $version"
Write-Host "  from    : $assetUrl"

Invoke-WebRequest -Uri $assetUrl -OutFile $archivePath -UseBasicParsing
Write-Ok "Downloaded $([Math]::Round((Get-Item $archivePath).Length / 1MB, 1)) MB"

# The release notes carry the expected hash between stable markers. Verify when it is
# there; say so plainly when it is not, rather than implying a check that did not happen.
$expected = $null
if ($release.body -match '(?s)<!--\s*BEGIN SHA win-x64\s*-->\s*([0-9a-fA-F]{64})\s*<!--\s*END SHA win-x64\s*-->') {
    $expected = $Matches[1]
}

$actual = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
if ($expected) {
    if ($actual -ne $expected.ToUpperInvariant()) {
        Write-Bad "SHA256 mismatch. Expected $expected, got $actual."
        Remove-Item -LiteralPath $archivePath -Force
        exit 1
    }
    Write-Ok "SHA256 verified against the published release"
}
else {
    Write-Warn "The release notes carried no published hash; download NOT verified. SHA256 is $actual"
}

Expand-Archive -LiteralPath $archivePath -DestinationPath $RunnerDirectory -Force
Remove-Item -LiteralPath $archivePath -Force
Write-Ok "Extracted"

# ============================================================================
# Step 4: register and install as a service
# ============================================================================

Write-Step "Registering with $Repository"

# Fetched at run time rather than typed: registration tokens expire in an hour, and one
# pasted into a terminal lands in the shell history of a machine that also holds the
# production credential.
try {
    $token = & gh api -X POST "repos/$Repository/actions/runners/registration-token" -q .token 2>$null
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($token)) { throw "gh returned nothing" }
    Write-Ok "Registration token obtained via gh"
}
catch {
    Write-Bad "Could not get a registration token. Is gh installed and authenticated with admin rights on $Repository?"
    Write-Bad "Check with: gh auth status"
    exit 1
}

Write-Host ""
Write-Host "Enter the password for $ServiceAccount (the account the SERVICE runs as," -ForegroundColor White
Write-Host "not the production administrator - that comes next)." -ForegroundColor White
$serviceCredential = Get-Credential -UserName $ServiceAccount -Message "Password for the runner service account $ServiceAccount"
if (-not $serviceCredential) { Write-Bad "No credential supplied."; exit 1 }

Push-Location $RunnerDirectory
try {
    $configArgs = @(
        '--unattended'
        '--url', "https://github.com/$Repository"
        '--token', $token
        '--name', $RunnerName
        '--labels', ($Labels -join ',')
        '--runasservice'
        '--windowslogonaccount', $serviceCredential.UserName
        '--windowslogonpassword', $serviceCredential.GetNetworkCredential().Password
    )

    & .\config.cmd @configArgs
    if ($LASTEXITCODE -ne 0) { throw "config.cmd exited $LASTEXITCODE" }
}
catch {
    Write-Bad "Runner registration failed: $($_.Exception.Message)"
    Pop-Location
    exit 1
}
Pop-Location

$service = Get-Service actions.runner.* | Where-Object { $_.Name -like "*$($Repository.Replace('/', '-'))*" } | Select-Object -First 1
if (-not $service) { Write-Bad "The service was not created."; exit 1 }
if ($service.Status -ne 'Running') { Start-Service $service.Name }
Write-Ok "Service $($service.Name) is $((Get-Service $service.Name).Status)"

# ============================================================================
# Step 5: seal the production credential as the service account
# ============================================================================

Write-Step "Sealing the production deploy credential"

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $DeployCredentialPath) | Out-Null

Write-Host ""
Write-Host "Now the PRODUCTION deploy account - an administrator on:" -ForegroundColor White
$ProductionNodes | ForEach-Object { Write-Host "    $_" -ForegroundColor White }
Write-Host "This is sealed under $ServiceAccount and never leaves this machine." -ForegroundColor White
$deployCredential = Get-Credential -Message "Production deploy account (administrator on $($ProductionNodes -join ' and '))"
if (-not $deployCredential) { Write-Bad "No credential supplied."; exit 1 }

# Export-Clixml seals under whoever runs it, so the export has to happen as the service
# account or the service will not be able to open it. Handing the values over the command
# line would put them in a process listing, so they go through the child's stdin instead.
$exportScript = @'
$ErrorActionPreference = 'Stop'
$user = [Console]::In.ReadLine()
$pass = [Console]::In.ReadLine()
$target = [Console]::In.ReadLine()
$secure = ConvertTo-SecureString $pass -AsPlainText -Force
(New-Object System.Management.Automation.PSCredential($user, $secure)) | Export-Clixml -LiteralPath $target
'@
$exportScriptPath = Join-Path $env:TEMP "shopinventory-seal-$([Guid]::NewGuid().ToString('N')).ps1"
Set-Content -LiteralPath $exportScriptPath -Value $exportScript -Encoding UTF8

try {
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = "powershell.exe"
    $psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$exportScriptPath`""
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardError = $true
    $psi.UserName = $serviceCredential.GetNetworkCredential().UserName
    $psi.Domain = $serviceCredential.GetNetworkCredential().Domain
    $psi.Password = $serviceCredential.Password

    $process = [System.Diagnostics.Process]::Start($psi)
    $process.StandardInput.WriteLine($deployCredential.UserName)
    $process.StandardInput.WriteLine($deployCredential.GetNetworkCredential().Password)
    $process.StandardInput.WriteLine($DeployCredentialPath)
    $process.StandardInput.Close()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()

    if ($process.ExitCode -ne 0) { throw "sealing process exited $($process.ExitCode): $stderr" }
}
finally {
    Remove-Item -LiteralPath $exportScriptPath -Force -ErrorAction SilentlyContinue
}

if (-not (Test-Path -LiteralPath $DeployCredentialPath)) {
    Write-Bad "The credential file was not created."
    exit 1
}
Write-Ok "Sealed to $DeployCredentialPath"

& icacls.exe $DeployCredentialPath /inheritance:r /grant "$($serviceCredential.UserName):(R)" | Out-Null
Write-Ok "Readable only by $ServiceAccount"

[Environment]::SetEnvironmentVariable("SHOPINVENTORY_DEPLOY_CREDENTIAL", $DeployCredentialPath, "Machine")
Write-Ok "SHOPINVENTORY_DEPLOY_CREDENTIAL set machine-wide"

# ============================================================================
# Step 6: prove the seal opens for the service, and only for it
# ============================================================================

Write-Step "Verifying"

$verifyScript = @'
$ErrorActionPreference = 'Stop'
$target = [Console]::In.ReadLine()
(Import-Clixml -LiteralPath $target).UserName
'@
$verifyScriptPath = Join-Path $env:TEMP "shopinventory-verify-$([Guid]::NewGuid().ToString('N')).ps1"
Set-Content -LiteralPath $verifyScriptPath -Value $verifyScript -Encoding UTF8

try {
    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = "powershell.exe"
    $psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$verifyScriptPath`""
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.UserName = $serviceCredential.GetNetworkCredential().UserName
    $psi.Domain = $serviceCredential.GetNetworkCredential().Domain
    $psi.Password = $serviceCredential.Password

    $process = [System.Diagnostics.Process]::Start($psi)
    $process.StandardInput.WriteLine($DeployCredentialPath)
    $process.StandardInput.Close()
    $readBack = $process.StandardOutput.ReadToEnd().Trim()
    $process.WaitForExit()

    if ($process.ExitCode -ne 0 -or $readBack -ne $deployCredential.UserName) {
        Write-Bad "The service account could not read the credential back. Got '$readBack'."
        exit 1
    }
    Write-Ok "$ServiceAccount reads it back as $readBack"
}
finally {
    Remove-Item -LiteralPath $verifyScriptPath -Force -ErrorAction SilentlyContinue
}

# Restart so the service picks up the machine variable set above - it does not see it otherwise.
Restart-Service $service.Name
Write-Ok "Service restarted; it can now see SHOPINVENTORY_DEPLOY_CREDENTIAL"

Write-Host ""
Write-Host "========================================" -ForegroundColor Green
Write-Host "Runner ready" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Green
Write-Host ""
Write-Host "  runner   : $RunnerName" -ForegroundColor White
Write-Host "  labels   : self-hosted, windows, $($Labels -join ', ')" -ForegroundColor White
Write-Host "  service  : $($service.Name) as $ServiceAccount" -ForegroundColor White
Write-Host "  credential: $DeployCredentialPath" -ForegroundColor White
Write-Host ""
Write-Host "Still to do, in GitHub - the runner does nothing without it:" -ForegroundColor Yellow
Write-Host "  Settings -> Environments -> New environment, named exactly 'production'," -ForegroundColor White
Write-Host "  then tick Required reviewers and add who may release." -ForegroundColor White
Write-Host ""
Write-Host "Without that environment the deployment runs on merge with no gate at all." -ForegroundColor Yellow
Write-Host ""
Write-Host "Then: Actions -> Deploy to production -> Run workflow, and approve it once by hand." -ForegroundColor White
Write-Host ""

exit 0
