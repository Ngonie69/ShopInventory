# Which OpenWA install a gateway process belongs to, told from its command line. Dot-sourced by
# Run-OpenWA.ps1, Start-OpenWA.ps1, Stop-OpenWA.ps1 and Install-OpenWAProduction.ps1, so that they all
# tell installs apart the same way.
#
# On 16 Oct 2026 three sessions ran OpenWA tests on one laptop at once, each against a fake gateway in
# its own folder under %TEMP%, and each run's stop ended the others' gateways. node was started as
# `node .\dist\main.js`, a command line that does not say which folder it runs from, so every script
# matched every gateway on the machine. Now node is started with the absolute path of its entry point,
# and the supervisor names its root on its own command line (-OpenWARoot, or the folder beside its
# script). A node still started the old way is tied to a root through its parent, when that is a
# supervisor; otherwise its root cannot be told.

function ConvertTo-OpenWARoot {
    # A folder from a command line as one comparable string, or '' when the path is relative: that
    # names a folder only together with the process's working directory, which is not on the command line.
    param([string]$Path)

    $Path = $Path.Trim().Trim('"', "'")
    if (-not $Path -or -not [IO.Path]::IsPathRooted($Path) -or $Path -match '^[\\/](?![\\/])') {
        return ''
    }

    try {
        return [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    }
    catch {
        return ''
    }
}

function Get-OpenWASupervisorRoot {
    # The root a Run-OpenWA.ps1 command line runs against, '' when it cannot be told, or $null when
    # the command line is not one.
    param([string]$CommandLine)

    if (-not $CommandLine -or $CommandLine -notlike '*Run-OpenWA.ps1*') {
        return $null
    }

    $named = [regex]::Match($CommandLine, '(?i)-OpenWARoot(?::|\s+)(?:"([^"]*)"|''([^'']*)''|([^\s"'']+))')
    if ($named.Success) {
        return ConvertTo-OpenWARoot ($named.Groups[1].Value + $named.Groups[2].Value + $named.Groups[3].Value)
    }

    # Without -OpenWARoot the supervisor runs the OpenWA folder beside its scripts folder.
    $script = [regex]::Match($CommandLine, '(?i)"([^"]*Run-OpenWA\.ps1)"|''([^'']*Run-OpenWA\.ps1)''|([^\s"'']*Run-OpenWA\.ps1)')
    $scriptPath = ConvertTo-OpenWARoot ($script.Groups[1].Value + $script.Groups[2].Value + $script.Groups[3].Value)
    if (-not $scriptPath) {
        return ''
    }

    return ConvertTo-OpenWARoot (Join-Path (Split-Path -Parent $scriptPath) '..\OpenWA')
}

function Get-OpenWAProcessRoot {
    # For one process of a snapshot: the OpenWA root it belongs to, '' for a gateway node or supervisor
    # whose root cannot be told, or $null for anything else, including a process whose command line
    # this console cannot read.
    param([object]$Process, [object[]]$Snapshot)

    $commandLine = $Process.CommandLine
    if (-not $commandLine) {
        return $null
    }

    if ($Process.Name -eq 'powershell.exe' -or $Process.Name -eq 'pwsh.exe') {
        return Get-OpenWASupervisorRoot -CommandLine $commandLine
    }

    if ($Process.Name -ne 'node.exe') {
        return $null
    }

    # node <root>\dist\main.js, as Run-OpenWA.ps1 and Start-OpenWA.ps1 start it, or nest's own start.
    $entry = [regex]::Match($commandLine, '(?i)(?:"([^"]*?)|([^\s"]*?))[\\/](?:dist[\\/]main\b|node_modules[\\/]\.bin[\\/][^\s"]*nest\.js)')
    if ($entry.Success) {
        $root = ConvertTo-OpenWARoot ($entry.Groups[1].Value + $entry.Groups[2].Value)
    }
    elseif ($commandLine -like '*npm-cli.js*run start*OpenWA*') {
        $root = ''
    }
    else {
        return $null
    }

    if ($root) {
        return $root
    }

    # Started relatively, as `node .\dist\main.js`. Run-OpenWA.ps1 did that until 16 Oct 2026, and a
    # supervisor started before then still does, so its children are tied to its root. The parent must
    # have started first: Windows reuses the PID of a process that has exited.
    $parent = @($Snapshot | Where-Object {
            $_ -and [int]$_.ProcessId -eq [int]$Process.ParentProcessId -and
            ($_.Name -eq 'powershell.exe' -or $_.Name -eq 'pwsh.exe') -and
            (-not $_.CreationDate -or -not $Process.CreationDate -or $_.CreationDate -le $Process.CreationDate)
        }) | Select-Object -First 1
    if ($parent) {
        $parentRoot = Get-OpenWASupervisorRoot -CommandLine $parent.CommandLine
        if ($parentRoot) {
            return $parentRoot
        }
    }

    return ''
}

function Test-OpenWARootIs {
    # Whether a process's root (from Get-OpenWAProcessRoot) counts as this install's. A gateway whose
    # root cannot be told counts as every install's: on a server with one install it is that install's,
    # and ending or waiting for it is what these scripts did before they could tell roots apart.
    # Untyped: a [string] parameter would turn $null (not a gateway) into '' (a gateway of any root).
    param($ProcessRoot, [string]$Root)

    if ($null -eq $ProcessRoot) {
        return $false
    }

    return $ProcessRoot -eq '' -or [string]::Equals($ProcessRoot, (ConvertTo-OpenWARoot $Root), [StringComparison]::OrdinalIgnoreCase)
}

function Select-OpenWAGateway {
    # The gateway node and supervisor processes of one install, each with the root it was matched by
    # (Root is '' where that could not be told).
    param([object[]]$Snapshot, [string]$Root, [string[]]$Name = @('node.exe', 'powershell.exe', 'pwsh.exe'))

    $Snapshot = @($Snapshot | Where-Object { $_ })
    foreach ($process in @($Snapshot | Where-Object { $Name -contains $_.Name })) {
        $processRoot = Get-OpenWAProcessRoot -Process $process -Snapshot $Snapshot
        if (Test-OpenWARootIs -ProcessRoot $processRoot -Root $Root) {
            $process | Add-Member -NotePropertyName OpenWARoot -NotePropertyValue $processRoot -Force -PassThru
        }
    }
}

function Test-OpenWAPathInCommandLine {
    # Whether a command line names a path under this root, in either slash style. The separator after
    # the root keeps C:\x\OpenWA from matching C:\x\OpenWA2.
    param([string]$CommandLine, [string]$Root)

    if (-not $CommandLine) {
        return $false
    }

    $trimmed = $Root.TrimEnd('\', '/')
    foreach ($prefix in @("$trimmed\", (($trimmed -replace '\\', '/') + '/'))) {
        if ($CommandLine.IndexOf($prefix, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            return $true
        }
    }

    return $false
}
