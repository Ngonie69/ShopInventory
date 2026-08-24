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

$wanted = 'Import-PersistentCredential', 'Get-DeploymentCredential',
          'Export-SerializedCredential', 'Import-SerializedCredential'
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
Write-Host "Per-server credential re-sealing" -ForegroundColor Cyan

# A per-server file given through -AdditionalSerializedCredentialPaths used to be handed straight
# to the child, and -SerializedCredentialPath deletes what it reads. So it worked once and then the
# operator's own file was gone. The fix reads it and re-seals a single-use copy; these cover the
# mechanism that makes that safe.
Check "a re-sealed copy carries the same credential the operator supplied" {
    $secret = ConvertTo-SecureString 'node-58-password' -AsPlainText -Force
    (New-Object System.Management.Automation.PSCredential('KEFALOS\deploy58', $secret)) |
        Export-Clixml -LiteralPath $fixture

    $operatorCopy = Import-PersistentCredential -Path $fixture
    $oneShotPath = Export-SerializedCredential -Credential $operatorCopy
    try {
        $childSees = Import-SerializedCredential -Path $oneShotPath
        if ($childSees.UserName -ne 'KEFALOS\deploy58') { throw "child received '$($childSees.UserName)'" }
        if ($childSees.GetNetworkCredential().Password -ne 'node-58-password') { throw "the password did not survive re-sealing" }
    }
    finally {
        Remove-Item -LiteralPath $oneShotPath -Force -ErrorAction SilentlyContinue
    }
}

Check "the child consuming its copy leaves the operator's file untouched" {
    $oneShotPath = Export-SerializedCredential -Credential (Import-PersistentCredential -Path $fixture)
    $null = Import-SerializedCredential -Path $oneShotPath

    if (Test-Path -LiteralPath $oneShotPath) { throw "the single-use copy was not consumed, so credentials linger in TEMP" }
    if (-not (Test-Path -LiteralPath $fixture)) { throw "the operator's own credential file was deleted - the bug is back" }
}

Write-Host ""
Write-Host "Unattended switches still exist on the script" -ForegroundColor Cyan

Check "the workflow's parameters are all still bindable" {
    $parameters = (Get-Command $ScriptPath).Parameters
    foreach ($name in 'CredentialPath', 'NonInteractive', 'SuppressExitPrompt', 'FailOnVerificationError', 'DeployTarget', 'WebEmailSmtpPassword', 'AdditionalProductionServers') {
        if (-not $parameters.ContainsKey($name)) { throw "-$name is gone; .github/workflows/deploy-production.yml passes it" }
    }
}

Write-Host ""
Write-Host "Multi-server child processes" -ForegroundColor Cyan

# A multi-server run deploys nothing itself: it re-invokes this same script once per server and
# each child does the real work. Any switch missing from that argument list is simply not applied
# to any node, and nothing says so - the parent still prints "Multi-server deployment completed!".
# Three if-statements test this same condition - two of them only pick the wording for the banner.
# The fan-out is the one that builds the child argument list, so match on that rather than on
# position, which would silently start checking a Write-Host block if the banners ever move.
$multiServerBlock = $ast.FindAll({
        param($node)
        $node -is [System.Management.Automation.Language.IfStatementAst] -and
        $node.Clauses[0].Item1.Extent.Text -match 'targetServers\.Count\s+-gt\s+1' -and
        $node.Extent.Text -match '\$argumentList'
    }, $true) | Select-Object -First 1

Check "the multi-server fan-out block is still recognisable" {
    if (-not $multiServerBlock) { throw "could not find the 'if (`$targetServers.Count -gt 1)' block" }
}

# Not $switch: that is an automatic variable in PowerShell, and inside the Check scriptblock it
# resolves to the enumerator rather than to this loop, so every comparison silently reads empty.
foreach ($switchName in '-NonInteractive', '-FailOnVerificationError', '-SuppressExitPrompt') {
    $expected = $switchName
    Check "children are launched with $expected" {
        if (-not $multiServerBlock) { throw "the fan-out block was not found, so this cannot be checked" }
        if ($multiServerBlock.Extent.Text -notmatch [regex]::Escape("'$expected'")) {
            throw "$expected is never passed to the per-server child process"
        }
    }
}

# The functional tests above prove re-sealing preserves the credential and spares the source file.
# This one proves the script actually re-seals, rather than reverting to handing the child the
# operator's own path - which no unit test can catch, because the deletion happens in a child
# process during a real deployment.
Check "the operator's own credential path is never given to a child" {
    if (-not $multiServerBlock) { throw "the fan-out block was not found, so this cannot be checked" }

    $assignments = $multiServerBlock.FindAll({
            param($node)
            $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
            $node.Left.Extent.Text -eq '$childCredentialPath'
        }, $true)

    if ($assignments.Count -eq 0) { throw "nothing assigns `$childCredentialPath any more" }

    foreach ($assignment in $assignments) {
        $right = $assignment.Right.Extent.Text
        if ($right -match 'additionalCredentialPathByServer') {
            throw "`$childCredentialPath is assigned '$right' - the child deletes what it is given, so this consumes the operator's file"
        }
    }
}

Remove-Item -LiteralPath $fixture -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "$script:pass passed, $script:fail failed" -ForegroundColor $(if ($script:fail) { 'Red' } else { 'Green' })
if ($script:fail) { exit 1 }
exit 0
