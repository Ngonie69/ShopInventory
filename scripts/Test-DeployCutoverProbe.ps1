# Exercises the probe Update-Production.ps1 runs across a cutover.
#
# A cutover takes the public port off the live IIS slot and gives it to the new one, and nobody could
# say what that costs. It is known to cost something: on 14 September 2026 three merges deployed
# between 16:00 and 16:40 CAT, KEFSHOP's till lost its notification connection at the end of each,
# and a sale posted at 16:41 got a 502 while the API went on writing it. That is why a merge waits
# for the evening. The probe measures it, so the question can be settled with numbers.
#
# It measures; it changes nothing. There are no tests here for how the binding moves, because this
# change does not touch that.
#
# The two things worth proving are that the arithmetic over the samples is right, and that the part
# producing those samples really notices a port that stops answering - a probe that quietly reported
# zero would be worse than no probe at all, because the number is going to be believed. The functions
# are lifted out of the real script by AST, as Test-DeploySlotSeeding.ps1 does. Nothing here touches
# IIS or reaches production.

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

foreach ($name in 'Get-LongestOutage', 'Start-PublicPortProbe', 'Stop-PublicPortProbe') {
    $definition = $ast.Find({
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
        }, $true)

    if (-not $definition) {
        throw "Could not extract $name from $ScriptPath - it was renamed or removed."
    }

    . ([scriptblock]::Create($definition.Extent.Text))
}

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

function Assert {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

# Samples at a fixed cadence from an arbitrary start, so the arithmetic is readable: '1101' is a
# probe that answered, one that did not, then two that did.
function New-Samples {
    param([string]$Pattern, [int]$IntervalMilliseconds = 40)

    $start = [DateTime]::new(2026, 9, 16, 19, 30, 0, [DateTimeKind]::Utc)
    $index = -1

    return @($Pattern.ToCharArray() | ForEach-Object {
            $index++
            [pscustomobject]@{ At = $start.AddMilliseconds($index * $IntervalMilliseconds); Ok = ($_ -eq '1') }
        })
}

Write-Host ""
Write-Host "What the cutover probe reports" -ForegroundColor Cyan

Check "A cutover nobody noticed reports no outage at all" {
    $outage = Get-LongestOutage -Samples (New-Samples '11111111')

    Assert ($outage.Probes -eq 8) "Counted $($outage.Probes) probes."
    Assert ($outage.Failures -eq 0) "Counted $($outage.Failures) failures."
    Assert ($outage.LongestOutageMs -eq 0) "Reported $($outage.LongestOutageMs)ms."
}

Check "An outage is measured from the last answer to the next one" {
    # One missed probe between two that answered, 40ms apart: at most 80ms unanswered.
    $outage = Get-LongestOutage -Samples (New-Samples '1101')

    Assert ($outage.Failures -eq 1) "Counted $($outage.Failures) failures."
    Assert ($outage.LongestOutageMs -eq 80) "Reported $($outage.LongestOutageMs)ms, expected 80."
}

Check "The longest of several outages is the one reported" {
    # Two misses in a row span three 40ms gaps from the last answer to the next; one miss spans two.
    $outage = Get-LongestOutage -Samples (New-Samples '1100101')

    Assert ($outage.Failures -eq 3) "Counted $($outage.Failures) failures."
    Assert ($outage.LongestOutageMs -eq 120) "Reported $($outage.LongestOutageMs)ms, expected 120."
}

Check "A port still down when the probe stops is measured to the last sample" {
    $outage = Get-LongestOutage -Samples (New-Samples '1000')

    Assert ($outage.Failures -eq 3) "Counted $($outage.Failures) failures."
    Assert ($outage.LongestOutageMs -eq 120) "Reported $($outage.LongestOutageMs)ms, expected 120."
}

Check "A port already down when the probe starts is measured from the first sample" {
    $outage = Get-LongestOutage -Samples (New-Samples '0001')

    Assert ($outage.Failures -eq 3) "Counted $($outage.Failures) failures."
    Assert ($outage.LongestOutageMs -eq 120) "Reported $($outage.LongestOutageMs)ms, expected 120."
}

Check "The widest gap between two probes is reported, because it bounds what could have been missed" {
    # Samples 40ms apart except for one 200ms stall: an outage inside that stall would not have been
    # seen, and the number says so rather than reporting a clean cutover.
    $samples = @(New-Samples '111')
    $samples += [pscustomobject]@{ At = $samples[2].At.AddMilliseconds(200); Ok = $true }

    $outage = Get-LongestOutage -Samples $samples

    Assert ($outage.Failures -eq 0) "Counted $($outage.Failures) failures."
    Assert ($outage.MaxSampleGapMs -eq 200) "Reported a widest gap of $($outage.MaxSampleGapMs)ms, expected 200."
}

Check "Samples that arrive out of order are put back in order first" {
    $samples = New-Samples '1101'
    $shuffled = @($samples[3], $samples[0], $samples[2], $samples[1])

    $outage = Get-LongestOutage -Samples $shuffled

    Assert ($outage.LongestOutageMs -eq 80) "Reported $($outage.LongestOutageMs)ms, expected 80."
}

Write-Host ""
Write-Host "The probe against a real socket" -ForegroundColor Cyan

# Get-LongestOutage above is arithmetic on samples handed to it. These two prove the part that
# produces those samples: a runspace polling a port that really does stop answering and start again.
function Get-FreeLoopbackPort {
    $probe = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, 0)
    $probe.Start()
    $port = $probe.LocalEndpoint.Port
    $probe.Stop()
    return $port
}

# Enough of an HTTP server to answer 200 and hang up. HttpListener would want a URL reservation,
# which needs an administrator; a TCP socket on loopback needs nobody.
#
# Two ways to make it stop serving, because a cutover has been seen to do both. Set Refusing and it
# takes connections and drops them without answering. Set StatusLine to '404 Not Found' and it keeps
# answering, with the status HTTP.sys itself returns when the port is still bound but no site owns
# the prefix - which is what the 14 September 2026 till log recorded, and the case a probe is most
# likely to get wrong, because a 404 arrives looking exactly like a healthy reply.
#
# FirstResponseDelayMilliseconds holds back the reply to the first connection only, which stands in
# for a first request that is slow for reasons that have nothing to do with the port. Served counts
# the connections it has taken.
function Start-TinyHttpServer {
    param(
        [int]$Port,
        [int]$FirstResponseDelayMilliseconds = 0
    )

    $shared = [hashtable]::Synchronized(@{ Stop = $false; Listening = $false; Refusing = $false; StatusLine = '200 OK'; Served = 0 })

    $runspace = [runspacefactory]::CreateRunspace()
    $runspace.Open()
    $runspace.SessionStateProxy.SetVariable('shared', $shared)
    $runspace.SessionStateProxy.SetVariable('serverPort', $Port)
    $runspace.SessionStateProxy.SetVariable('firstResponseDelay', $FirstResponseDelayMilliseconds)

    $shell = [powershell]::Create()
    $shell.Runspace = $runspace
    [void]$shell.AddScript({
            $listener = New-Object System.Net.Sockets.TcpListener([System.Net.IPAddress]::Loopback, $serverPort)
            $listener.Start()
            $shared.Listening = $true

            try {
                while (-not $shared.Stop) {
                    if (-not $listener.Pending()) {
                        Start-Sleep -Milliseconds 5
                        continue
                    }

                    $client = $listener.AcceptTcpClient()
                    $shared.Served++
                    try {
                        if ($shared.Served -eq 1 -and $firstResponseDelay -gt 0) { Start-Sleep -Milliseconds $firstResponseDelay }

                        # Taken and dropped, with nothing written back.
                        if ($shared.Refusing) { continue }

                        $stream = $client.GetStream()
                        # Without this a client that connects and then abandons the request leaves the
                        # read blocking for as long as the TCP stack takes to give up on it.
                        $stream.ReadTimeout = 500

                        $buffer = New-Object byte[] 1024
                        [void]$stream.Read($buffer, 0, $buffer.Length)

                        $response = "HTTP/1.1 $($shared.StatusLine)`r`nContent-Type: text/plain`r`nContent-Length: 2`r`nConnection: close`r`n`r`nok"
                        $bytes = [System.Text.Encoding]::ASCII.GetBytes($response)
                        $stream.Write($bytes, 0, $bytes.Length)
                        $stream.Flush()
                    }
                    catch {
                        # A probe that gave up mid-request is not the server's problem.
                    }
                    finally {
                        $client.Close()
                    }
                }
            }
            finally {
                $listener.Stop()
                $shared.Listening = $false
            }
        })

    $server = [pscustomobject]@{ Shared = $shared; Shell = $shell; Runspace = $runspace; Handle = $null }
    $server.Handle = $shell.BeginInvoke()

    $deadline = (Get-Date).AddSeconds(5)
    while (-not $shared.Listening -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 10 }
    if (-not $shared.Listening) { throw "The stand-in server never started listening on $Port." }

    return $server
}

function Stop-TinyHttpServer {
    param([object]$Server)

    if ($null -eq $Server) { return }

    $Server.Shared.Stop = $true
    try { [void]$Server.Shell.EndInvoke($Server.Handle) } catch { }
    $Server.Shell.Dispose()
    $Server.Runspace.Dispose()
}

Check "A port that answers throughout is reported as no outage at all" {
    # The false positive that matters: if the probe cried wolf, every deployment would look like it
    # dropped requests and the number would stop meaning anything.
    $port = Get-FreeLoopbackPort
    $server = Start-TinyHttpServer -Port $port

    try {
        $probe = Start-PublicPortProbe -Url "http://127.0.0.1:$port/health/live" -IntervalMilliseconds 40
        Assert ($null -ne $probe) "The probe did not start."
        Start-Sleep -Milliseconds 600
        $outage = Stop-PublicPortProbe -Probe $probe
    }
    finally {
        Stop-TinyHttpServer -Server $server
    }

    Assert ($null -ne $outage) "The probe collected no samples."
    Assert ($outage.Probes -ge 5) "Only $($outage.Probes) probes in 600ms."
    Assert ($outage.Failures -eq 0) "$($outage.Failures) of $($outage.Probes) probes failed against a port that never stopped."
    Assert ($outage.LongestOutageMs -eq 0) "Reported $($outage.LongestOutageMs)ms against a port that never stopped."
}

Check "A port that stops answering and comes back is measured" {
    $port = Get-FreeLoopbackPort
    $server = Start-TinyHttpServer -Port $port

    try {
        $probe = Start-PublicPortProbe -Url "http://127.0.0.1:$port/health/live" -IntervalMilliseconds 40
        Assert ($null -ne $probe) "The probe did not start."

        Start-Sleep -Milliseconds 300
        $server.Shared.Refusing = $true     # the cutover
        Start-Sleep -Milliseconds 600
        $server.Shared.Refusing = $false    # and the far side of it
        Start-Sleep -Milliseconds 400

        $outage = Stop-PublicPortProbe -Probe $probe
    }
    finally {
        Stop-TinyHttpServer -Server $server
    }

    Assert ($null -ne $outage) "The probe collected no samples."
    Assert ($outage.Probes -ge 8) "Only $($outage.Probes) probes across 1.3 seconds: the probe was barely running."
    # The one that matters. A probe that starts late sees only the port coming back and reports a
    # clean cutover, which is the worst thing this measurement could do - it would say a deploy
    # dropped nothing when it dropped everything. Start-PublicPortProbe waits for its first sample
    # before returning, and these two hold it to that.
    Assert ($outage.Failures -ge 1) "The probe saw no failure across a 600ms gap: it was not watching."
    Assert ($outage.LongestOutageMs -ge 300) "Reported $($outage.LongestOutageMs)ms for a gap of about 600ms."
    Assert ($outage.LongestOutageMs -le 3000) "Reported $($outage.LongestOutageMs)ms for a gap of about 600ms."
}

Check "A 404 from a port that is still bound counts as an outage, not as an answer" {
    # The failure this whole measurement exists for. Through the 14 September 2026 gap the port stayed
    # bound and HTTP.sys answered 404 - the till log records exactly that, 404 then 502. A 404 arrives
    # looking like a perfectly good reply, so a probe that only asks "did the request complete" would
    # report a spotless cutover through the outage that cost a customer their receipt.
    $port = Get-FreeLoopbackPort
    $server = Start-TinyHttpServer -Port $port

    try {
        $probe = Start-PublicPortProbe -Url "http://127.0.0.1:$port/health/live" -IntervalMilliseconds 40
        Assert ($null -ne $probe) "The probe did not start."

        Start-Sleep -Milliseconds 300
        $server.Shared.StatusLine = '404 Not Found'
        Start-Sleep -Milliseconds 600
        $server.Shared.StatusLine = '200 OK'
        Start-Sleep -Milliseconds 400

        $outage = Stop-PublicPortProbe -Probe $probe
    }
    finally {
        Stop-TinyHttpServer -Server $server
    }

    Assert ($null -ne $outage) "The probe collected no samples."
    Assert ($outage.Failures -ge 1) "The probe counted 404s as answers, so an outage like 14 September would read as a clean cutover."
    Assert ($outage.LongestOutageMs -ge 300) "Reported $($outage.LongestOutageMs)ms for a 600ms run of 404s."
    Assert ($outage.LongestOutageMs -le 3000) "Reported $($outage.LongestOutageMs)ms for a 600ms run of 404s."
}

Check "A slow first request is the probe's own warm-up, not a sample" {
    # The first request a runspace makes pays for JIT and the networking stack's first use. Measured on
    # loaded machines: 3.6 seconds in a fresh pwsh, ~200ms in Windows PowerShell 5.1 set up the way a
    # deploy leaves it, and 4-23ms for every request after it. Counted against the probe's 300ms timeout it is a failure against a
    # healthy port, and it would be reported as an outage that ended before the switch began. How
    # slow that request is depends on the machine, so a server that holds back its first reply for
    # well past 300ms stands in for it here. Only a probe that warms up first and does not count
    # the warm-up gets through this with no failures.
    $port = Get-FreeLoopbackPort
    $server = Start-TinyHttpServer -Port $port -FirstResponseDelayMilliseconds 1500

    try {
        $probe = Start-PublicPortProbe -Url "http://127.0.0.1:$port/health/live" -IntervalMilliseconds 40
        Assert ($null -ne $probe) "The probe did not start."

        # Read before anything else can move them: what the probe had done when it handed back control.
        $servedAtReturn = $server.Shared.Served
        $samplesAtReturn = $probe.Shared.Samples.Count

        Start-Sleep -Milliseconds 400
        $firstSample = $probe.Shared.Samples[0]
        $outage = Stop-PublicPortProbe -Probe $probe
    }
    finally {
        Stop-TinyHttpServer -Server $server
    }

    # Still returns only once a real sample exists - the warm-up is not allowed to stand in for one.
    Assert ($samplesAtReturn -ge 1) "The probe returned before its first real sample, so it could miss the start of a cutover."
    Assert ($servedAtReturn -ge 2) "The server had taken $servedAtReturn connection(s) when the probe returned: there was no warm-up request ahead of the first sample."
    Assert ($firstSample.Ok) "The first sample failed against a port that was only slow to answer once: the warm-up was counted, or never happened."
    Assert ($null -ne $outage) "The probe collected no samples."
    Assert ($outage.Failures -eq 0) "$($outage.Failures) of $($outage.Probes) probes failed against a port that never stopped."
    Assert ($outage.LongestOutageMs -eq 0) "Reported a phantom outage of $($outage.LongestOutageMs)ms before any switch."
}

Write-Host ""
Write-Host "The cutover itself is not touched" -ForegroundColor Cyan

Check "The public binding still moves the way it always has" {
    # This change measures the cutover; it does not alter it. An attempt at altering it on
    # 16 September 2026 took the Web down for four minutes, so the separation is the point, and
    # anything that edits these functions should not arrive under a commit that claims to measure.
    $text = Get-Content -LiteralPath $ScriptPath -Raw

    Assert ($text -match 'Remove-PublicPortBinding -SiteName \$siteName -Port \$DeploymentPlan\.PublicPort') `
        "Switch-PublicTrafficToSite no longer removes the old binding the way it did."
    Assert ($text -match 'Ensure-PublicPortBinding -SiteName \$DestinationSiteName -Port \$DeploymentPlan\.PublicPort') `
        "Switch-PublicTrafficToSite no longer adds the destination binding the way it did."
    Assert ($text -notmatch 'CommitChanges') `
        "A batched applicationHost.config commit is back. That is the change that took the Web down on 16 September 2026; land it on its own evidence, not here."
}

Write-Host ""
Write-Host "$script:pass passed, $script:fail failed." -ForegroundColor $(if ($script:fail -gt 0) { 'Red' } else { 'Green' })

if ($script:fail -gt 0) { exit 1 }
exit 0
