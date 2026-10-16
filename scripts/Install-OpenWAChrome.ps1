#Requires -Version 5.1
<#
.SYNOPSIS
    Installs the Chrome for Testing build that OpenWA's Puppeteer was released against, and reports
    which session profiles a newer Chrome has already opened.

.DESCRIPTION
    Puppeteer is tested against one Chrome build, named in puppeteer-core's revisions.js. The machine's
    Google Chrome updates itself and drifts away from it: on 2026-10-16 10.10.10.9 ran Chrome 155 under
    a Puppeteer built for 146, and sends failed on "Attempted to use detached Frame" minutes after a
    session read ready. Chrome for Testing does not update itself, so the gateway keeps the build its
    Puppeteer expects until the Puppeteer itself changes.

    The version is read from the puppeteer-core that whatsapp-web.js loads, and installed with the
    @puppeteer/browsers already in OpenWA's node_modules, run on the gateway's own Node, into
    <OpenWARoot>\chrome. A stamp there records the version and the executable, so a re-run with the
    same Puppeteer does nothing. npm ci empties node_modules only, so the browser survives a reinstall.

    It also lists every session profile under data\sessions whose 'Last Version' is newer than that
    build. Chrome cannot read IndexedDB written by a newer Chrome: it reports "unknown schema", deletes
    the database and starts empty. WhatsApp Web keeps its login there, so such a session needs its QR
    scanned again. The caller decides what to do about it; this script changes no profile.

.OUTPUTS
    An object with Version, ExecutablePath, Installed (whether this run installed it) and
    NewerProfiles (each with Name, Path and LastVersion). Under -WhatIf, ExecutablePath is where it
    would be and Installed is $false.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [string]$OpenWARoot,
    [Parameter(Mandatory = $true)]
    [string]$NodeHome
)

$ErrorActionPreference = 'Stop'

$OpenWARoot = (Resolve-Path -LiteralPath $OpenWARoot).Path
$node = Join-Path $NodeHome 'node.exe'
$npx = Join-Path $NodeHome 'npx.cmd'
foreach ($required in @($node, $npx)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "$required was not found." }
}

# The puppeteer-core that whatsapp-web.js actually loads (it requires 'puppeteer', which requires
# 'puppeteer-core'), resolved by Node rather than assumed hoisted.
# Each step resolves a package's main entry from the one before; revisions.js sits beside
# puppeteer-core's. One line, because Windows PowerShell mangles some multi-line native arguments.
$resolveRevisions = "const { createRequire: r } = require('module'), path = require('path'); " +
    "const wwebjs = r(path.join(process.cwd(), 'package.json')).resolve('whatsapp-web.js'); " +
    "const core = r(r(wwebjs).resolve('puppeteer')).resolve('puppeteer-core'); " +
    "console.log(path.join(path.dirname(core), 'revisions.js'));"

Push-Location $OpenWARoot
try {
    $revisionsPath = & $node -e $resolveRevisions
    if ($LASTEXITCODE -ne 0 -or -not $revisionsPath) {
        throw "Could not find puppeteer-core's revisions.js from whatsapp-web.js under $OpenWARoot. Are its dependencies installed?"
    }
}
finally { Pop-Location }

$revisions = Get-Content -LiteralPath $revisionsPath -Raw
$match = [regex]::Match($revisions, "(?m)^\s*chrome:\s*'(\d+(?:\.\d+){3})'")
if (-not $match.Success) {
    throw "No chrome revision found in $revisionsPath."
}
$version = $match.Groups[1].Value

$browserRoot = Join-Path $OpenWARoot 'chrome'
$stamp = Join-Path $browserRoot '.shopinventory-chrome'

# The stamp holds the line @puppeteer/browsers prints: "chrome@<version> <executable>".
$executable = $null
if (Test-Path -LiteralPath $stamp) {
    $stamped = [regex]::Match((Get-Content -LiteralPath $stamp -Raw).Trim(), '^chrome@(\S+) (.+)$')
    if ($stamped.Success -and $stamped.Groups[1].Value -eq $version -and (Test-Path -LiteralPath $stamped.Groups[2].Value)) {
        $executable = $stamped.Groups[2].Value
    }
}

$installed = $false
if (-not $executable) {
    if ($PSCmdlet.ShouldProcess($browserRoot, "Install Chrome for Testing $version")) {
        New-Item -ItemType Directory -Path $browserRoot -Force | Out-Null
        # Keeps the browser out of the submodule's git status without touching the fork's .gitignore.
        Set-Content -LiteralPath (Join-Path $browserRoot '.gitignore') -Value '*' -Encoding ASCII

        $env:PATH = "$NodeHome;$env:PATH"
        Push-Location $OpenWARoot
        try {
            # --no: use the @puppeteer/browsers in OpenWA's node_modules, never fetch one from the registry.
            # Quoted, because a bare @puppeteer is PowerShell splatting.
            $output = & $npx --no '@puppeteer/browsers' install "chrome@$version" --path $browserRoot
            if ($LASTEXITCODE -ne 0) {
                throw "npx @puppeteer/browsers install chrome@$version failed with exit code $LASTEXITCODE."
            }
        }
        finally { Pop-Location }

        $line = @($output | Where-Object { $_ -match "^chrome@$([regex]::Escape($version)) " }) | Select-Object -Last 1
        if (-not $line) { throw "@puppeteer/browsers did not report where it installed chrome@${version}: $output" }
        $executable = ($line -replace "^chrome@$([regex]::Escape($version)) ", '').Trim()
        if (-not (Test-Path -LiteralPath $executable)) { throw "@puppeteer/browsers reported $executable, which does not exist." }

        Set-Content -LiteralPath $stamp -Value "chrome@$version $executable" -Encoding ASCII
        $installed = $true
    }
    else {
        $executable = Join-Path $browserRoot "chrome\win64-$version\chrome-win64\chrome.exe"
    }
}

$newerProfiles = @()
$sessionsPath = Join-Path $OpenWARoot 'data\sessions'
if (Test-Path -LiteralPath $sessionsPath) {
    foreach ($sessionDir in Get-ChildItem -LiteralPath $sessionsPath -Directory) {
        $lastVersionFile = Join-Path $sessionDir.FullName 'Last Version'
        if (-not (Test-Path -LiteralPath $lastVersionFile)) { continue }
        $lastVersion = (Get-Content -LiteralPath $lastVersionFile -Raw).Trim()
        $parsed = $null
        if ([version]::TryParse($lastVersion, [ref]$parsed) -and $parsed -gt [version]$version) {
            $newerProfiles += [pscustomobject]@{ Name = $sessionDir.Name; Path = $sessionDir.FullName; LastVersion = $lastVersion }
        }
    }
}

[pscustomobject]@{
    Version        = $version
    ExecutablePath = $executable
    Installed      = $installed
    NewerProfiles  = $newerProfiles
}
