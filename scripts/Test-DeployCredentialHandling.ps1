# Exercises the credential handling Update-Production.ps1 relies on when it runs unattended from
# the deploy workflow. A break here does not fail a build or a test - it hangs a deployment on a
# credential prompt nobody can see, or silently reports a dead site as deployed.
#
# The function bodies are lifted out of the real script by AST rather than copied, so this cannot
# drift from what actually ships. Nothing here can reach production: every credential path in the
# script resolves before the first Test-Connection, which is what makes the guard safe to trust.

param(
    [string]$ScriptPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'Update-Production.ps1')
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $ScriptPath)) {
    throw "Could not find Update-Production.ps1 at '$ScriptPath'."
}

$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$null, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) {
    $parseErrors | ForEach-Object { Write-Host "  line $($_.Extent.StartLineNumber): $($_.Message)" -ForegroundColor Yellow }
    throw "Update-Production.ps1 does not parse."
}

$wanted = 'Import-PersistentCredential', 'Get-DeploymentCredential'
$source = ($ast.FindAll({
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -in $wanted
        }, $true) | ForEach-Object { $_.Extent.Text }) -join "`n`n"

foreach ($name in $wanted) {
    if ($source -notmatch [regex]::Escape("function $name")) {
        throw "Could not extract $name from $ScriptPath - it was renamed or removed."
    }
}

. ([scriptblock]::Create($source))

$script:pass = 0
$script:fail = 0

function Check {
    param([string]$Name, [scriptblock]$Body)

    try {
        & $Body
        $script:pass++
        Write-Host "  PASS  $Name" -ForegroundColor Green
    }
    catch {
        $script:fail++
        Write-Host "  FAIL  $Name" -ForegroundColor Red
        Write-Host "        $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

function Assert-Throws {
    param([scriptblock]$Body, [string]$Match)

    try {
        & $Body
    }
    catch {
        if ($_.Exception.Message -notmatch $Match) {
            throw "threw, but the message did not match '$Match': $($_.Exception.Message)"
        }
        return
    }

    throw "expected a throw matching '$Match', but the call succeeded"
}

$fixture = Join-Path ([System.IO.Path]::GetTempPath()) "shopinventory-credtest.xml"
$absent = Join-Path ([System.IO.Path]::GetTempPath()) "shopinventory-credtest-absent.xml"
Remove-Item -LiteralPath $absent -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "Import-PersistentCredential" -ForegroundColor Cyan

Check "an unset path means 'not configured', not an error" {
    if ($null -ne (Import-PersistentCredential -Path '')) { throw "expected `$null" }
    if ($null -ne (Import-PersistentCredential -Path $null)) { throw "expected `$null" }
}

Check "a missing file names the path it looked for" {
    Assert-Throws { Import-PersistentCredential -Path $absent } 'Credential file not found'
}

Check "a file that is not a credential is rejected" {
    'just a string' | Export-Clixml -LiteralPath $fixture
    Assert-Throws { Import-PersistentCredential -Path $fixture } 'does not contain a PSCredential'
}

Check "a real credential round-trips with its password intact" {
    $secret = ConvertTo-SecureString 'not-a-real-password' -AsPlainText -Force
    (New-Object System.Management.Automation.PSCredential('KEFALOS\deploy', $secret)) |
        Export-Clixml -LiteralPath $fixture

    $loaded = Import-PersistentCredential -Path $fixture
    if ($loaded -isnot [PSCredential]) { throw "expected a PSCredential, got $($loaded.GetType().Name)" }
    if ($loaded.UserName -ne 'KEFALOS\deploy') { throw "username came back as '$($loaded.UserName)'" }
    if ($loaded.GetNetworkCredential().Password -ne 'not-a-real-password') { throw "the password did not survive the round trip" }
}

# The entire reason this exists rather than reusing Import-SerializedCredential, which deletes the
# file it reads. If that behaviour ever leaks in here, the first deploy works and every later one
# fails with a missing credential file.
Check "reading does not consume the file, so the next deploy still has one" {
    $null = Import-PersistentCredential -Path $fixture
    if (-not (Test-Path -LiteralPath $fixture)) { throw "the file was deleted on read" }
    if ((Import-PersistentCredential -Path $fixture).UserName -ne 'KEFALOS\deploy') {
        throw "the second read did not return the credential"
    }
}

Write-Host ""
Write-Host "Get-DeploymentCredential" -ForegroundColor Cyan

# Matched against the script's own wording, not the word "NonInteractive". PowerShell raises its
# own "PowerShell is in NonInteractive mode" when Get-Credential has no console, so a looser match
# passes even with the guard deleted - which is exactly what it is here to detect.
Check "-NonInteractive fails fast instead of prompting a console nobody is watching" {
    $NonInteractive = $true
    Assert-Throws { Get-DeploymentCredential -Server '10.10.10.9' } 'No deployment credential was supplied'
}

Check "the failure names the switch that fixes it" {
    $NonInteractive = $true
    Assert-Throws { Get-DeploymentCredential -Server '10.10.10.9' } 'CredentialPath'
}

Write-Host ""
Write-Host "Unattended switches still exist on the script" -ForegroundColor Cyan

Check "the workflow's parameters are all still bindable" {
    $parameters = (Get-Command $ScriptPath).Parameters
    foreach ($name in 'CredentialPath', 'NonInteractive', 'SuppressExitPrompt', 'FailOnVerificationError', 'DeployTarget', 'WebEmailSmtpPassword') {
        if (-not $parameters.ContainsKey($name)) { throw "-$name is gone; .github/workflows/deploy-production.yml passes it" }
    }
}

Remove-Item -LiteralPath $fixture -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "$script:pass passed, $script:fail failed" -ForegroundColor $(if ($script:fail) { 'Red' } else { 'Green' })
if ($script:fail) { exit 1 }
exit 0
