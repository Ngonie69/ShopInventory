<#
.SYNOPSIS
    Reports what AOMEI Backupper on this server backs up, where to, and whether it is succeeding.
    Changes nothing.

.DESCRIPTION
    Run on the production database server (10.10.10.9, KFL-DNS2) in an elevated Windows
    PowerShell 5.1 session. Get-DisasterRecoveryState.ps1 found the "AOMEI Backupper Scheduler
    Service" running there. AOMEI keeps its own schedule rather than using Windows Task Scheduler,
    so this script reads what AOMEI leaves on disk:

      - the service, its install folder, version and the account it runs as
      - its config and log files, and the destinations named in them
      - the backup images themselves (.adi image backups, .afi file backups), locally and at any
        destination that is reachable from this session
      - its Application event log entries and open network connections

    Any line that looks like a credential (pass, pwd, user, cred, key, token) is never printed.
    Output goes to the console and to a transcript file next to the script.

.EXAMPLE
    .\Get-AomeiBackupState.ps1
#>
[CmdletBinding()]
param(
    # Override when the install folder cannot be found from the service.
    [string]$InstallDir,
    # How deep to look for .adi/.afi images on each fixed drive.
    [int]$ImageSearchDepth = 5,
    [int]$LogTailLines = 80
)

$ErrorActionPreference = 'Continue'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $scriptDir) { $scriptDir = (Get-Location).Path }
$transcriptPath = Join-Path $scriptDir "aomei-state-$env:COMPUTERNAME-$stamp.txt"
Start-Transcript -Path $transcriptPath | Out-Null

$secretPattern = '(?i)pass|pwd|user(name)?\s*[=:]|cred|secret|token|key\s*[=:]'
$imagePattern = '(?i)\.(adi|afi)$'

function Write-Section([string]$title) {
    Write-Host ''
    Write-Host ('=' * 78) -ForegroundColor Cyan
    Write-Host "  $title" -ForegroundColor Cyan
    Write-Host ('=' * 78) -ForegroundColor Cyan
}

function Read-TextLoose([string]$path) {
    # AOMEI writes some files as UTF-16 and some as ANSI. Detect UTF-16 by the zero bytes and
    # strip anything unprintable, so a binary file yields fragments rather than noise.
    try {
        $bytes = [IO.File]::ReadAllBytes($path)
    } catch { return '' }
    if ($bytes.Length -eq 0) { return '' }
    $sample = [Math]::Min($bytes.Length, 4000)
    $zeros = 0
    for ($i = 1; $i -lt $sample; $i += 2) { if ($bytes[$i] -eq 0) { $zeros++ } }
    if ($zeros -gt ($sample / 4)) { $text = [Text.Encoding]::Unicode.GetString($bytes) }
    else { $text = [Text.Encoding]::Default.GetString($bytes) }
    # A binary file (AOMEI may keep its task list in one) decoded as text is mostly symbols.
    # Keep only its readable runs, as `strings` would, so paths inside it still turn up.
    $readable = ([regex]::Matches($text, '[\u0009\u000A\u000D\u0020-\u007E]')).Count
    if ($readable -lt ($text.Length * 0.85)) {
        return (([regex]::Matches($text, '[\u0020-\u007E]{6,}') | ForEach-Object { $_.Value }) -join "`n")
    }
    return ($text -replace '[^\u0009\u000A\u000D\u0020-\u007E\u00A0-\uFFFF]', ' ')
}

function Get-SafeLines([string]$text) {
    $text -split "`r?`n" | Where-Object { $_.Trim() -and $_ -notmatch $secretPattern }
}

# ---------------------------------------------------------------------------------------------
Write-Section 'Host'
Write-Host "Computer : $env:COMPUTERNAME"
Write-Host "Run at   : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')"
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
Write-Host "Elevated : $isAdmin"

# ---------------------------------------------------------------------------------------------
Write-Section 'AOMEI services and processes'
$services = @(Get-CimInstance Win32_Service | Where-Object { $_.Name -match '(?i)aomei|backupper' -or $_.DisplayName -match '(?i)aomei|backupper' })
if (-not $services) { Write-Host 'No AOMEI service found.' -ForegroundColor Yellow }
foreach ($service in $services) {
    Write-Host "Service  : $($service.Name)  [$($service.State), $($service.StartMode)]  runs as $($service.StartName)"
    Write-Host "Binary   : $($service.PathName)"
    if (-not $InstallDir -and $service.PathName) {
        $exe = ($service.PathName -replace '^"([^"]+)".*$', '$1') -replace '^(\S+\.exe).*$', '$1'
        if (Test-Path $exe) { $InstallDir = Split-Path -Parent $exe }
    }
}
$processes = @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $_.ProcessName -match '(?i)aomei|backupper|ABService|ABCore|Brlog|ambackup' })
$processes | Select-Object Id, ProcessName, StartTime, Path | Format-Table -AutoSize | Out-String -Width 250 | Write-Host

if (-not $InstallDir) {
    $InstallDir = @(
        Get-ChildItem 'C:\Program Files (x86)', 'C:\Program Files' -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match '(?i)aomei' } |
            ForEach-Object { $_.FullName }
    )[0]
}
Write-Host "Install folder: $InstallDir"

# ---------------------------------------------------------------------------------------------
Write-Section 'Version and edition'
if ($InstallDir -and (Test-Path $InstallDir)) {
    Get-ChildItem $InstallDir -Recurse -Depth 2 -Filter '*.exe' -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '(?i)backupper|ABService|ABCore|ambackup' } |
        ForEach-Object {
            $v = $_.VersionInfo
            [pscustomobject]@{ File = $_.FullName; Version = $v.ProductVersion; Product = $v.ProductName; Description = $v.FileDescription }
        } | Format-Table -AutoSize | Out-String -Width 250 | Write-Host
} else { Write-Host 'Install folder not found. Rerun with -InstallDir <path>.' -ForegroundColor Yellow }
foreach ($root in 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall') {
    Get-ChildItem $root -ErrorAction SilentlyContinue | ForEach-Object { Get-ItemProperty $_.PSPath } |
        Where-Object { $_.DisplayName -match '(?i)aomei' } |
        Select-Object DisplayName, DisplayVersion, InstallDate, InstallLocation |
        Format-List | Out-String | Write-Host
}

# ---------------------------------------------------------------------------------------------
Write-Section 'Config, database and log files (newest first)'
$dataDirs = @($InstallDir) + @(
    Get-ChildItem 'C:\ProgramData', "$env:SystemRoot\System32\config\systemprofile\AppData\Roaming", "$env:APPDATA" -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '(?i)aomei' } | ForEach-Object { $_.FullName }
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -Unique
Write-Host "Folders read: $($dataDirs -join '; ')"
$dataFiles = @(foreach ($dir in $dataDirs) {
    Get-ChildItem $dir -Recurse -Depth 3 -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Extension -match '(?i)^\.(ini|xml|dat|db|log|txt|cfg|json)$' } |
        # Translation files (de.txt, zh-CN.txt, nl-be.txt...) are full of example paths such as
        # \192.168.0.100\backup, which read as destinations and slow every scan.
        Where-Object { $_.Name -notmatch '(?i)^[a-z]{2}(-[a-z]{2,4})?\.txt$' }
})
$dataFiles | Sort-Object LastWriteTime -Descending | Select-Object -First 40 FullName, LastWriteTime,
    @{ n = 'KB'; e = { [math]::Round($_.Length / 1KB, 1) } } | Format-Table -AutoSize | Out-String -Width 250 | Write-Host

# ---------------------------------------------------------------------------------------------
Write-Section 'Destinations named in config and logs'
# UNC paths, drive-letter paths to images, and cloud/NAS hints. Credential-looking lines are dropped
# before matching, so a destination on the same line as a password is not shown either.
$destinationPattern = '(\\\\[A-Za-z0-9._-]+\\[^\s"<>|*?]+)|([A-Za-z]:\\[^\s"<>|*?]*\.(adi|afi))|([A-Za-z]:\\[^\s"<>|*?]*(backup|aomei)[^\s"<>|*?]*)'
$destinations = @{}
foreach ($file in $dataFiles) {
    if ($file.Length -gt 20MB) { continue }
    foreach ($line in (Get-SafeLines (Read-TextLoose $file.FullName))) {
        foreach ($m in [regex]::Matches($line, $destinationPattern, 'IgnoreCase')) {
            $value = $m.Value.TrimEnd('.', ',', ';', ')', ']')
            if ($value -match '(?i)\\program files|\\windows\\|\\programdata\\aomei') { continue }
            if (-not $destinations.ContainsKey($value)) { $destinations[$value] = $file.Name }
        }
    }
}
if ($destinations.Count) {
    $destinations.GetEnumerator() | Sort-Object Name | Select-Object -First 60 @{ n = 'Path'; e = { $_.Name } }, @{ n = 'SeenIn'; e = { $_.Value } } |
        Format-Table -AutoSize | Out-String -Width 250 | Write-Host
} else { Write-Host 'No destination paths found in AOMEI config or logs.' -ForegroundColor Yellow }

Write-Section 'Lines mentioning tasks, schedules and results (latest 60)'
$keywordPattern = '(?i)task|schedul|success|succeed|fail|error|complete|finish|backup (start|end)|destination|target|incremental|differential|full backup|system backup|disk backup|partition backup|file backup'
$keywordLines = foreach ($file in ($dataFiles | Sort-Object LastWriteTime)) {
    if ($file.Length -gt 20MB) { continue }
    Get-SafeLines (Read-TextLoose $file.FullName) | Where-Object { $_ -match $keywordPattern } |
        ForEach-Object { "[$($file.Name)] $($_.Trim())" }
}
$keywordLines = @($keywordLines | Where-Object { $_.Length -lt 400 })
if ($keywordLines) { $keywordLines | Select-Object -Last 60 | ForEach-Object { Write-Host $_ } }
else { Write-Host 'No task or result lines found.' -ForegroundColor Yellow }

Write-Section "Tail of the newest log files ($LogTailLines lines each, credential lines removed)"
$logs = @($dataFiles | Where-Object { $_.Extension -match '(?i)^\.(log|txt)$' } | Sort-Object LastWriteTime -Descending | Select-Object -First 3)
if (-not $logs) { Write-Host 'No log files found.' -ForegroundColor Yellow }
foreach ($log in $logs) {
    Write-Host "--- $($log.FullName)  (last written $($log.LastWriteTime))"
    @(Get-SafeLines (Read-TextLoose $log.FullName)) | Select-Object -Last $LogTailLines | ForEach-Object { Write-Host $_ }
}

# ---------------------------------------------------------------------------------------------
Write-Section 'Registry (values with credential-looking names are skipped)'
foreach ($root in 'HKLM:\SOFTWARE\AOMEI', 'HKLM:\SOFTWARE\WOW6432Node\AOMEI', 'HKLM:\SOFTWARE\AomeiBR', 'HKLM:\SOFTWARE\WOW6432Node\AomeiBR') {
    if (-not (Test-Path $root)) { continue }
    Get-ChildItem $root -Recurse -ErrorAction SilentlyContinue | Select-Object -First 50 | ForEach-Object {
        $key = $_
        $props = Get-ItemProperty $key.PSPath -ErrorAction SilentlyContinue
        $names = @($props.PSObject.Properties | Where-Object { $_.Name -notlike 'PS*' -and $_.Name -notmatch $secretPattern -and $_.Name -notmatch '(?i)pass|pwd|user|cred|key|token|secret' })
        if ($names) {
            Write-Host $key.Name
            foreach ($n in $names) {
                $value = "$($n.Value)"
                if ($value -match $secretPattern) { $value = '(hidden)' }
                if ($value.Length -gt 200) { $value = $value.Substring(0, 200) + '...' }
                Write-Host "    $($n.Name) = $value"
            }
        }
    }
}

# ---------------------------------------------------------------------------------------------
Write-Section "Backup images on fixed drives (.adi/.afi, depth $ImageSearchDepth)"
$roots = Get-CimInstance Win32_LogicalDisk -Filter 'DriveType=3' | ForEach-Object { "$($_.DeviceID)\" }
# Filter on the name rather than with -Include: Windows PowerShell 5.1 ignores -Depth when
# -Include is given, and walks the whole drive.
$localImages = @(foreach ($root in $roots) {
    Get-ChildItem -Path $root -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -notin @('Windows', 'Program Files', 'Program Files (x86)', '$Recycle.Bin', 'System Volume Information') } |
        ForEach-Object {
            Get-ChildItem -Path $_.FullName -Recurse -Depth $ImageSearchDepth -File -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -match $imagePattern }
        }
})
if ($localImages) {
    $localImages | Sort-Object LastWriteTime -Descending | Select-Object -First 30 FullName, LastWriteTime,
        @{ n = 'GB'; e = { [math]::Round($_.Length / 1GB, 2) } } | Format-Table -AutoSize | Out-String -Width 250 | Write-Host
    Write-Host "Images on this server's own disks are lost with the server." -ForegroundColor Yellow
} else { Write-Host 'No .adi/.afi images on local fixed drives.' }

Write-Section 'Backup images at the destinations named above (if reachable from this session)'
$folders = @($destinations.Keys | ForEach-Object {
    if ($_ -match $imagePattern) { Split-Path -Parent $_ } else { $_ }
} | Where-Object { $_ } | Select-Object -Unique)
if (-not $folders) { Write-Host 'No destination folders to check.' }
foreach ($folder in $folders) {
    if (-not (Test-Path -LiteralPath $folder -ErrorAction SilentlyContinue)) {
        Write-Host "$folder : not reachable from this session (the service may reach it with its own credentials)." -ForegroundColor Yellow
        continue
    }
    $images = @(Get-ChildItem -LiteralPath $folder -Recurse -Depth 3 -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match $imagePattern })
    if (-not $images) { Write-Host "$folder : reachable, no .adi/.afi images." -ForegroundColor Yellow; continue }
    $newest = $images | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $totalGb = [math]::Round((($images | Measure-Object Length -Sum).Sum) / 1GB, 2)
    Write-Host "$folder : $($images.Count) images, $totalGb GB, newest $($newest.Name) at $($newest.LastWriteTime)" -ForegroundColor Green
    $images | Sort-Object LastWriteTime -Descending | Select-Object -First 15 FullName, LastWriteTime,
        @{ n = 'GB'; e = { [math]::Round($_.Length / 1GB, 2) } } | Format-Table -AutoSize | Out-String -Width 250 | Write-Host
}

# ---------------------------------------------------------------------------------------------
Write-Section 'Application event log entries from AOMEI (last 60 days)'
try {
    $events = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = (Get-Date).AddDays(-60) } -ErrorAction Stop |
        Where-Object { $_.ProviderName -match '(?i)aomei|backupper|ambackup' })
    if ($events) {
        $events | Select-Object -First 40 TimeCreated, ProviderName, LevelDisplayName, Id,
            @{ n = 'Message'; e = { ($_.Message -replace '\s+', ' ').Substring(0, [Math]::Min(160, ($_.Message -replace '\s+', ' ').Length)) } } |
            Format-Table -AutoSize -Wrap | Out-String -Width 250 | Write-Host
    } else { Write-Host 'No AOMEI entries in the Application log.' }
} catch { Write-Host "Could not read the Application log: $($_.Exception.Message)" -ForegroundColor Yellow }

Write-Section 'Network connections held by AOMEI processes'
foreach ($p in $processes) {
    Get-NetTCPConnection -OwningProcess $p.Id -ErrorAction SilentlyContinue |
        Where-Object { $_.RemoteAddress -notin @('0.0.0.0', '::', '127.0.0.1', '::1') } |
        Select-Object @{ n = 'Process'; e = { $p.ProcessName } }, RemoteAddress, RemotePort, State |
        Format-Table -AutoSize | Out-String | Write-Host
}
Write-Host 'SMB connections from this server (a NAS destination shows here while a backup runs):'
Get-SmbConnection -ErrorAction SilentlyContinue | Select-Object ServerName, ShareName, Dialect |
    Format-Table -AutoSize | Out-String | Write-Host

Write-Section 'Done'
Write-Host "Nothing was changed. Transcript: $transcriptPath"
Stop-Transcript | Out-Null
