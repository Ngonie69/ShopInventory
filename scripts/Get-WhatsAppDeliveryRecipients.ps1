<#
.SYNOPSIS
    Read-only list of who each WhatsApp document went to, and who saved that number and how.

.DESCRIPTION
    Run on KFL-DNS2 (10.10.10.9) in Windows PowerShell 5.1. It changes nothing: Postgres is opened
    with default_transaction_read_only=on and the gateway is not called.

    Use it to compare what ShopInventory addressed against what the sending phone shows:
      1. Every delivery created in the window, newest first: when it went (CAT), the document and its
         file name (what the phone shows), the customer it belongs to, the number it was addressed
         to, and where that number came from: saved on the customer (by whom, from which screen,
         when) or typed for that one send.
      2. Numbers that were sent documents of more than one customer in the window.
      3. Numbers saved on more than one customer (all live contacts, not only the window).
      4. Numbers saved in the window, grouped by who saved them and from where.
      5. Everything the OpenWA gateway logged as sent in the window, from every session and by any
         caller (ShopInventory, the Fiscalisation platform, the dashboard), each message matched to
         the ShopInventory delivery it was, or flagged when it was none.

    If a document sits in a chat on the phone whose number is not the one in section 1, ShopInventory
    never addressed it there. Section 5 then says whether anything else asked the gateway to send it.
    The gateway logs the chat it was ASKED to send to, not where WhatsApp Web put the message, so a
    document on the phone in a chat that section 5 never names was misrouted by WhatsApp Web itself.
    OpenWA's bulk sends are not in that log. If the numbers match, the number on the customer is the
    wrong one, and section 1 says who saved it.

    Numbers print as their last four digits unless -ShowFullNumbers is given.

.PARAMETER Days
    How far back to look at deliveries. Default 7.

.PARAMETER ShowFullNumbers
    Print whole numbers instead of the last four digits.

.PARAMETER CsvPath
    Also write section 1 to this CSV file (numbers masked the same way as on screen).

.PARAMETER OpenWaBaseUrl
    Overrides OpenWA__BaseUrl from web.config.

.PARAMETER OpenWaApiKey
    Overrides OpenWA__ApiKey from web.config.

.EXAMPLE
    & .\Get-WhatsAppDeliveryRecipients.ps1 -Days 7 -CsvPath .\whatsapp-deliveries.csv
#>
param(
    [int]$Days = 7,
    [switch]$ShowFullNumbers,
    [string]$CsvPath,
    [string]$InetpubRoot = 'C:\inetpub',
    [int]$Limit = 1000,
    [string]$OpenWaBaseUrl,
    [string]$OpenWaApiKey
)

$ErrorActionPreference = 'Stop'

function Write-Section([string]$Title) {
    Write-Host ''
    Write-Host ('=' * 78)
    Write-Host $Title
    Write-Host ('=' * 78)
}

function Format-Number([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return '(none)' }
    if ($ShowFullNumbers) { return $Value }
    $digits = ($Value -replace '\D', '')
    if ($digits.Length -le 4) { return '****' }
    return '****' + $digits.Substring($digits.Length - 4)
}

function Get-Text($Value) {
    if ($null -eq $Value -or [string]::IsNullOrWhiteSpace([string]$Value)) { return '' }
    return [string]$Value
}

# --- Connection string and gateway settings from the API's web.config ------------------------
$connectionString = $null
$configBaseUrl = $null
$configApiKey = $null
foreach ($dir in (Get-ChildItem -Path $InetpubRoot -Directory -Filter 'ShopInventory-API*' -ErrorAction SilentlyContinue)) {
    $configPath = Join-Path $dir.FullName 'web.config'
    if (-not (Test-Path $configPath)) { continue }
    [xml]$config = Get-Content -Path $configPath -Raw
    foreach ($var in $config.SelectNodes('//environmentVariable')) {
        $name = $var.GetAttribute('name')
        $value = $var.GetAttribute('value')
        if ($name -eq 'ConnectionStrings__DefaultConnection' -and -not $connectionString) { $connectionString = $value }
        if ($name -eq 'OpenWA__BaseUrl' -and -not $configBaseUrl) { $configBaseUrl = $value }
        if ($name -eq 'OpenWA__ApiKey' -and -not $configApiKey) { $configApiKey = $value }
    }
}
if (-not $OpenWaBaseUrl) { $OpenWaBaseUrl = $configBaseUrl }
if (-not $OpenWaApiKey) { $OpenWaApiKey = $configApiKey }
if ($OpenWaBaseUrl) { $OpenWaBaseUrl = $OpenWaBaseUrl.TrimEnd('/') }
if (-not $connectionString) {
    $prodJson = Join-Path $InetpubRoot 'ShopInventory-API\appsettings.Production.json'
    if (Test-Path $prodJson) {
        $connectionString = (Get-Content $prodJson -Raw | ConvertFrom-Json).ConnectionStrings.DefaultConnection
    }
}
if (-not $connectionString) { throw 'No connection string found in any API web.config or appsettings.Production.json.' }

$parts = @{}
foreach ($pair in $connectionString -split ';') {
    $kv = $pair -split '=', 2
    if ($kv.Count -eq 2) { $parts[$kv[0].Trim().ToLowerInvariant()] = $kv[1].Trim() }
}
$pgHost = if ($parts['host']) { $parts['host'] } else { $parts['server'] }
$pgPort = if ($parts['port']) { $parts['port'] } else { '5432' }
$pgDb   = $parts['database']
$pgUser = if ($parts['username']) { $parts['username'] } elseif ($parts['user id']) { $parts['user id'] } else { $parts['user'] }

$psql = (Get-Command psql -ErrorAction SilentlyContinue).Source
if (-not $psql) {
    $psql = Get-ChildItem 'C:\Program Files\PostgreSQL\*\bin\psql.exe' -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
}
if (-not $psql) { throw 'psql.exe not found.' }

# One JSON document per query, so the quoting survives PowerShell 5.1 (SQL goes through a file, never -c).
function Invoke-JsonQuery([string]$Sql) {
    $sqlFile = Join-Path $env:TEMP ("whatsapp-recipients-{0}.sql" -f [guid]::NewGuid().ToString('N'))
    "SELECT coalesce(json_agg(t), '[]'::json) FROM ($Sql) t;" | Set-Content -Path $sqlFile -Encoding ASCII
    $env:PGPASSWORD = $parts['password']
    $env:PGOPTIONS = '-c default_transaction_read_only=on'
    $env:PGCLIENTENCODING = 'UTF8'
    $previousEncoding = [Console]::OutputEncoding
    try {
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
        $raw = & $psql -w -h $pgHost -p $pgPort -U $pgUser -d $pgDb -A -t -q -P pager=off -f $sqlFile
        if ($LASTEXITCODE -ne 0) { throw "psql exited with $LASTEXITCODE" }
        # Windows PowerShell 5.1 hands a JSON array down the pipe as one object; unroll it.
        return @(($raw -join "`n") | ConvertFrom-Json | ForEach-Object { $_ })
    } finally {
        [Console]::OutputEncoding = $previousEncoding
        Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
        Remove-Item Env:PGOPTIONS -ErrorAction SilentlyContinue
        Remove-Item Env:PGCLIENTENCODING -ErrorAction SilentlyContinue
        Remove-Item $sqlFile -ErrorAction SilentlyContinue
    }
}

$cat = "'Africa/Harare'"

# --- 1. Deliveries --------------------------------------------------------------------------
Write-Section "1. WhatsApp deliveries created in the last $Days day(s), newest first (times CAT)"
$deliveries = Invoke-JsonQuery (@"
SELECT d."Id",
       d."Status",
       d."Trigger",
       d."DocumentType",
       d."DocumentNumber",
       d."FileName",
       d."CardCode",
       d."CardName",
       d."RouteCustomerName",
       d."RecipientE164",
       d."RecipientName",
       d."RequestedBy",
       d."ConsentAffirmed",
       d."SessionId",
       to_char(d."CreatedAtUtc" AT TIME ZONE $cat, 'YYYY-MM-DD HH24:MI') AS "Created",
       to_char(d."SendIssuedAtUtc" AT TIME ZONE $cat, 'YYYY-MM-DD HH24:MI:SS') AS "Issued",
       to_char(d."SentAtUtc" AT TIME ZONE $cat, 'YYYY-MM-DD HH24:MI:SS') AS "Sent",
       left(d."StatusReason", 160) AS "StatusReason",
       d."ContactId",
       c."OwnerName" AS "ContactOwner",
       c."CardCode" AS "ContactCardCode",
       c."RouteCustomerId" AS "ContactRouteCustomerId",
       c."ContactName",
       c."ConsentSource",
       c."ConsentRecordedBy",
       left(c."ConsentNote", 120) AS "ConsentNote",
       to_char(c."CreatedAtUtc" AT TIME ZONE $cat, 'YYYY-MM-DD HH24:MI') AS "ContactCreated",
       to_char(c."ConsentRecordedAtUtc" AT TIME ZONE $cat, 'YYYY-MM-DD HH24:MI') AS "ConsentRecorded",
       c."UpdatedBy" AS "ContactUpdatedBy",
       to_char(c."RemovedAtUtc" AT TIME ZONE $cat, 'YYYY-MM-DD HH24:MI') AS "ContactRemoved",
       c."RemovedBy" AS "ContactRemovedBy"
FROM "CustomerDocumentDeliveries" d
LEFT JOIN "CustomerWhatsAppContacts" c ON c."Id" = d."ContactId"
WHERE d."CreatedAtUtc" >= now() - interval '$Days days'
ORDER BY d."Id" DESC
LIMIT $Limit
"@)

$rows = New-Object System.Collections.Generic.List[object]
if ($deliveries.Count -eq 0) { Write-Host '  (none)' }
foreach ($d in $deliveries) {
    $customer = if ($d.RouteCustomerName) { "$($d.RouteCustomerName) (shop on van card $($d.CardCode))" }
                elseif ($d.CardName) { "$($d.CardName) ($($d.CardCode))" }
                else { Get-Text $d.CardCode }

    if ($d.ContactId) {
        $source = switch (Get-Text $d.ConsentSource) {
            'Web' { 'the Web (customer page or invoice Send)' }
            'VanHandset' { 'a van handset' }
            'Till' { 'a till' }
            default { Get-Text $d.ConsentSource }
        }
        $how = "saved on '$($d.ContactOwner)' by $($d.ConsentRecordedBy) from $source; contact added $($d.ContactCreated), consent last recorded $($d.ConsentRecorded)"
        if ($d.ContactUpdatedBy -and $d.ContactUpdatedBy -ne $d.ConsentRecordedBy) { $how += ", last changed by $($d.ContactUpdatedBy)" }
        if ($d.ContactRemoved) { $how += "; since removed $($d.ContactRemoved) by $($d.ContactRemovedBy)" }
        if ($d.ConsentNote) { $how += "; note: $($d.ConsentNote)" }
    } else {
        $how = "typed for this send only by $($d.RequestedBy) (not saved on the customer)"
    }

    $when = if ($d.Sent) { "sent $($d.Sent)" } elseif ($d.Issued) { "handed to the gateway $($d.Issued)" } else { "not sent (created $($d.Created))" }
    $name = if ($d.ContactName) { $d.ContactName } elseif ($d.RecipientName) { $d.RecipientName } else { '' }

    Write-Host ''
    Write-Host ("  #{0}  {1,-15} {2}  {3} {4}  [{5}]" -f $d.Id, $d.Status, $when, $d.DocumentType, $d.DocumentNumber, $d.Trigger)
    Write-Host ("      customer : {0}" -f $customer)
    Write-Host ("      to       : {0}{1}" -f (Format-Number $d.RecipientE164), $(if ($name) { "  '$name'" } else { '' }))
    Write-Host ("      number   : {0}" -f $how)
    if ($d.RequestedBy -and $d.Trigger -ne 'Auto') { Write-Host ("      asked by : {0}{1}" -f $d.RequestedBy, $(if ($d.ConsentAffirmed) { ' (confirmed the customer agreed)' } else { '' })) }
    if ($d.FileName) { Write-Host ("      file     : {0}" -f $d.FileName) }
    if ($d.StatusReason) { Write-Host ("      reason   : {0}" -f $d.StatusReason) }

    $rows.Add([pscustomobject]@{
        DeliveryId     = $d.Id
        Status         = $d.Status
        Trigger        = $d.Trigger
        CreatedCat     = $d.Created
        SentCat        = $d.Sent
        Document       = "$($d.DocumentType) $($d.DocumentNumber)"
        FileName       = $d.FileName
        Customer       = $customer
        To             = Format-Number $d.RecipientE164
        ToName         = $name
        NumberSource   = if ($d.ContactId) { "Saved:$($d.ConsentSource)" } else { 'TypedForThisSend' }
        SavedBy        = if ($d.ContactId) { $d.ConsentRecordedBy } else { $d.RequestedBy }
        SavedOn        = $d.ContactOwner
        ContactAdded   = $d.ContactCreated
        RequestedBy    = $d.RequestedBy
        Session        = $d.SessionId
    })
}

$sessions = @($deliveries | Where-Object { $_.SessionId } | Group-Object SessionId)
if ($sessions.Count -gt 0) {
    Write-Host ''
    Write-Host ('  Sent from {0} session(s): {1}' -f $sessions.Count, (($sessions | ForEach-Object { "$($_.Name) x$($_.Count)" }) -join ', '))
}

if ($CsvPath) {
    $rows | Export-Csv -Path $CsvPath -NoTypeInformation -Encoding UTF8
    Write-Host ''
    Write-Host "  Written to $CsvPath"
}

# --- 2. One number, several customers' documents ----------------------------------------------
Write-Section "2. Numbers sent documents of more than one customer in the last $Days day(s)"
$shared = Invoke-JsonQuery (@"
SELECT d."RecipientE164",
       count(*) AS "Documents",
       count(DISTINCT coalesce('R' || d."RouteCustomerId"::text, 'C' || d."CardCode")) AS "Customers",
       string_agg(DISTINCT coalesce(d."RouteCustomerName", d."CardName", d."CardCode"), ' | ') AS "CustomerNames",
       string_agg(DISTINCT coalesce(c."ConsentRecordedBy", d."RequestedBy"), ', ') AS "SavedOrTypedBy"
FROM "CustomerDocumentDeliveries" d
LEFT JOIN "CustomerWhatsAppContacts" c ON c."Id" = d."ContactId"
WHERE d."CreatedAtUtc" >= now() - interval '$Days days'
GROUP BY d."RecipientE164"
HAVING count(DISTINCT coalesce('R' || d."RouteCustomerId"::text, 'C' || d."CardCode")) > 1
ORDER BY count(*) DESC
"@)
if ($shared.Count -eq 0) { Write-Host '  (none)' }
foreach ($s in $shared) {
    Write-Host ("  {0}  {1} documents for {2} customers, by {3}" -f (Format-Number $s.RecipientE164), $s.Documents, $s.Customers, $s.SavedOrTypedBy)
    Write-Host ("      {0}" -f $s.CustomerNames)
}

# --- 3. One number saved on several customers --------------------------------------------------
Write-Section '3. Numbers saved on more than one customer (every live contact)'
$multi = Invoke-JsonQuery @'
SELECT "PhoneE164",
       count(*) AS "Customers",
       string_agg("OwnerName" || ' (by ' || "ConsentRecordedBy" || ' via ' || "ConsentSource" || ', '
                  || to_char("CreatedAtUtc" AT TIME ZONE 'Africa/Harare', 'YYYY-MM-DD') || ')', ' | ' ORDER BY "CreatedAtUtc") AS "Owners"
FROM "CustomerWhatsAppContacts"
WHERE "RemovedAtUtc" IS NULL
GROUP BY "PhoneE164"
HAVING count(*) > 1
ORDER BY count(*) DESC
'@
if ($multi.Count -eq 0) { Write-Host '  (none)' }
foreach ($m in $multi) {
    Write-Host ("  {0}  on {1} customers" -f (Format-Number $m.PhoneE164), $m.Customers)
    Write-Host ("      {0}" -f $m.Owners)
}

# --- 4. Who saved numbers in the window ----------------------------------------------------------
Write-Section "4. Numbers saved in the last $Days day(s), by who saved them"
$savers = Invoke-JsonQuery (@"
SELECT "ConsentRecordedBy", "ConsentSource",
       count(*) AS "Contacts",
       count(*) FILTER (WHERE "AutoSendInvoices") AS "Automatic",
       count(*) FILTER (WHERE "RemovedAtUtc" IS NOT NULL) AS "Removed"
FROM "CustomerWhatsAppContacts"
WHERE "CreatedAtUtc" >= now() - interval '$Days days'
GROUP BY "ConsentRecordedBy", "ConsentSource"
ORDER BY count(*) DESC
"@)
if ($savers.Count -eq 0) { Write-Host '  (none)' }
foreach ($v in $savers) {
    Write-Host ("  {0,-30} {1,-11} {2,4} saved, {3} automatic, {4} since removed" -f $v.ConsentRecordedBy, $v.ConsentSource, $v.Contacts, $v.Automatic, $v.Removed)
}

# --- 5. What the gateway logged as sent ------------------------------------------------------------
function ConvertTo-UtcTime($Value) {
    if ($null -eq $Value) { return $null }
    # pwsh 7 hands back a DateTime already; Windows PowerShell 5.1 hands back the ISO string.
    if ($Value -is [datetime]) { return $Value.ToUniversalTime() }
    $styles = [System.Globalization.DateTimeStyles]::AssumeUniversal -bor [System.Globalization.DateTimeStyles]::AdjustToUniversal
    return [datetime]::Parse([string]$Value, [System.Globalization.CultureInfo]::InvariantCulture, $styles)
}

function Get-Digits([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return '' }
    return ($Value.Split('@')[0] -replace '\D', '')
}

Write-Section "5. Everything the gateway logged as sent in the last $Days day(s) (times CAT)"
if (-not $OpenWaBaseUrl -or -not $OpenWaApiKey) {
    Write-Host '  OpenWA__BaseUrl or OpenWA__ApiKey is not set in any API web.config; pass -OpenWaBaseUrl and -OpenWaApiKey.'
} else {
    $headers = @{ 'X-API-Key' = $OpenWaApiKey }
    $since = (Get-Date).ToUniversalTime().AddDays(-$Days)

    # Which delivery each logged document was: same chat, same file name.
    $byChatAndFile = @{}
    foreach ($d in $deliveries) {
        if (-not $d.FileName) { continue }
        $key = (Get-Digits $d.RecipientE164) + '|' + $d.FileName
        if (-not $byChatAndFile.ContainsKey($key)) { $byChatAndFile[$key] = New-Object System.Collections.Generic.List[string] }
        $byChatAndFile[$key].Add("#$($d.Id)")
    }
    $addressed = @{}
    foreach ($d in $deliveries) { $addressed[(Get-Digits $d.RecipientE164)] = $true }

    try {
        $gatewaySessions = @((Invoke-RestMethod -Method Get -Uri "$OpenWaBaseUrl/api/sessions" -Headers $headers -TimeoutSec 20) | ForEach-Object { $_ })
    } catch {
        $gatewaySessions = @()
        Write-Host "  !! Could not list the gateway's sessions at ${OpenWaBaseUrl}: $($_.Exception.Message)"
    }

    $strangers = @{}
    foreach ($s in $gatewaySessions) {
        $sent = New-Object System.Collections.Generic.List[object]
        $offset = 0
        $pageSize = 200
        $complete = $false
        try {
            while ($offset -lt 20000) {
                $page = Invoke-RestMethod -Method Get -Headers $headers -TimeoutSec 30 `
                    -Uri ("{0}/api/sessions/{1}/messages?limit={2}&offset={3}" -f $OpenWaBaseUrl, [uri]::EscapeDataString($s.id), $pageSize, $offset)
                $messages = @($page.messages | ForEach-Object { $_ })
                foreach ($m in $messages) {
                    $at = ConvertTo-UtcTime $m.createdAt
                    if ($at -ge $since -and $m.direction -ne 'incoming') { $sent.Add([pscustomobject]@{ At = $at; Message = $m }) }
                }
                # Newest first: stop at the first page that reaches past the window, or the last page.
                $oldest = if ($messages.Count -gt 0) { ConvertTo-UtcTime $messages[-1].createdAt } else { $null }
                if ($messages.Count -lt $pageSize -or ($oldest -and $oldest -lt $since)) { $complete = $true; break }
                $offset += $pageSize
            }
        } catch {
            Write-Host ''
            Write-Host ("  !! Could not read the log of session '{0}' ({1}): {2}" -f $s.name, $s.id, $_.Exception.Message)
        }

        Write-Host ''
        Write-Host ("  Session '{0}'  {1}  phone {2}  id {3}: {4} sent{5}" -f `
            $s.name, $s.status, (Format-Number $s.phone), $s.id, $sent.Count, $(if ($complete) { '' } else { ' (log not read to the end)' }))

        foreach ($entry in ($sent | Sort-Object At -Descending)) {
            $m = $entry.Message
            $chat = Get-Digits $m.chatId
            $key = $chat + '|' + $m.body
            if ($byChatAndFile.ContainsKey($key)) {
                $verdict = 'ShopInventory delivery ' + ($byChatAndFile[$key] -join '/')
            } elseif ($m.type -ne 'text' -and $addressed.ContainsKey($chat)) {
                $verdict = '!! not a ShopInventory delivery, to a number ShopInventory did send to'
            } elseif ($m.type -ne 'text') {
                $verdict = '!! not a ShopInventory delivery, to a number ShopInventory never addressed'
            } else {
                $verdict = 'text, not a document'
            }
            if ($verdict -like '!!*' -or $m.type -eq 'text') { $strangers[$chat] = $true }

            $body = Get-Text $m.body
            $body = ($body -replace '\s+', ' ')
            if ($body.Length -gt 60) { $body = $body.Substring(0, 57) + '...' }
            $chatShown = if ($m.chatId -match '@(c\.us|s\.whatsapp\.net)$' -or $m.chatId -notmatch '@') { Format-Number $chat } else { (Format-Number $chat) + ' @' + $m.chatId.Split('@')[1] }
            Write-Host ("    {0}  {1,-8} {2,-10} to {3,-16} {4}" -f $entry.At.AddHours(2).ToString('yyyy-MM-dd HH:mm:ss'), $m.type, $m.status, $chatShown, $body)
            Write-Host ("        {0}" -f $verdict)
        }
    }

    Write-Host ''
    $notOurs = @($strangers.Keys | Where-Object { -not $addressed.ContainsKey($_) } | Sort-Object)
    if ($notOurs.Count -gt 0) {
        Write-Host ('  Chats the gateway was asked to send to that no ShopInventory delivery names: {0}' -f (($notOurs | ForEach-Object { Format-Number $_ }) -join ', '))
    } else {
        Write-Host '  Every chat the gateway was asked to send to is one a ShopInventory delivery names.'
    }
    Write-Host '  A document on the phone in a chat not listed in this section was put there by WhatsApp Web, not asked for.'
}

Write-Host ''
Write-Host 'Read-only: nothing was changed.'
