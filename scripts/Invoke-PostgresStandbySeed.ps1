[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PrimaryHost,

    [Parameter(Mandatory = $true)]
    [string]$StandbyHost,

    [Parameter(Mandatory = $true)]
    [System.Management.Automation.PSCredential]$StandbyCredential,

    [Parameter(Mandatory = $true)]
    [string]$ReplicationPassword,

    [Parameter(Mandatory = $true)]
    [string]$PostgresSuperuserPassword,

    [string]$ReplicationUser = 'repl_user',

    [string]$ReplicationSlot = 'shopinventory_standby',

    [string]$PostgresSuperuser = 'postgres',

    [string]$PgVersion = '17',

    [int]$Port = 5432
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptBlock = {
    param(
        [string]$PrimaryHost,
        [string]$ReplicationUser,
        [string]$ReplicationPassword,
        [string]$ReplicationSlot,
        [string]$PostgresSuperuser,
        [string]$PostgresSuperuserPassword,
        [string]$PgVersion,
        [int]$Port
    )

    Set-StrictMode -Version Latest
    $ErrorActionPreference = 'Stop'

    $pgRoot = "C:\Program Files\PostgreSQL\$PgVersion"
    $pgBin = Join-Path $pgRoot 'bin'
    $pgData = Join-Path $pgRoot 'data'
    $pgService = "postgresql-x64-$PgVersion"
    $backupStamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $oldDataPath = "$pgData.pre-ha-$backupStamp"

    $service = Get-CimInstance Win32_Service |
        Where-Object { $_.Name -eq $pgService } |
        Select-Object -First 1

    if (-not $service) {
        throw "PostgreSQL service $pgService was not found on $env:COMPUTERNAME."
    }

    $serviceAccount = $service.StartName

    Stop-Service -Name $pgService -Force -ErrorAction SilentlyContinue

    if (Test-Path $pgData) {
        Rename-Item -Path $pgData -NewName (Split-Path $oldDataPath -Leaf)
    }

    $stdoutPath = Join-Path $env:TEMP 'shopinventory-pg-basebackup-stdout.log'
    $stderrPath = Join-Path $env:TEMP 'shopinventory-pg-basebackup-stderr.log'

    Remove-Item $stdoutPath, $stderrPath -ErrorAction SilentlyContinue

    $env:PGPASSWORD = $ReplicationPassword
    try {
        $pgBaseBackupArgs = @(
            '-h', $PrimaryHost,
            '-p', $Port,
            '-U', $ReplicationUser,
            '-D', $pgData,
            '-R',
            '-X', 'stream',
            '-C',
            '-S', $ReplicationSlot,
            '-P'
        )

        $quotedArgs = foreach ($arg in $pgBaseBackupArgs) {
            if ($arg -match '\s') {
                '"{0}"' -f $arg
            }
            else {
                $arg
            }
        }

        $pgBaseBackupCommand = '"{0}" {1} 1>"{2}" 2>"{3}"' -f (Join-Path $pgBin 'pg_basebackup.exe'), ($quotedArgs -join ' '), $stdoutPath, $stderrPath

        cmd.exe /c $pgBaseBackupCommand | Out-Null
        $processExitCode = $LASTEXITCODE

        $stdoutText = if (Test-Path $stdoutPath) { (Get-Content $stdoutPath | Out-String -Width 220).Trim() } else { '' }
        $stderrText = if (Test-Path $stderrPath) { (Get-Content $stderrPath | Out-String -Width 220).Trim() } else { '' }
        $backupOutput = @($stdoutText, $stderrText) -join [Environment]::NewLine
        $backupOutput = $backupOutput.Trim()

        if ($processExitCode -ne 0) {
            throw "pg_basebackup failed with exit code $processExitCode.`n$backupOutput"
        }
    }
    finally {
        Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
        Remove-Item $stdoutPath, $stderrPath -ErrorAction SilentlyContinue
    }

    $pgHbaContent = @'
# ShopInventory pg_hba.conf standby template
# Host: 10.10.10.58
# Purpose: client authentication for the passive standby and for the same node
#          after promotion during failover

# TYPE  DATABASE                        USER            ADDRESS         METHOD

# Loopback administration and local service access on the standby host
host    all                             all             127.0.0.1/32   scram-sha-256
host    all                             all             ::1/128        scram-sha-256

# After promotion, allow the former primary on 10.10.10.9 to reconnect here as
# the new standby using the replication user
host    replication                     repl_user       10.10.10.9/32  scram-sha-256

# Optional loopback replication testing on the standby host itself
host    replication                     repl_user       127.0.0.1/32   scram-sha-256
host    replication                     repl_user       ::1/128        scram-sha-256

# ShopInventory application access from either host. This keeps failover simple
# because the promoted standby can accept the same app credentials immediately.
host    shopinventory,shopinventoryweb  shopinventory   10.10.10.9/32  scram-sha-256
host    shopinventory,shopinventoryweb  shopinventory   10.10.10.58/32 scram-sha-256

# Administrative access from the two database hosts
host    all                             postgres        10.10.10.9/32  scram-sha-256
host    all                             postgres        10.10.10.58/32 scram-sha-256
'@

    Set-Content -Path (Join-Path $pgData 'pg_hba.conf') -Value $pgHbaContent -Encoding ASCII

    icacls $pgData /grant "${serviceAccount}:(OI)(CI)F" /T | Out-Null

    Start-Service -Name $pgService

    $env:PGPASSWORD = $PostgresSuperuserPassword
    try {
        [pscustomobject]@{
            HostName = $env:COMPUTERNAME
            OldDataPath = $oldDataPath
            ServiceAccount = $serviceAccount
            BackupOutput = $backupOutput
            StandbySignal = [bool](Test-Path (Join-Path $pgData 'standby.signal'))
            AutoConf = (Get-Content (Join-Path $pgData 'postgresql.auto.conf') | Out-String -Width 220).Trim()
            RecoveryState = (& (Join-Path $pgBin 'psql.exe') -U $PostgresSuperuser -h 127.0.0.1 -p $Port -d postgres -Atqc 'SELECT pg_is_in_recovery();' 2>&1 | Out-String).Trim()
            Version = (& (Join-Path $pgBin 'psql.exe') -U $PostgresSuperuser -h 127.0.0.1 -p $Port -d postgres -Atqc 'SELECT version();' 2>&1 | Out-String).Trim()
        }
    }
    finally {
        Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
    }
}

$invokeCommandArgs = @{
    ComputerName = $StandbyHost
    Credential = $StandbyCredential
    ScriptBlock = $scriptBlock
    ArgumentList = @($PrimaryHost, $ReplicationUser, $ReplicationPassword, $ReplicationSlot, $PostgresSuperuser, $PostgresSuperuserPassword, $PgVersion, $Port)
}

Invoke-Command @invokeCommandArgs