#Requires -Version 5.1
<#
.SYNOPSIS
    Moves the Microsoft servicing packages from one patch version to the next, in every project.

.DESCRIPTION
    EF Core, the ASP.NET Core packages and the System.* ones ship as one train: 10.0.12 of each, then
    10.0.13 of each. The projects reference them at one version and they are moved together, because
    a project left a patch behind keeps the old transitives the newer ones were published to replace
    (System.Security.Cryptography.Xml arrives through Microsoft.AspNetCore.DataProtection, for one).

    There is no Directory.Packages.props, so the version is written out on some twenty lines across
    four project files. This rewrites every <PackageReference> whose id matches -IdPattern and whose
    Version is exactly -From, and prints each one it changed. Nothing else in the file moves: the
    byte-order mark and the line endings are written back as they were read.

    A package at -From whose id does not match is left alone and named, so that a third-party package
    that happens to share the number is a decision rather than an accident.

.PARAMETER From
    The version to replace, e.g. 10.0.12.

.PARAMETER To
    The version to write, e.g. 10.0.13.

.PARAMETER Project
    Project files to rewrite. Defaults to every .csproj in the repository, and then the dotnet-ef
    tool in .config/dotnet-tools.json is moved as well. Naming one project at a time lets each be
    restored and audited before the next is touched; finish with a run that names none.

.PARAMETER IdPattern
    Regular expression a package id must match to be moved.

.EXAMPLE
    pwsh -NoProfile -File scripts/Set-AlignedPackageVersion.ps1 -From 10.0.12 -To 10.0.13

.EXAMPLE
    pwsh -NoProfile -File scripts/Set-AlignedPackageVersion.ps1 -From 10.0.12 -To 10.0.13 -Project ShopInventory/ShopInventory.csproj

    Then, before the next project:
    dotnet list ShopInventory/ShopInventory.csproj package --vulnerable --include-transitive
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$From,
    [Parameter(Mandatory)][string]$To,
    [string[]]$Project,
    [string]$IdPattern = '^(Microsoft|System)\.'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

$wholeRepository = -not $Project
if ($wholeRepository) {
    $Project =Get-ChildItem -Path $repoRoot -Recurse -Filter *.csproj -File |
        Where-Object { $_.FullName -notmatch '[\\/](bin|obj|\.claude|node_modules)[\\/]' } |
        ForEach-Object { $_.FullName }
}

# [^>] also matches a line break, so a reference laid out one attribute to a line is found too.
$reference = [regex]'(?<head><PackageReference\b[^>]*?\bInclude="(?<id>[^"]+)"[^>]*?\bVersion=")(?<version>[^"]+)(?<tail>")'

$changed = 0
foreach ($path in $Project) {
    $full = (Resolve-Path -LiteralPath $path).Path
    $name = Split-Path -Leaf $full

    $bytes = [System.IO.File]::ReadAllBytes($full)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $text = [System.IO.File]::ReadAllText($full)

    $moved = New-Object System.Collections.Generic.List[string]
    $leftAlone = New-Object System.Collections.Generic.List[string]

    $rewritten = $reference.Replace($text, {
        param($match)

        if ($match.Groups['version'].Value -ne $From) {
            return $match.Value
        }

        $id = $match.Groups['id'].Value
        if ($id -notmatch $IdPattern) {
            $leftAlone.Add($id)
            return $match.Value
        }

        $moved.Add($id)
        return $match.Groups['head'].Value + $To + $match.Groups['tail'].Value
    })

    if ($moved.Count -gt 0) {
        [System.IO.File]::WriteAllText($full, $rewritten, (New-Object System.Text.UTF8Encoding($hasBom)))
    }

    foreach ($id in $moved) {
        Write-Host ("{0}: {1} {2} -> {3}" -f $name, $id, $From, $To)
    }
    foreach ($id in $leftAlone) {
        Write-Host ("{0}: {1} is at {2} and was left alone (id does not match {3})" -f $name, $id, $From, $IdPattern)
    }

    $changed += $moved.Count
}

# dotnet-ef is on the same train. Update-Production.ps1 builds the migration bundles with the copy
# pinned in the tool manifest, and that copy is meant to match the EF Core packages above.
$manifest = Join-Path $repoRoot '.config/dotnet-tools.json'
if ($wholeRepository -and (Test-Path -LiteralPath $manifest)) {
    $text = [System.IO.File]::ReadAllText($manifest)
    $tool = [regex]('(?<head>"dotnet-ef"\s*:\s*\{\s*"version"\s*:\s*")' + [regex]::Escape($From) + '(?<tail>")')

    if ($tool.IsMatch($text)) {
        $rewritten = $tool.Replace($text, { param($match) $match.Groups['head'].Value + $To + $match.Groups['tail'].Value })
        [System.IO.File]::WriteAllText($manifest, $rewritten, (New-Object System.Text.UTF8Encoding($false)))
        Write-Host ("dotnet-tools.json: dotnet-ef {0} -> {1}" -f $From, $To)
        $changed++
    }
}

Write-Host ("{0} reference(s) moved from {1} to {2}." -f $changed, $From, $To)
