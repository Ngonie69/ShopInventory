<#
.SYNOPSIS
    Seals the production deploy credential for an already-registered deploy runner.

.DESCRIPTION
    Steps 5 and 6 of Install-DeployRunner.ps1, on their own: seal the credential as the
    service account, prove it opens, lock it down, publish the path, restart the service.

    Use this when the runner is already registered and running as the right account but the
    credential is missing - an install that failed after registration. Install-DeployRunner.ps1
    refuses to run again in that state, by design: it will not touch a configured runner, and
    re-registering costs a removal token and a fresh registration token.

    Everything runs as the service account through a secondary logon, because Export-Clixml
    seals under whoever writes it and the service is what has to open it later.

.PARAMETER ServiceAccount
    The account the runner service runs as. Must match the service's actual identity - this is
    checked, because sealing under the wrong account produces a file the service cannot read,
    and that failure does not appear until a deployment runs.

.EXAMPLE
    .\scripts\Complete-DeployRunnerSeal.ps1 -ServiceAccount "KFL-DNS2\svc-shopinv-runner"

.NOTES
    Run as Administrator. Full context in docs/operations/github-actions-deploy-runner.md.
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ServiceAccount,
    [string]$DeployCredentialPath = "C:\ProgramData\ShopInventory\deploy.credential.xml",
    [string]$ServiceNamePattern = "actions.runner.*"
)

$ErrorActionPreference = 'Stop'

function Write-Ok   { param($m) Write-Host "  [+] $m" -ForegroundColor Green }
function Write-Bad  { param($m) Write-Host "  [-] $m" -ForegroundColor Red }
function Write-Warn { param($m) Write-Host "  [!] $m" -ForegroundColor Yellow }
function Write-Step { param($m) Write-Host ""; Write-Host "=== $m ===" -ForegroundColor Cyan }

# Runs a script as the service account, feeding it lines on stdin. No temp file: the payload
# goes in as -EncodedCommand, so there is nothing on disk for the service account to be denied
# read access to - which is exactly how the installer's own sealing step fails, writing its
# helper into the calling administrator's Temp folder, which no other account can read.
function Invoke-AsServiceAccount {
    param($Credential, [string]$Body, [string[]]$InputLines)

    $psi = [System.Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = "powershell.exe"
    $arguments = "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " +
        [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($Body))

    # CreateProcessWithLogonW - which is what setting UserName selects - caps the command line
    # at 1024 characters and rejects a longer one with a bare "The parameter is incorrect",
    # thrown before the logon is even attempted. Left unguarded that reads like a credential
    # fault and sends you hunting the wrong thing. Base64 of UTF-16 costs four characters per
    # two of script, so a payload over ~360 characters trips it: keep the bodies terse.
    if ($arguments.Length -gt 1024) {
        throw ("Payload too long: $($arguments.Length) characters of command line, limit 1024. " +
               "Shorten the script body (every comment in it counts) or write it to a file the " +
               "service account can read.")
    }

    $psi.Arguments = $arguments
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $psi.UserName = $Credential.GetNetworkCredential().UserName
    $psi.Domain = $Credential.GetNetworkCredential().Domain
    $psi.Password = $Credential.Password

    $process = [System.Diagnostics.Process]::Start($psi)
    foreach ($line in $InputLines) { $process.StandardInput.WriteLine($line) }
    $process.StandardInput.Close()
    $out = $process.StandardOutput.ReadToEnd().Trim()
    $err = $process.StandardError.ReadToEnd().Trim()
    $process.WaitForExit()

    return [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $out; Error = $err }
}

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Bad "This needs Administrator - it sets a machine-wide variable and restarts a service."
    exit 1
}

Write-Step "Checking the runner service"

$service = Get-Service $ServiceNamePattern -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $service) {
    Write-Bad "No runner service matching '$ServiceNamePattern'. Register the runner first."
    exit 1
}

$startName = (Get-CimInstance Win32_Service -Filter "Name='$($service.Name)'").StartName
$wantLeaf = $ServiceAccount.Split('\')[-1]
$gotLeaf = ([string]$startName).Split('\')[-1]
if ($gotLeaf -ne $wantLeaf) {
    Write-Bad "The service runs as '$startName', not $ServiceAccount."
    Write-Bad "Sealing under a different account produces a file the service cannot read."
    exit 1
}
Write-Ok "$($service.Name) runs as $startName"

Write-Step "Sealing the production deploy credential"

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $DeployCredentialPath) | Out-Null

Write-Host ""
Write-Host "First the SERVICE account's password - the seal is made under it." -ForegroundColor White
$serviceCredential = Get-Credential -UserName $ServiceAccount -Message "Password for $ServiceAccount"
if (-not $serviceCredential) { Write-Bad "No credential supplied."; exit 1 }

$probe = Invoke-AsServiceAccount -Credential $serviceCredential -Body 'exit 0' -InputLines @()
if ($probe.ExitCode -ne 0) {
    Write-Bad "Could not log on as $ServiceAccount. $($probe.Error)"
    exit 1
}
Write-Ok "$ServiceAccount logs on"

Write-Host ""
Write-Host "Now the PRODUCTION deploy account - a DOMAIN administrator on both IIS nodes." -ForegroundColor White
$deployCredential = Get-Credential -Message "Production deploy account (domain administrator on both nodes)"
if (-not $deployCredential) { Write-Bad "No credential supplied."; exit 1 }

if (-not $deployCredential.UserName.Contains('\')) {
    Write-Warn "'$($deployCredential.UserName)' carries no domain prefix. A LOCAL account fails"
    Write-Warn "when the primary deploys itself over loopback WinRM - the token filter strips"
    Write-Warn "its administrator rights. Use DOMAIN\account unless you are sure."
}

# Terse on purpose - see the limit guarded in Invoke-AsServiceAccount. Reads username,
# password and target path from stdin, so no secret ever reaches a command line or a file.
# ProgressPreference is set because progress records reach the parent as CLIXML on stderr,
# where they would bury a real error.
$sealBody = @'
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
$u=[Console]::In.ReadLine()
$p=[Console]::In.ReadLine()
$t=[Console]::In.ReadLine()
(New-Object System.Management.Automation.PSCredential($u,(ConvertTo-SecureString $p -AsPlainText -Force)))|Export-Clixml -LiteralPath $t
'@

$seal = Invoke-AsServiceAccount -Credential $serviceCredential -Body $sealBody -InputLines @(
    $deployCredential.UserName
    $deployCredential.GetNetworkCredential().Password
    $DeployCredentialPath
)
if ($seal.ExitCode -ne 0) { Write-Bad "Sealing failed: $($seal.Error)"; exit 1 }
if (-not (Test-Path -LiteralPath $DeployCredentialPath)) { Write-Bad "No credential file was created."; exit 1 }
Write-Ok "Sealed to $DeployCredentialPath"

Write-Step "Verifying"

$verifyBody = @'
$ErrorActionPreference='Stop'
$ProgressPreference='SilentlyContinue'
$t=[Console]::In.ReadLine()
(Import-Clixml -LiteralPath $t).UserName
'@

$verify = Invoke-AsServiceAccount -Credential $serviceCredential -Body $verifyBody -InputLines @($DeployCredentialPath)
if ($verify.ExitCode -ne 0 -or $verify.Output -ne $deployCredential.UserName) {
    Write-Bad "The service account could not read it back. Got '$($verify.Output)'. $($verify.Error)"
    exit 1
}
Write-Ok "$ServiceAccount reads it back as $($verify.Output)"

& icacls.exe $DeployCredentialPath /inheritance:r /grant "$($serviceCredential.UserName):(R)" | Out-Null
Write-Ok "Readable only by $ServiceAccount"

[Environment]::SetEnvironmentVariable("SHOPINVENTORY_DEPLOY_CREDENTIAL", $DeployCredentialPath, "Machine")
Write-Ok "SHOPINVENTORY_DEPLOY_CREDENTIAL set machine-wide"

# The service reads machine variables when it starts, so it cannot see the one just set.
Restart-Service $service.Name
Write-Ok "$($service.Name) restarted; it can now see SHOPINVENTORY_DEPLOY_CREDENTIAL"

Write-Host ""
Write-Host "Runner ready." -ForegroundColor Green
Write-Host "Trigger the first deployment by hand: Actions -> Deploy to production -> Run workflow." -ForegroundColor White
Write-Host ""
exit 0
