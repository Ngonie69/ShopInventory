# Exercises the OpenWA supervisor: scripts\Run-OpenWA.ps1 restarting node after a crash or after it
# stops answering /api/health while still running, and scripts\Stop-OpenWA.ps1 stopping it without
# the supervisor fighting back.
#
# On 14 Oct 2026 the production gateway on 10.10.10.9 died on an unhandled Chrome protocol timeout
# and stayed down for two days: the boot task ran node once, and Task Scheduler's restart settings
# only cover a task that fails to launch. Run-OpenWA.ps1 now supervises node. This runs it against a
# fake dist\main.js in a throwaway copy of the checkout layout under %TEMP%, so nothing here touches
# OpenWA, its sessions or production. Run it under Windows PowerShell 5.1, the servers' shell:
#
#     powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\scripts\Test-OpenWASupervisor.ps1
#
# It ends every node.exe whose command line looks like the gateway's, so it refuses to start while a
# real gateway is running on this machine. Scenario 7 registers and removes a scheduled task
# as the current user; -SkipScheduledTask leaves it out.

param(
    # Defaults to this script's checkout. Resolved below, because Windows PowerShell 5.1 leaves
    # $PSScriptRoot empty in a default value when the script is run with -File.
    [string]$RepoRoot,
    [string]$NodeHome = 'C:\Users\ngoni\.config\herd\bin\nvm\v20.20.2',
    [string]$ChromePath = 'C:\Program Files\Google\Chrome\Application\chrome.exe',
    # The fake gateway's port, written to its .env. Not OpenWA's 2785, so it cannot be mistaken for a real one.
    [int]$HealthPort = 27855,
    [switch]$SkipScheduledTask
)

$ErrorActionPreference = 'Stop'
if (-not $RepoRoot) {
    $RepoRoot = Split-Path -Parent $PSScriptRoot
}

$fake = Join-Path ([IO.Path]::GetTempPath()) 'openwa-supervisor-test'
$scripts = Join-Path $fake 'scripts'
$root = Join-Path $fake 'OpenWA'
$logs = Join-Path $root 'logs'
$supervisorLog = Join-Path $logs 'openwa-supervisor.txt'
$script:failures = 0

# Stands in for OpenWA's dist\main.js, driven by ..\mode.txt:
#   crash3       exit 1 on runs 1-3 (run 2 first leaves a headless Chrome on a session profile), then stay up
#   always-crash exit 1 after 1.5 s, every run
#   slow-crash   exit 1 after 4 s, every run (outlasts a small -HealthyRunSeconds)
#   clean        exit 0 after 1 s
#   hang-once    run 1 leaves a headless Chrome on a session profile, answers /api/health twice, then
#                wedges its event loop and stays alive; later runs answer /api/health and stay up
#   slow-start   answers /api/health only from 15 s after it starts, then stays up
# The modes that serve /api/health use PORT from ..\.env, as OpenWA does.
#   chrome-elsewhere  start a headless Chrome on a profile OUTSIDE the OpenWA folder, then stay up
$fakeMain = @'
// Stand-in for OpenWA's dist\main.js, driven by ..\mode.txt:
//   crash3       exit 1 on runs 1-3 (run 2 first leaves a headless Chrome on a session profile), then stay up
//   always-crash exit 1 after 1.5 s, every run
//   slow-crash   exit 1 after 4 s, every run (outlasts a small -HealthyRunSeconds)
//   clean        exit 0 after 1 s
//   hang-once    run 1 leaves a headless Chrome on a session profile, answers /api/health twice, then
//                wedges its event loop and stays alive; later runs answer /api/health and stay up
//   slow-start   answers /api/health only from 15 s after it starts, then stays up
//   chrome-elsewhere  start a headless Chrome on a profile OUTSIDE the OpenWA folder, then stay up
const fs = require('fs');
const http = require('http');
const path = require('path');
const { spawn } = require('child_process');

const root = path.resolve(__dirname, '..');
const counterFile = path.join(root, 'runs.txt');
const run = (fs.existsSync(counterFile) ? parseInt(fs.readFileSync(counterFile, 'utf8'), 10) : 0) + 1;
fs.writeFileSync(counterFile, String(run));
const mode = fs.readFileSync(path.join(root, 'mode.txt'), 'utf8').trim();

console.log(`fake gateway run ${run} pid ${process.pid} mode ${mode}`);
console.error(`(node:${process.pid}) [DEP0040] DeprecationWarning: The \`punycode\` module is deprecated.`);

function crash(afterMs) {
  setTimeout(() => {
    console.error('ProtocolError: Protocol error (Runtime.callFunctionOn): Runtime.callFunctionOn timed out. (simulated)');
    process.exit(1);
  }, afterMs);
}

function leaveChrome() {
  const profile = path.join(root, 'data', 'sessions', 'session-test');
  const chrome = spawn(process.env.PUPPETEER_EXECUTABLE_PATH,
    ['--headless=new', '--no-first-run', `--user-data-dir=${profile}`, 'about:blank'],
    { detached: true, stdio: 'ignore' });
  chrome.unref();
  console.log(`left Chrome PID ${chrome.pid} on ${profile}`);
}

// Serves /api/health; onAnswer(count) runs after each answer has been sent.
function serveHealth(onAnswer) {
  const port = parseInt(/^PORT=(\d+)/m.exec(fs.readFileSync(path.join(root, '.env'), 'utf8'))[1], 10);
  let answers = 0;
  http.createServer((req, res) => {
    if (req.url !== '/api/health') { res.writeHead(404); res.end(); return; }
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ status: 'ok', timestamp: new Date().toISOString() }), () => {
      answers++;
      console.log(`answered /api/health (${answers})`);
      if (onAnswer) onAnswer(answers);
    });
  }).listen(port, '127.0.0.1', () => console.log(`listening on ${port}`));
}

if (mode === 'hang-once' && run === 1) {
  leaveChrome();
  serveHealth((answers) => {
    if (answers === 2) {
      setTimeout(() => {
        console.log('wedging the event loop');
        // Blocks the main thread for good, as a deadlock would: the process stays alive and the port
        // stays open, but nothing is answered again.
        Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0);
      }, 100);
    }
  });
} else if (mode === 'hang-once') {
  serveHealth();
} else if (mode === 'slow-start') {
  setTimeout(() => serveHealth(), 15000);
} else if (mode === 'clean') {
  setTimeout(() => process.exit(0), 1000);
} else if (mode === 'always-crash') {
  crash(1500);
} else if (mode === 'slow-crash') {
  crash(4000);
} else if (mode === 'chrome-elsewhere') {
  // Only the process tree ties this Chrome to the gateway: its command line names no OpenWA path,
  // as when the command line cannot be read at all.
  const profile = path.join(root, '..', 'elsewhere', 'profile');
  const chrome = spawn(process.env.PUPPETEER_EXECUTABLE_PATH,
    ['--headless=new', '--no-first-run', `--user-data-dir=${profile}`, 'about:blank'],
    { stdio: 'ignore' });
  console.log(`started Chrome PID ${chrome.pid} on ${profile}`);
  setInterval(() => console.log(`alive ${new Date().toISOString()}`), 2000);
} else if (mode === 'crash3' && run <= 3) {
  if (run === 2) {
    leaveChrome();
    crash(4000);
  } else {
    crash(1500);
  }
} else {
  setInterval(() => console.log(`alive ${new Date().toISOString()}`), 2000);
}
'@

"Windows PowerShell $($PSVersionTable.PSVersion) ($($PSVersionTable.PSEdition))"

$running = @(Get-CimInstance Win32_Process -Filter "name = 'node.exe' or name = 'powershell.exe' or name = 'pwsh.exe'" |
    Where-Object { $_.ProcessId -ne $PID -and $_.CommandLine -and ($_.CommandLine -like '* .\dist\main.js*' -or $_.CommandLine -like '*\OpenWA\dist\main*' -or $_.CommandLine -like '*Run-OpenWA.ps1*' -or $_.CommandLine -like '*Test-OpenWASupervisor.ps1*') })
if ($running.Count -gt 0) {
    throw "An OpenWA gateway, supervisor or another run of this test is already running here (PID $(($running | ForEach-Object { $_.ProcessId }) -join ', ')). This test would stop it, or the two runs would stop each other's fake gateway."
}
if (Get-NetTCPConnection -LocalPort $HealthPort -State Listen -ErrorAction SilentlyContinue) {
    throw "Something is already listening on port $HealthPort. Pass a free one with -HealthPort."
}
$healthUrl = "http://127.0.0.1:$HealthPort/api/health"

# Same layout as production: <checkout>\scripts beside <checkout>\OpenWA, so Stop-OpenWA.ps1 runs with its default root.
Remove-Item -LiteralPath $fake -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $scripts, (Join-Path $root 'dist') -Force | Out-Null
Copy-Item (Join-Path $RepoRoot 'scripts\Run-OpenWA.ps1'), (Join-Path $RepoRoot 'scripts\Stop-OpenWA.ps1') $scripts
Set-Content -LiteralPath (Join-Path $root 'dist\main.js') -Value $fakeMain -Encoding Ascii
# The supervisor is never given -Port here: it has to find the port in .env, as it does on the server.
Set-Content -LiteralPath (Join-Path $root '.env') -Value @('# fake gateway', "PORT=$HealthPort") -Encoding Ascii

function Check {
    param([string]$Name, [bool]$Condition, [string]$Detail = '')
    if ($Condition) { Write-Host "  PASS  $Name" -ForegroundColor Green }
    else { Write-Host "  FAIL  $Name $Detail" -ForegroundColor Red; $script:failures++ }
}

function Reset-Fake {
    param([string]$Mode)
    Remove-Item -LiteralPath $logs -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath (Join-Path $root 'runs.txt') -Force -ErrorAction SilentlyContinue
    Set-Content -LiteralPath (Join-Path $root 'mode.txt') -Value $Mode
}

function Get-Runs {
    $path = Join-Path $root 'runs.txt'
    if (Test-Path -LiteralPath $path) { [int](Get-Content -LiteralPath $path -Raw) } else { 0 }
}

function Get-FakeNode {
    @(Get-CimInstance Win32_Process -Filter "name = 'node.exe'" | Where-Object { $_.CommandLine -like '* .\dist\main.js*' })
}

function Get-FakeChrome {
    @(Get-CimInstance Win32_Process -Filter "name = 'chrome.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf($root, [StringComparison]::OrdinalIgnoreCase) -ge 0 })
}

function Get-ElsewhereChrome {
    $elsewhere = Join-Path $fake 'elsewhere'
    @(Get-CimInstance Win32_Process -Filter "name = 'chrome.exe'" | Where-Object { $_.CommandLine -and $_.CommandLine.IndexOf($elsewhere, [StringComparison]::OrdinalIgnoreCase) -ge 0 })
}

function Get-SupervisorLines {
    if (Test-Path -LiteralPath $supervisorLog) { @(Get-Content -LiteralPath $supervisorLog) } else { @() }
}

function Wait-For {
    param([scriptblock]$Condition, [int]$TimeoutSeconds, [string]$What)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (& $Condition) { return $true }
        Start-Sleep -Milliseconds 250
    }
    Write-Host "  timed out after ${TimeoutSeconds}s waiting for: $What" -ForegroundColor Yellow
    return $false
}

function Get-SupervisorArguments {
    param([hashtable]$Extra)
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$scripts\Run-OpenWA.ps1`"",
        '-OpenWARoot', "`"$root`"", '-NodeHome', "`"$NodeHome`"", '-ChromePath', "`"$ChromePath`"", '-SkipBuild')
    foreach ($key in $Extra.Keys) { $arguments += "-$key"; $arguments += [string]$Extra[$key] }
    $arguments
}

function Start-Supervisor {
    param([hashtable]$Extra)
    Start-Process powershell.exe -ArgumentList (Get-SupervisorArguments $Extra) -WindowStyle Hidden -PassThru
}

function Invoke-Stop {
    # -AsOperator runs it the way the installer and an operator do (& / .\); otherwise with -File.
    param([switch]$AsOperator)
    $ErrorActionPreference = 'Continue'
    $watch = [Diagnostics.Stopwatch]::StartNew()
    if ($AsOperator) {
        "    > & '$scripts\Stop-OpenWA.ps1'"
        $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "& '$scripts\Stop-OpenWA.ps1'" 2>&1
    }
    else {
        "    > powershell.exe -File $scripts\Stop-OpenWA.ps1"
        $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$scripts\Stop-OpenWA.ps1" 2>&1
    }
    $watch.Stop()
    $script:lastStopOutput = @($output | ForEach-Object { "$_" })
    $output | ForEach-Object { "    stop> $_" }
    "    Stop-OpenWA.ps1 took $([Math]::Round($watch.Elapsed.TotalSeconds, 1))s"
}

function Show-SupervisorLog {
    Get-SupervisorLines | ForEach-Object { "    log> $_" }
}

# ---------------------------------------------------------------------------------------------------
"`n=== 1. Exits 1 three times (run 2 leaves a Chrome on a session profile), then stays up; stop while running"
Reset-Fake 'crash3'
$p = Start-Supervisor @{ RestartDelaySeconds = 2; MaxRestartDelaySeconds = 6; HealthyRunSeconds = 600 }
$up = Wait-For { (Get-Runs) -ge 4 -and @(Get-FakeNode).Count -eq 1 } 90 'run 4 up'
Start-Sleep -Seconds 4
$nodes = @(Get-FakeNode)
Show-SupervisorLog
$lines = Get-SupervisorLines
Check 'run 4 started and is still running 4s later' ($up -and $nodes.Count -eq 1 -and (Get-Runs) -eq 4)
Check 'backoff 2s, 4s, then capped at 6s' (
    ($lines -match 'Restarting in 2s').Count -eq 1 -and ($lines -match 'Restarting in 4s').Count -eq 1 -and ($lines -match 'Restarting in 6s').Count -eq 1)
Check 'each restart logged the exit code 1' (($lines -match 'exited with code 1 after').Count -eq 3)
Check 'orphaned Chrome on the session profile was ended before the restart' (@($lines -match 'Ending \d+ Chrome').Count -eq 1 -and @($lines -match 'still running 15s later').Count -eq 0 -and @(Get-FakeChrome).Count -eq 0)
$errLogs = @(Get-ChildItem -LiteralPath $logs -Filter 'openwa-*.err.log')
$outLogs = @(Get-ChildItem -LiteralPath $logs -Filter 'openwa-*.out.log')
"    logs: $(($errLogs | ForEach-Object Name) -join ', ')"
Check 'four runs, four separate out/err logs' ($errLogs.Count -eq 4 -and $outLogs.Count -eq 4)
Check 'stderr (punycode warning) went to the err log without ending the supervisor' (
    @($errLogs | Where-Object { (Get-Content -LiteralPath $_.FullName -Raw) -match 'punycode' }).Count -eq 4)
Check 'crash reason is in the err logs of runs 1-3' (@($errLogs | Where-Object { (Get-Content -LiteralPath $_.FullName -Raw) -match 'callFunctionOn timed out' }).Count -eq 3)
Invoke-Stop
$exited = $p.WaitForExit(15000)
Check 'supervisor exited after the stop, with code 0' ($exited -and $p.ExitCode -eq 0) "exited=$exited code=$($p.ExitCode)"
Start-Sleep -Seconds 8
Check 'no node running and no restart 8s after the stop' (@(Get-FakeNode).Count -eq 0 -and (Get-Runs) -eq 4) "runs=$(Get-Runs)"
Check 'supervisor logged the stop' (@(@(Get-SupervisorLines) -match 'because a stop was requested. Not restarting').Count -eq 1)
Show-SupervisorLog | Select-Object -Last 2

# ---------------------------------------------------------------------------------------------------
"`n=== 2. Stop during the backoff"
Reset-Fake 'always-crash'
$p = Start-Supervisor @{ RestartDelaySeconds = 30 }
$waiting = Wait-For { @(Get-SupervisorLines) -match 'Restarting in 30s' } 30 'first backoff'
Start-Sleep -Seconds 2
Check 'node crashed once and the supervisor is waiting 30s to restart it' ($waiting -and @(Get-FakeNode).Count -eq 0 -and -not $p.HasExited)
Invoke-Stop -AsOperator
$exited = $p.WaitForExit(5000)
Check 'supervisor exited within 5s of the stop, with code 0' ($exited -and $p.ExitCode -eq 0) "exited=$exited code=$($p.ExitCode)"
Start-Sleep -Seconds 3
Check 'node was not restarted' ((Get-Runs) -eq 1 -and @(Get-FakeNode).Count -eq 0) "runs=$(Get-Runs)"
Show-SupervisorLog

# ---------------------------------------------------------------------------------------------------
"`n=== 3. A run that outlasted -HealthyRunSeconds resets the backoff"
Reset-Fake 'slow-crash'
$p = Start-Supervisor @{ RestartDelaySeconds = 2; MaxRestartDelaySeconds = 60; HealthyRunSeconds = 3 }
$three = Wait-For { (@(Get-SupervisorLines) -match 'Restarting in').Count -ge 3 } 60 'three restarts'
Invoke-Stop
$exited = $p.WaitForExit(15000)
Show-SupervisorLog
$restarts = @(@(Get-SupervisorLines) -match 'Restarting in')
Check 'every restart after a 4s run waited 2s, not 4s/8s' ($three -and @($restarts | Where-Object { $_ -notmatch 'Restarting in 2s' }).Count -eq 0)
Check 'supervisor exited with code 0' ($exited -and $p.ExitCode -eq 0)

# ---------------------------------------------------------------------------------------------------
"`n=== 4. Exit code 0 is not restarted"
Reset-Fake 'clean'
$p = Start-Supervisor @{ RestartDelaySeconds = 1 }
$exited = $p.WaitForExit(20000)
Start-Sleep -Seconds 3
Show-SupervisorLog
Check 'supervisor exited by itself with code 0 after one run' ($exited -and $p.ExitCode -eq 0 -and (Get-Runs) -eq 1)

# ---------------------------------------------------------------------------------------------------
"`n=== 5. node answers /api/health, then wedges and stays alive: ended and restarted"
# Checks every 1s, 3 failures, 1s timeout: the server's 30s x 4 scaled down.
$watchdog = @{ RestartDelaySeconds = 2; HealthCheckSeconds = 1; HealthCheckFailures = 3; HealthCheckTimeoutSeconds = 1 }
Reset-Fake 'hang-once'
$p = Start-Supervisor $watchdog
$failing = Wait-For { @(Get-SupervisorLines) -match 'Health check 1 of 3 failed' } 30 'first failed health check'
$wedged = @(Get-FakeNode)
Check 'run 1 answered, then failed a check while its node was still running, with a Chrome on a session profile' (
    $failing -and $wedged.Count -eq 1 -and (Get-Runs) -eq 1 -and @(Get-FakeChrome).Count -gt 0 -and
    @(@(Get-SupervisorLines) -match 'node answered .* \(run 1\)').Count -eq 1) "nodes=$($wedged.Count) runs=$(Get-Runs) chrome=$(@(Get-FakeChrome).Count)"
$restarted = Wait-For { @(@(Get-SupervisorLines) -match 'node answered .* \(run 2\)').Count -eq 1 } 60 'run 2 to answer'
Start-Sleep -Seconds 4
Show-SupervisorLog
$lines = Get-SupervisorLines
$nodes = @(Get-FakeNode)
Check 'supervisor found the port in OpenWA\.env' (@($lines -match [regex]::Escape("Once node answers $healthUrl")).Count -eq 1)
Check 'three failed checks in a row, then node ended and restarted after the 2s backoff' (
    @($lines -match 'Health check 3 of 3 failed').Count -eq 1 -and @($lines -match 'failed 3 health checks in a row. Ending it').Count -eq 1 -and
    @($lines -match "ended after \d+s for not answering $([regex]::Escape($healthUrl)). Restarting in 2s").Count -eq 1)
Check 'the wedged node is gone and exactly one new node is running' ($restarted -and $nodes.Count -eq 1 -and $wedged.Count -eq 1 -and $nodes[0].ProcessId -ne $wedged[0].ProcessId -and (Get-Runs) -eq 2) "runs=$(Get-Runs)"
Check 'the Chrome run 1 left on the session profile was ended' (@($lines -match 'Ending \d+ Chrome').Count -ge 1 -and @(Get-FakeChrome).Count -eq 0)
Check 'run 2 has failed no check since it answered' (@($lines -match 'Health check \d+ of 3 failed').Count -eq 3)
$probe = try { (Invoke-WebRequest -Uri $healthUrl -UseBasicParsing -TimeoutSec 5).StatusCode } catch { $_.Exception.Message }
Check 'run 2 answers /api/health from here too' ($probe -eq 200) "probe=$probe"
Invoke-Stop
$exited = $p.WaitForExit(15000)
Check 'supervisor exited after the stop, with code 0' ($exited -and $p.ExitCode -eq 0) "exited=$exited code=$($p.ExitCode)"

# ---------------------------------------------------------------------------------------------------
"`n=== 6. A gateway that takes 15s to answer is not ended while it starts"
# With the same 1s x 3 checks, a watchdog that counted before the first answer would end it at about 3s.
Reset-Fake 'slow-start'
$p = Start-Supervisor $watchdog
$answered = Wait-For { @(Get-SupervisorLines) -match 'node answered' } 45 'first answer'
Start-Sleep -Seconds 4
Show-SupervisorLog
$lines = Get-SupervisorLines
$after = if ("$($lines -match 'node answered')" -match 'answered \S+ (\d+)s after') { [int]$Matches[1] } else { -1 }
Check 'node answered 15s or more after it started' ($answered -and $after -ge 15) "after=${after}s"
Check 'one run, still running, no failed check and nothing ended' (
    (Get-Runs) -eq 1 -and @(Get-FakeNode).Count -eq 1 -and @($lines -match 'Health check \d+ of|Ending it|Restarting in').Count -eq 0) "runs=$(Get-Runs)"
Invoke-Stop
$exited = $p.WaitForExit(15000)
Check 'supervisor exited after the stop, with code 0' ($exited -and $p.ExitCode -eq 0) "exited=$exited code=$($p.ExitCode)"

# ---------------------------------------------------------------------------------------------------
"`n=== 7. Installer restart path under Task Scheduler (Stop-OpenWAAndWait, then Start-ScheduledTask)"
$taskName = 'ShopInventory-OpenWA-SupervisorTest'
# The installer's own two functions, lifted verbatim so the test runs what it runs.
$installerAst = [Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'scripts\Install-OpenWAProduction.ps1'), [ref]$null, [ref]$null)
$functions = $installerAst.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -in 'Test-OpenWAListening', 'Stop-OpenWAAndWait' }, $true) |
    ForEach-Object { $_.Extent.Text }
Set-Content -LiteralPath (Join-Path $scripts 'Restart-LikeInstaller.ps1') -Value (@(
        'param([string]$TaskName)', '$ErrorActionPreference = ''Stop''') + $functions + @(
        'Stop-OpenWAAndWait -Port 2785 -TaskName $TaskName',
        'Write-Host "  Stop-OpenWAAndWait returned; task state $((Get-ScheduledTask -TaskName $TaskName).State)"',
        'Start-ScheduledTask -TaskName $TaskName'))

Reset-Fake 'crash3'
$registered = $false
if ($SkipScheduledTask) {
    Write-Host "  SKIPPED: -SkipScheduledTask" -ForegroundColor Yellow
}
else { try {
    $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument ((@('-WindowStyle', 'Hidden') + (Get-SupervisorArguments @{ RestartDelaySeconds = 1 })) -join ' ')
    $principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited
    Register-ScheduledTask -TaskName $taskName -Action $action -Principal $principal -Settings (New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Hours 1)) -Force | Out-Null
    $registered = $true
}
catch {
    Write-Host "  SKIPPED: could not register a test task as this user: $($_.Exception.Message)" -ForegroundColor Yellow
} }

if ($registered) {
    try {
        Start-ScheduledTask -TaskName $taskName
        $up = Wait-For { (Get-Runs) -ge 4 -and @(Get-FakeNode).Count -eq 1 } 60 'run 4 up under the task'
        Check 'under the task: three crashes restarted, run 4 up, task Running' ($up -and (Get-ScheduledTask -TaskName $taskName).State -eq 'Running')
        $before = @(Get-FakeNode)[0].ProcessId
        $watch = [Diagnostics.Stopwatch]::StartNew()
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $scripts 'Restart-LikeInstaller.ps1') -TaskName $taskName 2>&1 | ForEach-Object { "    installer> $_" }
        "    installer restart took $([Math]::Round($watch.Elapsed.TotalSeconds, 1))s"
        $restarted = Wait-For { (Get-Runs) -ge 5 -and @(Get-FakeNode).Count -eq 1 } 30 'run 5 up after the installer restart'
        Start-Sleep -Seconds 3
        $after = @(Get-FakeNode)
        Check 'installer path: old node stopped, task restarted, exactly one new node running' (
            $restarted -and $after.Count -eq 1 -and $after[0].ProcessId -ne $before -and (Get-Runs) -eq 5 -and (Get-ScheduledTask -TaskName $taskName).State -eq 'Running')
        Show-SupervisorLog | Select-Object -Last 3
        Invoke-Stop
        $stopped = Wait-For { (Get-ScheduledTask -TaskName $taskName).State -ne 'Running' } 15 'task to finish'
        Start-Sleep -Seconds 3
        $info = Get-ScheduledTaskInfo -TaskName $taskName
        Check 'stop ends the task with result 0 and nothing restarts' ($stopped -and $info.LastTaskResult -eq 0 -and @(Get-FakeNode).Count -eq 0 -and (Get-Runs) -eq 5) "result=$($info.LastTaskResult) runs=$(Get-Runs)"
    }
    finally {
        Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    }
}

# ---------------------------------------------------------------------------------------------------
"`n=== 8. Stop ends the gateway's Chrome by process tree, where its command line names no OpenWA path"
Reset-Fake 'chrome-elsewhere'
$p = Start-Supervisor @{ RestartDelaySeconds = 2 }
$up = Wait-For { @(Get-FakeNode).Count -eq 1 -and @(Get-ElsewhereChrome).Count -gt 0 } 30 'node and its Chrome up'
Start-Sleep -Seconds 2
$chromeBefore = @(Get-ElsewhereChrome).Count
Check "node is up with its Chrome ($chromeBefore processes) on a profile outside the OpenWA folder" ($up -and $chromeBefore -gt 0)
Invoke-Stop
$exited = $p.WaitForExit(15000)
Start-Sleep -Seconds 2
Check 'every one of those Chrome processes was ended' (@(Get-ElsewhereChrome).Count -eq 0) "left=$(@(Get-ElsewhereChrome).Count)"
Check 'the stop reported ending them' (@($script:lastStopOutput -match 'Ended \d+ Chrome process').Count -eq 1)
Check 'node gone, supervisor exited 0, no restart' ($exited -and $p.ExitCode -eq 0 -and @(Get-FakeNode).Count -eq 0 -and (Get-Runs) -eq 1)

# ---------------------------------------------------------------------------------------------------
"`n=== 9. Stop ends an orphaned Chrome on a session profile when no gateway is running"
Reset-Fake 'clean'
$orphanProfile = Join-Path $root 'data\sessions\session-orphan'
Start-Process $ChromePath -ArgumentList '--headless=new', '--no-first-run', "--user-data-dir=$orphanProfile", 'about:blank' | Out-Null
$orphaned = Wait-For { @(Get-FakeChrome).Count -gt 0 } 20 'orphan Chrome up'
Start-Sleep -Seconds 2
Check 'an orphaned Chrome holds a session profile, and no node or supervisor runs' ($orphaned -and @(Get-FakeNode).Count -eq 0)
Invoke-Stop
Start-Sleep -Seconds 2
Check 'the stop ended it' (@(Get-FakeChrome).Count -eq 0 -and @($script:lastStopOutput -match 'Ended \d+ Chrome process').Count -eq 1) "left=$(@(Get-FakeChrome).Count)"

# ---------------------------------------------------------------------------------------------------
"`n=== 10. Sorting processes, including ones whose command line cannot be read (made-up snapshots)"
$stopAst = [Management.Automation.Language.Parser]::ParseFile((Join-Path $scripts 'Stop-OpenWA.ps1'), [ref]$null, [ref]$null)
. ([scriptblock]::Create($stopAst.Find({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Select-OpenWAProcesses' }, $true).Extent.Text))

function New-Proc {
    param([int]$Id, [string]$Name, [int]$Parent, [string]$CommandLine)
    [pscustomobject]@{ ProcessId = [uint32]$Id; Name = $Name; ParentProcessId = [uint32]$Parent; CommandLine = $(if ($CommandLine) { $CommandLine } else { $null }) }
}
$prodRoot = 'C:\ShopInventory\OpenWA'
$userChrome = New-Proc 200 'chrome.exe' 50 '"chrome.exe" --user-data-dir=C:\Users\someone\AppData\Local\Google\Chrome\User Data'

# An unelevated console looking at the SYSTEM boot task's gateway: nothing readable.
$asSeenUnelevated = @(
    (New-Proc 100 'node.exe' 90 ''), (New-Proc 101 'chrome.exe' 100 ''), (New-Proc 102 'chrome.exe' 101 ''),
    (New-Proc 90 'powershell.exe' 4 ''), $userChrome)
$sorted = Select-OpenWAProcesses -Snapshot $asSeenUnelevated -Root $prodRoot -ExcludePid 1
Check 'unelevated view of a SYSTEM gateway: node and Chrome reported as hidden, none taken as OpenWA' (
    $sorted.Node.Count -eq 0 -and $sorted.Chrome.Count -eq 0 -and $sorted.Hidden.Count -eq 3 -and
    @($sorted.Hidden | Where-Object { $_.ProcessId -eq 200 }).Count -eq 0)

# Elevated: a browser whose command line is hidden is still found through node, an orphan by its profile.
$asSeenElevated = @(
    (New-Proc 100 'node.exe' 90 '"C:\node\node.exe" .\dist\main.js'),
    (New-Proc 101 'chrome.exe' 100 ''),
    (New-Proc 102 'chrome.exe' 101 '"chrome.exe" --type=renderer'),
    (New-Proc 300 'chrome.exe' 999 '"chrome.exe" --user-data-dir=C:\ShopInventory\OpenWA\data\sessions\session-fiscal-alerts about:blank'),
    (New-Proc 301 'chrome.exe' 300 '"chrome.exe" --type=gpu-process --user-data-dir=C:/ShopInventory/OpenWA/data/sessions/session-fiscal-alerts'),
    (New-Proc 90 'powershell.exe' 4 'powershell.exe -NoProfile -File "C:\ShopInventory\scripts\Run-OpenWA.ps1" -OpenWARoot "C:\ShopInventory\OpenWA"'),
    $userChrome)
$sorted = Select-OpenWAProcesses -Snapshot $asSeenElevated -Root $prodRoot -ExcludePid 1
$chromeIds = @($sorted.Chrome | ForEach-Object { [int]$_.ProcessId } | Sort-Object)
Check 'elevated: node, supervisor, tree Chrome (hidden command line too) and orphan Chrome found; your own Chrome left alone' (
    $sorted.Node.Count -eq 1 -and $sorted.Supervisor.Count -eq 1 -and ($chromeIds -join ',') -eq '101,102,300,301' -and $sorted.Hidden.Count -eq 0) "chrome=$($chromeIds -join ',')"

$afterNode = @($asSeenElevated | Where-Object { $_.ProcessId -ne 100 })
$sorted = Select-OpenWAProcesses -Snapshot $afterNode -Root $prodRoot -EndedNodePids @(100) -ExcludePid 90
$chromeIds = @($sorted.Chrome | ForEach-Object { [int]$_.ProcessId } | Sort-Object)
Check 'after node is ended its Chrome is still found by the ended PID; the excluded PID is not a supervisor' (
    $sorted.Node.Count -eq 0 -and $sorted.Supervisor.Count -eq 0 -and ($chromeIds -join ',') -eq '101,102,300,301') "chrome=$($chromeIds -join ',')"

# ---------------------------------------------------------------------------------------------------
"`n=== 11. Unelevated with the gateway's processes hidden: Stop-OpenWA.ps1 refuses and changes nothing"
# The real script, with Get-CimInstance shadowed by a function returning what an unelevated console sees
# of a SYSTEM gateway. This laptop has no second account to run a real one under.
Reset-Fake 'clean'
$mock = @'
function global:Get-CimInstance {
    foreach ($p in @(@{ i = 7100; n = 'node.exe'; pp = 7000 }, @{ i = 7101; n = 'chrome.exe'; pp = 7100 }, @{ i = 7102; n = 'chrome.exe'; pp = 7101 })) {
        [pscustomobject]@{ ProcessId = [uint32]$p.i; Name = $p.n; ParentProcessId = [uint32]$p.pp; CommandLine = $null }
    }
}
'@
$isElevated = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($isElevated) {
    Write-Host '  SKIPPED: this console is elevated, so the refusal does not apply' -ForegroundColor Yellow
}
else {
    $ErrorActionPreference = 'Continue'
    $refusal = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$mock; & '$scripts\Stop-OpenWA.ps1'" 2>&1 | ForEach-Object { "$_" })
    $refusalCode = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    $refusal | Select-Object -First 3 | ForEach-Object { "    stop> $_" }
    Check 'it refuses, naming the hidden PIDs and asking for elevation' (
        ($refusal -join ' ') -match 'not elevated' -and ($refusal -join ' ') -match '7100 \(node\.exe\)' -and ($refusal -join ' ') -match 'elevated PowerShell' -and $refusalCode -ne 0)
    Check 'no stop marker was written, so a later crash is still restarted' (-not (Test-Path -LiteralPath (Join-Path $logs 'stop-requested')))

    # A Chrome of this account caught while starting has no readable command line either. It must not
    # be taken for the SYSTEM gateway: seen when a test started a browser just before a stop.
    $ownMock = @'
function global:Get-CimInstance {
    [pscustomobject]@{ ProcessId = [uint32]7201; Name = 'chrome.exe'; ParentProcessId = [uint32]7200; CommandLine = $null }
}
function global:Invoke-CimMethod {
    $me = [Security.Principal.WindowsIdentity]::GetCurrent().Name.Split('\')
    [pscustomobject]@{ ReturnValue = 0; Domain = $me[0]; User = $me[1] }
}
'@
    $ErrorActionPreference = 'Continue'
    $ownResult = @(& powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "$ownMock; & '$scripts\Stop-OpenWA.ps1'" 2>&1 | ForEach-Object { "$_" })
    $ErrorActionPreference = 'Stop'
    $ownResult | Select-Object -First 2 | ForEach-Object { "    stop> $_" }
    Check "an unreadable Chrome of this account is not a reason to refuse" (($ownResult -join ' ') -notmatch 'not elevated' -and ($ownResult -join ' ') -match 'OpenWA is not running')
}

Get-FakeNode | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
@(Get-FakeChrome) + @(Get-ElsewhereChrome) | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

"`n$(if ($script:failures) { "$script:failures check(s) FAILED" } else { 'All checks passed' })"
exit $script:failures
