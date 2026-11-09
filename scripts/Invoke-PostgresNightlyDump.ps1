<#
.SYNOPSIS
    Dumps the production PostgreSQL databases to a dated set, copies it off the server, and prunes
    old sets. Meant to run nightly from the task Register-PostgresNightlyDump.ps1 creates.

.DESCRIPTION
    Closes open item 1 of docs/operations/disaster-recovery.md: until this runs there is no restore
    point for bad data, because the streaming standby copies a bad write within milliseconds.

    For each database:
      1. pg_dump -Fc into a local staging folder (fast disk, so the dump's snapshot is held briefly).
      2. pg_restore --list on the file, which proves the archive is complete and readable.
      3. SHA-256 of the file.
    Then the set is copied to -Destination, every file's hash is checked there, and only then is
    manifest.json written. A folder without a manifest is an incomplete copy and is never counted
    or pruned as a backup.

    Pruning runs only after a fully successful night, removes only folders this tool wrote, and
    always leaves at least -MinimumSetsKept sets, however old, so a run of failures can never
    delete the last good backup.

    What it reads: the API site's web.config for the connection string, every row of the named
    databases (pg_dump runs in a read-only snapshot). What it writes: files under -StagingRoot,
    -Destination and -LogRoot, one Application event log entry (source ShopInventoryBackup,
    1000 = success, 1001 = failure), and last-run.json in both roots.

    pg_dump takes ACCESS SHARE locks, so an EF migration that alters a table waits until the dump
    finishes. Schedule it away from the 19:30 deploy window.

.EXAMPLE
    .\Invoke-PostgresNightlyDump.ps1 -Destination '\\10.10.10.254\svr_backup\ShopInventory-pgdump'

.EXAMPLE
    # Include the Workshop app's database once its owner agrees.
    .\Invoke-PostgresNightlyDump.ps1 -Destination 'D:\pgdump' -Databases ShopInventory, ShopInventoryWeb, workshopdb
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Destination,

    # ShopInventoryWeb is the capitalised one; the lowercase shopinventoryweb is a stale copy.
    [string[]]$Databases = @('ShopInventory', 'ShopInventoryWeb'),

    [ValidateRange(1, 365)]
    [int]$KeepDays = 14,

    # Sets kept at the destination no matter how old they are.
    [ValidateRange(1, 100)]
    [int]$MinimumSetsKept = 3,

    [string]$StagingRoot = 'C:\ShopInventoryBackups\staging',

    # Local sets kept in staging after a successful copy. They make a restore fast when the share
    # is slow or down; each costs one night's dump size on C:.
    [ValidateRange(0, 30)]
    [int]$KeepLocalSets = 1,

    [string]$LogRoot = 'C:\ShopInventoryBackups\logs',

    # Overrides for testing or for a non-standard install. Normally both are found.
    [string]$ConnectionString,
    [string]$PgBin,
    [string]$ApiSitePath = 'C:\inetpub\ShopInventory-API',
    [int]$ApiPublicPort = 5106
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'PostgresBackup.Common.ps1')
$Databases = Split-NameList $Databases

$startedUtc = [DateTime]::UtcNow
$setName = $startedUtc.ToString('yyyyMMdd-HHmmss') + 'Z'
New-Item -ItemType Directory -Force -Path $LogRoot | Out-Null
$transcriptPath = Join-Path $LogRoot "pgdump-$setName.log"
Start-Transcript -Path $transcriptPath | Out-Null

$status = [ordered]@{
    result         = 'failed'
    set            = $setName
    startedUtc     = $startedUtc.ToString('o')
    finishedUtc    = $null
    destination    = $Destination
    databases      = @()
    pruned         = @()
    error          = $null
    log            = $transcriptPath
}

function Write-Status {
    $status.finishedUtc = [DateTime]::UtcNow.ToString('o')
    $json = $status | ConvertTo-Json -Depth 6
    foreach ($root in @($StagingRoot, $Destination) | Select-Object -Unique) {
        try {
            if (Test-Path $root) {
                [IO.File]::WriteAllText((Join-Path $root 'last-run.json'), $json, (New-Object Text.UTF8Encoding $false))
            }
        } catch { Write-Host "  (could not write last-run.json to $root : $($_.Exception.Message))" }
    }
}

$exitCode = 1
try {
    Write-Host "Set         : $setName"
    Write-Host "Computer    : $env:COMPUTERNAME as $env:USERDOMAIN\$env:USERNAME"

    $pgBinPath = Find-PgBin $PgBin
    $resolved = Resolve-PgConnection -ConnectionString $ConnectionString -ApiSitePath $ApiSitePath -ApiPublicPort $ApiPublicPort
    $connection = $resolved.Parts
    $pgDump = Join-Path $pgBinPath 'pg_dump.exe'
    $pgRestore = Join-Path $pgBinPath 'pg_restore.exe'
    $dumpVersion = (& $pgDump --version) -join ' '
    Write-Host "pg_dump     : $pgDump ($dumpVersion)"
    Write-Host "Connection  : $($connection.Host):$($connection.Port) as $($connection.User), from $($resolved.Source) (password not shown)"

    # One read gives the server's identity and every database's size.
    $serverRows = Invoke-PgScalar -PgBin $pgBinPath -Connection $connection -Database $connection.Database -ReadOnly -Sql @'
SELECT 'server|' || current_setting('server_version') || '|' || pg_is_in_recovery()::text;
SELECT 'db|' || datname || '|' || pg_database_size(datname)::text FROM pg_database WHERE NOT datistemplate;
'@
    $serverLine = @($serverRows | Where-Object { $_ -like 'server|*' })[0] -split '\|'
    $serverVersion = $serverLine[1]
    # Ordinal: a PowerShell hashtable ignores case, and PostgreSQL database names do not.
    $sizes = New-Object 'System.Collections.Generic.Dictionary[string,long]' ([StringComparer]::Ordinal)
    foreach ($row in ($serverRows | Where-Object { $_ -like 'db|*' })) {
        $fields = $row -split '\|'
        $sizes[$fields[1]] = [int64]$fields[2]
    }
    Write-Host "Server      : PostgreSQL $serverVersion, standby: $($serverLine[2])"

    # Names are case-sensitive in PostgreSQL. A misspelt one fails the night rather than
    # silently producing a set that lacks it.
    $missing = @($Databases | Where-Object { -not $sizes.ContainsKey($_) })
    if ($missing.Count -gt 0) {
        throw "Database(s) not on the server: $($missing -join ', '). Present: $((@($sizes.Keys) | Sort-Object) -join ', ')"
    }

    # A custom-format dump is compressed; the database size is a safe ceiling for it.
    $needed = [int64](($Databases | ForEach-Object { $sizes[$_] } | Measure-Object -Sum).Sum)
    New-Item -ItemType Directory -Force -Path $StagingRoot | Out-Null
    $stagingDrive = (Get-Item $StagingRoot).PSDrive
    $free = [int64]$stagingDrive.Free
    Write-Host ("Staging     : {0} ({1:N1} GB free; databases total {2:N1} GB)" -f $StagingRoot, ($free / 1GB), ($needed / 1GB))
    if ($free -lt ($needed + 5GB)) {
        throw ("Not enough free space on {0}: {1:N1} GB free, need the databases' {2:N1} GB plus 5 GB headroom. The primary stops when its disk fills, so this run will not risk it." -f $stagingDrive.Root, ($free / 1GB), ($needed / 1GB))
    }

    $setStaging = Join-Path $StagingRoot $setName
    New-Item -ItemType Directory -Force -Path $setStaging | Out-Null

    # Find out now whether the destination is writable, so the reason is clear. An unreachable
    # share does not stop the dump: a set on this server's disk is still a restore point for bad
    # data, which is the gap this closes. The night is still reported as failed.
    $sameRoot = $false
    try {
        $sameRoot = [IO.Path]::GetFullPath($Destination).TrimEnd('\') -eq [IO.Path]::GetFullPath($StagingRoot).TrimEnd('\')
    } catch { }
    $destinationError = $null
    if (-not $sameRoot) {
        try {
            if (-not (Test-Path -LiteralPath $Destination)) {
                New-Item -ItemType Directory -Force -Path $Destination -ErrorAction Stop | Out-Null
            }
            $probe = Join-Path $Destination ".write-probe-$setName"
            [IO.File]::WriteAllText($probe, 'probe')
            Remove-Item -LiteralPath $probe -Force
            Write-Host "Destination : $Destination (writable)"
        } catch {
            $destinationError = "Destination $Destination is not writable as $env:USERDOMAIN\$env:USERNAME : $($_.Exception.Message)"
            Write-Host "Destination : NOT WRITABLE - $destinationError" -ForegroundColor Red
            Write-Host '              Dumping to staging anyway; the night will be reported as failed.' -ForegroundColor Yellow
        }
    }

    $entries = @()
    foreach ($database in $Databases) {
        $file = Join-Path $setStaging "$database.dump"
        Write-Host ''
        Write-Host "--- $database ($([math]::Round($sizes[$database] / 1MB, 1)) MB on disk)"
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $dump = Invoke-PgTool -Exe $pgDump -Connection $connection -ReadOnly `
            -Arguments @('-d', $database, '-Fc', '-Z', '6', '--no-password', '-f', $file)
        $clock.Stop()
        if ($dump.ExitCode -ne 0) {
            throw "pg_dump of $database failed (exit $($dump.ExitCode)): $(($dump.Output | Select-Object -Last 5) -join ' | ')"
        }
        $dump.Output | ForEach-Object { Write-Host "  $_" }

        # Reading the table of contents back proves the archive is whole: a truncated custom-format
        # file fails here. It does not prove the data restores; Test-PostgresDumpRestore.ps1 does.
        $list = & $pgRestore --list $file 2>&1 | ForEach-Object { "$_" }
        if ($LASTEXITCODE -ne 0) {
            throw "pg_restore --list could not read $file (exit $LASTEXITCODE): $(($list | Select-Object -Last 3) -join ' | ')"
        }
        $tableData = @($list | Where-Object { $_ -match '^\d+;\s+\d+\s+\d+\s+TABLE DATA ' }).Count
        $tocEntries = @($list | Where-Object { $_ -match '^\d+;' }).Count

        $bytes = (Get-Item $file).Length
        $hash = Get-FileSha256 $file
        Write-Host ("  {0:N1} MB in {1:N0} s; {2} TOC entries, {3} tables with data; sha256 {4}" -f ($bytes / 1MB), $clock.Elapsed.TotalSeconds, $tocEntries, $tableData, $hash.Substring(0, 16))

        $entries += [ordered]@{
            database        = $database
            file            = "$database.dump"
            bytes           = $bytes
            sha256          = $hash
            databaseBytes   = $sizes[$database]
            seconds         = [math]::Round($clock.Elapsed.TotalSeconds, 1)
            tocEntries      = $tocEntries
            tablesWithData  = $tableData
        }
    }
    $status.databases = $entries

    $manifest = [ordered]@{
        tool            = 'Invoke-PostgresNightlyDump.ps1'
        set             = $setName
        startedUtc      = $startedUtc.ToString('o')
        finishedUtc     = [DateTime]::UtcNow.ToString('o')
        computer        = $env:COMPUTERNAME
        server          = "$($connection.Host):$($connection.Port)"
        serverVersion   = $serverVersion
        pgDumpVersion   = $dumpVersion
        format          = 'custom (-Fc), restore with pg_restore of the same or a newer major version'
        databases       = $entries
    }
    $manifestJson = $manifest | ConvertTo-Json -Depth 6

    # The local set is complete: mark it, so a restore can use it even if the copy below fails.
    [IO.File]::WriteAllText((Join-Path $setStaging $script:ManifestName), $manifestJson, (New-Object Text.UTF8Encoding $false))
    if ($destinationError) {
        throw "Dumped to $setStaging on this server only. $destinationError"
    }

    # Copy, then check every hash at the far end, then write the manifest there. The manifest is
    # what marks a set complete, so it goes last.
    if ($sameRoot) {
        Write-Host ''
        Write-Host 'Destination is the staging folder: nothing to copy. This set is on the database server only.' -ForegroundColor Yellow
        $setDestination = $setStaging
    } else {
        Write-Host ''
        Write-Host "--- copying to $Destination"
        $setDestination = Join-Path $Destination $setName
        New-Item -ItemType Directory -Force -Path $setDestination | Out-Null
        foreach ($entry in $entries) {
            $source = Join-Path $setStaging $entry.file
            $target = Join-Path $setDestination $entry.file
            $clock = [Diagnostics.Stopwatch]::StartNew()
            Copy-Item -Path $source -Destination $target -Force
            $clock.Stop()
            $remoteHash = Get-FileSha256 $target
            if ($remoteHash -ne $entry.sha256) {
                throw "Copy of $($entry.file) to $setDestination does not match: sha256 $remoteHash, expected $($entry.sha256)."
            }
            Write-Host ("  {0} copied in {1:N0} s, hash checked" -f $entry.file, $clock.Elapsed.TotalSeconds)
        }
    }
    if (-not $sameRoot) {
        [IO.File]::WriteAllText((Join-Path $setDestination $script:ManifestName), $manifestJson, (New-Object Text.UTF8Encoding $false))
    }
    Write-Host "Set complete: $setDestination"

    # Prune the destination. Only after success, only our folders, never below the floor.
    # @() throughout: a function returning one folder hands back the folder, not an array, and
    # .Count on it fails under strict mode.
    $sets = @(Get-DumpSets $Destination)
    $cutoff = $startedUtc.AddDays(-$KeepDays).ToString('yyyyMMdd-HHmmss') + 'Z'
    $removable = @($sets | Select-Object -First ([math]::Max(0, $sets.Count - $MinimumSetsKept)) |
        Where-Object { [string]::CompareOrdinal($_.Name, $cutoff) -lt 0 })
    foreach ($old in $removable) {
        Remove-Item -Path $old.FullName -Recurse -Force
        $status.pruned += $old.Name
        Write-Host "  pruned $($old.Name) (older than $KeepDays days)"
    }
    $remaining = @(Get-DumpSets $Destination)
    Write-Host ("Destination now holds {0} set(s); oldest {1}" -f $remaining.Count, $remaining[0].Name)

    # Staging keeps only the newest -KeepLocalSets sets.
    if (-not $sameRoot) {
        $localSets = @(Get-DumpSets $StagingRoot)
        foreach ($old in ($localSets | Select-Object -First ([math]::Max(0, $localSets.Count - $KeepLocalSets)))) {
            Remove-Item -Path $old.FullName -Recurse -Force
            Write-Host "  removed local copy $($old.Name)"
        }
        # A staging folder left by a failed night has no manifest; clear any older than today's.
        Get-ChildItem -Path $StagingRoot -Directory |
            Where-Object { $_.Name -match $script:DumpSetNamePattern -and $_.Name -ne $setName -and -not (Test-Path (Join-Path $_.FullName $script:ManifestName)) } |
            ForEach-Object { Remove-Item -Path $_.FullName -Recurse -Force; Write-Host "  removed incomplete local folder $($_.Name)" }
    }

    # Logs older than 60 days.
    Get-ChildItem -Path $LogRoot -Filter 'pgdump-*.log' -File |
        Where-Object { $_.LastWriteTimeUtc -lt $startedUtc.AddDays(-60) } |
        Remove-Item -Force

    $status.result = 'succeeded'
    $exitCode = 0
    $summary = ($entries | ForEach-Object { '{0} {1:N1} MB' -f $_.database, ($_.bytes / 1MB) }) -join ', '
    Write-BackupEvent -EventId 1000 -EntryType Information -Message "PostgreSQL dump set $setName is complete at $setDestination : $summary."
} catch {
    $status.error = $_.Exception.Message
    Write-Host ''
    Write-Host "FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Write-BackupEvent -EventId 1001 -EntryType Error -Message "PostgreSQL dump set $setName FAILED: $($_.Exception.Message) Log: $transcriptPath"
} finally {
    Write-Status
    Write-Host ''
    Write-Host "Result: $($status.result). Log: $transcriptPath"
    Stop-Transcript | Out-Null
}
exit $exitCode
