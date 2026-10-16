# Exercises the gateway's pinned browser: scripts\Install-OpenWAChrome.ps1 installing Chrome for
# Testing at the version OpenWA's Puppeteer declares, and scripts\Install-OpenWAProduction.ps1
# pointing the gateway at it, keeping a newer Chrome's sessions off it until told otherwise.
#
# On 2026-10-16 the gateway on 10.10.10.9 ran Google Chrome 155 under a Puppeteer built for 146. An
# older Chrome deletes the IndexedDB a newer one wrote, and WhatsApp keeps its login there, so the
# installer must not make that switch under a paired session unattended.
#
# Everything runs in a throwaway layout under %TEMP%: a fake OpenWA whose node_modules links to a real
# OpenWA checkout's packages, and a fake dist\main.js that answers the health check and records the
# Chrome it was given. The first run downloads Chrome for Testing (~170MB, a few minutes). Run it
# under Windows PowerShell 5.1, the servers' shell:
#
#     powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-OpenWAChromeInstall.ps1 -OpenWASource C:\path\to\checkout\OpenWA
#
# -OpenWASource defaults to this checkout's OpenWA, which in a worktree is empty: point it at a checkout
# with node_modules installed. It refuses to run while a real gateway is running on this machine,
# because stopping the fake one uses Stop-OpenWA.ps1, which matches any gateway's node.
#
# -KeepDownload keeps the browser a previous run downloaded (its stamp is still removed), so scenario 1
# runs npx against an existing folder instead of downloading again.

param(
    # Defaults to this script's checkout. Resolved below, because Windows PowerShell 5.1 leaves
    # $PSScriptRoot empty in a default value when the script is run with -File.
    [string]$RepoRoot,
    [string]$OpenWASource,
    [string]$NodeHome = 'C:\Users\ngoni\.config\herd\bin\nvm\v20.20.2',
    [string]$SystemChrome = 'C:\Program Files\Google\Chrome\Application\chrome.exe',
    [int]$Port = 2799,
    [switch]$KeepDownload
)

$ErrorActionPreference = 'Stop'
if (-not $RepoRoot) { $RepoRoot = Split-Path -Parent $PSScriptRoot }
if (-not $OpenWASource) { $OpenWASource = Join-Path $RepoRoot 'OpenWA' }

$fake = Join-Path ([IO.Path]::GetTempPath()) 'openwa-chrome-test'
$scripts = Join-Path $fake 'scripts'
$root = Join-Path $fake 'OpenWA'
$sessions = Join-Path $root 'data\sessions'
$profileDir = Join-Path $sessions 'session-Sales'
$gatewayChrome = Join-Path $fake 'gateway-chrome.txt'
$script:failures = 0

function Check {
    param([string]$Name, [bool]$Passed, [string]$Detail)
    if ($Passed) { Write-Host "  PASS  $Name" -ForegroundColor Green }
    else { Write-Host "  FAIL  $Name $Detail" -ForegroundColor Red; $script:failures++ }
}

function Get-GatewayNode {
    @(Get-CimInstance Win32_Process -Filter "name = 'node.exe'" | Where-Object {
        $_.CommandLine -and ($_.CommandLine -like '*\OpenWA\dist\main*' -or $_.CommandLine -like '* .\dist\main.js*') })
}

function Get-ProfileChrome {
    @(Get-CimInstance Win32_Process -Filter "name = 'chrome.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf($profileDir, [StringComparison]::OrdinalIgnoreCase) -ge 0 })
}

function Invoke-Installer {
    # The way an operator runs it on the server: a separate Windows PowerShell with -File.
    param([string[]]$Extra)
    $ErrorActionPreference = 'Continue'
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "$scripts\Install-OpenWAProduction.ps1",
        '-Port', $Port, '-TaskName', 'ShopInventory-OpenWA-ChromeTest', '-SkipFirewall', '-SkipScheduledTask', '-StartTimeoutSeconds', '60') + $Extra
    # To files, not a pipe: the gateway it starts inherits the pipe and holds it open, so reading
    # one would wait for the gateway to exit.
    $stdout = Join-Path $fake 'installer.out.txt'
    $stderr = Join-Path $fake 'installer.err.txt'
    $quoted = $arguments | Where-Object { $null -ne $_ -and "$_" -ne '' } | ForEach-Object { if ("$_" -match '\s') { "`"$_`"" } else { "$_" } }
    $process = Start-Process powershell.exe -ArgumentList $quoted -RedirectStandardOutput $stdout -RedirectStandardError $stderr -WindowStyle Hidden -PassThru
    $null = $process.Handle  # without it, ExitCode reads empty after the exit
    $process.WaitForExit()
    $script:installerExit = $process.ExitCode
    $output = @(Get-Content -LiteralPath $stdout) + @(Get-Content -LiteralPath $stderr)
    $output | Where-Object { $_ -match 'Chrome|WARNING|Backed up|Restart|already|running|Started|answering|Keeping|session|QR|throw|Exception|error' } |
        ForEach-Object { "    installer> $_" } | Write-Host
    return , $output
}

function Get-EnvChrome {
    $line = @(Get-Content -LiteralPath (Join-Path $root '.env') | Where-Object { $_ -like 'PUPPETEER_EXECUTABLE_PATH=*' })[0]
    ($line.Substring('PUPPETEER_EXECUTABLE_PATH='.Length)) -replace '/', '\'
}

function Get-RunningChrome {
    if (Test-Path -LiteralPath $gatewayChrome) { (Get-Content -LiteralPath $gatewayChrome -Raw).Trim() } else { $null }
}

if (Get-GatewayNode) {
    throw 'An OpenWA gateway is running on this machine. Stop it first: this test stops gateways with Stop-OpenWA.ps1.'
}
foreach ($required in @((Join-Path $OpenWASource 'node_modules\whatsapp-web.js'), (Join-Path $OpenWASource 'node_modules\@puppeteer\browsers'), (Join-Path $NodeHome 'node.exe'), $SystemChrome)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "$required was not found. See -OpenWASource, -NodeHome and -SystemChrome." }
}

# The version, read here independently of the script under test.
$revisions = Get-Content -LiteralPath (Join-Path $OpenWASource 'node_modules\puppeteer-core\lib\cjs\puppeteer\revisions.js') -Raw
$expected = [regex]::Match($revisions, "chrome: '([\d.]+)'").Groups[1].Value
"Puppeteer under $OpenWASource declares Chrome $expected; Google Chrome here is $((Get-Item $SystemChrome).VersionInfo.ProductVersion)."

# --- layout -------------------------------------------------------------------------------------
$kept = Join-Path ([IO.Path]::GetTempPath()) 'openwa-chrome-test-download'
if ($KeepDownload -and (Test-Path -LiteralPath (Join-Path $root 'chrome\chrome'))) {
    if (Test-Path -LiteralPath $kept) { Remove-Item -LiteralPath $kept -Recurse -Force }
    Move-Item -LiteralPath (Join-Path $root 'chrome\chrome') -Destination $kept
}
if (Test-Path -LiteralPath $fake) {
    # Junctions first, so removing the folder never reaches the real node_modules behind them.
    Get-ChildItem -LiteralPath (Join-Path $root 'node_modules') -Recurse -Force -ErrorAction SilentlyContinue |
        Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } | ForEach-Object { [IO.Directory]::Delete($_.FullName) }
    Remove-Item -LiteralPath $fake -Recurse -Force
}
New-Item -ItemType Directory -Path $scripts, (Join-Path $root 'dist'), (Join-Path $root 'node_modules\@puppeteer'), $sessions -Force | Out-Null
foreach ($name in 'Install-OpenWAProduction.ps1', 'Install-OpenWAChrome.ps1', 'Run-OpenWA.ps1', 'Start-OpenWA.ps1', 'Stop-OpenWA.ps1', 'Register-OpenWAStartupTask.ps1') {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $scripts
}
# Node resolves a junction to its real path, so each package's own dependencies come from the real tree.
foreach ($package in 'whatsapp-web.js', '@puppeteer\browsers') {
    New-Item -ItemType Junction -Path (Join-Path $root "node_modules\$package") -Target (Join-Path $OpenWASource "node_modules\$package") | Out-Null
}
# npx runs a package through its shim in node_modules\.bin, which npm ci writes; these name the package relatively.
New-Item -ItemType Directory -Path (Join-Path $root 'node_modules\.bin') | Out-Null
Copy-Item -Path (Join-Path $OpenWASource 'node_modules\.bin\browsers*') -Destination (Join-Path $root 'node_modules\.bin')
Set-Content -LiteralPath (Join-Path $root 'package.json') -Encoding ASCII -Value '{ "name": "openwa-fake", "version": "0.0.0", "private": true, "scripts": { "build": "node -e 0" } }'
Set-Content -LiteralPath (Join-Path $root 'package-lock.json') -Encoding ASCII -Value '{ "name": "openwa-fake", "lockfileVersion": 3 }'
$sha = [Security.Cryptography.SHA256]::Create()
$lockHash = [BitConverter]::ToString($sha.ComputeHash([IO.File]::ReadAllBytes((Join-Path $root 'package-lock.json')))) -replace '-', ''
Set-Content -LiteralPath (Join-Path $root 'node_modules\.shopinventory-install') -Value $lockHash -Encoding ASCII
Set-Content -LiteralPath (Join-Path $root 'dist\main.js') -Encoding ASCII -Value @'
// Stand-in for OpenWA's dist\main.js: answers /api/health on .env's PORT and records the Chrome it was given.
const fs = require('fs'), http = require('http'), path = require('path');
const port = /^PORT=(\d+)/m.exec(fs.readFileSync('.env', 'utf8'))[1];
fs.writeFileSync(path.join('..', 'gateway-chrome.txt'), process.env.PUPPETEER_EXECUTABLE_PATH || '');
http.createServer((req, res) => { res.writeHead(200); res.end('ok'); }).listen(port, '127.0.0.1');
'@
# A committed checkout, so the installer's build stamp holds and a re-run with nothing changed is one.
Set-Content -LiteralPath (Join-Path $root '.gitignore') -Encoding ASCII -Value 'node_modules/', 'dist/', 'data/', 'logs/', '.env'
& git -C $root init -q
if ($KeepDownload -and (Test-Path -LiteralPath $kept)) {
    New-Item -ItemType Directory -Path (Join-Path $root 'chrome') | Out-Null
    Move-Item -LiteralPath $kept -Destination (Join-Path $root 'chrome\chrome')
}
& git -C $root add .gitignore package.json package-lock.json
& git -C $root -c user.name=test -c user.email=test@example.invalid commit -q -m fake

try {
    # --- Install-OpenWAChrome.ps1 on its own ------------------------------------------------------
    "`n=== 1. First install, with -File"
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $ErrorActionPreference = 'Continue'
    $printed = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$scripts\Install-OpenWAChrome.ps1" -OpenWARoot $root -NodeHome $NodeHome 2>&1 | ForEach-Object { "$_" })
    $ErrorActionPreference = 'Stop'
    $printed | Select-Object -Last 8 | ForEach-Object { "    chrome> $_" }
    "    took $([Math]::Round($watch.Elapsed.TotalSeconds))s"
    $stampPath = Join-Path $root 'chrome\.shopinventory-chrome'
    $stamp = (Get-Content -LiteralPath $stampPath -Raw).Trim()
    $exe = $stamp -replace '^chrome@\S+ ', ''
    Check 'exits 0 and prints the version' ($LASTEXITCODE -eq 0 -and ($printed -join ' ') -match [regex]::Escape($expected)) ($printed -join ' | ')
    Check "stamp reads chrome@$expected and an executable that exists" ($stamp -like "chrome@$expected *" -and (Test-Path -LiteralPath $exe)) $stamp
    Check "the executable is Chrome $expected, under OpenWA\chrome" ((Get-Item $exe).VersionInfo.ProductVersion -eq $expected -and $exe.StartsWith((Join-Path $root 'chrome'), [StringComparison]::OrdinalIgnoreCase)) $exe
    Check 'the version is not written into either script' (-not (Select-String -LiteralPath "$PSScriptRoot\Install-OpenWAChrome.ps1", "$PSScriptRoot\Install-OpenWAProduction.ps1" -SimpleMatch $expected -Quiet))
    $untracked = @(& git -C $root status --porcelain --untracked-files=all 2>$null | Where-Object { $_ -match '^\?\? chrome' })
    Check 'git status shows nothing under chrome\' ($untracked.Count -eq 0) ($untracked -join ', ')

    "`n=== 2. Re-run: nothing to do"
    $stampTime = (Get-Item -LiteralPath $stampPath).LastWriteTimeUtc
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $again = & "$scripts\Install-OpenWAChrome.ps1" -OpenWARoot $root -NodeHome $NodeHome
    "    took $([Math]::Round($watch.Elapsed.TotalSeconds, 1))s"
    Check 'not installed again, same executable, stamp untouched' (-not $again.Installed -and $again.ExecutablePath -eq $exe -and (Get-Item -LiteralPath $stampPath).LastWriteTimeUtc -eq $stampTime)

    "`n=== 3. Stamp from another version: installs (npx finds the folder and returns at once)"
    Set-Content -LiteralPath $stampPath -Value 'chrome@1.0.0.0 C:\nowhere\chrome.exe' -Encoding ASCII
    $redo = & "$scripts\Install-OpenWAChrome.ps1" -OpenWARoot $root -NodeHome $NodeHome
    Check 'installed, stamp rewritten to the declared version' ($redo.Installed -and $redo.ExecutablePath -eq $exe -and (Get-Content -LiteralPath $stampPath -Raw).Trim() -eq $stamp)

    "`n=== 4. -WhatIf with no stamp: changes nothing, predicts the same path"
    Remove-Item -LiteralPath $stampPath
    $dry = & "$scripts\Install-OpenWAChrome.ps1" -OpenWARoot $root -NodeHome $NodeHome -WhatIf
    Check 'no install, no stamp, predicted path is the real one' (-not $dry.Installed -and -not (Test-Path -LiteralPath $stampPath) -and $dry.ExecutablePath -eq $exe) $dry.ExecutablePath
    $null = & "$scripts\Install-OpenWAChrome.ps1" -OpenWARoot $root -NodeHome $NodeHome

    "`n=== 5. Profiles a newer Chrome opened"
    foreach ($case in @(@('session-Sales', '999.0.0.0'), @('session-Older', '100.0.0.0'), @('session-Same', $expected), @('session-Fresh', $null))) {
        New-Item -ItemType Directory -Path (Join-Path $sessions $case[0]) -Force | Out-Null
        if ($case[1]) { Set-Content -LiteralPath (Join-Path $sessions "$($case[0])\Last Version") -Value $case[1] -NoNewline -Encoding ASCII }
    }
    $listed = @((& "$scripts\Install-OpenWAChrome.ps1" -OpenWARoot $root -NodeHome $NodeHome).NewerProfiles)
    Check 'only the profile last opened by a newer Chrome is listed' ($listed.Count -eq 1 -and $listed[0].Name -eq 'session-Sales' -and $listed[0].LastVersion -eq '999.0.0.0') (($listed | ForEach-Object { $_.Name }) -join ', ')
    Remove-Item -LiteralPath (Join-Path $sessions 'session-Older'), (Join-Path $sessions 'session-Same'), (Join-Path $sessions 'session-Fresh') -Recurse

    # --- the installer -----------------------------------------------------------------------------
    # session-Sales now looks like .9's: opened by a newer Chrome, with an IndexedDB and a cache.
    New-Item -ItemType Directory -Path (Join-Path $profileDir 'Default\IndexedDB\https_web.whatsapp.com_0.indexeddb.leveldb'), (Join-Path $profileDir 'Default\Cache\Cache_Data') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $profileDir 'Default\IndexedDB\https_web.whatsapp.com_0.indexeddb.leveldb\000003.log') -Value 'login' -Encoding ASCII
    Set-Content -LiteralPath (Join-Path $profileDir 'Default\Cache\Cache_Data\data_0') -Value 'cache' -Encoding ASCII
    Set-Content -LiteralPath (Join-Path $root '.env') -Value "PUPPETEER_EXECUTABLE_PATH=$($SystemChrome -replace '\\', '/')" -Encoding ASCII

    "`n=== 6. Installer, newer session, no -AcceptSessionRepair: keeps the Chrome in .env"
    $out = Invoke-Installer
    Check 'exits 0' ($script:installerExit -eq 0) "exit $script:installerExit"
    Check '.env and the started gateway keep Google Chrome' ((Get-EnvChrome) -eq $SystemChrome -and (Get-RunningChrome) -eq $SystemChrome) "env=$(Get-EnvChrome) running=$(Get-RunningChrome)"
    Check 'it says why, and how to switch' (($out -join "`n") -match 'Keeping the gateway on' -and ($out -join "`n") -match '-AcceptSessionRepair')
    Check 'no backup taken' (-not (Test-Path -LiteralPath (Join-Path $root 'data\sessions-backup')))

    "`n=== 7. Installer with -AcceptSessionRepair while running, a Chrome holding the profile"
    $holder = Start-Process -FilePath $SystemChrome -ArgumentList '--headless=new', '--no-sandbox', '--disable-gpu', "--user-data-dir=`"$profileDir`"", 'about:blank' -PassThru -WindowStyle Hidden
    Start-Sleep -Seconds 3
    Check 'a Chrome holds the profile before the run' (@(Get-ProfileChrome).Count -gt 0)
    # That Chrome stamps the profile with its own version, so the backup is named after it.
    $out = Invoke-Installer -Extra @('-AcceptSessionRepair')
    Check 'exits 0' ($script:installerExit -eq 0) "exit $script:installerExit"
    Check '.env and the restarted gateway use Chrome for Testing' ((Get-EnvChrome) -eq $exe -and (Get-RunningChrome) -eq $exe) "env=$(Get-EnvChrome) running=$(Get-RunningChrome)"
    Check 'the Chrome holding the profile was ended' (@(Get-ProfileChrome).Count -eq 0)
    $backup = @(Get-ChildItem -LiteralPath (Join-Path $root 'data\sessions-backup') -Directory -ErrorAction SilentlyContinue)
    $copied = if ($backup.Count -eq 1) { Join-Path $backup[0].FullName 'session-Sales' } else { '' }
    Check 'one backup, named after the Chrome that last opened it' ($backup.Count -eq 1 -and $backup[0].Name -match '^\d{8}-\d{6}-chrome-\d+\.') (($backup | ForEach-Object { $_.Name }) -join ', ')
    Check 'the backup holds the IndexedDB and Last Version, not the cache' ($copied -and
        (Test-Path -LiteralPath (Join-Path $copied 'Default\IndexedDB\https_web.whatsapp.com_0.indexeddb.leveldb\000003.log')) -and
        (Test-Path -LiteralPath (Join-Path $copied 'Last Version')) -and -not (Test-Path -LiteralPath (Join-Path $copied 'Default\Cache')))
    Check 'it warns that the session needs its QR scanned' (($out -join "`n") -match 'scanned again')

    "`n=== 8. Installer re-run, nothing changed"
    $before = @(Get-GatewayNode)
    # The fake gateway never opens the profile, so mark it as Chrome for Testing would have.
    Set-Content -LiteralPath (Join-Path $profileDir 'Last Version') -Value $expected -NoNewline -Encoding ASCII
    $out = Invoke-Installer
    $after = @(Get-GatewayNode)
    Check 'exits 0, gateway left running, still on Chrome for Testing' ($script:installerExit -eq 0 -and $before.Count -eq 1 -and $after.Count -eq 1 -and $after[0].ProcessId -eq $before[0].ProcessId -and (Get-EnvChrome) -eq $exe)
    Check 'no second backup' (@(Get-ChildItem -LiteralPath (Join-Path $root 'data\sessions-backup') -Directory).Count -eq 1)

    "`n=== 9. Installer with -ChromePath: the override wins"
    $out = Invoke-Installer -Extra @('-ChromePath', $SystemChrome)
    Check 'exits 0; .env and the restarted gateway use the named Chrome' ($script:installerExit -eq 0 -and (Get-EnvChrome) -eq $SystemChrome -and (Get-RunningChrome) -eq $SystemChrome) "env=$(Get-EnvChrome) running=$(Get-RunningChrome)"
    Check 'Chrome for Testing is not looked at' (($out -join "`n") -match 'Chrome for Testing not installed')
}
catch {
    Write-Host "  FAIL  the test threw: $_" -ForegroundColor Red
    $script:failures++
}
finally {
    $ErrorActionPreference = 'Continue'
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$scripts\Stop-OpenWA.ps1" -OpenWARoot $root *> $null
    Get-ProfileChrome | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}

"`n$(if ($script:failures) { "$script:failures check(s) FAILED" } else { 'All checks passed' })"
exit $script:failures
