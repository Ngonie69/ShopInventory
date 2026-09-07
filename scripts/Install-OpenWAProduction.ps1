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
      2. Builds the submodule at OpenWA/ if dist\main.js is missing.
      3. Writes OpenWA\.env for a production run - SQLite, local storage, no Redis, the installed
         Chrome rather than a downloaded Chromium.
      4. Starts it once so it mints its admin API key into data\.api-key.
      5. Registers a Scheduled Task that starts it as SYSTEM at boot.
      6. Opens the API port to this host only, if a firewall rule is missing.
      7. Prints the four OpenWA__* values to set on the API, and the command that verifies them.

    It does NOT write the API's configuration. Those values live in each node's web.config and are
    applied by Set-OpenWAApiConfig.ps1, so that one host's install cannot silently repoint both API
    nodes at itself.

    Re-runnable. An existing install keeps its data directory, its API key and its paired session;
    only the .env and the scheduled task are rewritten.

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
    [switch]$SkipScheduledTask
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
$needsInstall = -not (Test-Path -LiteralPath (Join-Path $openWaRoot 'node_modules'))
$needsBuild = -not (Test-Path -LiteralPath $distEntry)

if ($needsInstall) {
    if ($PSCmdlet.ShouldProcess($openWaRoot, "npm ci")) {
        Write-Host "  Installing dependencies (this takes a few minutes)..." -ForegroundColor Yellow
        Push-Location $openWaRoot
        try {
            # Chromium is ~150MB and this host already has Chrome; PUPPETEER_EXECUTABLE_PATH below
            # is what actually gets used, so downloading it would be dead weight.
            $env:PUPPETEER_SKIP_CHROMIUM_DOWNLOAD = 'true'
            & $npm ci
            if ($LASTEXITCODE -ne 0) { throw "npm ci failed with exit code $LASTEXITCODE." }
        }
        finally { Pop-Location }
    }
}
else {
    Write-Host "  Dependencies already installed." -ForegroundColor Green
}

if ($needsBuild) {
    if ($PSCmdlet.ShouldProcess($openWaRoot, "npm run build")) {
        Write-Host "  Building..." -ForegroundColor Yellow
        Push-Location $openWaRoot
        try {
            & $npm run build
            if ($LASTEXITCODE -ne 0) { throw "npm run build failed with exit code $LASTEXITCODE." }
        }
        finally { Pop-Location }
    }
}
else {
    Write-Host "  dist\main.js is already built." -ForegroundColor Green
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
if ($PSCmdlet.ShouldProcess($envPath, "Write production .env")) {
    Set-Content -LiteralPath $envPath -Value $envLines -Encoding UTF8
    Write-Host "  Wrote $envPath" -ForegroundColor Green
}

Write-Section "Start"

$runner = Join-Path $PSScriptRoot 'Run-OpenWA.ps1'
if (-not (Test-Path -LiteralPath $runner)) {
    throw "Run-OpenWA.ps1 was not found beside this script at $runner."
}

$alreadyListening = $false
try {
    $alreadyListening = [bool](Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
}
catch {
    $alreadyListening = $false
}

if ($alreadyListening) {
    Write-Host "  Something is already listening on $Port. Leaving it running." -ForegroundColor Green
}
elseif ($PSCmdlet.ShouldProcess("localhost:$Port", "Start OpenWA")) {
    & (Join-Path $PSScriptRoot 'Start-OpenWA.ps1')

    $ready = $false
    foreach ($attempt in 1..30) {
        Start-Sleep -Seconds 2
        try {
            $probe = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/api/health" -UseBasicParsing -TimeoutSec 5
            if ($probe.StatusCode -eq 200) { $ready = $true; break }
        }
        catch { }
    }

    if (-not $ready) {
        throw "OpenWA did not answer http://127.0.0.1:$Port/api/health within 60s. Read the newest log under $openWaRoot\logs."
    }

    Write-Host "  OpenWA is answering on $Port." -ForegroundColor Green
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
this one from the OpenWA dashboard, or stop OpenWA, delete data\openwa.sqlite and start again
(which also drops the paired session and every captured webhook registration).
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

    .\scripts\Install-OpenWAUserStartup.ps1
"@
    }
    elseif ($PSCmdlet.ShouldProcess($TaskName, "Register scheduled task")) {
        & (Join-Path $PSScriptRoot 'Register-OpenWAStartupTask.ps1')
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
