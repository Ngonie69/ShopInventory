# Exercises how Update-Production.ps1 gives a blue/green API slot its Firebase key.
#
# The key is gitignored, so a package built from a fresh clone - the deploy runner's checkout, or any
# worktree - does not contain it. Until the key was carried over from the live slot, every such
# deployment published an API with push notifications dead, reported behind nothing but a warning.
#
# The function is lifted out of the real script by AST, as Test-DeployCredentialHandling.ps1 does, and
# runs against throwaway folders standing in for the package, the active slot and the idle slot.
# Nothing here touches IIS or reaches production.

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

$definition = $ast.Find({
        param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Initialize-SlotFirebaseKey'
    }, $true)
if (-not $definition) {
    throw "Could not extract Initialize-SlotFirebaseKey from $ScriptPath - it was renamed or removed."
}

. ([scriptblock]::Create($definition.Extent.Text))

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

$keyName = 'firebase-service-account.json'
$root = Join-Path ([System.IO.Path]::GetTempPath()) ("shopinventory-slotseed-" + [Guid]::NewGuid().ToString('N'))

# One folder each for the extracted package, the active slot and the idle slot, with a key in
# whichever of them the case names. The key's content says where it came from.
function New-Case {
    param([string]$Name, [string]$PackageKey, [string]$ActiveKey, [string]$IdleKey)

    $paths = @{}
    foreach ($part in 'package', 'active', 'idle') {
        $dir = Join-Path (Join-Path $root $Name) $part
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        $paths[$part] = $dir
    }

    foreach ($pair in @(@('package', $PackageKey), @('active', $ActiveKey), @('idle', $IdleKey))) {
        if ($pair[1]) {
            Set-Content -LiteralPath (Join-Path $paths[$pair[0]] $keyName) -Value $pair[1] -NoNewline
        }
    }

    [pscustomobject]@{
        Plan       = [pscustomobject]@{ Name = 'API'; TargetSlot = 'Blue'; CurrentPath = $paths.active; TargetPath = $paths.idle }
        PackageKey = Join-Path $paths.package $keyName
        ActiveKey  = Join-Path $paths.active $keyName
        TargetKey  = Join-Path $paths.idle $keyName
    }
}

function Read-Key {
    param([string]$Path)
    if (Test-Path -LiteralPath $Path) { return Get-Content -LiteralPath $Path -Raw }
    return $null
}

function Assert-Seeded {
    param([object]$Case, [string]$ExpectedSource, [string]$ExpectedKey)

    $source = Initialize-SlotFirebaseKey -DeploymentPlan $Case.Plan -ExtractedKeyPath $Case.PackageKey 6>$null
    if ($source -ne $ExpectedSource) { throw "reported source '$source', expected '$ExpectedSource'" }

    # An empty ExpectedKey means the slot must be left without a key at all. Asserted on the file,
    # not by comparing strings: a [string] parameter turns $null into '', which never equals the
    # $null Read-Key returns for a missing file.
    if ([string]::IsNullOrEmpty($ExpectedKey)) {
        if (Test-Path -LiteralPath $Case.TargetKey) { throw "idle slot holds '$(Read-Key $Case.TargetKey)', expected no key file" }
        return
    }

    $actual = Read-Key $Case.TargetKey
    if ($actual -ne $ExpectedKey) { throw "idle slot holds '$actual', expected '$ExpectedKey'" }
}

try {
    Write-Host ""
    Write-Host "Initialize-SlotFirebaseKey" -ForegroundColor Cyan

    Check "a package without the key takes it from the active slot" {
        Assert-Seeded -Case (New-Case -Name 'carry' -ActiveKey 'live') -ExpectedSource 'ActiveSite' -ExpectedKey 'live'
    }

    Check "the active slot keeps its own key - copied, not moved" {
        $case = New-Case -Name 'copy' -ActiveKey 'live'
        Assert-Seeded -Case $case -ExpectedSource 'ActiveSite' -ExpectedKey 'live'
        if ((Read-Key $case.ActiveKey) -ne 'live') { throw "the active slot lost its key" }
    }

    Check "the active slot's key replaces a stale one left in the idle slot" {
        Assert-Seeded -Case (New-Case -Name 'stale' -ActiveKey 'live' -IdleKey 'stale') -ExpectedSource 'ActiveSite' -ExpectedKey 'live'
    }

    Check "a key in the package wins, so rotating the key is an ordinary deployment" {
        Assert-Seeded -Case (New-Case -Name 'rotate' -PackageKey 'new' -ActiveKey 'live' -IdleKey 'stale') -ExpectedSource 'Package' -ExpectedKey 'new'
    }

    Check "with nothing in the package or the active slot, a key already in the idle slot is kept" {
        Assert-Seeded -Case (New-Case -Name 'retain' -IdleKey 'slot') -ExpectedSource 'Retained' -ExpectedKey 'slot'
    }

    Check "with no key anywhere it reports Missing and writes nothing" {
        Assert-Seeded -Case (New-Case -Name 'missing') -ExpectedSource 'Missing' -ExpectedKey $null
    }

    Check "a first deployment, with no active slot, still takes the package key" {
        $case = New-Case -Name 'first-package' -PackageKey 'new'
        $case.Plan.CurrentPath = $null
        Assert-Seeded -Case $case -ExpectedSource 'Package' -ExpectedKey 'new'
    }

    Check "a first deployment with no active slot and no package key reports Missing" {
        $case = New-Case -Name 'first-missing'
        $case.Plan.CurrentPath = $null
        Assert-Seeded -Case $case -ExpectedSource 'Missing' -ExpectedKey $null
    }
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "Cutover call site" -ForegroundColor Cyan

$calls = @($ast.FindAll({
            param($node)
            $node -is [System.Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Initialize-SlotFirebaseKey'
        }, $true))

Check "the key is seeded exactly once per slot" {
    if ($calls.Count -ne 1) { throw "expected one call to Initialize-SlotFirebaseKey, found $($calls.Count)" }
}

Check "only the API slot is seeded, after the package is copied into it" {
    $call = $calls[0]

    $copy = $ast.Find({
            param($node)
            $node -is [System.Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'robocopy' -and
            $node.Extent.Text -match '\$Plan\.TargetPath'
        }, $true)
    if (-not $copy) { throw "could not find the robocopy that fills `$Plan.TargetPath" }
    if ($call.Extent.StartOffset -lt $copy.Extent.StartOffset) { throw "the key is seeded before robocopy fills the slot, which would not overwrite it" }

    $parent = $call.Parent
    while ($parent -and -not ($parent -is [System.Management.Automation.Language.IfStatementAst])) { $parent = $parent.Parent }
    if (-not $parent -or $parent.Clauses[0].Item1.Extent.Text -notmatch '\$Plan\.Name\s+-eq\s+''API''') {
        throw "the call is not inside if (`$Plan.Name -eq 'API')"
    }
}

Check "its return value is discarded, so it cannot leak into the cutover result" {
    $parent = $calls[0].Parent
    while ($parent -and -not ($parent -is [System.Management.Automation.Language.AssignmentStatementAst])) {
        if ($parent -is [System.Management.Automation.Language.StatementBlockAst]) { $parent = $null; break }
        $parent = $parent.Parent
    }
    if (-not $parent -or $parent.Left.Extent.Text -ne '$null') {
        throw "the call's output is not assigned to `$null - the returned source would join the remote script block's output"
    }
}

Write-Host ""
Write-Host "$script:pass passed, $script:fail failed" -ForegroundColor $(if ($script:fail) { 'Red' } else { 'Green' })
if ($script:fail) { exit 1 }
exit 0
