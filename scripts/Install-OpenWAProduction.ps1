#Requires -Version 5.1
<#
.SYNOPSIS
    Installs, configures and starts the OpenWA gateway on the production host, and prints the
    API settings that must be applied to go with it.

.DESCRIPTION
    OpenWA is not an IIS site and Update-Production.ps1 does not deploy it. It is a Node service
    driving a headless Chrome that holds the WhatsApp Web session, so it needs its own install,
    its own restart-after-reboot arrangement, and a data directory that survives both.

    Run this on ONE host only. WhatsApp Web allows a number one linked browser session, so a second
    OpenWA instance paired to the same number fights the first for it: both flap between connected
    and disconnected, and inbound messages arrive at whichever won last. The load-balanced pair
    (10.10.10.9 and 10.10.10.58) therefore share a single gateway, and both API nodes are pointed at
    it by OpenWA:BaseUrl.

    What this does:

      1. Checks Node and Chrome are present and new enough.
      2. Installs the submodule's dependencies and builds it when nothing is built yet, or when its
         package-lock.json or checked-out commit differs from what the current build was made from.
         A running gateway is stopped only for a dependency install, which replaces files it holds
         open; a rebuild happens while it runs, and it is restarted onto the new build after.
      3. Writes OpenWA\.env for a production run - SQLite, local storage, no Redis, the installed
         Chrome rather than a downloaded Chromium, and a request body limit that fits an invoice PDF.
      4. Starts it, so it mints its admin API key into data\.api-key on a first install. A gateway
         that was rebuilt or reinstalled, or whose .env changed, is restarted - under the boot task
         when it is registered, so it outlives this console's session.
      5. Registers a Scheduled Task that starts it as SYSTEM at boot.
      6. Opens the API port to this host only, if a firewall rule is missing.
      7. Prints the four OpenWA__* values to set on the API, and the command that verifies them.

    It does NOT write the API's configuration. Those values live in each node's web.config and are
    applied by Set-OpenWAApiConfig.ps1, so that one host's install cannot silently repoint both API
    nodes at itself.

    Re-runnable, and the way to upgrade: update the submodule, then run this again. An existing
    install keeps its data directory, its API key and its paired session. After a restart the
    gateway starts every session that was running again (from Ngonie69/OpenWA#3 on; older gateways
    start none), and a paired one reconnects without a new QR code. A re-run with nothing changed
    leaves a running gateway alone.

.PARAMETER RepositoryRoot
    The ShopInventory checkout holding the OpenWA submodule. Defaults to this script's parent.

.PARAMETER Port
    The port OpenWA listens on. Must match the port in the API's OpenWA:BaseUrl.

.PARAMETER ApiNodeAddresses
    The hosts allowed to reach that port. Everything else is refused, because an OpenWA API key is
    the whole of its authentication and this service can send WhatsApp messages as the business.

.PARAMETER SkipFirewall
    Leave the firewall alone. Use when the rule is managed elsewhere.

.PARAMETER SkipScheduledTask
    Do not register the boot task. Requires elevation when not set; without the task OpenWA does not
    come back after a reboot and the console reports the gateway unreachable with no other clue.

.PARAMETER StartTimeoutSeconds
    How long to wait for a started gateway to answer its health check while its node process is
    alive. A gateway whose process exits fails at once instead.

.PARAMETER WhatIf
    Report what would change without changing it.

.EXAMPLE
    .\scripts\Install-OpenWAProduction.ps1

.EXAMPLE
    .\scripts\Install-OpenWAProduction.ps1 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot),
    [int]$Port = 2785,
    [string[]]$ApiNodeAddresses = @('10.10.10.9', '10.10.10.58'),
    [string]$TaskName = 'ShopInventory-OpenWA',
    [switch]$SkipFirewall,
    [switch]$SkipScheduledTask,
    [int]$StartTimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'

function Write-Section {
    param([string]$Text)
    Write-Host ""
    Write-Host "== $Text ==" -ForegroundColor Cyan
}

function Resolve-Executable {
    param([string]$Name, [string[]]$Candidates)

    $onPath = Get-Command $Name -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    foreach ($candidate in $Candidates) {
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }

    return $null
}

function Test-OpenWAListening {
    param([int]$Port)

    try {
        return [bool](Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
    }
    catch {
        return $false
    }
}

function Stop-OpenWAAndWait {
    param([int]$Port, [string]$TaskName)

    & (Join-Path $PSScriptRoot 'Stop-OpenWA.ps1')

    # The boot task's runner exits once node does. Wait for both: a start issued while the task still
    # shows Running is ignored as a second instance, and the gateway would stay down.
    foreach ($attempt in 1..15) {
        $task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
        $taskRunning = $task -and $task.State -eq 'Running'
        if (-not (Test-OpenWAListening -Port $Port) -and -not $taskRunning) {
            return
        }
        Start-Sleep -Seconds 2
    }

    throw "OpenWA still holds port $Port 30s after it was stopped. Stop it with .\scripts\Stop-OpenWA.ps1, then re-run."
}

function Test-OpenWAProcessRunning {
    # The same match Stop-OpenWA.ps1 uses to find the gateway's node process.
    [bool](Get-CimInstance Win32_Process -Filter "name = 'node.exe'" -ErrorAction SilentlyContinue |
        Where-Object {
            $commandLine = $_.CommandLine
            $commandLine -and (
                $commandLine -like "*\OpenWA\dist\main*" -or
                $commandLine -like "* .\dist\main.js*"
            )
        })
}

function Wait-OpenWAHealthy {
    param([int]$Port, [string]$TaskName, [int]$TimeoutSeconds, [string]$LogsPath)

    # On 10.10.10.9 a restart took longer than a minute to answer: the old 60s wait threw while the
    # boot task was still bringing up a healthy gateway, and the run stopped before registering the
    # task and checking the firewall. So wait as long as node is alive, and give up early only once
    # it is gone - that is a gateway that crashed, not one that is slow.
    $graceSeconds = 20
    $started = Get-Date
    $nextNote = 30

    while ($true) {
        Start-Sleep -Seconds 2
        try {
            $probe = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/api/health" -UseBasicParsing -TimeoutSec 5
            if ($probe.StatusCode -eq 200) {
                Write-Host "  OpenWA is answering on $Port." -ForegroundColor Green
                return
            }
        }
        catch { }

        $elapsed = [int]((Get-Date) - $started).TotalSeconds

        if ($elapsed -ge $graceSeconds -and -not (Test-OpenWAProcessRunning)) {
            $task = Get-ScheduledTaskInfo -TaskName $TaskName -ErrorAction SilentlyContinue
            $taskNote = if ($task) { " The '$TaskName' task last returned $($task.LastTaskResult)." } else { '' }
            throw "OpenWA exited without answering http://127.0.0.1:$Port/api/health.$taskNote Read the newest log under $LogsPath."
        }

        if ($elapsed -ge $TimeoutSeconds) {
            throw "OpenWA is running but did not answer http://127.0.0.1:$Port/api/health within ${TimeoutSeconds}s. It may still be starting: check it again, then re-run this script to finish the remaining steps. Logs are under $LogsPath."
        }

        if ($elapsed -ge $nextNote) {
            Write-Host "  Still starting (${elapsed}s)..."
            $nextNote += 30
        }
    }
}

function Read-Stamp {
    param([string]$Path)

    if (Test-Path -LiteralPath $Path) {
        return (Get-Content -LiteralPath $Path -Raw).Trim()
    }

    return $null
}

function Test-DependenciesComplete {
    # Whether npm finds every dependency in package.json installed, at a version it accepts.
    param([string]$Root, [string]$Npm)

    $ErrorActionPreference = 'Continue'
    Push-Location $Root
    try {
        & $Npm ls --depth=0 *> $null
        return $LASTEXITCODE -eq 0
    }
    finally {
        Pop-Location
    }
}

function Get-Sha256 {
    # Not Get-FileHash: in Windows PowerShell it honours -WhatIf and returns nothing, so a dry run
    # would report the dependencies current when a real run reinstalls them.
    param([string]$Path)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha.ComputeHash([System.IO.File]::ReadAllBytes($Path))) -replace '-', '')
    }
    finally {
        $sha.Dispose()
    }
}

function Get-SourceVersion {
    # The checked-out commit, or $null when it cannot vouch for the source - not a git checkout, or
    # tracked files edited in place. A $null always rebuilds.
    param([string]$Root)

    # Windows PowerShell turns a native command's redirected stderr into errors, which 'Stop' would
    # make fatal; git writes to stderr for an ordinary "not a repository".
    $ErrorActionPreference = 'Continue'

    $git = Get-Command git -ErrorAction SilentlyContinue
    if (-not $git) { return $null }

    $commit = & $git.Source -C $Root rev-parse HEAD 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $commit) { return $null }

    $edits = & $git.Source -C $Root status --porcelain --untracked-files=no 2>$null
    if ($LASTEXITCODE -ne 0 -or $edits) { return $null }

    return ([string]$commit).Trim()
}

$openWaRoot = Join-Path $RepositoryRoot 'OpenWA'

Write-Section "Prerequisites"

if (-not (Test-Path -LiteralPath (Join-Path $openWaRoot 'package.json'))) {
    throw @"
OpenWA is not checked out at $openWaRoot.

It is a git submodule, and a plain clone leaves it empty. Populate it with:

    git -C "$RepositoryRoot" submodule update --init --recursive
"@
}
Write-Host "  OpenWA source      $openWaRoot" -ForegroundColor Green

$node = Resolve-Executable -Name 'node' -Candidates @(
    'C:\Program Files\nodejs\node.exe',
    'C:\Users\ngoni\.config\herd\bin\nvm\v20.20.2\node.exe'
)
if (-not $node) {
    throw "Node was not found. Install Node 20 LTS or later, then re-run."
}

$nodeVersion = & $node --version
$nodeMajor = [int](($nodeVersion.TrimStart('v') -split '\.')[0])
if ($nodeMajor -lt 20) {
    throw "Node $nodeVersion is too old. whatsapp-web.js and its Puppeteer need Node 20 or later."
}
Write-Host "  Node               $node ($nodeVersion)" -ForegroundColor Green

# Start-OpenWA, Register-OpenWAStartupTask and Install-OpenWAUserStartup each default -NodeHome to a
# developer machine's nvm folder, so every call below passes the Node this script resolved. Without
# it the first start fails on a server ("Node executable not found"), and the boot task registers
# against a path that does not exist and fails silently at the next reboot.
$nodeHome = Split-Path -Parent $node

$chrome = Resolve-Executable -Name 'chrome' -Candidates @(
    'C:\Program Files\Google\Chrome\Application\chrome.exe',
    'C:\Program Files (x86)\Google\Chrome\Application\chrome.exe'
)
if (-not $chrome) {
    throw @"
Google Chrome was not found.

OpenWA drives a real browser to hold the WhatsApp Web session. Install Chrome for all users, or
let Puppeteer download its own Chromium by clearing PUPPETEER_SKIP_CHROMIUM_DOWNLOAD - the
installed browser is preferred because it is patched by whatever patches the rest of the estate.
"@
}
Write-Host "  Chrome             $chrome" -ForegroundColor Green

$npm = Resolve-Executable -Name 'npm.cmd' -Candidates @((Join-Path (Split-Path $node) 'npm.cmd'))
if (-not $npm) { throw "npm was not found beside $node." }

Write-Section "Build"

$distEntry = Join-Path $openWaRoot 'dist\main.js'
$nodeModules = Join-Path $openWaRoot 'node_modules'

# What the installed dependencies and the built dist were made from. A submodule update changes the
# source under a dist\main.js that is still there, and a re-run that only checked for that file kept
# serving the old build. npm ci and the build each empty their folder first, so a stamp never outlives
# what it describes.
$installStamp = Join-Path $nodeModules '.shopinventory-install'
$buildStamp = Join-Path $openWaRoot 'dist\.shopinventory-build'
$lockVersion = Get-Sha256 -Path (Join-Path $openWaRoot 'package-lock.json')
$sourceVersion = Get-SourceVersion -Root $openWaRoot

$needsInstall = (-not (Test-Path -LiteralPath $nodeModules)) -or ((Read-Stamp -Path $installStamp) -ne $lockVersion)

# An install made before these stamps has none. Reinstalling it would stop the gateway for the minutes
# npm ci takes, so when npm finds the installed tree complete it is adopted instead.
if ($needsInstall -and (Test-Path -LiteralPath $nodeModules) -and -not (Test-Path -LiteralPath $installStamp)) {
    if (Test-DependenciesComplete -Root $openWaRoot -Npm $npm) {
        Write-Host "  Adopting the dependencies already installed: npm finds them complete." -ForegroundColor Green
        if ($PSCmdlet.ShouldProcess($installStamp, "Record the installed dependencies")) {
            Set-Content -LiteralPath $installStamp -Value $lockVersion -Encoding ASCII
        }
        $needsInstall = $false
    }
}

$needsBuild = $needsInstall -or (-not (Test-Path -LiteralPath $distEntry)) -or (-not $sourceVersion) -or
    ((Read-Stamp -Path $buildStamp) -ne $sourceVersion)

# npm ci replaces node_modules, which a running gateway holds open, so it has to stop first. A rebuild
# alone does not: the gateway keeps serving the build it loaded until it is restarted onto the new one
# below, and a build that fails leaves it running.
if ($needsInstall -and (Test-OpenWAListening -Port $Port)) {
    if ($PSCmdlet.ShouldProcess("localhost:$Port", "Stop OpenWA to reinstall its dependencies")) {
        Write-Host "  Stopping OpenWA: its dependencies changed." -ForegroundColor Yellow
        Stop-OpenWAAndWait -Port $Port -TaskName $TaskName
    }
}

if ($needsInstall) {
    if ($PSCmdlet.ShouldProcess($openWaRoot, "npm ci")) {
        Write-Host "  Installing dependencies (this takes a few minutes)..." -ForegroundColor Yellow
        Push-Location $openWaRoot
        try {
            # This host already has Chrome and PUPPETEER_EXECUTABLE_PATH below is what gets used, so
            # Puppeteer's own ~150MB browser is dead weight - and when its download fails, npm ci fails.
            # Current Puppeteer reads PUPPETEER_SKIP_DOWNLOAD and ignores the older name, which stays
            # for older versions.
            $env:PUPPETEER_SKIP_DOWNLOAD = 'true'
            $env:PUPPETEER_SKIP_CHROMIUM_DOWNLOAD = 'true'
            & $npm ci
            if ($LASTEXITCODE -ne 0) { throw "npm ci failed with exit code $LASTEXITCODE." }
            Set-Content -LiteralPath $installStamp -Value $lockVersion -Encoding ASCII
        }
        finally { Pop-Location }
    }
}
else {
    Write-Host "  Dependencies already installed from this package-lock.json." -ForegroundColor Green
}

if ($needsBuild) {
    if ($PSCmdlet.ShouldProcess($openWaRoot, "npm run build")) {
        Write-Host "  Building..." -ForegroundColor Yellow
        Push-Location $openWaRoot
        try {
            & $npm run build
            if ($LASTEXITCODE -ne 0) {
                throw "npm run build failed with exit code $LASTEXITCODE. A running OpenWA keeps serving the build it loaded, but dist is now incomplete: fix the build before anything restarts it."
            }
            if ($sourceVersion) {
                Set-Content -LiteralPath $buildStamp -Value $sourceVersion -Encoding ASCII
            }
        }
        finally { Pop-Location }
    }
}
else {
    Write-Host "  dist\main.js is already built from $sourceVersion." -ForegroundColor Green
}

Write-Section "Configuration"

$dataPath = Join-Path $openWaRoot 'data'
if (-not (Test-Path -LiteralPath $dataPath)) {
    New-Item -ItemType Directory -Path $dataPath -Force | Out-Null
}

# NODE_ENV=production matters for more than logging: it is what makes OpenWA mint a random admin
# key on first run instead of the well-known 'dev-admin-key', and what makes it withhold validation
# detail from error bodies.
$envLines = @(
    '# Written by scripts/Install-OpenWAProduction.ps1. Re-running the installer rewrites this file.',
    '',
    'NODE_ENV=production',
    "PORT=$Port",
    'LOG_LEVEL=info',
    '',
    '# The largest request body OpenWA accepts. ShopInventory sends invoices as base64 PDFs, a third',
    '# larger than the file, and Express''s own default of 100kb refused every one with 413.',
    'API_BODY_LIMIT=16mb',
    '',
    '# SQLite beside the app. The volume here is a message log, not a business database, and a',
    '# Postgres dependency would mean the gateway cannot start while the cluster is failing over.',
    'DATABASE_TYPE=sqlite',
    'DATABASE_NAME=./data/openwa.sqlite',
    'DATABASE_SYNCHRONIZE=true',
    'DATABASE_LOGGING=false',
    '',
    'ENGINE_TYPE=whatsapp-web.js',
    'SESSION_DATA_PATH=./data/sessions',
    'PUPPETEER_HEADLESS=true',
    'PUPPETEER_SKIP_CHROMIUM_DOWNLOAD=true',
    "PUPPETEER_EXECUTABLE_PATH=$($chrome -replace '\\', '/')",
    '# --no-sandbox is required when the task runs as SYSTEM: Chrome refuses its sandbox under an',
    '# elevated account and exits before the session ever opens.',
    'PUPPETEER_ARGS=--no-sandbox,--disable-setuid-sandbox,--disable-dev-shm-usage,--disable-gpu',
    '',
    'STORAGE_TYPE=local',
    'STORAGE_LOCAL_PATH=./data/media',
    '',
    'WEBHOOK_TIMEOUT=10000',
    'WEBHOOK_MAX_RETRIES=3',
    'WEBHOOK_RETRY_DELAY=5000',
    '',
    '# Nothing else on this host, and no container runtime.',
    'REDIS_ENABLED=false',
    'REDIS_BUILTIN=false',
    'QUEUE_ENABLED=false',
    'CACHE_ENABLED=false',
    'POSTGRES_BUILTIN=false',
    'MINIO_BUILTIN=false',
    'DASHBOARD_ENABLED=false',
    'PROXY_ENABLED=false',
    '',
    '# The port is firewalled to the API nodes, so CORS is not the control that matters here.',
    'CORS_ORIGINS=*',
    'ENABLE_SWAGGER=false'
)

$envPath = Join-Path $openWaRoot '.env'
$currentEnv = @()
if (Test-Path -LiteralPath $envPath) {
    $currentEnv = @(Get-Content -LiteralPath $envPath)
}

# A running gateway read its .env when it started, so a change only takes effect with a restart.
$envChanged = ($currentEnv -join "`n") -ne ($envLines -join "`n")
if (-not $envChanged) {
    Write-Host "  $envPath is already current." -ForegroundColor Green
}
elseif ($PSCmdlet.ShouldProcess($envPath, "Write production .env")) {
    Set-Content -LiteralPath $envPath -Value $envLines -Encoding UTF8
    Write-Host "  Wrote $envPath" -ForegroundColor Green
}

Write-Section "Start"

$runner = Join-Path $PSScriptRoot 'Run-OpenWA.ps1'
if (-not (Test-Path -LiteralPath $runner)) {
    throw "Run-OpenWA.ps1 was not found beside this script at $runner."
}

$alreadyListening = Test-OpenWAListening -Port $Port

# A running gateway read its .env and loaded its build when it started; either changing takes a restart.
if ($alreadyListening -and ($envChanged -or $needsBuild)) {
    if ($PSCmdlet.ShouldProcess("localhost:$Port", "Restart OpenWA onto the new build and .env")) {
        Write-Host "  Restarting OpenWA onto the new build and .env." -ForegroundColor Yellow
        Stop-OpenWAAndWait -Port $Port -TaskName $TaskName
        $alreadyListening = $false
    }
}

if ($alreadyListening) {
    Write-Host "  OpenWA is already running this build and .env. Leaving it running." -ForegroundColor Green
}
elseif ($PSCmdlet.ShouldProcess("localhost:$Port", "Start OpenWA")) {
    # A gateway started from here runs in this console's session and dies when it signs out. Where
    # the boot task is registered, start that instead: it runs as SYSTEM, the way the gateway runs
    # after a reboot. A first install has no task yet - it is registered below.
    $bootTask = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    $canStartTask = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)

    if ($bootTask -and $canStartTask) {
        Start-ScheduledTask -TaskName $TaskName
        Write-Host "  Started the '$TaskName' boot task." -ForegroundColor Green
    }
    else {
        & (Join-Path $PSScriptRoot 'Start-OpenWA.ps1') -NodeHome $nodeHome -ChromePath $chrome
    }

    Wait-OpenWAHealthy -Port $Port -TaskName $TaskName -TimeoutSeconds $StartTimeoutSeconds -LogsPath (Join-Path $openWaRoot 'logs')
}

$apiKeyPath = Join-Path $dataPath '.api-key'
$apiKey = $null
if (Test-Path -LiteralPath $apiKeyPath) {
    $apiKey = (Get-Content -LiteralPath $apiKeyPath -Raw).Trim()
}

if ($apiKey -eq 'dev-admin-key') {
    Write-Warning @"
OpenWA is holding the development API key 'dev-admin-key'.

That key is seeded when NODE_ENV is not 'production', and it is public - it is in the repository.
It is now fixed in the database, and rewriting .env does not replace it. Mint a real one and revoke
this one from the OpenWA dashboard, or stop OpenWA, delete data\main.sqlite* and start again.
API keys live in main.sqlite, not openwa.sqlite (sessions, webhooks and messages): deleting only
openwa.sqlite drops the paired session and keeps this key.
"@
}

Write-Section "Restart after reboot"

if ($SkipScheduledTask) {
    Write-Warning "Skipped by request. OpenWA will NOT come back after a reboot."
}
else {
    $isElevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
        [Security.Principal.WindowsBuiltInRole]::Administrator)

    if (-not $isElevated) {
        Write-Warning @"
Not elevated, so the boot task was not registered. Either re-run this from an elevated PowerShell,
or install the per-user fallback, which starts OpenWA when that user signs in:

    .\scripts\Install-OpenWAUserStartup.ps1 -NodeHome '$nodeHome' -ChromePath '$chrome'
"@
    }
    elseif ($PSCmdlet.ShouldProcess($TaskName, "Register scheduled task")) {
        & (Join-Path $PSScriptRoot 'Register-OpenWAStartupTask.ps1') -NodeHome $nodeHome -ChromePath $chrome
        Write-Host "  Registered $TaskName." -ForegroundColor Green
    }
}

Write-Section "Firewall"

if ($SkipFirewall) {
    Write-Host "  Skipped by request." -ForegroundColor Yellow
}
else {
    $ruleName = "ShopInventory OpenWA $Port"
    $existing = Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue

    if ($existing) {
        Write-Host "  Rule '$ruleName' already exists." -ForegroundColor Green
    }
    elseif ($PSCmdlet.ShouldProcess($ruleName, "Create inbound rule")) {
        try {
            # Scoped to the API nodes on purpose. An OpenWA API key is this service's entire
            # authentication, and holding one means being able to send WhatsApp messages as the
            # business - so the port should not be reachable from the general network.
            New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow `
                -Protocol TCP -LocalPort $Port -RemoteAddress $ApiNodeAddresses | Out-Null
            Write-Host "  Allowed $($ApiNodeAddresses -join ', ') to reach $Port." -ForegroundColor Green
        }
        catch {
            Write-Warning "Could not create the firewall rule (needs elevation): $($_.Exception.Message)"
        }
    }
}

Write-Section "Next: point the API at this gateway"

$thisHost = try {
    (Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -like '10.*' } |
        Select-Object -First 1).IPAddress
}
catch { $null }
if (-not $thisHost) { $thisHost = '10.10.10.9' }

Write-Host @"

OpenWA is installed. It is not yet connected to the API - that is a separate step, on each API
node, because the settings live in each node's web.config:

    .\scripts\Set-OpenWAApiConfig.ps1 ``
        -OpenWaBaseUrl 'http://${thisHost}:$Port' ``
        -OpenWaApiKey  '<the key in $apiKeyPath>' ``
        -WebhookSecret '<a new secret, the same string on every node>'

Then, on this host, confirm the whole path works:

    .\scripts\Test-WhatsAppDeliveryPath.ps1 ``
        -ApiBaseUrl 'http://${thisHost}:5106' ``
        -OpenWaBaseUrl 'http://127.0.0.1:$Port' ``
        -OpenWaApiKey '<the key>' -WebhookSecret '<the secret>' ``
        -Username '<an admin>' -Password '<their password>'

Last, pair the handset: open the WhatsApp console, create a session, Start it, and scan the QR from
the business phone (WhatsApp > Linked devices > Link a device). That step needs the phone in hand
and cannot be scripted.
"@ -ForegroundColor White
