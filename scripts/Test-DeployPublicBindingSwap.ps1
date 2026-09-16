# Exercises how Update-Production.ps1 hands the public port from the live slot to the new one.
#
# The cutover used to remove the port binding from each old site and then add it to the new one, a
# separate write to applicationHost.config every time. Between the first removal and the addition the
# port belonged to nobody: HTTP.sys answered new requests 404 and cut off the ones already running,
# which nginx reported as 502. On 14 September 2026 a till sale posted at 16:41 got a 502 while the
# API went on writing it, and the customer left with no receipt. That is what held production deploys
# out of trading hours.
#
# Both halves now go in as one commit, so no committed state has the port unowned. The two things
# worth proving are that the plan moves the right bindings and only those, and that applying it
# commits exactly once - so the tests below count commits and read the configuration as each commit
# left it.
#
# The functions are lifted out of the real script by AST, as Test-DeploySlotSeeding.ps1 does, and run
# against a stand-in with Microsoft.Web.Administration's shape. Nothing here touches IIS or reaches
# production.

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

foreach ($name in 'Get-PublicBindingPlan', 'Invoke-PublicBindingPlan', 'Get-LongestOutage', 'Start-PublicPortProbe', 'Stop-PublicPortProbe') {
    $definition = $ast.Find({
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
        }, $true)

    if (-not $definition) {
        throw "Could not extract $name from $ScriptPath - it was renamed or removed."
    }

    . ([scriptblock]::Create($definition.Extent.Text))
}

# Microsoft.Web.Administration's shape, in the two details the cutover uses: a binding collection that
# takes (bindingInformation, protocol) and gives back the binding, and a manager whose CommitChanges
# is the only thing that reaches the live configuration. Written in C# rather than as PowerShell
# objects because the real code enumerates the collection AND mutates it, which a PSCustomObject
# cannot do. Remove is void here for the same reason it is void there: a bool return would land in
# the pipeline.
if (-not ('FakeServerManager' -as [type])) {
    Add-Type -TypeDefinition @'
using System.Collections.Generic;

public class FakeBinding
{
    public string BindingInformation { get; set; }
    public string Protocol { get; set; }
}

public class FakeBindingCollection : List<FakeBinding>
{
    public FakeBinding Add(string bindingInformation, string protocol)
    {
        FakeBinding binding = new FakeBinding();
        binding.BindingInformation = bindingInformation;
        binding.Protocol = protocol;
        base.Add(binding);
        return binding;
    }

    public new void Remove(FakeBinding binding)
    {
        base.Remove(binding);
    }
}

public class FakeSite
{
    public string Name { get; set; }
    public FakeBindingCollection Bindings = new FakeBindingCollection();
}

public class FakeServerManager
{
    public List<FakeSite> Sites = new List<FakeSite>();
    public int Commits;

    // What the configuration said at each commit, so a test can ask how many sites held the port at
    // the only moments a request could have seen it.
    public List<string> CommittedStates = new List<string>();

    public void CommitChanges()
    {
        Commits++;
        List<string> held = new List<string>();
        foreach (FakeSite site in Sites)
        {
            foreach (FakeBinding binding in site.Bindings)
            {
                held.Add(site.Name + " " + binding.Protocol + " " + binding.BindingInformation);
            }
        }
        CommittedStates.Add(string.Join("; ", held.ToArray()));
    }

    public void Dispose()
    {
    }
}
'@
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

# @{ 'Site name' = @('*:5106:', 'https|*:443:') } - a binding is http unless it is written protocol|info.
function New-Manager {
    param([hashtable]$Sites)

    $manager = New-Object FakeServerManager

    foreach ($siteName in $Sites.Keys) {
        $site = New-Object FakeSite
        $site.Name = $siteName

        foreach ($spec in @($Sites[$siteName])) {
            $protocol = 'http'
            $information = $spec
            if ($spec -like '*|*') {
                $protocol, $information = $spec.Split('|', 2)
            }

            [void]$site.Bindings.Add($information, $protocol)
        }

        $manager.Sites.Add($site)
    }

    return $manager
}

# The same snapshot the real Switch-PublicTrafficToSite builds from Get-WebBinding.
function Get-Snapshot {
    param([object]$Manager)

    return @(@($Manager.Sites) | ForEach-Object {
            [pscustomobject]@{
                Name     = $_.Name
                Bindings = @(@($_.Bindings) | ForEach-Object {
                        [pscustomobject]@{ Protocol = $_.Protocol; BindingInformation = $_.BindingInformation }
                    })
            }
        })
}

function Get-SitesHoldingPort {
    param([object]$Manager, [int]$Port)

    return @(@($Manager.Sites) | Where-Object {
            @(@($_.Bindings) | Where-Object { $_.Protocol -eq 'http' -and $_.BindingInformation -match "^[^:]*:${Port}:" }).Count -gt 0
        } | ForEach-Object { $_.Name })
}

Write-Host ""
Write-Host "Which bindings move" -ForegroundColor Cyan

Check "A normal cutover takes the port off the live site and gives it to the new slot" {
    $manager = New-Manager @{
        'ShopInventory-API'       = @('*:5106:')
        'ShopInventory-API-Blue'  = @('*:15106:')
        'ShopInventory-API-Green' = @('*:15116:')
    }

    $plan = Get-PublicBindingPlan -Sites (Get-Snapshot -Manager $manager) -DestinationSiteName 'ShopInventory-API-Green' -Port 5106

    Assert (@($plan.Remove).Count -eq 1) "Expected one removal, got $(@($plan.Remove).Count)."
    Assert ($plan.Remove[0].SiteName -eq 'ShopInventory-API') "Removed from $($plan.Remove[0].SiteName)."
    Assert ($plan.Remove[0].BindingInformation -eq '*:5106:') "Removed '$($plan.Remove[0].BindingInformation)'."
    Assert ($plan.Add) "The new slot has no binding on 5106, so one has to be added."
}

Check "A slot that already holds the port is left alone, and nothing is added twice" {
    $manager = New-Manager @{
        'ShopInventory-API'      = @()
        'ShopInventory-API-Blue' = @('*:15106:', '*:5106:')
    }

    $plan = Get-PublicBindingPlan -Sites (Get-Snapshot -Manager $manager) -DestinationSiteName 'ShopInventory-API-Blue' -Port 5106

    Assert (@($plan.Remove).Count -eq 0) "Nothing should be removed, got $(@($plan.Remove).Count)."
    Assert (-not $plan.Add) "The destination already has the port."
}

Check "A slot keeps its own private port" {
    $manager = New-Manager @{
        'ShopInventory-API'      = @('*:5106:')
        'ShopInventory-API-Blue' = @('*:15106:')
    }

    $plan = Get-PublicBindingPlan -Sites (Get-Snapshot -Manager $manager) -DestinationSiteName 'ShopInventory-API-Blue' -Port 5106
    Invoke-PublicBindingPlan -Plan $plan -Manager $manager

    $blue = @($manager.Sites) | Where-Object { $_.Name -eq 'ShopInventory-API-Blue' }
    $information = @(@($blue.Bindings) | ForEach-Object { $_.BindingInformation })

    Assert ($information -contains '*:15106:') "The private port was lost: $($information -join ', ')."
    Assert ($information -contains '*:5106:') "The public port was not added: $($information -join ', ')."
}

Check "An https binding on the same port is not touched" {
    $manager = New-Manager @{
        'ShopInventory-API'      = @('*:5106:', 'https|*:5106:')
        'ShopInventory-API-Blue' = @('*:15106:')
    }

    $plan = Get-PublicBindingPlan -Sites (Get-Snapshot -Manager $manager) -DestinationSiteName 'ShopInventory-API-Blue' -Port 5106

    Assert (@($plan.Remove).Count -eq 1) "Only the http binding moves, got $(@($plan.Remove).Count) removals."

    Invoke-PublicBindingPlan -Plan $plan -Manager $manager
    $public = @($manager.Sites) | Where-Object { $_.Name -eq 'ShopInventory-API' }
    $left = @(@($public.Bindings) | ForEach-Object { "$($_.Protocol) $($_.BindingInformation)" })

    Assert ($left.Count -eq 1 -and $left[0] -eq 'https *:5106:') "The https binding should survive: $($left -join ', ')."
}

Check "A binding bound to one address, and one with a host header, both move" {
    $manager = New-Manager @{
        'ShopInventory-Web'       = @('10.10.10.9:5107:')
        'ShopInventory-Web-Old'   = @('*:5107:sis.kefaloscheese.com')
        'ShopInventory-Web-Green' = @('*:15107:')
    }

    $plan = Get-PublicBindingPlan -Sites (Get-Snapshot -Manager $manager) -DestinationSiteName 'ShopInventory-Web-Green' -Port 5107

    Assert (@($plan.Remove).Count -eq 2) "Both should move, got $(@($plan.Remove).Count)."

    Invoke-PublicBindingPlan -Plan $plan -Manager $manager
    $holders = @(Get-SitesHoldingPort -Manager $manager -Port 5107)

    Assert ($holders.Count -eq 1 -and $holders[0] -eq 'ShopInventory-Web-Green') "Port 5107 is held by: $($holders -join ', ')."
}

Check "A port whose number merely starts with the public one is not mistaken for it" {
    # 51060 and 5107 both survive a switch of 5106. The first is why the match is anchored on the
    # colon that follows the port, and 15106 on the destination is the same trap from the other side.
    $manager = New-Manager @{
        'Something-Else'         = @('*:51060:', '*:5107:')
        'ShopInventory-API'      = @('*:5106:')
        'ShopInventory-API-Blue' = @('*:15106:')
    }

    $plan = Get-PublicBindingPlan -Sites (Get-Snapshot -Manager $manager) -DestinationSiteName 'ShopInventory-API-Blue' -Port 5106

    Assert (@($plan.Remove).Count -eq 1) "Only ShopInventory-API holds 5106, got $(@($plan.Remove).Count) removals."
    Assert ($plan.Remove[0].SiteName -eq 'ShopInventory-API') "Removed from $($plan.Remove[0].SiteName)."
}

Check "Rolling back moves the port the other way" {
    $manager = New-Manager @{
        'ShopInventory-API'       = @()
        'ShopInventory-API-Blue'  = @('*:15106:')
        'ShopInventory-API-Green' = @('*:15116:', '*:5106:')
    }

    $plan = Get-PublicBindingPlan -Sites (Get-Snapshot -Manager $manager) -DestinationSiteName 'ShopInventory-API-Blue' -Port 5106
    Invoke-PublicBindingPlan -Plan $plan -Manager $manager

    $holders = @(Get-SitesHoldingPort -Manager $manager -Port 5106)
    Assert ($holders.Count -eq 1 -and $holders[0] -eq 'ShopInventory-API-Blue') "Port 5106 is held by: $($holders -join ', ')."
}

Write-Host ""
Write-Host "The port is never unowned" -ForegroundColor Cyan

Check "The whole swap is one commit" {
    # The heart of it. Four commits is four chances for a request to arrive while nobody holds 5106,
    # and that is what a till sale hit on 14 September 2026.
    $manager = New-Manager @{
        'ShopInventory-API'       = @('*:5106:')
        'ShopInventory-API-Old'   = @('*:5106:api.internal')
        'ShopInventory-API-Green' = @('*:15116:')
    }

    $plan = Get-PublicBindingPlan -Sites (Get-Snapshot -Manager $manager) -DestinationSiteName 'ShopInventory-API-Green' -Port 5106

    Assert (@($plan.Remove).Count -eq 2) "Two removals and one addition: $(@($plan.Remove).Count) removals."

    Invoke-PublicBindingPlan -Plan $plan -Manager $manager

    Assert ($manager.Commits -eq 1) "Expected exactly one commit, got $($manager.Commits)."
}

Check "No committed configuration leaves the public port unowned" {
    $manager = New-Manager @{
        'ShopInventory-API'       = @('*:5106:')
        'ShopInventory-API-Green' = @('*:15116:')
    }

    $plan = Get-PublicBindingPlan -Sites (Get-Snapshot -Manager $manager) -DestinationSiteName 'ShopInventory-API-Green' -Port 5106
    Invoke-PublicBindingPlan -Plan $plan -Manager $manager

    foreach ($state in @($manager.CommittedStates)) {
        $holders = @(@($state -split '; ') | Where-Object { $_ -match ' http \*?[^:]*:5106:' })
        Assert ($holders.Count -eq 1) "A committed configuration had $($holders.Count) sites on 5106: '$state'."
    }
}

Check "Nothing is committed when the destination site does not exist" {
    $manager = New-Manager @{
        'ShopInventory-API' = @('*:5106:')
    }

    $plan = Get-PublicBindingPlan -Sites (Get-Snapshot -Manager $manager) -DestinationSiteName 'ShopInventory-API-Green' -Port 5106

    $threw = $false
    try { Invoke-PublicBindingPlan -Plan $plan -Manager $manager } catch { $threw = $true }

    Assert $threw "Handing the port to a site IIS does not have should throw."
    Assert ($manager.Commits -eq 0) "Nothing should have been committed, got $($manager.Commits) commits."

    # And the live site still has the port, so traffic keeps being served.
    $holders = @(Get-SitesHoldingPort -Manager $manager -Port 5106)
    Assert ($holders.Count -eq 1 -and $holders[0] -eq 'ShopInventory-API') "The live site lost the port: $($holders -join ', ')."
}

Check "A site named in the plan but missing from IIS is skipped, and the rest still applies" {
    $manager = New-Manager @{
        'ShopInventory-API'       = @('*:5106:')
        'ShopInventory-API-Green' = @('*:15116:')
    }

    $plan = Get-PublicBindingPlan -Sites (Get-Snapshot -Manager $manager) -DestinationSiteName 'ShopInventory-API-Green' -Port 5106
    $plan.Remove = @($plan.Remove) + [pscustomobject]@{ SiteName = 'Deleted-Site'; BindingInformation = '*:5106:' }

    Invoke-PublicBindingPlan -Plan $plan -Manager $manager

    Assert ($manager.Commits -eq 1) "Expected one commit, got $($manager.Commits)."
    $holders = @(Get-SitesHoldingPort -Manager $manager -Port 5106)
    Assert ($holders.Count -eq 1 -and $holders[0] -eq 'ShopInventory-API-Green') "Port 5106 is held by: $($holders -join ', ')."
}

Write-Host ""
Write-Host "What the cutover probe reports" -ForegroundColor Cyan

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
    # A clean cutover is only as believable as the sampling behind it. Samples 40ms apart except for
    # one 200ms stall: an outage inside that stall would not have been seen, and the number says so.
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
# Without this the number a deployment prints is only as good as an untested loop, and it is the
# number being used to say a merge may ship while the shops are trading.
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
# Set Refusing to make it take connections and drop them without answering. That is the cutover as a
# caller experiences it, and closer to the real thing than stopping the listener would be: during the
# gap the port stayed bound and HTTP.sys answered 404, which is a failed probe just the same. It is
# also the difference between a test that runs in a second and one that spent 75 of them waiting on a
# half-torn-down socket.
function Start-TinyHttpServer {
    param([int]$Port)

    $shared = [hashtable]::Synchronized(@{ Stop = $false; Listening = $false; Refusing = $false })

    $runspace = [runspacefactory]::CreateRunspace()
    $runspace.Open()
    $runspace.SessionStateProxy.SetVariable('shared', $shared)
    $runspace.SessionStateProxy.SetVariable('serverPort', $Port)

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
                    try {
                        # Taken and dropped, with nothing written back.
                        if ($shared.Refusing) { continue }

                        $stream = $client.GetStream()
                        # Without this a client that connects and then abandons the request leaves the
                        # read blocking for as long as the TCP stack takes to give up on it.
                        $stream.ReadTimeout = 500

                        $buffer = New-Object byte[] 1024
                        [void]$stream.Read($buffer, 0, $buffer.Length)

                        $response = "HTTP/1.1 200 OK`r`nContent-Type: text/plain`r`nContent-Length: 2`r`nConnection: close`r`n`r`nok"
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

Write-Host ""
Write-Host "$script:pass passed, $script:fail failed." -ForegroundColor $(if ($script:fail -gt 0) { 'Red' } else { 'Green' })

if ($script:fail -gt 0) { exit 1 }
exit 0
