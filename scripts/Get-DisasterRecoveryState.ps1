<#
.SYNOPSIS
    Reports what disaster-recovery protection the production box actually has. Changes nothing.

.DESCRIPTION
    Run on the production API server (10.10.10.9) in an elevated Windows PowerShell 5.1 session.
    It answers the questions the repo cannot:

      - Is anything taking database backups (scheduled tasks, backup agents, dump files on disk)?
      - Is WAL archiving on, and is it succeeding?
      - Is any standby replicating from this server?
      - What do Windows Server Backup and volume shadow copies hold?
      - Which nodes are in the Quartz cluster right now?
      - Is the in-app backup's cloud upload configured?

    Every SQL statement runs in a read-only transaction (PGOPTIONS default_transaction_read_only=on).
    The database password is read from the API site's web.config, used only for psql, and cleared
    afterwards. It is never printed. Output goes to the console and to a transcript file next to
    the script.

.EXAMPLE
    .\Get-DisasterRecoveryState.ps1
    .\Get-DisasterRecoveryState.ps1 -SkipFileSearch
#>
[CmdletBinding()]
param(
    # Folder holding the API site. The script also looks at the -Blue and -Green slots beside it.
    [string]$ApiSitePath = 'C:\inetpub\ShopInventory-API',
    [string]$WebSitePath = 'C:\inetpub\ShopInventory-Web',
    # Public ports. The slot IIS serves on these is the live one, and is read first: the base
    # folders and the idle slot can hold stale web.config files.
    [int]$ApiPublicPort = 5106,
    [int]$WebPublicPort = 5107,
    # The file search for dumps walks every fixed drive to this depth. It is the slow part.
    [int]$FileSearchDepth = 4,
    [switch]$SkipFileSearch
)

$ErrorActionPreference = 'Continue'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $scriptDir) { $scriptDir = (Get-Location).Path }
$transcriptPath = Join-Path $scriptDir "dr-state-$env:COMPUTERNAME-$stamp.txt"
Start-Transcript -Path $transcriptPath | Out-Null

function Write-Section([string]$title) {
    Write-Host ''
    Write-Host ('=' * 78) -ForegroundColor Cyan
    Write-Host "  $title" -ForegroundColor Cyan
    Write-Host ('=' * 78) -ForegroundColor Cyan
}

function Get-ConnectionStringParts([string]$connectionString) {
    $parts = @{}
    foreach ($pair in $connectionString -split ';') {
        $index = $pair.IndexOf('=')
        if ($index -lt 1) { continue }
        $key = $pair.Substring(0, $index).Trim().ToLowerInvariant()
        $parts[$key] = $pair.Substring($index + 1).Trim()
    }
    return $parts
}

function Find-SettingInSite([string]$sitePath, [string]$envVarName, [string]$jsonSection, [string]$jsonKey) {
    # web.config environment variable first (that is how production is configured), then
    # appsettings.Production.json.
    $webConfig = Join-Path $sitePath 'web.config'
    if (Test-Path $webConfig) {
        try {
            [xml]$xml = Get-Content -Path $webConfig -Raw
            $node = $xml.SelectSingleNode("//environmentVariable[@name='$envVarName']")
            if ($node -and $node.value) { return @{ Value = $node.value; Source = $webConfig } }
        } catch { Write-Host "  Could not parse $webConfig : $($_.Exception.Message)" -ForegroundColor Yellow }
    }
    $appSettings = Join-Path $sitePath 'appsettings.Production.json'
    if (Test-Path $appSettings) {
        try {
            $json = Get-Content -Path $appSettings -Raw | ConvertFrom-Json
            $section = $json.$jsonSection
            if ($section -and $section.$jsonKey) { return @{ Value = $section.$jsonKey; Source = $appSettings } }
        } catch { Write-Host "  Could not parse $appSettings : $($_.Exception.Message)" -ForegroundColor Yellow }
    }
    return $null
}

function Get-SitePaths([string]$basePath, [int]$publicPort) {
    $candidates = @(@($basePath, "$basePath-Blue", "$basePath-Green") | Where-Object { Test-Path $_ })
    $live = $null
    if (-not $candidates) { return $candidates }
    if (-not (Get-Module -ListAvailable -Name WebAdministration)) {
        Write-Host '  IIS WebAdministration module not available; cannot tell which slot is live.' -ForegroundColor Yellow
        return $candidates
    }
    try {
        Import-Module WebAdministration -ErrorAction Stop
        foreach ($site in (Get-Website)) {
            $bindings = @($site.bindings.Collection | ForEach-Object { $_.bindingInformation })
            if (-not ($bindings | Where-Object { $_ -match ":$publicPort`:" })) { continue }
            $path = [Environment]::ExpandEnvironmentVariables($site.physicalPath).TrimEnd('\')
            if ($candidates -contains $path) { $live = $path; break }
        }
    } catch { }
    if ($live) {
        Write-Host "  IIS serves :$publicPort from $live (read first)"
        return @($live) + @($candidates | Where-Object { $_ -ne $live })
    }
    Write-Host "  Could not tell which folder IIS serves on :$publicPort; reading $($candidates -join ', ') in that order." -ForegroundColor Yellow
    return $candidates
}

# ---------------------------------------------------------------------------------------------
Write-Section 'Host'
Write-Host "Computer : $env:COMPUTERNAME"
Write-Host "Run at   : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')"
Write-Host "User     : $env:USERDOMAIN\$env:USERNAME"
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Write-Host "Elevated : $isAdmin"
if (-not $isAdmin) { Write-Host '  Not elevated: wbadmin, vssadmin and some scheduled tasks will be missing.' -ForegroundColor Yellow }
try {
    $cs = Get-CimInstance Win32_ComputerSystem
    Write-Host "Model    : $($cs.Manufacturer) / $($cs.Model)   (a hypervisor name here means VM-level snapshots may exist outside this box)"
} catch { }

Write-Section 'Disks'
Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' |
    Select-Object DeviceID, VolumeName,
        @{ n = 'SizeGB'; e = { [math]::Round($_.Size / 1GB, 1) } },
        @{ n = 'FreeGB'; e = { [math]::Round($_.FreeSpace / 1GB, 1) } } |
    Format-Table -AutoSize | Out-String | Write-Host
Get-CimInstance Win32_MappedLogicalDisk -ErrorAction SilentlyContinue |
    Select-Object DeviceID, ProviderName | Format-Table -AutoSize | Out-String | Write-Host

# ---------------------------------------------------------------------------------------------
Write-Section 'PostgreSQL services and installs'
Get-Service -Name 'postgresql*' -ErrorAction SilentlyContinue |
    Select-Object Name, Status, StartType, DisplayName | Format-Table -AutoSize | Out-String | Write-Host
$pgInstalls = Get-ChildItem 'C:\Program Files\PostgreSQL' -Directory -ErrorAction SilentlyContinue
$pgInstalls | Select-Object Name, FullName | Format-Table -AutoSize | Out-String | Write-Host
# Indexed rather than Select-Object -First 1, which stops the pipeline and leaves a
# TerminatingError line in the Windows PowerShell 5.1 transcript.
$psql = @($pgInstalls |
    Sort-Object { [int]($_.Name -replace '\D', '0') } -Descending |
    ForEach-Object { Join-Path $_.FullName 'bin\psql.exe' } |
    Where-Object { Test-Path $_ })[0]
if ($psql) { Write-Host "psql     : $psql" } else { Write-Host 'psql.exe not found under C:\Program Files\PostgreSQL' -ForegroundColor Yellow }

# ---------------------------------------------------------------------------------------------
Write-Section 'Scheduled tasks that look like backups'
$backupPattern = 'pg_dump|pg_basebackup|pg_dumpall|backup|wbadmin|robocopy|postgres|veeam|dump'
try {
    $tasks = Get-ScheduledTask -ErrorAction Stop | Where-Object { $_.TaskPath -notlike '\Microsoft\*' }
    $hits = foreach ($task in $tasks) {
        $actionText = ($task.Actions | ForEach-Object { "$($_.Execute) $($_.Arguments)" }) -join ' | '
        if ("$($task.TaskName) $actionText" -match $backupPattern) {
            $info = $task | Get-ScheduledTaskInfo -ErrorAction SilentlyContinue
            [pscustomobject]@{
                Task       = "$($task.TaskPath)$($task.TaskName)"
                State      = $task.State
                LastRun    = $info.LastRunTime
                LastResult = $info.LastTaskResult
                NextRun    = $info.NextRunTime
                Action     = $actionText
            }
        }
    }
    if ($hits) { $hits | Format-List | Out-String | Write-Host }
    else { Write-Host "None of the $(@($tasks).Count) non-Microsoft scheduled tasks mention a backup tool." -ForegroundColor Yellow }
    Write-Host 'All non-Microsoft scheduled tasks, for reference:'
    $tasks | Select-Object TaskPath, TaskName, State | Format-Table -AutoSize | Out-String | Write-Host
} catch { Write-Host "Could not list scheduled tasks: $($_.Exception.Message)" -ForegroundColor Yellow }

Write-Section 'Nightly pg_dump (Register-PostgresNightlyDump.ps1)'
$nowUtc = [DateTime]::UtcNow
foreach ($name in @('PostgreSQL nightly dump', 'PostgreSQL restore check')) {
    $task = Get-ScheduledTask -TaskPath '\ShopInventory\' -TaskName $name -ErrorAction SilentlyContinue
    if (-not $task) { Write-Host "Task \ShopInventory\$name : NOT INSTALLED" -ForegroundColor Yellow; continue }
    $info = $task | Get-ScheduledTaskInfo -ErrorAction SilentlyContinue
    Write-Host "Task \ShopInventory\$name : $($task.State), runs as $($task.Principal.UserId), last run $($info.LastRunTime) result $($info.LastTaskResult), next $($info.NextRunTime)"
}
$lastRunPath = 'C:\ShopInventoryBackups\staging\last-run.json'
if (Test-Path $lastRunPath) {
    $lastRun = Get-Content $lastRunPath -Raw | ConvertFrom-Json
    $age = $nowUtc - [DateTime]::Parse($lastRun.finishedUtc).ToUniversalTime()
    $colour = if ($lastRun.result -eq 'succeeded' -and $age.TotalHours -le 26) { 'Green' } else { 'Red' }
    Write-Host ("Last dump  : {0}, set {1}, {2:N1} hours ago, to {3}" -f $lastRun.result, $lastRun.set, $age.TotalHours, $lastRun.destination) -ForegroundColor $colour
    if ($lastRun.error) { Write-Host "  error: $($lastRun.error)" -ForegroundColor Red }
    if ($age.TotalHours -gt 26) { Write-Host '  OVER 26 HOURS OLD: the nightly dump has stopped.' -ForegroundColor Red }
    foreach ($db in @($lastRun.databases)) { Write-Host ("  {0}: {1:N1} MB in {2} s" -f $db.database, ($db.bytes / 1MB), $db.seconds) }
    $checkPath = Join-Path "$($lastRun.destination)" 'last-restore-check.json'
    if (Test-Path $checkPath) {
        $check = Get-Content $checkPath -Raw | ConvertFrom-Json
        $checkAge = $nowUtc - [DateTime]::Parse($check.finishedUtc).ToUniversalTime()
        $colour = if ($check.result -eq 'passed' -and $checkAge.TotalDays -le 8) { 'Green' } else { 'Red' }
        Write-Host ("Last restore check: {0}, {1:N1} days ago, set {2}" -f $check.result, $checkAge.TotalDays, $check.set) -ForegroundColor $colour
    } else { Write-Host "Last restore check: none found at $checkPath" -ForegroundColor Yellow }
} else {
    Write-Host "No $lastRunPath : the nightly dump has never run on this server." -ForegroundColor Yellow
}

Write-Section 'Backup agents (services)'
$agents = Get-Service -ErrorAction SilentlyContinue | Where-Object {
    $_.Name -match 'veeam|acronis|backup|wbengine|arcserve|commvault|altaro|nakivo|cohesity|rubrik|azure.*recovery' -or
    $_.DisplayName -match 'veeam|acronis|backup|arcserve|commvault|altaro|nakivo|cohesity|rubrik'
}
if ($agents) { $agents | Select-Object Name, Status, StartType, DisplayName | Format-Table -AutoSize | Out-String | Write-Host }
else { Write-Host 'No backup agent service found.' -ForegroundColor Yellow }

Write-Section 'Windows Server Backup and shadow copies'
if ($isAdmin) {
    if (Get-Command wbadmin.exe -ErrorAction SilentlyContinue) {
        Write-Host '--- wbadmin get versions'
        & wbadmin.exe get versions 2>&1 | Out-String | Write-Host
    } else { Write-Host 'wbadmin not installed (Windows Server Backup feature absent).' }
    Write-Host '--- vssadmin list shadows'
    & vssadmin.exe list shadows 2>&1 | Out-String | Write-Host
} else { Write-Host 'Skipped: needs an elevated session.' -ForegroundColor Yellow }

# ---------------------------------------------------------------------------------------------
Write-Section 'App deploy backups and in-app backup folders'
Get-ChildItem 'C:\inetpub' -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like 'ShopInventory*' } |
    Select-Object Name, LastWriteTime | Format-Table -AutoSize | Out-String | Write-Host
foreach ($site in (Get-SitePaths $ApiSitePath $ApiPublicPort)) {
    $backupPathSetting = Find-SettingInSite $site 'Backup__Path' 'Backup' 'Path'
    $folder = Join-Path $site 'Backups'
    if ($backupPathSetting) {
        $folder = $backupPathSetting.Value
        if (-not [IO.Path]::IsPathRooted($folder)) { $folder = Join-Path $site $folder }
    }
    Write-Host "$site -> in-app backup folder: $folder"
    if (Test-Path $folder) {
        Get-ChildItem $folder -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending |
            Select-Object -First 10 Name, LastWriteTime, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 2) } } |
            Format-Table -AutoSize | Out-String | Write-Host
    } else { Write-Host '  (folder does not exist)' }

    $provider = Find-SettingInSite $site 'Backup__CloudStorage__Provider' 'Backup' 'CloudStorage'
    $bucket = Find-SettingInSite $site 'Backup__CloudStorage__BucketName' 'Backup' 'CloudStorage'
    $providerText = '(not set)'
    if ($provider) {
        if ($provider.Value -is [string]) { $providerText = $provider.Value }
        elseif ($provider.Value.Provider) { $providerText = $provider.Value.Provider }
    }
    $bucketText = '(not set)'
    if ($bucket) {
        if ($bucket.Value -is [string]) { $bucketText = $bucket.Value }
        elseif ($bucket.Value.BucketName) { $bucketText = $bucket.Value.BucketName }
    }
    Write-Host "  Cloud upload provider: $providerText   bucket: $bucketText"
}

# ---------------------------------------------------------------------------------------------
if (-not $SkipFileSearch) {
    Write-Section "Dump and base-backup files on fixed drives (depth $FileSearchDepth, newest 25)"
    # Filter on the name rather than with -Include: Windows PowerShell 5.1 ignores -Depth when
    # -Include is given, and walks the whole drive.
    $namePattern = '\.(dump|backup|sql|sql\.gz|pgdump|tar\.gz)$|^backup_manifest$|^base\.tar'
    $roots = Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' | ForEach-Object { "$($_.DeviceID)\" }
    $found = foreach ($root in $roots) {
        Get-ChildItem -Path $root -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -notin @('Windows', 'Program Files', 'Program Files (x86)', 'ProgramData', '$Recycle.Bin', 'System Volume Information') } |
            ForEach-Object {
                Get-ChildItem -Path $_.FullName -Recurse -Depth $FileSearchDepth -File -ErrorAction SilentlyContinue |
                    Where-Object { $_.Name -match $namePattern }
            }
    }
    $found = $found | Where-Object { $_.Length -gt 1MB -or $_.Name -eq 'backup_manifest' }
    if ($found) {
        $found | Sort-Object LastWriteTime -Descending | Select-Object -First 25 FullName, LastWriteTime,
            @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize | Out-String -Width 250 | Write-Host
    } else { Write-Host 'No dump or base-backup files over 1 MB found.' -ForegroundColor Yellow }
}

# ---------------------------------------------------------------------------------------------
Write-Section 'Database (read-only)'
$connection = $null
foreach ($site in (Get-SitePaths $ApiSitePath $ApiPublicPort)) {
    $setting = Find-SettingInSite $site 'ConnectionStrings__DefaultConnection' 'ConnectionStrings' 'DefaultConnection'
    if ($setting) { $connection = $setting; break }
}
$webConnection = $null
foreach ($site in (Get-SitePaths $WebSitePath $WebPublicPort)) {
    $setting = Find-SettingInSite $site 'ConnectionStrings__DefaultConnection' 'ConnectionStrings' 'DefaultConnection'
    if ($setting) { $webConnection = $setting; break }
}

if (-not $connection) {
    Write-Host "No API connection string found under $ApiSitePath (or its slots). Database checks skipped." -ForegroundColor Yellow
} elseif (-not $psql) {
    Write-Host 'psql not found. Database checks skipped.' -ForegroundColor Yellow
} else {
    $cs = Get-ConnectionStringParts $connection.Value
    $pgHost = $cs['host']; if (-not $pgHost) { $pgHost = $cs['server'] }
    $pgPort = $cs['port']; if (-not $pgPort) { $pgPort = '5432' }
    $pgDb = $cs['database']
    $pgUser = $cs['username']; if (-not $pgUser) { $pgUser = $cs['user id'] }
    if (-not $pgUser) { $pgUser = $cs['userid'] }
    Write-Host "API connection from : $($connection.Source)"
    Write-Host "Host/Port/DB/User   : $pgHost / $pgPort / $pgDb / $pgUser   (password not shown)"
    if ($webConnection) {
        $wcs = Get-ConnectionStringParts $webConnection.Value
        $webHost = $wcs['host']; if (-not $webHost) { $webHost = $wcs['server'] }
        Write-Host "Web connection      : $webHost / $($wcs['database'])   from $($webConnection.Source)"
    }
    if ($pgHost -match ',') { Write-Host '  Multi-host connection string: the two-host failover setup IS deployed.' -ForegroundColor Green }
    else { Write-Host '  Single-host connection string: the app cannot fail over to a standby.' -ForegroundColor Yellow }
    $firstHost = ($pgHost -split ',')[0]

    $sqlPath = Join-Path $env:TEMP "dr-state-$stamp.sql"
    @'
\pset pager off
\echo '--- server'
SELECT version(), pg_is_in_recovery() AS is_standby, now() AS server_time,
       pg_postmaster_start_time() AS started_at, current_setting('data_directory') AS data_directory;

\echo '--- WAL, archiving and replication settings'
SELECT name, setting FROM pg_settings
WHERE name IN ('wal_level','archive_mode','archive_command','archive_timeout','restore_command',
               'max_wal_senders','max_replication_slots','wal_keep_size','synchronous_commit',
               'synchronous_standby_names','hot_standby','full_page_writes','summarize_wal')
ORDER BY name;

\echo '--- archiver: has WAL archiving ever succeeded, and is it failing now?'
SELECT archived_count, last_archived_wal, last_archived_time,
       failed_count, last_failed_wal, last_failed_time, stats_reset
FROM pg_stat_archiver;

\echo '--- standbys streaming from this server (empty = no replica)'
SELECT application_name, client_addr, state, sync_state, sent_lsn, replay_lsn, replay_lag, backend_start
FROM pg_stat_replication;

\echo '--- replication slots (an inactive slot retains WAL and fills the disk)'
SELECT slot_name, slot_type, active, restart_lsn, wal_status,
       pg_size_pretty(pg_wal_lsn_diff(pg_current_wal_lsn(), restart_lsn)) AS retained_wal
FROM pg_replication_slots;

\echo '--- databases'
SELECT datname, pg_size_pretty(pg_database_size(datname)) AS size
FROM pg_database WHERE NOT datistemplate ORDER BY pg_database_size(datname) DESC;

\echo '--- in-app backups recorded in the Backups table (latest 10)'
SELECT "Id", "FileName", "BackupType", "Status", "StartedAt", "CompletedAt",
       round("SizeBytes" / 1048576.0, 2) AS mb, "IsOffsite"
FROM "Backups" ORDER BY "StartedAt" DESC LIMIT 10;

\echo '--- Quartz cluster members (last check-in converted from UTC ticks)'
SELECT sched_name, instance_name,
       to_timestamp((last_checkin_time - 621355968000000000) / 1e7) AS last_checkin_utc,
       checkin_interval
FROM qrtz_scheduler_state ORDER BY sched_name, instance_name;

\echo '--- ClusterNodes (build stamps and job vetoes)'
SELECT "MachineName", "ContentRoot", "BuildTimestampUtc", "StartedAtUtc", "LastSeenUtc", "JobsVetoed", "VetoReason"
FROM "ClusterNodes" ORDER BY "LastSeenUtc" DESC;

\echo '--- client addresses holding connections (which machines use this database)'
SELECT client_addr, datname, usename, application_name, count(*) AS connections
FROM pg_stat_activity WHERE backend_type = 'client backend'
GROUP BY client_addr, datname, usename, application_name ORDER BY connections DESC;
'@ | Set-Content -Path $sqlPath -Encoding ASCII

    $previousOptions = $env:PGOPTIONS
    try {
        $env:PGPASSWORD = $cs['password']
        $env:PGOPTIONS = '-c default_transaction_read_only=on'
        $env:PGCONNECT_TIMEOUT = '10'
        # -w: never prompt. A wrong password must fail, not sit waiting for keyboard input.
        & $psql -h $firstHost -p $pgPort -U $pgUser -d $pgDb -X -w -f $sqlPath 2>&1 | Out-String -Width 250 | Write-Host
    } finally {
        Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
        Remove-Item Env:\PGCONNECT_TIMEOUT -ErrorAction SilentlyContinue
        $env:PGOPTIONS = $previousOptions
        Remove-Item $sqlPath -ErrorAction SilentlyContinue
    }

    # The archive target named in archive_command, if it is a path we can see from here.
    Write-Section 'WAL archive target'
    try {
        $env:PGPASSWORD = $cs['password']
        $env:PGOPTIONS = '-c default_transaction_read_only=on'
        $env:PGCONNECT_TIMEOUT = '10'
        $archiveCommand = & $psql -h $firstHost -p $pgPort -U $pgUser -d $pgDb -X -w -A -t -c 'SHOW archive_command' 2>$null
    } finally {
        Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
        Remove-Item Env:\PGCONNECT_TIMEOUT -ErrorAction SilentlyContinue
        $env:PGOPTIONS = $previousOptions
    }
    Write-Host "archive_command: $archiveCommand"
    $match = [regex]::Match("$archiveCommand", '"([^"%]+)\\%f"')
    if ($match.Success) {
        $target = $match.Groups[1].Value
        if (Test-Path $target) {
            $files = Get-ChildItem $target -File -ErrorAction SilentlyContinue
            $newest = $files | Sort-Object LastWriteTime -Descending | Select-Object -First 1
            Write-Host "Target $target holds $(@($files).Count) files; newest $($newest.Name) at $($newest.LastWriteTime)"
        } else { Write-Host "Target $target is not reachable from this session." -ForegroundColor Yellow }
    } else { Write-Host 'No archive path to inspect (archiving off, or a command this script does not parse).' }
}

Write-Section 'Done'
Write-Host "Nothing was changed. Transcript: $transcriptPath"
Stop-Transcript | Out-Null
