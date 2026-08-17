#Requires -Version 5.1
<#
.SYNOPSIS
    Checks the hand-written API route catalogues against the controllers.

.DESCRIPTION
    The repo keeps three hand-maintained lists of API endpoints, none of them generated and, until
    this script, none checked by anything:

      * API.md                                              - the endpoint tables
      * ShopInventory.Web/Components/Pages/ApiExplorer.razor - the admin API Explorer's _apiCategories
      * ShopInventory.Web/README.md                          - its own endpoint table

    They drift, and the drift is invisible from the outside: a wrong path usually still *resolves*,
    because a bogus segment binds to a {param} route on the same controller and comes back as a
    polite domain 404. "/api/glaccount/active" answered "G/L account with code 'active' not found" —
    an answer that reads like data rather than like a broken URL.

    This parses the real routes out of the controllers ([Route] on the class plus the [Http*]
    attributes on each action), parses the catalogues, and diffs the two.

    An entry is anything a catalogue states as an endpoint:
      * a markdown line with an HTTP verb before the path (table rows and prose both qualify)
      * an ApiExplorer  new("GET", "/api/...", "...")  row
      * a "**Base route:**" line or an ApiExplorer BasePath, checked as a prefix rather than a route
    Bare mentions of a path in prose or in a JSON sample are ignored — pass -ShowMentions to see them.

.PARAMETER Ref
    Check a git ref's version of every file instead of the working tree. Always run the script once
    against a ref you know to be dirty (-Ref HEAD before you fix anything) as a negative control: a
    parser that silently matches nothing and a clean catalogue look identical otherwise.

.PARAMETER ShowUndocumented
    Also list real routes that no catalogue mentions. Informational; never fails the run.

.PARAMETER ShowMentions
    Also list catalogue paths that were parsed but not treated as entries.

.PARAMETER ListRoutes
    Print every route parsed out of the controllers and exit. Use this to sanity-check the parser.

.EXAMPLE
    pwsh scripts/Test-RouteCatalogues.ps1

.EXAMPLE
    pwsh scripts/Test-RouteCatalogues.ps1 -Ref HEAD~1 -ShowUndocumented

.NOTES
    Two traps this handles, both of which cost an earlier audit its answer:
      * an action can carry more than one [Http*] attribute - VanSalesCompatibilityController's
        CreateDirectInvoice answers both "order" and "order/with-batches", and StatementController's
        GenerateStatement answers both "generate/{cardCode}" and "{cardCode}/pdf"
      * the /health/* probes are MapHealthChecks calls in Program.cs, not controller routes, so a
        controllers-only sweep reports them as fictional

    Versioning does not affect paths: AddApiVersioning uses header/query readers with
    SubstituteApiVersionInUrl = false, so routes are /api/<controller>, never /api/v1/<controller>.
#>
[CmdletBinding()]
param(
    [string]$Ref,
    [switch]$ShowUndocumented,
    [switch]$ShowMentions,
    [switch]$ListRoutes
)

$ErrorActionPreference = 'Stop'

$repoRoot = (& git rev-parse --show-toplevel)
if ($LASTEXITCODE -ne 0) { throw 'Not inside a git repository.' }
$repoRoot = $repoRoot.Trim()

$ControllerDir = 'ShopInventory/Controllers'
$ProgramFile   = 'ShopInventory/Program.cs'
$Catalogues    = @(
    'API.md',
    'ShopInventory.Web/Components/Pages/ApiExplorer.razor',
    'ShopInventory.Web/README.md'
)

# Paths that are somebody else's API. API.md documents the ZIMRA FDMS platform at
# fiscal.kefaloscheese.com under "Platform endpoint" because this API calls it as a client; those
# routes are correct and will never appear on a controller here. Add to this list only for an
# external service, never to silence a route this API is supposed to serve.
$ExternalPaths = @(
    'api/sap/receipts/fiscalise',   # fiscalisation platform - fiscalise a document already in SAP
    'api/receipts/submit',          # fiscalisation platform - fiscalise a desktop/POS receipt
    'api/receipts/check',           # fiscalisation platform - read fiscal status back
    'api/fiscal-config',            # fiscalisation platform - device configuration
    'api/fiscal-status'             # fiscalisation platform - device status
)

# ---------------------------------------------------------------- file access

function Get-SourceText {
    param([string]$RelativePath)

    if ($Ref) {
        $text = & git show "${Ref}:${RelativePath}" 2>$null
        if ($LASTEXITCODE -ne 0) { return $null }
        return ($text -join "`n")
    }

    $full = Join-Path $repoRoot $RelativePath
    if (-not (Test-Path -LiteralPath $full)) { return $null }
    return (Get-Content -LiteralPath $full -Raw)
}

function Get-ControllerFiles {
    if ($Ref) {
        $names = & git ls-tree -r --name-only $Ref -- $ControllerDir
        if ($LASTEXITCODE -ne 0) { throw "Cannot list $ControllerDir at $Ref." }
        return @($names | Where-Object { $_ -like '*.cs' })
    }

    $dir = Join-Path $repoRoot $ControllerDir
    return @(Get-ChildItem -LiteralPath $dir -Filter *.cs -Recurse |
        ForEach-Object { $ControllerDir + '/' + $_.FullName.Substring($dir.Length + 1).Replace('\', '/') })
}

# ------------------------------------------------------------- normalisation

# Lowercase, no host, no query, no surrounding slashes, and every route parameter collapsed to {} so
# that {docEntry} in a catalogue and {id:int} on the action compare equal.
function ConvertTo-NormalisedRoute {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }

    $p = $Path.Trim()
    $p = $p -replace '^https?://[^/]+', ''
    foreach ($stop in '?', '#') {
        $at = $p.IndexOf($stop)
        if ($at -ge 0) { $p = $p.Substring(0, $at) }
    }
    $p = $p -replace '\{[^}]*\}', '{}'
    $p = $p.Trim('/')

    if ([string]::IsNullOrWhiteSpace($p)) { return $null }
    return $p.ToLowerInvariant()
}

# --------------------------------------------------------------- real routes

# path -> @{ Verbs = [ordered set of verbs]; Sources = [list of "Controller.Action"] }
$realRoutes = [ordered]@{}

function Add-RealRoute {
    param([string]$Path, [string]$Verb, [string]$Source)

    $key = ConvertTo-NormalisedRoute $Path
    if (-not $key) { return }

    if (-not $realRoutes.Contains($key)) {
        $realRoutes[$key] = @{ Verbs = New-Object 'System.Collections.Generic.HashSet[string]'; Sources = @() }
    }
    [void]$realRoutes[$key].Verbs.Add($Verb.ToUpperInvariant())
    if ($realRoutes[$key].Sources -notcontains $Source) {
        $realRoutes[$key].Sources += $Source
    }
}

$verbPattern = 'Get|Post|Put|Delete|Patch|Head|Options'

foreach ($file in Get-ControllerFiles) {
    $text = Get-SourceText $file
    if (-not $text) { continue }

    $pendingRoute   = $null
    $baseTemplate   = $null
    $controllerName = $null
    $actionName     = '?'

    foreach ($line in ($text -split "`r?`n")) {

        if ($line -match '^\s*\[Route\("([^"]+)"\)\]') {
            $pendingRoute = $Matches[1]
            continue
        }

        # Only a *Controller class takes the pending [Route]; a helper type declared in the same file
        # must not steal it, and must not clear the controller's base either.
        if ($line -match '(?:^|\s)class\s+([A-Za-z0-9_]+)') {
            $name = $Matches[1]
            if ($name -like '*Controller') {
                $controllerName = $name
                if ($pendingRoute) {
                    $baseTemplate = $pendingRoute -replace '\[controller\]', ($name -replace 'Controller$', '')
                    $pendingRoute = $null
                }
            }
            continue
        }

        # Stacked attributes are the point: one action can answer two routes.
        if ($line -match "^\s*\[Http($verbPattern)(?:\(""([^""]*)""\))?\]") {
            $verb     = $Matches[1]
            $template = $Matches[2]

            if (-not $baseTemplate) { continue }

            if ($template -and ($template.StartsWith('/') -or $template.StartsWith('~/'))) {
                $full = $template.TrimStart('~')
            }
            elseif ($template) {
                $full = "$baseTemplate/$template"
            }
            else {
                $full = $baseTemplate
            }

            Add-RealRoute -Path $full -Verb $verb -Source "$controllerName"
            continue
        }

        if ($line -match '^\s*public\s+(?:async\s+)?[A-Za-z0-9_<>,\[\]\?\. ]+\s+([A-Za-z0-9_]+)\s*\(') {
            $actionName = $Matches[1]
        }
    }
}

# The health probes are minimal-API, not controller actions. Left out, every catalogue that lists
# them reads as wrong.
$programText = Get-SourceText $ProgramFile
if ($programText) {
    foreach ($m in [regex]::Matches($programText, 'MapHealthChecks\(\s*"([^"]+)"')) {
        Add-RealRoute -Path $m.Groups[1].Value -Verb 'GET' -Source 'Program.cs (MapHealthChecks)'
    }
    foreach ($m in [regex]::Matches($programText, 'MapGet\(\s*"(/[^"]*)"')) {
        Add-RealRoute -Path $m.Groups[1].Value -Verb 'GET' -Source 'Program.cs (MapGet)'
    }
    foreach ($m in [regex]::Matches($programText, 'MapHub<[^>]+>\(\s*"([^"]+)"')) {
        Add-RealRoute -Path $m.Groups[1].Value -Verb 'GET' -Source 'Program.cs (MapHub)'
    }
}

if ($ListRoutes) {
    foreach ($key in $realRoutes.Keys) {
        $entry = $realRoutes[$key]
        '{0,-8} /{1}   [{2}]' -f (($entry.Verbs | Sort-Object) -join ','), $key, ($entry.Sources -join ', ')
    }
    Write-Host ''
    Write-Host ("{0} routes parsed." -f $realRoutes.Count)
    exit 0
}

if ($realRoutes.Count -eq 0) {
    throw 'Parsed zero routes from the controllers - the parser is broken, not the catalogues.'
}

# ---------------------------------------------------------- catalogue entries

# A path mention only counts as an *entry* when the catalogue states it as an endpoint. Everything
# else (a path inside a JSON sample, a path named in a sentence) is a mention, listed on request but
# never failed on: a sample response body is not a claim about routing.
$pathRegex = '(?<![A-Za-z0-9_])/?api/[A-Za-z0-9_\-\{\}/\.]*'

function Get-CatalogueEntries {
    param([string]$Text, [string]$File)

    $entries  = @()
    $mentions = @()
    $bases    = @()

    # In a code file the catalogue *is* the data literals. Reading its prose the way markdown prose is
    # read finds the comment above LoadIntoTester - "several modules have no plain GET on their base
    # path, /api/invoice only accepts POST there" - and reports the sentence that documents the quirk
    # as an instance of it.
    $structuredOnly = $File -match '\.(razor|cs)$'

    $lines = $Text -split "`r?`n"
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]
        $lineNo = $i + 1

        # ApiExplorer rows carry their own verb: new("GET", "/api/x", "...")
        foreach ($m in [regex]::Matches($line, 'new\(\s*"([A-Z]+)"\s*,\s*"([^"]+)"')) {
            $entries += [pscustomobject]@{
                File = $File; Line = $lineNo; Verb = $m.Groups[1].Value.ToUpperInvariant()
                Path = $m.Groups[2].Value; Text = $line.Trim()
            }
        }
        if ($line -match 'BasePath\s*=\s*"([^"]+)"') {
            $bases += [pscustomobject]@{ File = $File; Line = $lineNo; Path = $Matches[1]; Text = $line.Trim() }
        }
        if ($line -match '\*\*Base route:\*\*\s*`?([^`\s]+)') {
            $bases += [pscustomobject]@{ File = $File; Line = $lineNo; Path = $Matches[1]; Text = $line.Trim() }
        }
        if ($structuredOnly) { continue }
        if ($line -match 'new\(\s*"[A-Z]+"' -or $line -match 'BasePath\s*=') { continue }

        foreach ($m in [regex]::Matches($line, $pathRegex)) {
            $path = $m.Value.TrimEnd('.', ',', ':', ';', ')', '/')
            if ($path -match '^/?api/?$') { continue }

            # The verb, if any, is the last one before the path on the same line - that is how both a
            # table row (| GET | `/api/x` |) and a heading (#### GET `/api/x`) are written.
            $before = $line.Substring(0, $m.Index)
            $verbMatches = [regex]::Matches($before, '(?<![A-Za-z])(GET|POST|PUT|DELETE|PATCH|HEAD|OPTIONS)(?![A-Za-z])')

            if ($verbMatches.Count -gt 0) {
                $entries += [pscustomobject]@{
                    File = $File; Line = $lineNo
                    Verb = $verbMatches[$verbMatches.Count - 1].Groups[1].Value
                    Path = $path; Text = $line.Trim()
                }
            }
            else {
                $mentions += [pscustomobject]@{ File = $File; Line = $lineNo; Path = $path; Text = $line.Trim() }
            }
        }
    }

    return [pscustomobject]@{ Entries = $entries; Mentions = $mentions; Bases = $bases }
}

# ------------------------------------------------------------------- compare

# Does this path reach a route only by binding a literal segment to a {param}? That covers both a
# prose example written with a real id ("PUT /api/ItemVolumeConversion/yog143 updates the same row as
# YOG143") and the failure this whole script exists for: /api/glaccount/active bound to {code} and
# answered "G/L account with code 'active' not found", which reads like data rather than a broken URL.
# The two are indistinguishable from here, so they are reported for review rather than failed on.
function Get-ParameterBoundRoute {
    param([string]$Key)

    $wanted = $Key -split '/'
    foreach ($real in $realRoutes.Keys) {
        $actual = $real -split '/'
        if ($actual.Count -ne $wanted.Count) { continue }

        $matched = $true
        for ($s = 0; $s -lt $actual.Count; $s++) {
            if ($actual[$s] -eq '{}') { continue }
            if ($actual[$s] -ne $wanted[$s]) { $matched = $false; break }
        }
        if ($matched) { return $real }
    }
    return $null
}

$reviews   = @()
$failures  = @()
$allBases  = @()
$entryPaths = New-Object 'System.Collections.Generic.HashSet[string]'
$checked   = 0

foreach ($catalogue in $Catalogues) {
    $text = Get-SourceText $catalogue
    if (-not $text) {
        Write-Warning "Catalogue not found: $catalogue"
        continue
    }

    $parsed = Get-CatalogueEntries -Text $text -File $catalogue
    $allBases += $parsed.Bases

    foreach ($entry in $parsed.Entries) {
        $checked++
        $key = ConvertTo-NormalisedRoute $entry.Path
        if (-not $key) { continue }
        if ($ExternalPaths -contains $key) { continue }
        [void]$entryPaths.Add($key)

        if (-not $realRoutes.Contains($key)) {
            $bound = Get-ParameterBoundRoute $key
            if ($bound) {
                $reviews += [pscustomobject]@{
                    File = $entry.File; Line = $entry.Line; Kind = 'binds to {param}'
                    Detail = ('{0} /{1} reaches /{2}' -f $entry.Verb, $key, $bound)
                }
                continue
            }

            $failures += [pscustomobject]@{
                File = $entry.File; Line = $entry.Line; Kind = 'no such route'
                Detail = ('{0} /{1}' -f $entry.Verb, $key)
            }
            continue
        }

        if (-not $realRoutes[$key].Verbs.Contains($entry.Verb)) {
            $failures += [pscustomobject]@{
                File = $entry.File; Line = $entry.Line; Kind = 'wrong verb'
                Detail = ('/{0} does not accept {1} (it accepts {2})' -f
                    $key, $entry.Verb, (($realRoutes[$key].Verbs | Sort-Object) -join ', '))
            }
        }
    }

    foreach ($base in $parsed.Bases) {
        $checked++
        $key = ConvertTo-NormalisedRoute $base.Path
        if (-not $key) { continue }
        if ($ExternalPaths -contains $key) { continue }
        [void]$entryPaths.Add($key)

        $servesSomething = $false
        foreach ($real in $realRoutes.Keys) {
            if ($real -eq $key -or $real.StartsWith("$key/")) { $servesSomething = $true; break }
        }
        if (-not $servesSomething) {
            $failures += [pscustomobject]@{
                File = $base.File; Line = $base.Line; Kind = 'no such base route'
                Detail = "/$key serves no route"
            }
        }
    }

    if ($ShowMentions -and $parsed.Mentions.Count -gt 0) {
        Write-Host ''
        Write-Host "Mentions in ${catalogue} (not checked):" -ForegroundColor DarkGray
        foreach ($mention in $parsed.Mentions) {
            Write-Host ("  {0}:{1}  {2}" -f $mention.File, $mention.Line, $mention.Path) -ForegroundColor DarkGray
        }
    }
}

# ------------------------------------------------------------------- report

Write-Host ''
Write-Host ("Routes on the controllers : {0}" -f $realRoutes.Count)
Write-Host ("Catalogue claims checked  : {0}" -f $checked)

if ($ShowUndocumented) {
    $undocumented = @()
    foreach ($key in $realRoutes.Keys) {
        $covered = $false
        foreach ($claimed in $entryPaths) {
            if ($claimed -eq $key) { $covered = $true; break }
        }
        if (-not $covered) { $undocumented += $key }
    }

    Write-Host ''
    Write-Host ("Real routes no catalogue lists: {0}" -f $undocumented.Count) -ForegroundColor Yellow
    foreach ($route in $undocumented) {
        Write-Host ("  {0,-8} /{1}" -f (($realRoutes[$route].Verbs | Sort-Object) -join ','), $route) -ForegroundColor DarkYellow
    }
}

if ($reviews.Count -gt 0) {
    Write-Host ''
    Write-Host ("Claims that reach a route only by binding a literal to a route parameter: {0}" -f $reviews.Count) -ForegroundColor Yellow
    Write-Host '  (a worked example is fine here; a made-up path segment is not - read each one)' -ForegroundColor DarkYellow
    foreach ($review in ($reviews | Sort-Object File, Line)) {
        Write-Host ("  {0}:{1}  {2}" -f $review.File, $review.Line, $review.Detail) -ForegroundColor DarkYellow
    }
}

if ($failures.Count -eq 0) {
    Write-Host ''
    Write-Host 'Every catalogue claim is served by a route.' -ForegroundColor Green
    exit 0
}

Write-Host ''
Write-Host ("Catalogue claims no route serves: {0}" -f $failures.Count) -ForegroundColor Red
foreach ($group in ($failures | Group-Object File)) {
    Write-Host ''
    Write-Host ("  {0}" -f $group.Name) -ForegroundColor Red
    foreach ($failure in ($group.Group | Sort-Object Line)) {
        Write-Host ("    line {0,-5} {1,-18} {2}" -f $failure.Line, $failure.Kind, $failure.Detail)
    }
}

Write-Host ''
exit 1
