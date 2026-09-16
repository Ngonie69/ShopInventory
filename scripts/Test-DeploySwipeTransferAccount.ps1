# Exercises how Update-Production.ps1 gives a blue/green API slot its card settlement account.
#
# SAP__SwipeTransferAccount is operator-set, not packaged, so a fresh slot's web.config arrives without
# it. Until it is carried forward from the live slot, every deployment would silently take card
# settlement back to Unmapped - invoices raised and never settled, which is how 596.83 of counted card
# money sat open on 2026-09-15.
#
# The functions are lifted out of the real script by AST, as Test-DeploySlotSeeding.ps1 does, and run
# against throwaway folders standing in for the active and idle slots. Nothing here touches IIS or
# reaches production.

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

foreach ($name in 'Set-SlotSwipeTransferAccount', 'Set-WebConfigEnvironmentVariableValue', 'Get-WebConfigEnvironmentVariableValue') {
    $definition = $ast.Find({
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
        }.GetNewClosure(), $true)

    if (-not $definition) {
        throw "Could not extract $name from $ScriptPath - it was renamed or removed."
    }

    . ([scriptblock]::Create($definition.Extent.Text))
}

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

$root = Join-Path ([System.IO.Path]::GetTempPath()) ("shopinventory-swipeacct-" + [Guid]::NewGuid().ToString('N'))

# The shape a published API's web.config has: aspNetCore with an environmentVariables element.
$template = @'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <location path="." inheritInChildApplications="false">
    <system.webServer>
      <aspNetCore processPath="dotnet" arguments=".\ShopInventory.dll" hostingModel="inprocess">
        <environmentVariables>
          <environmentVariable name="ASPNETCORE_ENVIRONMENT" value="Production" />
        </environmentVariables>
      </aspNetCore>
    </system.webServer>
  </location>
</configuration>
'@

function New-Case {
    param([string]$Name, [string]$AccountOnLiveSlot)

    $paths = @{}
    foreach ($part in 'active', 'idle') {
        $dir = Join-Path (Join-Path $root $Name) $part
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $dir 'web.config') -Value $template
        $paths[$part] = $dir
    }

    if ($AccountOnLiveSlot) {
        Set-WebConfigEnvironmentVariableValue -WebConfigPath (Join-Path $paths.active 'web.config') -Name 'SAP__SwipeTransferAccount' -Value $AccountOnLiveSlot
    }

    [pscustomobject]@{ Name = 'API'; TargetSlot = 'Blue'; CurrentPath = $paths.active; TargetPath = $paths.idle }
}

function Read-TargetAccount {
    param([object]$Plan)
    Get-WebConfigEnvironmentVariableValue -WebConfigPath (Join-Path $Plan.TargetPath 'web.config') -Name 'SAP__SwipeTransferAccount'
}

try {
    Write-Host "`nCard settlement account on a blue/green slot" -ForegroundColor Cyan

    Check 'A value passed to the deploy is applied to the slot going live' {
        $plan = New-Case -Name 'from-input' -AccountOnLiveSlot $null
        $source = Set-SlotSwipeTransferAccount -DeploymentPlan $plan -Account '700800' 6>$null
        if ($source -ne 'input') { throw "reported '$source', expected 'input'" }
        $applied = Read-TargetAccount -Plan $plan
        if ($applied -ne '700800') { throw "slot has '$applied', expected '700800'" }
    }

    Check 'With nothing passed, the live slot''s value is carried forward' {
        $plan = New-Case -Name 'preserved' -AccountOnLiveSlot '700800'
        $source = Set-SlotSwipeTransferAccount -DeploymentPlan $plan -Account '' 6>$null
        if ($source -ne 'preserved') { throw "reported '$source', expected 'preserved'" }
        $applied = Read-TargetAccount -Plan $plan
        if ($applied -ne '700800') { throw "slot has '$applied', expected '700800'" }
    }

    Check 'A value passed wins over the one already live, so it can be changed' {
        $plan = New-Case -Name 'override' -AccountOnLiveSlot '700800'
        $null = Set-SlotSwipeTransferAccount -DeploymentPlan $plan -Account '700700' 6>$null
        $applied = Read-TargetAccount -Plan $plan
        if ($applied -ne '700700') { throw "slot has '$applied', expected '700700'" }
    }

    Check 'Configured nowhere, it says so and writes nothing' {
        $plan = New-Case -Name 'none' -AccountOnLiveSlot $null
        $source = Set-SlotSwipeTransferAccount -DeploymentPlan $plan -Account '' 6>$null
        if ($source -ne 'none') { throw "reported '$source', expected 'none'" }
        $applied = Read-TargetAccount -Plan $plan
        if (-not [string]::IsNullOrWhiteSpace($applied)) { throw "slot has '$applied', expected nothing" }
    }

    Check 'A first deploy with no live slot at all is not a failure' {
        # CurrentPath is empty the first time a node is set up.
        $plan = New-Case -Name 'first-deploy' -AccountOnLiveSlot $null
        $plan.CurrentPath = ''
        $source = Set-SlotSwipeTransferAccount -DeploymentPlan $plan -Account '700800' 6>$null
        if ($source -ne 'input') { throw "reported '$source', expected 'input'" }
        if ((Read-TargetAccount -Plan $plan) -ne '700800') { throw 'the account was not applied' }
    }

    Write-Host "`n  $script:pass passed, $script:fail failed" -ForegroundColor ($(if ($script:fail) { 'Red' } else { 'Green' }))
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

if ($script:fail -gt 0) { exit 1 }
