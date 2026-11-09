<#
.SYNOPSIS
    Shared helpers for the PostgreSQL backup scripts. Dot-source it; it does nothing on its own.

.DESCRIPTION
    Used by Invoke-PostgresNightlyDump.ps1, Test-PostgresDumpRestore.ps1 and
    Register-PostgresNightlyDump.ps1. Windows PowerShell 5.1 compatible, because that is what the
    production server runs.

    Credentials come from the live API site's web.config on every run, so a rotated database
    password is picked up without touching the scheduled task. The password is only ever placed
    in PGPASSWORD for the duration of one pg_* call and is never written to a log.
#>

Set-StrictMode -Version 2.0

# A dump set folder is named by the UTC time the run started. Pruning only ever touches folders
# with this exact shape that also hold this tool's manifest.
$script:DumpSetNamePattern = '^\d{8}-\d{6}Z$'
$script:ManifestName = 'manifest.json'

function Split-NameList([string[]]$Names) {
    # powershell.exe -File passes -Databases A,B as the single string "A,B" (only -Command parses
    # arrays), and a scheduled task runs with -File.
    return @($Names | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

function Get-PgConnectionParts([string]$ConnectionString) {
    $parts = @{}
    foreach ($pair in $ConnectionString -split ';') {
        $index = $pair.IndexOf('=')
        if ($index -lt 1) { continue }
        $key = $pair.Substring(0, $index).Trim().ToLowerInvariant()
        $parts[$key] = $pair.Substring($index + 1).Trim()
    }

    $pgHost = $parts['host']; if (-not $pgHost) { $pgHost = $parts['server'] }
    $user = $null
    foreach ($key in @('username', 'user id', 'userid', 'user name', 'user')) {
        if ($parts[$key]) { $user = $parts[$key]; break }
    }
    $port = $parts['port']; if (-not $port) { $port = '5432' }

    return [pscustomobject]@{
        # A multi-host string lists the primary first; dumps always read from that one.
        Host     = (($pgHost -split ',')[0]).Trim()
        Port     = $port
        Database = $parts['database']
        User     = $user
        Password = $parts['password']
    }
}

function Find-SiteSetting([string]$SitePath, [string]$EnvVarName, [string]$JsonSection, [string]$JsonKey) {
    # web.config environment variable first (that is how production is configured), then
    # appsettings.Production.json.
    $webConfig = Join-Path $SitePath 'web.config'
    if (Test-Path $webConfig) {
        try {
            [xml]$xml = Get-Content -Path $webConfig -Raw
            $node = $xml.SelectSingleNode("//environmentVariable[@name='$EnvVarName']")
            if ($node -and $node.value) { return [pscustomobject]@{ Value = $node.value; Source = $webConfig } }
        } catch { Write-Warning "Could not parse $webConfig : $($_.Exception.Message)" }
    }
    $appSettings = Join-Path $SitePath 'appsettings.Production.json'
    if (Test-Path $appSettings) {
        try {
            $json = Get-Content -Path $appSettings -Raw | ConvertFrom-Json
            $section = $json.$JsonSection
            if ($section -and $section.$JsonKey) { return [pscustomobject]@{ Value = $section.$JsonKey; Source = $appSettings } }
        } catch { Write-Warning "Could not parse $appSettings : $($_.Exception.Message)" }
    }
    return $null
}

function Get-LiveSitePaths([string]$BasePath, [int]$PublicPort) {
    # The slot IIS serves on the public port is the live one and is read first: the base folder
    # and the idle slot can hold stale web.config files (see docs/operations/disaster-recovery.md).
    $candidates = @(@($BasePath, "$BasePath-Blue", "$BasePath-Green") | Where-Object { Test-Path $_ })
    if (-not $candidates) { return @() }
    if (-not (Get-Module -ListAvailable -Name WebAdministration)) { return $candidates }
    try {
        Import-Module WebAdministration -ErrorAction Stop
        foreach ($site in (Get-Website)) {
            $bindings = @($site.bindings.Collection | ForEach-Object { $_.bindingInformation })
            if (-not ($bindings | Where-Object { $_ -match ":$PublicPort`:" })) { continue }
            $path = [Environment]::ExpandEnvironmentVariables($site.physicalPath).TrimEnd('\')
            if ($candidates -contains $path) {
                return @($path) + @($candidates | Where-Object { $_ -ne $path })
            }
        }
    } catch { }
    return $candidates
}

function Resolve-PgConnection {
    param(
        [string]$ConnectionString,
        [string]$ApiSitePath = 'C:\inetpub\ShopInventory-API',
        [int]$ApiPublicPort = 5106
    )
    if ($ConnectionString) {
        return [pscustomobject]@{ Parts = (Get-PgConnectionParts $ConnectionString); Source = '-ConnectionString' }
    }
    foreach ($site in (Get-LiveSitePaths $ApiSitePath $ApiPublicPort)) {
        $setting = Find-SiteSetting $site 'ConnectionStrings__DefaultConnection' 'ConnectionStrings' 'DefaultConnection'
        if ($setting) {
            return [pscustomobject]@{ Parts = (Get-PgConnectionParts $setting.Value); Source = $setting.Source }
        }
    }
    throw "No API connection string found under $ApiSitePath (or its -Blue/-Green slots). Pass -ConnectionString."
}

function Find-PgBin([string]$PgBin) {
    if ($PgBin) {
        if (-not (Test-Path (Join-Path $PgBin 'pg_dump.exe'))) { throw "pg_dump.exe not found in $PgBin" }
        return $PgBin
    }
    # Newest major first: a newer pg_dump reads an older server, never the other way round.
    $bin = @(Get-ChildItem 'C:\Program Files\PostgreSQL' -Directory -ErrorAction SilentlyContinue |
        Sort-Object { [int]($_.Name -replace '\D', '0') } -Descending |
        ForEach-Object { Join-Path $_.FullName 'bin' } |
        Where-Object { Test-Path (Join-Path $_ 'pg_dump.exe') })
    if ($bin.Count -eq 0) { throw 'pg_dump.exe not found under C:\Program Files\PostgreSQL. Pass -PgBin.' }
    return $bin[0]
}

function Invoke-PgTool {
    <#
        Runs one pg_* executable with the password in PGPASSWORD for the duration of the call only.
        Returns exit code and combined output. -w means it never prompts: a wrong password fails.
    #>
    param(
        [Parameter(Mandatory)] [string]$Exe,
        [Parameter(Mandatory)] $Connection,
        [Parameter(Mandatory)] [string[]]$Arguments,
        [switch]$ReadOnly
    )
    $previousOptions = $env:PGOPTIONS
    $previousErrorAction = $ErrorActionPreference
    try {
        $env:PGPASSWORD = $Connection.Password
        $env:PGCONNECT_TIMEOUT = '15'
        if ($ReadOnly) { $env:PGOPTIONS = '-c default_transaction_read_only=on' }
        # Native stderr lines become ErrorRecords under 2>&1; with Stop they would throw before
        # the exit code is read.
        $ErrorActionPreference = 'Continue'
        $connectionArgs = @('-h', $Connection.Host, '-p', $Connection.Port, '-U', $Connection.User, '-w')
        $output = & $Exe @connectionArgs @Arguments 2>&1 | ForEach-Object { "$_" }
        return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = @($output) }
    } finally {
        $ErrorActionPreference = $previousErrorAction
        Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
        Remove-Item Env:\PGCONNECT_TIMEOUT -ErrorAction SilentlyContinue
        $env:PGOPTIONS = $previousOptions
    }
}

function Invoke-PgScalar {
    param([Parameter(Mandatory)] [string]$PgBin, [Parameter(Mandatory)] $Connection,
          [Parameter(Mandatory)] [string]$Database, [Parameter(Mandatory)] [string]$Sql, [switch]$ReadOnly)
    # SQL goes through a file: Windows PowerShell 5.1 strips embedded double quotes from a native
    # argument, which folds PascalCase identifiers to lowercase.
    $sqlPath = [IO.Path]::Combine([IO.Path]::GetTempPath(), "pgbackup-$([guid]::NewGuid().ToString('N')).sql")
    try {
        [IO.File]::WriteAllText($sqlPath, $Sql, (New-Object Text.UTF8Encoding $false))
        $result = Invoke-PgTool -Exe (Join-Path $PgBin 'psql.exe') -Connection $Connection -ReadOnly:$ReadOnly `
            -Arguments @('-d', $Database, '-X', '-A', '-t', '-q', '-v', 'ON_ERROR_STOP=1', '-f', $sqlPath)
        if ($result.ExitCode -ne 0) {
            throw "psql failed on $Database (exit $($result.ExitCode)): $(($result.Output | Select-Object -First 5) -join ' | ')"
        }
        return @($result.Output | Where-Object { $_ -ne '' })
    } finally {
        Remove-Item $sqlPath -ErrorAction SilentlyContinue
    }
}

function Get-FileSha256([string]$Path) {
    # .NET rather than Get-FileHash: a 5.1 process started from PowerShell 7 inherits 7's module
    # path and cannot load Get-FileHash. Plain .NET works under any host.
    $sha = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::OpenRead($Path)
    try {
        return ([BitConverter]::ToString($sha.ComputeHash($stream)) -replace '-', '').ToLowerInvariant()
    } finally {
        $stream.Dispose()
        $sha.Dispose()
    }
}

function Write-BackupEvent {
    <#
        Writes to the Application event log under source ShopInventoryBackup, so a failed night is
        visible to anything that watches the event log. Never fails the run: creating the source
        needs elevation the first time, and a missing source must not hide the real result.
    #>
    param([int]$EventId, [ValidateSet('Information', 'Warning', 'Error')] [string]$EntryType, [string]$Message)
    try {
        if (-not [Diagnostics.EventLog]::SourceExists('ShopInventoryBackup')) {
            New-EventLog -LogName Application -Source 'ShopInventoryBackup' -ErrorAction Stop
        }
        Write-EventLog -LogName Application -Source 'ShopInventoryBackup' -EventId $EventId -EntryType $EntryType -Message $Message -ErrorAction Stop
    } catch {
        Write-Host "  (event log not written: $($_.Exception.Message))"
    }
}

function Get-DumpSets([string]$Root) {
    # Every folder this tool wrote, oldest first. A folder without a manifest is not ours.
    if (-not (Test-Path $Root)) { return @() }
    return @(Get-ChildItem -Path $Root -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match $script:DumpSetNamePattern -and (Test-Path (Join-Path $_.FullName $script:ManifestName)) } |
        Sort-Object Name)
}

function Read-DumpManifest([string]$SetPath) {
    return Get-Content -Path (Join-Path $SetPath $script:ManifestName) -Raw | ConvertFrom-Json
}
