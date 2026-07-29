[CmdletBinding()]
param(
    [string]$OpenWARoot = (Join-Path $PSScriptRoot "..\OpenWA"),
    [string]$NodeHome = "C:\Users\ngoni\.config\herd\bin\nvm\v20.20.2",
    [string]$ChromePath = "C:\Program Files\Google\Chrome\Application\chrome.exe",
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'

function Resolve-RequiredPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Description
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "$Description not found: $Path"
    }

    return (Resolve-Path -LiteralPath $Path).Path
}

function Get-OpenWAProcess {
    Get-CimInstance Win32_Process -Filter "name = 'node.exe'" -ErrorAction SilentlyContinue |
        Where-Object {
            $commandLine = $_.CommandLine
            $commandLine -and (
                $commandLine -like "*\OpenWA\dist\main*" -or
                $commandLine -like "* .\dist\main.js*" -or
                $commandLine -like "*\OpenWA\node_modules\.bin\*nest.js*" -or
                $commandLine -like "*npm-cli.js*run start*OpenWA*"
            )
        }
}

$resolvedOpenWARoot = Resolve-RequiredPath -Path $OpenWARoot -Description 'OpenWA root directory'
$nodeExe = Resolve-RequiredPath -Path (Join-Path $NodeHome 'node.exe') -Description 'Node executable'
$npmCmd = Resolve-RequiredPath -Path (Join-Path $NodeHome 'npm.cmd') -Description 'npm executable'
$resolvedChromePath = Resolve-RequiredPath -Path $ChromePath -Description 'Chrome executable'
$distMainPath = Join-Path $resolvedOpenWARoot 'dist\main.js'

$existingProcess = Get-OpenWAProcess | Select-Object -First 1
if ($existingProcess) {
    Write-Host "OpenWA is already running (PID $($existingProcess.ProcessId))."
    return
}

if (-not (Test-Path -LiteralPath $distMainPath)) {
    if ($SkipBuild) {
        throw "Compiled OpenWA entrypoint not found and -SkipBuild was supplied: $distMainPath"
    }

    Push-Location $resolvedOpenWARoot
    try {
        & $npmCmd run build
        if ($LASTEXITCODE -ne 0) {
            throw "OpenWA build failed with exit code $LASTEXITCODE"
        }
    }
    finally {
        Pop-Location
    }

    if (-not (Test-Path -LiteralPath $distMainPath)) {
        throw "Compiled OpenWA entrypoint still not found after build: $distMainPath"
    }
}

$logsDir = Join-Path $resolvedOpenWARoot 'logs'
New-Item -ItemType Directory -Path $logsDir -Force | Out-Null

$timestamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$stdoutLog = Join-Path $logsDir "openwa-$timestamp.out.log"
$stderrLog = Join-Path $logsDir "openwa-$timestamp.err.log"

$env:PATH = "$NodeHome;$env:PATH"
$env:PUPPETEER_SKIP_CHROMIUM_DOWNLOAD = 'true'
$env:PUPPETEER_EXECUTABLE_PATH = $resolvedChromePath

$process = Start-Process `
    -FilePath $nodeExe `
    -ArgumentList '.\dist\main.js' `
    -WorkingDirectory $resolvedOpenWARoot `
    -RedirectStandardOutput $stdoutLog `
    -RedirectStandardError $stderrLog `
    -WindowStyle Hidden `
    -PassThru

Write-Host "Started OpenWA runtime (PID $($process.Id))."
Write-Host "Stdout log: $stdoutLog"
Write-Host "Stderr log: $stderrLog"
Write-Host "Use scripts\\Stop-OpenWA.ps1 to stop the runtime."