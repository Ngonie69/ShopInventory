<#
.SYNOPSIS
    Counts the SAP SQLQueries objects in the production company and says which are dead weight.
    Read-only: Login, paged GETs, Logout.

.DESCRIPTION
    Every SQL statement ShopInventory runs in SAP is stored as a SQLQueries object. Until mid-2026
    the API minted a random code per request and tried to delete it afterwards. DELETE never
    finishes on this Service Layer (502 at 300 s, row kept), so about 4,100 objects leaked. Writes
    to the entity set slow down as it grows (POST took 30-43 s at ~4,300 rows, 2026-07-27).

    The leak was fixed by content-addressing (code = family + 12 hex of SHA-256 over the text) and
    bound parameters. This census answers what is left to decide:
      - How many objects exist now, and how many have the legacy random-suffix shape that nothing
        in the current code can ever ask for again.
      - Whether any family is still growing (objects created in the last 1 / 7 / 30 days).
      - How long the reads take, as a baseline before and after any cleanup.

    Run on a production API server (it reads SAP__* settings from the live API site's web.config,
    and pins the Service Layer certificate to SAP__AllowedServerCertificateThumbprints exactly as
    the API does). It writes a CSV of every code to -OutputFolder and compares with the previous
    run's CSV there, so two runs a week apart give a growth rate.

    It makes one SAP session as the API's own user and logs it out at the end. It runs no SQL,
    creates nothing and changes nothing in SAP.

.EXAMPLE
    .\Get-SapSqlQueryCensus.ps1

.EXAMPLE
    # Borrow a session someone already has open (it is not logged out).
    .\Get-SapSqlQueryCensus.ps1 -SessionId '<B1SESSION value>'
#>
[CmdletBinding()]
param(
    [string]$ApiSitePath = 'C:\inetpub\ShopInventory-API',
    [int]$ApiPublicPort = 5106,
    [string]$OutputFolder,
    # Overrides, for testing or for a host without the API site.
    [string]$ServiceLayerUrl,
    [string]$CompanyDB,
    [string]$UserName,
    [string]$SessionId,
    [int]$PageSize = 500,
    [int]$TopFamilies = 40,
    # Skip reading SqlText (megabytes). Then 12-hex codes cannot be told apart and show as
    # 'content-addressed?'.
    [switch]$SkipTextCheck,
    # Random 12-hex codes were last minted by builds before 8235dcb (2026-07-27). A hex code whose
    # text does not hash to it and that was created before this date is legacy.
    [DateTime]$LegacyBefore = '2026-08-01'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
. (Join-Path $PSScriptRoot 'PostgresBackup.Common.ps1')   # Find-SiteSetting, Get-LiveSitePaths

if (-not $OutputFolder) { $OutputFolder = Join-Path $PSScriptRoot 'sap-sqlqueries-census' }
New-Item -ItemType Directory -Force -Path $OutputFolder | Out-Null
$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + 'Z'
$transcriptPath = Join-Path $OutputFolder "census-$stamp.log"
Start-Transcript -Path $transcriptPath | Out-Null

function Get-SapSetting([string]$key) {
    # web.config SAP__Key first, then appsettings.Production.json, then appsettings.json.
    foreach ($site in (Get-LiveSitePaths $ApiSitePath $ApiPublicPort)) {
        $hit = Find-SiteSetting $site "SAP__$key" 'SAP' $key
        if ($hit) { return $hit.Value }
        $base = Join-Path $site 'appsettings.json'
        if (Test-Path $base) {
            $json = Get-Content $base -Raw | ConvertFrom-Json
            if ($json.PSObject.Properties['SAP'] -and $json.SAP.PSObject.Properties[$key] -and $json.SAP.$key) { return $json.SAP.$key }
        }
    }
    return $null
}

function Get-PinnedThumbprints {
    $pins = @()
    foreach ($site in (Get-LiveSitePaths $ApiSitePath $ApiPublicPort)) {
        $webConfig = Join-Path $site 'web.config'
        if (-not (Test-Path $webConfig)) { continue }
        [xml]$xml = Get-Content $webConfig -Raw
        $pins = @($xml.SelectNodes("//environmentVariable[starts-with(@name,'SAP__AllowedServerCertificateThumbprints__')]") |
            ForEach-Object { ($_.value -replace '\s', '').ToUpperInvariant() } | Where-Object { $_ })
        if ($pins.Count -gt 0) { break }
    }
    return $pins
}

# Same rule as the API (SAPServiceLayerClient): with a pin list, the certificate must match one of
# them; without one, normal Windows validation applies. C# so the callback does not depend on a
# PowerShell runspace being available on the thread that validates.
if (-not ('SapCensusTls' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
public static class SapCensusTls {
    public static string[] Pins = new string[0];
    public static string LastThumbprint;
    public static bool Validate(object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors errors) {
        var thumb = cert == null ? null : new X509Certificate2(cert).Thumbprint.Replace(" ", "").ToUpperInvariant();
        LastThumbprint = thumb;
        if (Pins.Length == 0) { return errors == SslPolicyErrors.None; }
        return thumb != null && Pins.Contains(thumb);
    }
    public static void Install(string[] pins) {
        Pins = pins ?? new string[0];
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        ServicePointManager.ServerCertificateValidationCallback = Validate;
    }
}
'@
}

function Invoke-Sap([string]$Method, [string]$Path, $Body, [hashtable]$Headers = @{}) {
    $uri = $script:baseUrl + $Path.TrimStart('/')
    $request = [Net.HttpWebRequest]::Create($uri)
    $request.Method = $Method
    $request.Timeout = 300000
    $request.ReadWriteTimeout = 300000
    $request.Accept = 'application/json'
    if ($script:session) { $request.Headers.Add('Cookie', "B1SESSION=$($script:session)") }
    foreach ($key in $Headers.Keys) { $request.Headers.Add($key, $Headers[$key]) }
    if ($null -ne $Body) {
        $bytes = [Text.Encoding]::UTF8.GetBytes(($Body | ConvertTo-Json -Compress))
        $request.ContentType = 'application/json'
        $stream = $request.GetRequestStream(); $stream.Write($bytes, 0, $bytes.Length); $stream.Dispose()
    }
    $clock = [Diagnostics.Stopwatch]::StartNew()
    try {
        $response = $request.GetResponse()
    } catch [Net.WebException] {
        $clock.Stop()
        $text = ''
        if ($_.Exception.Response) {
            $reader = New-Object IO.StreamReader($_.Exception.Response.GetResponseStream())
            $text = $reader.ReadToEnd(); $reader.Dispose()
        }
        $detail = $_.Exception.Message
        if ($_.Exception.Status -eq 'TrustFailure' -or $_.Exception.Status -eq 'SecureChannelFailure') {
            $detail += " (Service Layer serves certificate $([SapCensusTls]::LastThumbprint); pinned: $([SapCensusTls]::Pins -join ', '))"
        }
        throw "$Method $Path failed after $([math]::Round($clock.Elapsed.TotalSeconds, 1)) s: $detail $($text.Substring(0, [math]::Min(300, $text.Length)))"
    }
    $reader = New-Object IO.StreamReader($response.GetResponseStream())
    $content = $reader.ReadToEnd(); $reader.Dispose(); $response.Dispose()
    $clock.Stop()
    $parsed = $null
    if ($content) { $parsed = $content | ConvertFrom-Json }
    return [pscustomobject]@{ Body = $parsed; Seconds = $clock.Elapsed.TotalSeconds }
}

# What the codes look like. Before 8235dcb (2026-07-27) the API minted, per request:
#   prefix_<6 random digits>              WHS_ITEM_CODES_*, WHS_BATCHES_*, BATCH_SEARCH, SH_PL*, SO_UOM_*
#   PKG_STK_<ticks mod 10^8>              up to 8 digits
#   STK_ITEMS_<12 lowercase hex>          from a GUID
#   prefix_<12 UPPERCASE hex>             from a GUID - the same shape as a content-addressed code
# Since then a code is prefix_<first 12 hex of SHA-256 over the normalised text>, uppercase. The
# only way to tell the last two apart is to hash the stored text, which is what this does.
$hexTwelve = New-Object Text.RegularExpressions.Regex '^(?<family>.+)_(?<fp>[0-9A-Fa-f]{12})$'
$legacyDigits = New-Object Text.RegularExpressions.Regex '^(?<family>.+?)_?(?<suffix>\d{5,8})$'
$sha = [Security.Cryptography.SHA256]::Create()

function Get-SqlFingerprint([string]$sqlText) {
    # Exactly SAPServiceLayerClient.NormalizeSqlText, then ComputeSqlFingerprint.
    $text = "$sqlText".TrimEnd([char[]]@(';', ' ', "`t", "`r", "`n")).Replace("`r`n", "`n").Replace("`r", "`n").Trim()
    $hash = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($text))
    return ([BitConverter]::ToString($hash) -replace '-', '').Substring(0, 12)
}

function Get-CodeKind([string]$code, $sqlText, $createDate) {
    # The Production Plan app (repo production-plan) shares this company: 'PP' + 12 uppercase hex of
    # SHA-256 over its text. Some end in digits by chance and would otherwise read as legacy random.
    if ($code -cmatch '^PP[0-9A-F]{12}$') { return @('production plan', 'PP') }
    $m = $hexTwelve.Match($code)
    if ($m.Success) {
        $family = $m.Groups['family'].Value
        $fp = $m.Groups['fp'].Value
        if ($fp -cmatch '[a-f]') { return @('legacy random', $family) }        # GUID, lowercase
        if ($null -eq $sqlText) { return @('content-addressed?', $family) }      # -SkipTextCheck
        if ((Get-SqlFingerprint $sqlText) -ceq $fp) { return @('content-addressed', $family) }
        # SAP strips trailing spaces from each stored line, so a real content-addressed object can
        # fail the hash too. Before the fix shipped, though, nothing minted content-addressed codes.
        if ($createDate -and $createDate -lt $LegacyBefore) { return @('legacy random', $family) }
        return @('unverified hex', $family)
    }
    $m = $legacyDigits.Match($code)
    if ($m.Success) { return @('legacy random', $m.Groups['family'].Value) }
    return @('fixed', $code)
}

function ConvertTo-SapDate($value) {
    if (-not $value) { return $null }
    $parsed = [DateTime]::MinValue
    if ([DateTime]::TryParse("$value", [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeLocal, [ref]$parsed)) { return $parsed.Date }
    return $null
}

$borrowed = [bool]$SessionId
$script:session = $null
$exitCode = 1
try {
    if (-not $ServiceLayerUrl) { $ServiceLayerUrl = Get-SapSetting 'ServiceLayerUrl' }
    if (-not $CompanyDB) { $CompanyDB = Get-SapSetting 'CompanyDB' }
    if (-not $UserName) { $UserName = Get-SapSetting 'Username' }
    if (-not $ServiceLayerUrl) { throw "No SAP ServiceLayerUrl found under $ApiSitePath. Pass -ServiceLayerUrl." }
    $script:baseUrl = $ServiceLayerUrl.TrimEnd('/') + '/'
    $pins = @(Get-PinnedThumbprints)
    [SapCensusTls]::Install([string[]]$pins)

    Write-Host "Service Layer : $($script:baseUrl)"
    Write-Host "Company       : $CompanyDB"
    Write-Host "Certificate   : $(if ($pins.Count) { "pinned to $($pins.Count) thumbprint(s), as the API does" } else { 'normal Windows validation (no pins found)' })"

    if ($borrowed) {
        $script:session = $SessionId.Trim()
        Write-Host 'Session       : borrowed (will not be logged out)'
    } else {
        $password = Get-SapSetting 'Password'
        if (-not $password) { throw 'No SAP__Password in the API site configuration. Pass -SessionId to borrow a session instead.' }
        $login = Invoke-Sap 'POST' 'Login' @{ CompanyDB = $CompanyDB; UserName = $UserName; Password = $password }
        $password = $null
        $script:session = $login.Body.SessionId
        Write-Host ("Session       : logged in as {0} in {1:N1} s" -f $UserName, $login.Seconds)
    }

    # Count first: one cheap call, and the number to compare the paged read against.
    $countCall = Invoke-Sap 'GET' 'SQLQueries/$count' $null
    $reportedCount = [int]"$($countCall.Body)"
    Write-Host ("SQLQueries    : {0:N0} objects (`$count in {1:N2} s)" -f $reportedCount, $countCall.Seconds)

    $rows = New-Object Collections.Generic.List[object]
    $select = if ($SkipTextCheck) { 'SqlCode,SqlName,CreateDate,UpdateDate' } else { 'SqlCode,SqlName,SqlText,CreateDate,UpdateDate' }
    $path = "SQLQueries?`$select=$select"
    $pageTimes = @()
    while ($path) {
        $page = Invoke-Sap 'GET' $path $null @{ Prefer = "odata.maxpagesize=$PageSize" }
        $pageTimes += $page.Seconds
        foreach ($item in @($page.Body.value)) { $rows.Add($item) }
        $next = $null
        foreach ($name in @('odata.nextLink', '@odata.nextLink')) {
            if ($page.Body.PSObject.Properties[$name]) { $next = $page.Body.$name }
        }
        $path = $next
        Write-Host "  read $($rows.Count)..."
    }
    $timeStats = $pageTimes | Measure-Object -Average -Maximum
    Write-Host ("Paged read    : {0:N0} rows in {1} page(s), {2:N2} s average, {3:N2} s slowest" -f $rows.Count, $pageTimes.Count, $timeStats.Average, $timeStats.Maximum)
    if ($rows.Count -ne $reportedCount) {
        Write-Host "  The paged read returned $($rows.Count) rows but `$count said $reportedCount (writes during the read?)." -ForegroundColor Yellow
    }

    $today = (Get-Date).Date
    $records = foreach ($row in $rows) {
        $created = ConvertTo-SapDate $row.CreateDate
        $text = if ($SkipTextCheck) { $null } else { "$($row.SqlText)" }
        $kind = Get-CodeKind "$($row.SqlCode)" $text $created
        [pscustomobject]@{
            SqlCode    = "$($row.SqlCode)"
            SqlName    = "$($row.SqlName)"
            Kind       = $kind[0]
            Family     = $kind[1]
            CreateDate = $created
            UpdateDate = ConvertTo-SapDate $row.UpdateDate
            TextLength = if ($null -ne $text) { $text.Length } else { $null }
        }
    }
    $records = @($records)

    Write-Host ''
    Write-Host '--- by kind'
    $records | Group-Object Kind | Sort-Object Count -Descending | ForEach-Object {
        $created = @($_.Group | Where-Object { $_.CreateDate } | Sort-Object CreateDate)
        $oldest = if ($created.Count) { $created[0].CreateDate.ToString('yyyy-MM-dd') } else { '?' }
        $newest = if ($created.Count) { $created[$created.Count - 1].CreateDate.ToString('yyyy-MM-dd') } else { '?' }
        '{0,6:N0}  {1,-18} created {2} .. {3}' -f $_.Count, $_.Name, $oldest, $newest
    } | Write-Host
    Write-Host "  legacy random      a random suffix. No current code builds one, so nothing will ever ask for"
    Write-Host '                     these again: they are the cleanup candidates.'
    Write-Host '  content-addressed  the stored text hashes to the code. In use, or an older version of a'
    Write-Host '                     statement that has since changed (then also dead, but not provably).'
    Write-Host "  unverified hex     12-hex code created on or after $($LegacyBefore.ToString('yyyy-MM-dd')) whose text does not hash"
    Write-Host '                     to it (SAP trims trailing spaces per line). Listed below; leave them.'
    Write-Host '  fixed              a constant code such as ITEM_BATCHES_V1: in use.'
    Write-Host "  production plan    PP + 12 hex: the Production Plan app's queries, not ShopInventory's."
    $unverified = @($records | Where-Object { $_.Kind -eq 'unverified hex' })
    if ($unverified.Count) {
        Write-Host ''
        Write-Host '--- unverified hex codes'
        $unverified | Sort-Object CreateDate | Select-Object -First 30 | ForEach-Object {
            '  {0,-44} created {1}  {2}' -f $_.SqlCode, $(if ($_.CreateDate) { $_.CreateDate.ToString('yyyy-MM-dd') } else { '?' }), $_.SqlName
        } | Write-Host
    }

    Write-Host ''
    Write-Host '--- created recently (still growing?)'
    foreach ($days in 1, 7, 30) {
        $recent = @($records | Where-Object { $_.CreateDate -and $_.CreateDate -ge $today.AddDays(-$days) })
        $byKind = ($recent | Group-Object Kind | ForEach-Object { "$($_.Name) $($_.Count)" }) -join ', '
        '{0,3} day(s): {1,5:N0}   {2}' -f $days, $recent.Count, $byKind | Write-Host
    }
    $growing = @($records | Where-Object { $_.CreateDate -and $_.CreateDate -ge $today.AddDays(-7) } | Group-Object Family | Sort-Object Count -Descending | Select-Object -First 15)
    if ($growing.Count) {
        Write-Host '  families that gained objects in the last 7 days:'
        $growing | ForEach-Object { '    {0,5}  {1}' -f $_.Count, $_.Name } | Write-Host
    }

    Write-Host ''
    Write-Host '--- created per month'
    $records | Where-Object { $_.CreateDate } | Group-Object { $_.CreateDate.ToString('yyyy-MM') } | Sort-Object Name |
        ForEach-Object {
            $legacy = @($_.Group | Where-Object { $_.Kind -eq 'legacy random' }).Count
            '{0}  {1,6:N0}   (legacy {2:N0})' -f $_.Name, $_.Count, $legacy
        } | Write-Host

    Write-Host ''
    Write-Host "--- largest $TopFamilies families"
    $records | Group-Object Kind, Family | Sort-Object Count -Descending | Select-Object -First $TopFamilies | ForEach-Object {
        $first = $_.Group[0]
        $newest = @($_.Group | Where-Object { $_.CreateDate } | Sort-Object CreateDate -Descending)
        $newestText = if ($newest.Count) { $newest[0].CreateDate.ToString('yyyy-MM-dd') } else { '?' }
        '{0,6:N0}  {1,-18} {2,-40} newest {3}' -f $_.Count, $first.Kind, $first.Family, $newestText
    } | Write-Host

    $csvPath = Join-Path $OutputFolder "sqlqueries-$($CompanyDB)-$stamp.csv"
    $previous = @(Get-ChildItem $OutputFolder -Filter "sqlqueries-$($CompanyDB)-*.csv" | Where-Object { $_.FullName -ne $csvPath } | Sort-Object Name)
    $records | Sort-Object SqlCode | Select-Object SqlCode, SqlName, Kind, Family, TextLength,
        @{ n = 'CreateDate'; e = { if ($_.CreateDate) { $_.CreateDate.ToString('yyyy-MM-dd') } } },
        @{ n = 'UpdateDate'; e = { if ($_.UpdateDate) { $_.UpdateDate.ToString('yyyy-MM-dd') } } } |
        Export-Csv -Path $csvPath -NoTypeInformation -Encoding UTF8
    Write-Host ''
    Write-Host "Every code    : $csvPath"

    if ($previous.Count) {
        $before = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
        foreach ($code in (Import-Csv $previous[$previous.Count - 1].FullName | ForEach-Object { $_.SqlCode })) { [void]$before.Add($code) }
        $now = New-Object 'Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
        foreach ($record in $records) { [void]$now.Add($record.SqlCode) }
        $added = @($now | Where-Object { -not $before.Contains($_) })
        $removed = @($before | Where-Object { -not $now.Contains($_) })
        Write-Host ("Since {0}: {1:N0} added, {2:N0} removed" -f $previous[$previous.Count - 1].Name, $added.Count, $removed.Count)
        $familyOf = @{}
        foreach ($record in $records) { $familyOf[$record.SqlCode] = $record.Family }
        $added | Group-Object { $familyOf[$_] } | Sort-Object Count -Descending | Select-Object -First 15 |
            ForEach-Object { '    +{0,-5} {1}' -f $_.Count, $_.Name } | Write-Host
    } else {
        Write-Host 'No earlier census here. Run again in a week to get a growth rate.'
    }
    $exitCode = 0
} catch {
    Write-Host "FAILED: $($_.Exception.Message)" -ForegroundColor Red
} finally {
    if ($script:session -and -not $borrowed) {
        try { Invoke-Sap 'POST' 'Logout' $null | Out-Null; Write-Host 'Logged out.' } catch { Write-Host "Logout failed: $($_.Exception.Message)" -ForegroundColor Yellow }
    }
    Write-Host "Nothing in SAP was changed. Log: $transcriptPath"
    Stop-Transcript | Out-Null
}
exit $exitCode
