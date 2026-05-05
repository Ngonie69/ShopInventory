[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string[]]$NodeUrls,

    [Parameter(Mandatory = $true)]
    [string]$WorkerName,

    [string]$AppPoolName = 'ShopInventoryAPI',

    [int]$HandoffTimeoutSeconds = 90,

    [int]$PollIntervalSeconds = 5,

    [hashtable]$NodeComputerNames = @{},

    [System.Management.Automation.PSCredential]$Credential
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-HealthSnapshot {
    param(
        [Parameter(Mandatory = $true)]
        [string]$BaseUrl
    )

    $normalizedBaseUrl = $BaseUrl.TrimEnd('/')
    $healthUrl = "$normalizedBaseUrl/api/health"

    try {
        $health = Invoke-RestMethod -Uri $healthUrl -Method Get -TimeoutSec 15
        [pscustomobject]@{
            Url = $normalizedBaseUrl
            Reachable = $true
            Health = $health
            Error = $null
        }
    }
    catch {
        [pscustomobject]@{
            Url = $normalizedBaseUrl
            Reachable = $false
            Health = $null
            Error = $_.Exception.Message
        }
    }
}

function Get-WorkerStatesFromHealth {
    param(
        [Parameter(Mandatory = $true)]
        $Health
    )

    if (-not $Health.Dependencies -or -not $Health.Dependencies.Checks) {
        return @()
    }

    $workersCheck = @($Health.Dependencies.Checks | Where-Object { $_.Name -eq 'workers' } | Select-Object -First 1)
    if ($workersCheck.Count -eq 0 -or -not $workersCheck[0].Data) {
        return @()
    }

    $rows = @($workersCheck[0].Data.workers)
    $states = @()

    foreach ($row in $rows) {
        if ([string]::IsNullOrWhiteSpace($row)) {
            continue
        }

        $segments = $row -split '\|'
        if ($segments.Count -lt 2) {
            continue
        }

        $properties = @{}
        foreach ($segment in $segments[1..($segments.Count - 1)]) {
            $pair = $segment -split '=', 2
            if ($pair.Count -eq 2) {
                $properties[$pair[0]] = $pair[1]
            }
        }

        $states += [pscustomobject]@{
            WorkerName = $segments[0]
            InstanceId = $properties['instance']
            Mode = $properties['mode']
            HeartbeatUtc = $properties['heartbeat']
            LastSuccessUtc = $properties['success']
            ConsecutiveFailures = if ($properties.ContainsKey('failures')) { [int]$properties['failures'] } else { 0 }
        }
    }

    return $states
}

function Get-ClusterLeader {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$Snapshots,

        [Parameter(Mandatory = $true)]
        [string]$WorkerName
    )

    $workerStates = @(
        foreach ($snapshot in $Snapshots) {
            if (-not $snapshot.Reachable -or -not $snapshot.Health) {
                continue
            }

            Get-WorkerStatesFromHealth -Health $snapshot.Health |
                Where-Object { $_.WorkerName -eq $WorkerName }
        }
    )

    if ($workerStates.Count -eq 0) {
        return $null
    }

    $leaders = @($workerStates | Where-Object { $_.Mode -eq 'Leader' })
    if ($leaders.Count -eq 0) {
        return $null
    }

    return $leaders |
        Sort-Object -Property @{ Expression = { [datetime]$_.HeartbeatUtc }; Descending = $true } |
        Select-Object -First 1
}

function Get-NodeForInstance {
    param(
        [Parameter(Mandatory = $true)]
        [object[]]$Snapshots,

        [Parameter(Mandatory = $true)]
        [string]$InstanceId
    )

    return $Snapshots |
        Where-Object { $_.Reachable -and $_.Health -and $_.Health.Instance.InstanceId -eq $InstanceId } |
        Select-Object -First 1
}

function Restart-ApiAppPool {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ComputerName,

        [Parameter(Mandatory = $true)]
        [string]$PoolName,

        [System.Management.Automation.PSCredential]$Credential
    )

    $restartScript = {
        param([string]$AppPool)
        Import-Module WebAdministration
        Restart-WebAppPool -Name $AppPool
    }

    if ($ComputerName -eq '.' -or
        $ComputerName -eq 'localhost' -or
        $ComputerName -eq $env:COMPUTERNAME) {
        & $restartScript $PoolName
        return
    }

    if ($Credential) {
        Invoke-Command -ComputerName $ComputerName -Credential $Credential -ScriptBlock $restartScript -ArgumentList $PoolName
        return
    }

    Invoke-Command -ComputerName $ComputerName -ScriptBlock $restartScript -ArgumentList $PoolName
}

Write-Host "Collecting initial health from $($NodeUrls.Count) node(s)..."
$initialSnapshots = @($NodeUrls | ForEach-Object { Get-HealthSnapshot -BaseUrl $_ })

$initialLeader = Get-ClusterLeader -Snapshots $initialSnapshots -WorkerName $WorkerName
if (-not $initialLeader) {
    throw "Could not determine an active leader for worker '$WorkerName'. Check /api/health on the provided node URLs."
}

$leaderNode = Get-NodeForInstance -Snapshots $initialSnapshots -InstanceId $initialLeader.InstanceId
if (-not $leaderNode) {
    throw "Could not map leader instance '$($initialLeader.InstanceId)' to one of the provided node URLs. Use direct node URLs, not a load balancer VIP."
}

$leaderMachineName = if ($NodeComputerNames.ContainsKey($leaderNode.Url)) {
    [string]$NodeComputerNames[$leaderNode.Url]
}
else {
    [string]$leaderNode.Health.Instance.MachineName
}

Write-Host "Current leader for '$WorkerName': instance $($initialLeader.InstanceId) on $leaderMachineName via $($leaderNode.Url)"
Write-Host "Recycling IIS app pool '$AppPoolName' on $leaderMachineName..."

Restart-ApiAppPool -ComputerName $leaderMachineName -PoolName $AppPoolName -Credential $Credential

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
do {
    Start-Sleep -Seconds $PollIntervalSeconds
    $snapshots = @($NodeUrls | ForEach-Object { Get-HealthSnapshot -BaseUrl $_ })
    $newLeader = Get-ClusterLeader -Snapshots $snapshots -WorkerName $WorkerName

    if (-not $newLeader) {
        Write-Host "Leader not yet visible for '$WorkerName'. Waiting..."
        continue
    }

    $newLeaderNode = Get-NodeForInstance -Snapshots $snapshots -InstanceId $newLeader.InstanceId
    $newLeaderMachineName = if ($newLeaderNode) {
        [string]$newLeaderNode.Health.Instance.MachineName
    }
    else {
        'unknown'
    }

    Write-Host "Observed leader: instance $($newLeader.InstanceId) on $newLeaderMachineName"

    if ($newLeader.InstanceId -ne $initialLeader.InstanceId) {
        $healthyNodes = @(
            $snapshots |
                Where-Object { $_.Reachable -and $_.Health -and $_.Health.Readiness.Status -eq 'Healthy' }
        )

        if ($healthyNodes.Count -eq 0) {
            Write-Host 'Leader changed, but no node reports healthy readiness yet. Waiting...'
            continue
        }

        $stopwatch.Stop()
        Write-Host "Leader handoff succeeded in $([math]::Round($stopwatch.Elapsed.TotalSeconds, 1)) seconds."
        Write-Host "Old leader: $($initialLeader.InstanceId)"
        Write-Host "New leader: $($newLeader.InstanceId)"
        exit 0
    }
}
while ($stopwatch.Elapsed.TotalSeconds -lt $HandoffTimeoutSeconds)

$stopwatch.Stop()
throw "Leader handoff did not complete within $HandoffTimeoutSeconds seconds for worker '$WorkerName'."