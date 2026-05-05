[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PrimaryHost,

    [Parameter(Mandatory = $true)]
    [string]$StandbyHost,

    [System.Management.Automation.PSCredential]$Credential,

    [System.Management.Automation.PSCredential]$PrimaryCredential,

    [System.Management.Automation.PSCredential]$StandbyCredential,

    [int]$Port = 5432,

    [string]$PostgresSuperuser = 'postgres',

    [string]$PostgresPassword,

    [switch]$IncludeDatabaseChecks
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-Credential {
    param(
        [System.Management.Automation.PSCredential]$Specific,
        [System.Management.Automation.PSCredential]$Shared,
        [string]$HostName
    )

    if ($Specific) {
        return $Specific
    }

    if ($Shared) {
        return $Shared
    }

    throw "No credential supplied for $HostName. Provide -Credential or the host-specific credential parameter."
}

function Get-PostgresHostState {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ComputerName,

        [Parameter(Mandatory = $true)]
        [System.Management.Automation.PSCredential]$RemoteCredential,

        [Parameter(Mandatory = $true)]
        [int]$Port,

        [Parameter(Mandatory = $true)]
        [string]$ProbeTarget,

        [Parameter(Mandatory = $true)]
        [string]$PostgresSuperuser,

        [string]$PostgresPassword,

        [bool]$RunDatabaseChecks
    )

    $scriptBlock = {
        param(
            [int]$Port,
            [string]$ProbeTarget,
            [string]$PostgresSuperuser,
            [string]$PostgresPassword,
            [bool]$RunDatabaseChecks
        )

        function Get-ConfigValue {
            param(
                [string]$Path,
                [string]$SettingName
            )

            if (-not (Test-Path $Path)) {
                return $null
            }

            $pattern = "^\s*$([regex]::Escape($SettingName))\s*=\s*(.+?)\s*(?:#.*)?$"
            foreach ($line in Get-Content -Path $Path) {
                if ($line -match '^\s*#') {
                    continue
                }

                if ($line -match $pattern) {
                    return $matches[1].Trim().Trim("'")
                }
            }

            return $null
        }

        $services = Get-CimInstance Win32_Service |
            Where-Object { $_.Name -like 'postgresql-x64-*' }

        $service = $services |
            Sort-Object @(
                @{ Expression = { if ($_.State -eq 'Running') { 0 } else { 1 } }; Ascending = $true },
                @{ Expression = { $_.Name }; Descending = $true }
            ) |
            Select-Object -First 1

        if (-not $service) {
            return [pscustomobject]@{
                Reachable = $true
                HostName = $env:COMPUTERNAME
                ServiceFound = $false
                Error = 'No PostgreSQL Windows service matching postgresql-x64-* was found.'
            }
        }

        $pathName = $service.PathName
        $dataDirectory = $null
        $executablePath = $null

        if ($pathName -match '^("(?<exe>[^"]+)"|(?<exe>\S+))') {
            $executablePath = $matches['exe']
        }

        if ($pathName -match '-D\s+"(?<data>[^"]+)"') {
            $dataDirectory = $matches['data']
        }

        if (-not $dataDirectory -and $pathName -match '-D\s+(?<data>\S+)') {
            $dataDirectory = $matches['data']
        }

        $binDirectory = if ($executablePath) { Split-Path -Path $executablePath -Parent } else { $null }
        $installRoot = if ($binDirectory) { Split-Path -Path $binDirectory -Parent } else { $null }

        if (-not $dataDirectory -and $installRoot) {
            $candidateDataDirectory = Join-Path $installRoot 'data'
            if (Test-Path $candidateDataDirectory) {
                $dataDirectory = $candidateDataDirectory
            }
        }

        $postgresqlConf = if ($dataDirectory) { Join-Path $dataDirectory 'postgresql.conf' } else { $null }
        $postgresqlAutoConf = if ($dataDirectory) { Join-Path $dataDirectory 'postgresql.auto.conf' } else { $null }
        $pgHbaConf = if ($dataDirectory) { Join-Path $dataDirectory 'pg_hba.conf' } else { $null }
        $pgVersionFile = if ($dataDirectory) { Join-Path $dataDirectory 'PG_VERSION' } else { $null }
        $standbySignalFile = if ($dataDirectory) { Join-Path $dataDirectory 'standby.signal' } else { $null }
        $majorVersion = if ($pgVersionFile -and (Test-Path $pgVersionFile)) { (Get-Content -Path $pgVersionFile -TotalCount 1).Trim() } else { $null }

        $listenAddresses = if ($postgresqlConf) { Get-ConfigValue -Path $postgresqlConf -SettingName 'listen_addresses' } else { $null }
        $configuredPort = if ($postgresqlConf) { Get-ConfigValue -Path $postgresqlConf -SettingName 'port' } else { $null }
        $walLevel = if ($postgresqlConf) { Get-ConfigValue -Path $postgresqlConf -SettingName 'wal_level' } else { $null }
        $archiveMode = if ($postgresqlConf) { Get-ConfigValue -Path $postgresqlConf -SettingName 'archive_mode' } else { $null }
        $maxWalSenders = if ($postgresqlConf) { Get-ConfigValue -Path $postgresqlConf -SettingName 'max_wal_senders' } else { $null }
        $maxReplicationSlots = if ($postgresqlConf) { Get-ConfigValue -Path $postgresqlConf -SettingName 'max_replication_slots' } else { $null }
        $primaryConnInfo = if ($postgresqlConf) { Get-ConfigValue -Path $postgresqlConf -SettingName 'primary_conninfo' } else { $null }
        if ([string]::IsNullOrWhiteSpace($primaryConnInfo) -and $postgresqlAutoConf) {
            $primaryConnInfo = Get-ConfigValue -Path $postgresqlAutoConf -SettingName 'primary_conninfo'
        }

        $primarySlotName = if ($postgresqlConf) { Get-ConfigValue -Path $postgresqlConf -SettingName 'primary_slot_name' } else { $null }
        if ([string]::IsNullOrWhiteSpace($primarySlotName) -and $postgresqlAutoConf) {
            $primarySlotName = Get-ConfigValue -Path $postgresqlAutoConf -SettingName 'primary_slot_name'
        }

        $listener = Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue |
            Select-Object -First 1

        $firewallRule = Get-NetFirewallRule -Enabled True -Direction Inbound -Action Allow -ErrorAction SilentlyContinue |
            Get-NetFirewallPortFilter -ErrorAction SilentlyContinue |
            Where-Object {
                $_.Protocol -eq 'TCP' -and
                ($_.LocalPort -eq $Port -or $_.LocalPort -eq $Port.ToString())
            } |
            Select-Object -First 1

        $probe = Test-NetConnection -ComputerName $ProbeTarget -Port $Port -InformationLevel Quiet -WarningAction SilentlyContinue

        $databaseVersion = $null
        $databaseRecoveryState = $null
        $replicationState = $null
        $effectiveWalLevel = $walLevel
        $effectiveArchiveMode = $archiveMode
        $effectiveMaxWalSenders = $maxWalSenders
        $effectiveMaxReplicationSlots = $maxReplicationSlots
        if ($RunDatabaseChecks -and $binDirectory -and -not [string]::IsNullOrWhiteSpace($PostgresPassword)) {
            $psqlPath = Join-Path $binDirectory 'psql.exe'
            if (Test-Path $psqlPath) {
                $env:PGPASSWORD = $PostgresPassword
                try {
                    $databaseVersion = (& $psqlPath -U $PostgresSuperuser -h 127.0.0.1 -p $Port -d postgres -Atqc 'SELECT version();' 2>$null | Out-String).Trim()
                    $databaseRecoveryState = (& $psqlPath -U $PostgresSuperuser -h 127.0.0.1 -p $Port -d postgres -Atqc 'SELECT pg_is_in_recovery();' 2>$null | Out-String).Trim()
                    $effectiveWalLevel = (& $psqlPath -U $PostgresSuperuser -h 127.0.0.1 -p $Port -d postgres -Atqc 'SHOW wal_level;' 2>$null | Out-String).Trim()
                    $effectiveArchiveMode = (& $psqlPath -U $PostgresSuperuser -h 127.0.0.1 -p $Port -d postgres -Atqc 'SHOW archive_mode;' 2>$null | Out-String).Trim()
                    $effectiveMaxWalSenders = (& $psqlPath -U $PostgresSuperuser -h 127.0.0.1 -p $Port -d postgres -Atqc 'SHOW max_wal_senders;' 2>$null | Out-String).Trim()
                    $effectiveMaxReplicationSlots = (& $psqlPath -U $PostgresSuperuser -h 127.0.0.1 -p $Port -d postgres -Atqc 'SHOW max_replication_slots;' 2>$null | Out-String).Trim()
                    if ($databaseRecoveryState -eq 'f') {
                        $replicationState = (& $psqlPath -U $PostgresSuperuser -h 127.0.0.1 -p $Port -d postgres -Atqc 'SELECT string_agg(application_name || '':'':'' || client_addr::text || '':'':'' || state || '':'':'' || sync_state, ''; '') FROM pg_stat_replication;' 2>$null | Out-String).Trim()
                    }
                }
                finally {
                    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
                }
            }
        }

        [pscustomobject]@{
            Reachable = $true
            HostName = $env:COMPUTERNAME
            ServiceFound = $true
            ServiceName = $service.Name
            ServiceState = $service.State
            DetectedServiceNames = @($services | Select-Object -ExpandProperty Name)
            StartMode = $service.StartMode
            InstallRoot = $installRoot
            DataDirectory = $dataDirectory
            MajorVersion = $majorVersion
            StandbySignalExists = [bool]($standbySignalFile -and (Test-Path $standbySignalFile))
            PostgreSqlConfExists = [bool]($postgresqlConf -and (Test-Path $postgresqlConf))
            PgHbaConfExists = [bool]($pgHbaConf -and (Test-Path $pgHbaConf))
            ListenAddresses = $listenAddresses
            ConfiguredPort = $configuredPort
            WalLevel = $effectiveWalLevel
            ArchiveMode = $effectiveArchiveMode
            MaxWalSenders = $effectiveMaxWalSenders
            MaxReplicationSlots = $effectiveMaxReplicationSlots
            PrimaryConnInfo = $primaryConnInfo
            PrimarySlotName = $primarySlotName
            TcpListenerActive = [bool]$listener
            FirewallAllowsPort = [bool]$firewallRule
            ProbeTarget = $ProbeTarget
            ProbeTargetReachable = [bool]$probe
            DatabaseVersion = $databaseVersion
            DatabaseRecoveryState = $databaseRecoveryState
            ReplicationState = $replicationState
            Error = $null
        }
    }

    try {
        $invokeCommandArgs = @{
            ComputerName = $ComputerName
            Credential = $RemoteCredential
            ScriptBlock = $scriptBlock
            ArgumentList = @($Port, $ProbeTarget, $PostgresSuperuser, $PostgresPassword, $RunDatabaseChecks)
        }

        Invoke-Command @invokeCommandArgs
    }
    catch {
        [pscustomobject]@{
            Reachable = $false
            HostName = $ComputerName
            ServiceFound = $false
            Error = $_.Exception.Message
        }
    }
}

function Add-Issue {
    param(
        [System.Collections.Generic.List[string]]$Issues,
        [string]$Message
    )

    if (-not [string]::IsNullOrWhiteSpace($Message)) {
        $Issues.Add($Message)
    }
}

$primaryCredentialToUse = Resolve-Credential -Specific $PrimaryCredential -Shared $Credential -HostName $PrimaryHost
$standbyCredentialToUse = Resolve-Credential -Specific $StandbyCredential -Shared $Credential -HostName $StandbyHost

if (-not $primaryCredentialToUse) {
    throw "Resolved credential for $PrimaryHost is null."
}

if (-not $standbyCredentialToUse) {
    throw "Resolved credential for $StandbyHost is null."
}

$primaryProbeArgs = @{
    ComputerName = $PrimaryHost
    RemoteCredential = $primaryCredentialToUse
    Port = $Port
    ProbeTarget = $StandbyHost
    PostgresSuperuser = $PostgresSuperuser
    PostgresPassword = $PostgresPassword
    RunDatabaseChecks = $IncludeDatabaseChecks.IsPresent
}

$standbyProbeArgs = @{
    ComputerName = $StandbyHost
    RemoteCredential = $standbyCredentialToUse
    Port = $Port
    ProbeTarget = $PrimaryHost
    PostgresSuperuser = $PostgresSuperuser
    PostgresPassword = $PostgresPassword
    RunDatabaseChecks = $IncludeDatabaseChecks.IsPresent
}

$primary = Get-PostgresHostState @primaryProbeArgs

$standby = Get-PostgresHostState @standbyProbeArgs

$issues = New-Object System.Collections.Generic.List[string]

if (-not $primary.Reachable) {
    Add-Issue -Issues $issues -Message "Primary host $PrimaryHost is not reachable via PowerShell remoting: $($primary.Error)"
}

if (-not $standby.Reachable) {
    Add-Issue -Issues $issues -Message "Standby host $StandbyHost is not reachable via PowerShell remoting: $($standby.Error)"
}

if ($primary.Reachable -and -not $primary.ServiceFound) {
    Add-Issue -Issues $issues -Message "Primary host $PrimaryHost does not have a PostgreSQL Windows service installed."
}

if ($standby.Reachable -and -not $standby.ServiceFound) {
    Add-Issue -Issues $issues -Message "Standby host $StandbyHost does not have a PostgreSQL Windows service installed."
}

if ($primary.ServiceFound -and $primary.ServiceState -ne 'Running') {
    Add-Issue -Issues $issues -Message "Primary PostgreSQL service $($primary.ServiceName) is not running on $PrimaryHost."
}

if ($standby.ServiceFound -and $standby.ServiceState -ne 'Running') {
    Add-Issue -Issues $issues -Message "Standby PostgreSQL service $($standby.ServiceName) is not running on $StandbyHost."
}

if ($primary.ServiceFound -and -not $primary.TcpListenerActive) {
    Add-Issue -Issues $issues -Message "Primary host $PrimaryHost is not listening on TCP port $Port."
}

if ($standby.ServiceFound -and -not $standby.TcpListenerActive) {
    Add-Issue -Issues $issues -Message "Standby host $StandbyHost is not listening on TCP port $Port."
}

if ($primary.ServiceFound -and -not $primary.FirewallAllowsPort) {
    Add-Issue -Issues $issues -Message "Primary host $PrimaryHost does not have an enabled inbound Windows Firewall allow rule for TCP $Port."
}

if ($standby.ServiceFound -and -not $standby.FirewallAllowsPort) {
    Add-Issue -Issues $issues -Message "Standby host $StandbyHost does not have an enabled inbound Windows Firewall allow rule for TCP $Port."
}

if ($standby.ServiceFound -and -not $standby.ProbeTargetReachable) {
    Add-Issue -Issues $issues -Message "Standby host $StandbyHost cannot reach primary host $PrimaryHost on TCP $Port."
}

if ($primary.ServiceFound -and -not $primary.ProbeTargetReachable) {
    Add-Issue -Issues $issues -Message "Primary host $PrimaryHost cannot reach standby host $StandbyHost on TCP $Port."
}

if ($primary.MajorVersion -and $standby.MajorVersion -and $primary.MajorVersion -ne $standby.MajorVersion) {
    Add-Issue -Issues $issues -Message "PostgreSQL major versions do not match: primary=$($primary.MajorVersion), standby=$($standby.MajorVersion). Physical replication is blocked until they align."
}

if ($primary.ServiceFound -and $primary.WalLevel -and $primary.WalLevel -ne 'replica') {
    Add-Issue -Issues $issues -Message "Primary host $PrimaryHost has wal_level=$($primary.WalLevel). It must be replica for streaming standby support."
}

if ($primary.ServiceFound -and ($primary.MaxWalSenders -as [int]) -lt 1) {
    Add-Issue -Issues $issues -Message "Primary host $PrimaryHost has max_wal_senders=$($primary.MaxWalSenders). It must be at least 1 for replication."
}

if ($primary.ServiceFound -and ($primary.MaxReplicationSlots -as [int]) -lt 1) {
    Add-Issue -Issues $issues -Message "Primary host $PrimaryHost has max_replication_slots=$($primary.MaxReplicationSlots). It must be at least 1 for a retained standby slot."
}

if ($standby.ServiceFound -and ($standby.DatabaseRecoveryState -eq 't' -or $standby.StandbySignalExists) -and [string]::IsNullOrWhiteSpace($standby.PrimaryConnInfo)) {
    Add-Issue -Issues $issues -Message "Standby host $StandbyHost does not have primary_conninfo configured in postgresql.conf."
}

$ready = $issues.Count -eq 0

$recommendedAction = if ($primary.MajorVersion -and $standby.MajorVersion -and $primary.MajorVersion -ne $standby.MajorVersion) {
    "Run docs/operations/postgresql-primary-upgrade-10.10.10.9-17-to-18.md or reinstall the standby to the primary's major version before seeding."
}
elseif ($ready) {
    "Hosts look ready for docs/operations/postgresql-standby-seeding-10.10.10.58.md and then for multi-host ShopInventory connection strings."
}
else {
    "Fix the reported blockers, rerun this script, then proceed with the standby seeding runbook."
}

$summary = [pscustomobject]@{
    ReadyForStandbySeeding = $ready
    PrimaryHost = $PrimaryHost
    StandbyHost = $StandbyHost
    Port = $Port
    Primary = $primary
    Standby = $standby
    Issues = @($issues)
    RecommendedAction = $recommendedAction
}

Write-Host ''
Write-Host 'PostgreSQL HA readiness summary' -ForegroundColor Cyan
Write-Host ('-' * 32)
Write-Host "Primary version: $($primary.MajorVersion)"
Write-Host "Standby version: $($standby.MajorVersion)"
Write-Host "Primary service: $($primary.ServiceName) ($($primary.ServiceState))"
Write-Host "Standby service: $($standby.ServiceName) ($($standby.ServiceState))"
Write-Host "Standby -> Primary TCP ${Port}: $($standby.ProbeTargetReachable)"
Write-Host "Primary -> Standby TCP ${Port}: $($primary.ProbeTargetReachable)"
Write-Host "Ready for standby seeding: $ready"

if ($issues.Count -gt 0) {
    Write-Host ''
    Write-Host 'Blockers:' -ForegroundColor Yellow
    foreach ($issue in $issues) {
        Write-Host "- $issue"
    }
}

Write-Host ''
Write-Host "Recommended action: $recommendedAction" -ForegroundColor Green
Write-Host ''

$summary