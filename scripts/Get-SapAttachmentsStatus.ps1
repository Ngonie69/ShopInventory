<#
.SYNOPSIS
    Read-only: shows how the live ShopInventory API reads SAP attachments, and what it logged when
    it tried. Changes nothing and restarts nothing.

.DESCRIPTION
    Run on the API server (KFL-DNS2) in an elevated Windows PowerShell. For each ShopInventory API
    site it prints:

      - whether it holds the public API port, its folder, app pool and pool identity
      - each attachment setting the API will actually use, and where the value came from
        (web.config, then machine environment, then appsettings.<env>.json, then appsettings.json,
        then the code default)
      - whether the share login is set - the password is never printed, only its length
      - attachment warnings and errors from that site's own log for the last few days

    Paste the whole output back.

.EXAMPLE
    .\Get-SapAttachmentsStatus.ps1
#>
[CmdletBinding()]
param(
    [int]$ApiPort = 5106,
    [int]$LogDays = 2,
    [int]$MaxLogEntries = 25
)

$ErrorActionPreference = 'Stop'
Import-Module WebAdministration

# Configuration key -> the code's default when nothing sets it.
$settings = [ordered]@{
    'SAP:Enabled'                            = '(code default)'
    'SAP:AttachmentsPath'                    = '(none)'
    'SAP:AttachmentsUsername'                = '(none - app pool identity is used)'
    'SAP:AttachmentsPassword'                = '(none)'
    'SAP:AttachmentsDomain'                  = '(none)'
    'CreditNoteApprovals:AttachmentReadMode' = 'ServiceLayer (code default - SAP cannot serve this)'
    'FileStorage:UploadPath'                 = '(none)'
}
$secretKeys = @('SAP:AttachmentsPassword')

function Get-WebConfigVariables {
    param([string]$Path)
    $result = @{}
    if (-not (Test-Path -LiteralPath $Path)) { return $result }
    [xml]$xml = Get-Content -LiteralPath $Path -Raw
    foreach ($node in $xml.SelectNodes('//aspNetCore/environmentVariables/environmentVariable')) {
        $result[$node.GetAttribute('name')] = $node.GetAttribute('value')
    }
    return $result
}

function Get-JsonValue {
    param($Json, [string]$Key)
    if ($null -eq $Json) { return $null }
    $current = $Json
    foreach ($part in $Key.Split(':')) {
        if ($null -eq $current) { return $null }
        $property = $current.PSObject.Properties | Where-Object { $_.Name -ieq $part } | Select-Object -First 1
        if ($null -eq $property) { return $null }
        $current = $property.Value
    }
    return $current
}

function Read-JsonFile {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    try {
        return (Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json)
    }
    catch {
        Write-Host "    WARNING: could not parse $Path ($($_.Exception.Message)); its values are not shown." -ForegroundColor Yellow
        return $null
    }
}

function Format-Value {
    param([string]$Key, $Value)
    if ($secretKeys -contains $Key) {
        if ([string]::IsNullOrEmpty([string]$Value)) { return '(empty)' }
        return "(set, $(([string]$Value).Length) characters)"
    }
    return [string]$Value
}

Write-Host "SAP attachments status on $env:COMPUTERNAME at $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')" -ForegroundColor Cyan
Write-Host ""

$sites = @(Get-Website | Where-Object { $_.Name -like 'ShopInventory-API*' } | Sort-Object Name)
if ($sites.Count -eq 0) {
    Write-Host "No IIS site named ShopInventory-API* on this server." -ForegroundColor Red
    return
}

foreach ($site in $sites) {
    $folder = [Environment]::ExpandEnvironmentVariables([string]$site.physicalPath)
    $bindings = @($site.bindings.Collection | ForEach-Object { "$($_.protocol) $($_.bindingInformation)" })
    $isLive = @($site.bindings.Collection | Where-Object { $_.bindingInformation -like "*:$($ApiPort):*" }).Count -gt 0
    $pool = Get-Item "IIS:\AppPools\$($site.applicationPool)"
    $identity = [string]$pool.processModel.identityType
    if ($identity -eq 'SpecificUser') { $identity = "SpecificUser $($pool.processModel.userName)" }

    $title = if ($isLive) { "$($site.Name)   <-- LIVE (holds :$ApiPort)" } else { $site.Name }
    Write-Host "=== $title" -ForegroundColor $(if ($isLive) { 'Green' } else { 'White' })
    Write-Host "  Site state : $($site.State)    Pool: $($site.applicationPool) ($($pool.State)), identity $identity"
    Write-Host "  Folder     : $folder"
    Write-Host "  Bindings   : $($bindings -join ', ')"

    $webConfigPath = Join-Path $folder 'web.config'
    if (Test-Path -LiteralPath $webConfigPath) {
        Write-Host "  web.config : last written $((Get-Item -LiteralPath $webConfigPath).LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))"
    }
    else {
        Write-Host "  web.config : MISSING" -ForegroundColor Red
    }

    # Set-SapAttachmentsShareLogin.ps1 names its backups web.config.bak-yyyyMMdd-HHmmss, but other
    # tools use the same prefix, so a backup alone does not say which one wrote the file.
    $backups = @(Get-ChildItem -LiteralPath $folder -Filter 'web.config.bak-*' -ErrorAction SilentlyContinue | Sort-Object Name)
    if ($backups.Count -gt 0) {
        Write-Host "  web.config backups (any tool): $(($backups | ForEach-Object { $_.Name.Substring('web.config.bak-'.Length) }) -join ', ')"
    }
    else {
        Write-Host "  web.config backups (any tool): none"
    }

    $webConfig = Get-WebConfigVariables -Path $webConfigPath
    $environmentName = $webConfig['ASPNETCORE_ENVIRONMENT']
    if ([string]::IsNullOrWhiteSpace($environmentName)) {
        $environmentName = [Environment]::GetEnvironmentVariable('ASPNETCORE_ENVIRONMENT', 'Machine')
    }
    if ([string]::IsNullOrWhiteSpace($environmentName)) { $environmentName = 'Production' }

    $envJsonPath = Join-Path $folder "appsettings.$environmentName.json"
    $baseJsonPath = Join-Path $folder 'appsettings.json'
    $envJson = Read-JsonFile -Path $envJsonPath
    $baseJson = Read-JsonFile -Path $baseJsonPath
    Write-Host "  Environment: $environmentName    appsettings.$environmentName.json present: $(Test-Path -LiteralPath $envJsonPath)"

    Write-Host "  Settings the API uses:"
    foreach ($key in $settings.Keys) {
        $envName = $key.Replace(':', '__')
        $value = $null
        $source = $null

        if ($webConfig.ContainsKey($envName)) {
            $value = $webConfig[$envName]; $source = 'web.config'
        }
        elseif (-not [string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable($envName, 'Machine'))) {
            $value = [Environment]::GetEnvironmentVariable($envName, 'Machine'); $source = 'machine environment'
        }
        else {
            $fromEnvJson = Get-JsonValue -Json $envJson -Key $key
            $fromBaseJson = Get-JsonValue -Json $baseJson -Key $key
            if ($null -ne $fromEnvJson) { $value = $fromEnvJson; $source = "appsettings.$environmentName.json" }
            elseif ($null -ne $fromBaseJson) { $value = $fromBaseJson; $source = 'appsettings.json' }
        }

        if ($null -eq $source) {
            Write-Host ("    {0,-40} {1}" -f $key, $settings[$key]) -ForegroundColor DarkGray
        }
        else {
            Write-Host ("    {0,-40} {1}   [{2}]" -f $key, (Format-Value -Key $key -Value $value), $source)
        }
    }

    $logLines = @()
    for ($day = $LogDays - 1; $day -ge 0; $day--) {
        $logPath = Join-Path $folder ("logs\shopinventory-api-{0}.log" -f (Get-Date).AddDays(-$day).ToString('yyyyMMdd'))
        if (Test-Path -LiteralPath $logPath) {
            $logLines += @(Select-String -LiteralPath $logPath -Pattern '\[(WRN|ERR|FTL)\].*(attachment|AttachmentsPath|share sign-in|mount point|kfldb|b1_shf|SAP refused to add draft)' -Context 0, 3)
        }
    }

    Write-Host "  Attachment warnings/errors in its log, last $LogDays day(s): $($logLines.Count)"
    foreach ($match in ($logLines | Select-Object -Last $MaxLogEntries)) {
        $line = $match.Line
        if ($line.Length -gt 600) { $line = $line.Substring(0, 600) + ' ...' }
        Write-Host "    $line" -ForegroundColor Yellow
        foreach ($after in $match.Context.PostContext) {
            # Only the exception lines Serilog writes under the entry, not the next entry.
            if ($after -match '^\d{4}-\d{2}-\d{2} ') { break }
            $trimmed = $after.Trim()
            if ($trimmed.Length -gt 300) { $trimmed = $trimmed.Substring(0, 300) + ' ...' }
            if ($trimmed) { Write-Host "      $trimmed" -ForegroundColor DarkYellow }
        }
    }
    Write-Host ""
}

Write-Host "Done. Nothing was changed." -ForegroundColor Cyan
