<#
.SYNOPSIS
    Proves a dump set restores: restores each database into a throwaway database, compares every
    table's row count with the live one, then drops the copy.

.DESCRIPTION
    docs/operations/postgresql-ha.md: "Backups are only considered valid when a restore has been
    completed successfully." This is that restore, run weekly by the task
    Register-PostgresNightlyDump.ps1 creates, or by hand before trusting a set.

    For each database in the set:
      1. The file's SHA-256 must match the manifest.
      2. It is restored with pg_restore --exit-on-error into restorecheck_<name>. Any restore
         error fails the check.
      3. Every table is counted in the copy and in the live database (the live read is
         read-only). It fails when a table the dump lists is missing from the copy, when a table
         is empty in the copy while live holds -EmptyTableFailRows or more, or when the copy holds
         under half the live rows. Other differences are rows written since the dump: reported,
         not judged.
      4. The copy is dropped, unless -Keep.

    It only ever creates or drops a database whose name starts with restorecheck_, so it cannot
    touch a live database by mistake. Restoring needs about the database's size again in free
    space where PostgreSQL keeps its data; the check refuses to start without it.

    The result is written beside the set (restore-check-<time>.json) and to
    last-restore-check.json in the set's parent, and to the Application event log (source
    ShopInventoryBackup, 1010 = passed, 1011 = failed).

.EXAMPLE
    # Newest set under the share.
    .\Test-PostgresDumpRestore.ps1 -Root '\\10.10.10.254\svr_backup\ShopInventory-pgdump'

.EXAMPLE
    # A particular set, keeping the restored copy to look at.
    .\Test-PostgresDumpRestore.ps1 -SetPath 'D:\pgdump\20261110-000000Z' -Databases ShopInventory -Keep
#>
[CmdletBinding()]
param(
    # Folder holding dump sets; the newest complete one is checked.
    [string]$Root,
    # Or one set folder.
    [string]$SetPath,
    # Default: every database in the set's manifest.
    [string[]]$Databases,
    [ValidateRange(1, 16)]
    [int]$Jobs = 2,
    [switch]$Keep,
    # How many of the largest differences to print per database.
    [int]$ShowDifferences = 15,
    # A table empty in the copy fails the check when live holds at least this many rows; below it,
    # it is a note (queues empty overnight).
    [int]$EmptyTableFailRows = 1000,
    [string]$LogRoot = 'C:\ShopInventoryBackups\logs',
    [string]$ConnectionString,
    [string]$PgBin,
    [string]$ApiSitePath = 'C:\inetpub\ShopInventory-API',
    [int]$ApiPublicPort = 5106
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'PostgresBackup.Common.ps1')
if ($Databases) { $Databases = Split-NameList $Databases }

$startedUtc = [DateTime]::UtcNow
$stamp = $startedUtc.ToString('yyyyMMdd-HHmmss') + 'Z'
New-Item -ItemType Directory -Force -Path $LogRoot | Out-Null
$transcriptPath = Join-Path $LogRoot "restore-check-$stamp.log"
Start-Transcript -Path $transcriptPath | Out-Null

$report = [ordered]@{
    result      = 'failed'
    set         = $null
    startedUtc  = $startedUtc.ToString('o')
    finishedUtc = $null
    databases   = @()
    error       = $null
    log         = $transcriptPath
}

# Counts every ordinary table in one database. Output: schema.table|count, one per line.
$countSql = @'
SELECT format('SELECT %L || ''|'' || count(*) FROM %I.%I', n.nspname || '.' || c.relname, n.nspname, c.relname)
FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE c.relkind IN ('r', 'p') AND n.nspname NOT IN ('pg_catalog', 'information_schema') AND n.nspname NOT LIKE 'pg_toast%'
ORDER BY 1
\gexec
'@

function Get-TableCounts($pgBinPath, $connection, [string]$database, [switch]$ReadOnly) {
    $counts = New-Object 'System.Collections.Generic.Dictionary[string,long]' ([StringComparer]::Ordinal)
    foreach ($line in (Invoke-PgScalar -PgBin $pgBinPath -Connection $connection -Database $database -Sql $countSql -ReadOnly:$ReadOnly)) {
        $index = $line.LastIndexOf('|')
        if ($index -lt 1) { continue }
        $counts[$line.Substring(0, $index)] = [long]$line.Substring($index + 1)
    }
    return , $counts
}

function Remove-CheckDatabase($pgBinPath, $connection, [string]$name) {
    if (-not $name.StartsWith('restorecheck_', [StringComparison]::Ordinal)) {
        throw "Refusing to drop '$name': only restorecheck_ databases are dropped by this script."
    }
    Invoke-PgScalar -PgBin $pgBinPath -Connection $connection -Database $connection.Database `
        -Sql "DROP DATABASE IF EXISTS `"$name`" WITH (FORCE);" | Out-Null
}

$exitCode = 1
$setFolder = $null
try {
    if ($SetPath) {
        $setFolder = Get-Item -LiteralPath $SetPath
        if (-not (Test-Path (Join-Path $setFolder.FullName $script:ManifestName))) {
            throw "$SetPath has no manifest.json: it is not a complete dump set."
        }
    } elseif ($Root) {
        $sets = @(Get-DumpSets $Root)
        if ($sets.Count -eq 0) { throw "No complete dump set under $Root." }
        $setFolder = $sets[$sets.Count - 1]
    } else {
        throw 'Pass -Root (checks the newest set) or -SetPath.'
    }
    $manifest = Read-DumpManifest $setFolder.FullName
    $report.set = $setFolder.FullName
    Write-Host "Set        : $($setFolder.FullName)"
    Write-Host "Dumped     : $($manifest.startedUtc) on $($manifest.computer), PostgreSQL $($manifest.serverVersion)"

    $pgBinPath = Find-PgBin $PgBin
    $resolved = Resolve-PgConnection -ConnectionString $ConnectionString -ApiSitePath $ApiSitePath -ApiPublicPort $ApiPublicPort
    $connection = $resolved.Parts
    $pgRestore = Join-Path $pgBinPath 'pg_restore.exe'
    Write-Host "pg_restore : $pgRestore ($((& $pgRestore --version) -join ' '))"
    Write-Host "Connection : $($connection.Host):$($connection.Port) as $($connection.User), from $($resolved.Source)"

    $entries = @($manifest.databases)
    if ($Databases) {
        $entries = @($entries | Where-Object { $Databases -ccontains $_.database })
        $absent = @($Databases | Where-Object { -not ($manifest.databases.database -ccontains $_) })
        if ($absent.Count -gt 0) { throw "Not in this set: $($absent -join ', ')" }
    }

    # Room to restore, where the server keeps its data. Only checkable when the server is this box.
    $dataDirectory = @(Invoke-PgScalar -PgBin $pgBinPath -Connection $connection -Database $connection.Database -ReadOnly `
        -Sql "SELECT current_setting('data_directory');")[0]
    $neededBytes = [long](($entries | ForEach-Object { [long]$_.databaseBytes } | Measure-Object -Sum).Sum)
    if ($connection.Host -in @('localhost', '127.0.0.1', '::1', $env:COMPUTERNAME) -and (Test-Path $dataDirectory)) {
        $drive = (Get-Item $dataDirectory).PSDrive
        Write-Host ("Data dir   : {0} ({1:N1} GB free; restore needs about {2:N1} GB)" -f $dataDirectory, ($drive.Free / 1GB), ($neededBytes / 1GB))
        if ($drive.Free -lt ($neededBytes * 1.2 + 10GB)) {
            throw ("Not enough room to restore on {0}: {1:N1} GB free, need about {2:N1} GB plus 10 GB headroom. Filling the primary's disk stops it." -f $drive.Root, ($drive.Free / 1GB), ($neededBytes * 1.2 / 1GB))
        }
    } else {
        Write-Host "Data dir   : $dataDirectory on another host; free space not checked." -ForegroundColor Yellow
    }

    $allPassed = $true
    foreach ($entry in $entries) {
        $database = $entry.database
        $target = 'restorecheck_' + $database.ToLowerInvariant()
        $file = Join-Path $setFolder.FullName $entry.file
        $result = [ordered]@{ database = $database; restoredAs = $target; passed = $false; problems = @(); seconds = $null; tables = 0; rowsInCopy = 0; rowsLive = 0 }
        Write-Host ''
        Write-Host "--- $database -> $target"
        try {
            $hash = Get-FileSha256 $file
            if ($hash -ne $entry.sha256) { throw "$($entry.file) sha256 $hash does not match the manifest's $($entry.sha256): the file changed or the copy is damaged." }
            Write-Host '  hash matches the manifest'

            Remove-CheckDatabase $pgBinPath $connection $target
            Invoke-PgScalar -PgBin $pgBinPath -Connection $connection -Database $connection.Database `
                -Sql "CREATE DATABASE `"$target`" TEMPLATE template0;" | Out-Null

            $clock = [Diagnostics.Stopwatch]::StartNew()
            $restore = Invoke-PgTool -Exe $pgRestore -Connection $connection `
                -Arguments @('-d', $target, '--no-owner', '--no-privileges', '--exit-on-error', '-j', "$Jobs", $file)
            $clock.Stop()
            $result.seconds = [math]::Round($clock.Elapsed.TotalSeconds, 1)
            if ($restore.ExitCode -ne 0) {
                throw "pg_restore failed (exit $($restore.ExitCode)): $(($restore.Output | Select-Object -Last 5) -join ' | ')"
            }
            Write-Host ("  restored in {0:N0} s" -f $clock.Elapsed.TotalSeconds)

            $copy = Get-TableCounts $pgBinPath $connection $target
            $live = Get-TableCounts $pgBinPath $connection $database -ReadOnly
            $result.tables = $copy.Count
            $result.rowsInCopy = [long](($copy.Values | Measure-Object -Sum).Sum)
            $result.rowsLive = [long](($live.Values | Measure-Object -Sum).Sum)
            Write-Host ("  {0} tables, {1:N0} rows in the copy; {2} tables, {3:N0} rows live now" -f $copy.Count, $result.rowsInCopy, $live.Count, $result.rowsLive)

            # What the dump itself says it holds is the yardstick for "missing": a table created by a
            # migration after the dump is absent from the copy and that is correct.
            $dumpTables = @(& $pgRestore --list $file 2>$null | ForEach-Object {
                # "TABLE public Sales postgres", not "TABLE DATA public Sales postgres".
                $m = [regex]::Match("$_", '^\d+;\s+\d+\s+\d+\s+TABLE\s+(?!DATA\s)(\S+)\s+(\S+)\s')
                if ($m.Success) { "$($m.Groups[1].Value).$($m.Groups[2].Value)" }
            })
            foreach ($table in $dumpTables) {
                if (-not $copy.ContainsKey($table)) { $result.problems += "table $table is in the dump but not in the copy" }
            }
            $result.warnings = @()
            foreach ($table in $live.Keys) {
                if (-not $copy.ContainsKey($table)) {
                    $result.warnings += "table $table is live but not in the dump (created after it?)"
                } elseif ($copy[$table] -eq 0 -and $live[$table] -gt 0) {
                    # A queue can be empty at 02:00 and busy by 04:00. A table with thousands of
                    # rows does not empty and refill in that time.
                    $text = "table $table is empty in the copy but has $($live[$table]) rows live"
                    if ($live[$table] -ge $EmptyTableFailRows) { $result.problems += $text } else { $result.warnings += $text }
                }
            }
            if ($result.rowsLive -gt 0 -and $result.rowsInCopy -lt ($result.rowsLive / 2)) {
                $result.problems += ("the copy holds {0:N0} rows, under half of the {1:N0} live" -f $result.rowsInCopy, $result.rowsLive)
            }
            $result.warnings | ForEach-Object { Write-Host "  note: $_" -ForegroundColor Yellow }
            $differences = @($live.Keys | Where-Object { $copy.ContainsKey($_) -and $copy[$_] -ne $live[$_] } |
                ForEach-Object { [pscustomobject]@{ Table = $_; Copy = $copy[$_]; Live = $live[$_]; Difference = $live[$_] - $copy[$_] } } |
                Sort-Object { [math]::Abs($_.Difference) } -Descending)
            if ($differences.Count -gt 0) {
                Write-Host "  $($differences.Count) table(s) differ from live (rows written since the dump); largest:"
                $differences | Select-Object -First $ShowDifferences | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
            } else {
                Write-Host '  every table matches live exactly'
            }
            $result.differingTables = $differences.Count

            if ($result.problems.Count -gt 0) {
                $result.problems | ForEach-Object { Write-Host "  PROBLEM: $_" -ForegroundColor Red }
            } else {
                $result.passed = $true
                Write-Host '  PASSED' -ForegroundColor Green
            }
        } catch {
            $result.problems += $_.Exception.Message
            Write-Host "  FAILED: $($_.Exception.Message)" -ForegroundColor Red
        } finally {
            if (-not $Keep) {
                try { Remove-CheckDatabase $pgBinPath $connection $target; Write-Host "  dropped $target" }
                catch { Write-Host "  could not drop $target : $($_.Exception.Message)" -ForegroundColor Yellow }
            } else {
                Write-Host "  kept $target (-Keep); drop it when done: DROP DATABASE `"$target`";" -ForegroundColor Yellow
            }
        }
        if (-not $result.passed) { $allPassed = $false }
        $report.databases += $result
    }

    if ($allPassed) {
        $report.result = 'passed'
        $exitCode = 0
        Write-BackupEvent -EventId 1010 -EntryType Information -Message "Restore check of $($setFolder.FullName) passed for $(($entries.database) -join ', ')."
    } else {
        Write-BackupEvent -EventId 1011 -EntryType Error -Message "Restore check of $($setFolder.FullName) FAILED. Log: $transcriptPath"
    }
} catch {
    $report.error = $_.Exception.Message
    Write-Host "FAILED: $($_.Exception.Message)" -ForegroundColor Red
    Write-BackupEvent -EventId 1011 -EntryType Error -Message "Restore check FAILED: $($_.Exception.Message) Log: $transcriptPath"
} finally {
    $report.finishedUtc = [DateTime]::UtcNow.ToString('o')
    $json = $report | ConvertTo-Json -Depth 6
    if ($setFolder) {
        foreach ($path in @((Join-Path $setFolder.FullName "restore-check-$stamp.json"), (Join-Path $setFolder.Parent.FullName 'last-restore-check.json'))) {
            try { [IO.File]::WriteAllText($path, $json, (New-Object Text.UTF8Encoding $false)) }
            catch { Write-Host "  (could not write $path : $($_.Exception.Message))" }
        }
    }
    Write-Host ''
    Write-Host "Result: $($report.result). Log: $transcriptPath"
    Stop-Transcript | Out-Null
}
exit $exitCode
