param(
    [Parameter(Mandatory = $true)]
    [string]$PrimaryHost,
    [Parameter(Mandatory = $true)]
    [string]$StandbyHost,
    [Parameter(Mandatory = $true)]
    [string]$Username,
    [Parameter(Mandatory = $true)]
    [string]$Password,
    [string]$ApiDatabase = "shopinventory",
    [string]$WebDatabase = "shopinventoryweb",
    [int]$Port = 5432,
    [int]$MaximumPoolSize = 100,
    [int]$MinimumPoolSize = 10,
    [int]$ConnectionIdleLifetime = 300,
    [int]$ConnectionPruningInterval = 10,
    [int]$Timeout = 30,
    [int]$CommandTimeout = 60,
    [int]$Keepalive = 60,
    [int]$ReadBufferSize = 16384,
    [int]$WriteBufferSize = 16384
)

function New-HaConnectionString {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Database
    )

    $builder = New-Object System.Data.Common.DbConnectionStringBuilder
    $builder['Host'] = "$PrimaryHost,$StandbyHost"
    $builder['Port'] = $Port
    $builder['Database'] = $Database
    $builder['Username'] = $Username
    $builder['Password'] = $Password
    $builder['Target Session Attributes'] = 'read-write'
    $builder['Load Balance Hosts'] = 'false'
    $builder['Host Recheck Seconds'] = 5
    $builder['Connection Idle Lifetime'] = $ConnectionIdleLifetime
    $builder['Connection Pruning Interval'] = $ConnectionPruningInterval
    $builder['Timeout'] = $Timeout
    $builder['Command Timeout'] = $CommandTimeout
    $builder['Keepalive'] = $Keepalive
    $builder['Read Buffer Size'] = $ReadBufferSize
    $builder['Write Buffer Size'] = $WriteBufferSize
    $builder['Maximum Pool Size'] = $MaximumPoolSize
    $builder['Minimum Pool Size'] = $MinimumPoolSize

    return $builder.ConnectionString
}

$apiConnectionString = New-HaConnectionString -Database $ApiDatabase
$webConnectionString = New-HaConnectionString -Database $WebDatabase

[pscustomobject]@{
    PrimaryHost                 = $PrimaryHost
    StandbyHost                 = $StandbyHost
    ApiDbConnectionString       = $apiConnectionString
    WebDbConnectionString       = $webConnectionString
    UpdateProductionCommandHint = '.\Update-Production.ps1 -DeployTarget Both -ApiDbConnectionString "<api-connection-string>" -WebDbConnectionString "<web-connection-string>"'
}