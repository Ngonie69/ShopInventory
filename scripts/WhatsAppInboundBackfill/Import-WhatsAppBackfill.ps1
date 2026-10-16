<#
.SYNOPSIS
    Adds the received WhatsApp messages the API never stored to WhatsAppWebhookEvents, by replaying
    them through the API's own webhook. Dry run unless -Apply.

.DESCRIPTION
    Reads the folder Export-WhatsAppReceivedMessages.js wrote and, for each message:

      - leaves out what the API would not keep as a received message: the session's own messages
        (fromMe) and WhatsApp Status posts;
      - leaves out what WhatsAppWebhookEvents already holds, matched on the deduplication key
        ShopInventory#762 builds (openwa:message.received:{sessionId}:{messageId}) OR on the same
        session and message id under any other key - the first message ever received is stored
        under OpenWA's broken `msg_unknown`, and it must not be added twice;
      - everything left is recoverable.

    Without -Apply it only reads (Postgres with default_transaction_read_only=on) and writes
    backfill-plan.csv beside the export for review.

    With -Apply it POSTs each recoverable payload, byte for byte as exported, to the webhook OpenWA
    delivers to, signed with the API's own OpenWA:WebhookSecret. So the row is written by the same
    handler that writes a live message: same parsing, same columns, same key. It sends the key in
    X-OpenWA-Idempotency-Key as well, so an API that does not have #762 yet stores the same key; on
    one that does, the handler derives it from the body and the header is not read. DeliveryId is
    `backfill-<uuid>`, which marks every recovered row. Afterwards it reads the table again and
    reports, per message, whether the row is there.

    Idempotent: a second run finds the rows stored and sends nothing, and a later redelivery of the
    same message by OpenWA replays the stored receipt instead of adding a copy.

    Deploy #762 before running with -Apply. Until it is live every new message is still being lost,
    and a backfill run before it would have to be repeated for the days in between.

    Run on KFL-DNS2 (10.10.10.9) in Windows PowerShell 5.1.

.PARAMETER ExportPath
    The --out folder of Export-WhatsAppReceivedMessages.js.

.PARAMETER Apply
    Send the recoverable messages. Without it nothing is written anywhere except backfill-plan.csv.

.PARAMETER WebhookUrl
.PARAMETER WebhookSecret
.PARAMETER ConnectionString
    Override what is read from the API's web.config (OpenWA__WebhookPublicUrl, OpenWA__WebhookSecret,
    ConnectionStrings__DefaultConnection). For running against a non-production API.

.PARAMETER DropOversizedMedia
    The webhook takes at most 1 MB (ReceiveOpenWaWebhook has no [MaxRequestBodySize], so the
    middleware's default applies), and a message whose media makes the body larger is answered 413 -
    live, too. With this switch such a message is sent again without data.media, so its row (type,
    sender, time, caption) is stored and only the attachment is lost; Media dropped is recorded.
    Without it those messages are left out and reported, to wait for a larger limit.

.PARAMETER WebhookBodyLimitBytes
    The limit the dry run measures against. Default 1 MB.

.PARAMETER DelayMilliseconds
    Pause between sends. The webhook is on the API's sliding-window rate limit; a 429 is waited
    out and retried, but there is no reason to provoke one.

.EXAMPLE
    .\Import-WhatsAppBackfill.ps1 -ExportPath C:\Temp\wa-backfill

.EXAMPLE
    .\Import-WhatsAppBackfill.ps1 -ExportPath C:\Temp\wa-backfill -Apply
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$ExportPath,
    [switch]$Apply,
    [string]$InetpubRoot = 'C:\inetpub',
    [string]$WebhookUrl,
    [string]$WebhookSecret,
    [string]$ConnectionString,
    [switch]$DropOversizedMedia,
    [int]$WebhookBodyLimitBytes = 1MB,
    [int]$DelayMilliseconds = 250
)

$ErrorActionPreference = 'Stop'

# The handler's limit (IdempotencyRequests holds 200 characters); a longer key is hashed, not cut.
$MaxDeduplicationKeyLength = 200

function Write-Section([string]$Title) {
    Write-Host ''
    Write-Host ('=' * 78)
    Write-Host $Title
    Write-Host ('=' * 78)
}

function Get-Masked([string]$Value) {
    if ([string]::IsNullOrWhiteSpace($Value)) { return '(none)' }
    $digits = ($Value -replace '\D', '')
    if ($digits.Length -le 4) { return '****' }
    return '****' + $digits.Substring($digits.Length - 4)
}

function Get-Sha256Hex([string]$Text) {
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Text))) -replace '-', '').ToLowerInvariant()
    } finally {
        $sha.Dispose()
    }
}

# ReceiveOpenWAWebhookHandler.DeduplicationKey for a message.received with a message id.
function Get-DeduplicationKey([string]$SessionId, [string]$MessageId) {
    $session = if ($null -eq $SessionId) { '' } else { $SessionId.Trim() }
    $key = 'openwa:message.received:{0}:{1}' -f $session, $MessageId.Trim()
    if ($key.Length -gt $MaxDeduplicationKeyLength) {
        $key = 'openwa:sha256:' + (Get-Sha256Hex $key)
    }
    return $key
}

function Get-HmacSignature([string]$Secret, [byte[]]$Payload) {
    # The handler trims the configured secret before it signs.
    $hmac = [System.Security.Cryptography.HMACSHA256]::new([Text.Encoding]::UTF8.GetBytes($Secret.Trim()))
    try {
        return 'sha256=' + ([BitConverter]::ToString($hmac.ComputeHash($Payload)) -replace '-', '').ToLowerInvariant()
    } finally {
        $hmac.Dispose()
    }
}

# --- Settings ------------------------------------------------------------------------------
$configWebhookUrl = $null
$configSecret = $null
$configConnection = $null
foreach ($dir in Get-ChildItem -Path $InetpubRoot -Directory -Filter 'ShopInventory-API*' -ErrorAction SilentlyContinue) {
    $configPath = Join-Path $dir.FullName 'web.config'
    if (-not (Test-Path $configPath)) { continue }
    [xml]$config = Get-Content -Path $configPath -Raw
    foreach ($var in $config.SelectNodes('//environmentVariable')) {
        $name = $var.GetAttribute('name')
        $value = $var.GetAttribute('value')
        if ($name -eq 'ConnectionStrings__DefaultConnection' -and -not $configConnection) { $configConnection = $value }
        if ($name -eq 'OpenWA__WebhookPublicUrl' -and -not $configWebhookUrl) { $configWebhookUrl = $value }
        if ($name -eq 'OpenWA__WebhookSecret' -and -not $configSecret) { $configSecret = $value }
    }
}
if (-not $WebhookUrl) { $WebhookUrl = $configWebhookUrl }
if (-not $WebhookSecret) { $WebhookSecret = $configSecret }
if (-not $ConnectionString) { $ConnectionString = $configConnection }
if (-not $ConnectionString) { throw 'No connection string: pass -ConnectionString or run on the API host.' }

$parts = @{}
foreach ($pair in $ConnectionString -split ';') {
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

# Read-only. One JSON document per query, so the quoting survives PowerShell 5.1 (SQL goes through a
# file, never -c). The rows are written by the API's webhook, never by this script.
function Invoke-JsonQuery([string]$Sql) {
    $sqlFile = Join-Path $env:TEMP ("whatsapp-backfill-{0}.sql" -f [guid]::NewGuid().ToString('N'))
    "SELECT coalesce(json_agg(t), '[]'::json) FROM ($Sql) t;" | Set-Content -Path $sqlFile -Encoding ASCII
    $env:PGPASSWORD = $parts['password']
    $env:PGOPTIONS = '-c default_transaction_read_only=on'
    try {
        $raw = & $psql -w -h $pgHost -p $pgPort -U $pgUser -d $pgDb -A -t -q -P pager=off -f $sqlFile
        if ($LASTEXITCODE -ne 0) { throw "psql exited with $LASTEXITCODE" }
        # The comma keeps a one-row result an array: returned bare, 5.1 hands back a PSCustomObject with no Count.
        return ,@((($raw -join "`n") | ConvertFrom-Json) | ForEach-Object { $_ })
    } finally {
        Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
        Remove-Item Env:PGOPTIONS -ErrorAction SilentlyContinue
        Remove-Item $sqlFile -ErrorAction SilentlyContinue
    }
}

function Get-StoredMessages {
    $stored = Invoke-JsonQuery @'
SELECT "Id", "IdempotencyKey", "SessionName", "MessageId"
FROM "WhatsAppWebhookEvents"
WHERE "EventType" = 'message.received'
'@
    $byKey = @{}
    $byMessage = @{}
    foreach ($row in $stored) {
        if ($row.IdempotencyKey) { $byKey[[string]$row.IdempotencyKey] = $row.Id }
        if ($row.MessageId) { $byMessage[('{0}|{1}' -f ([string]$row.SessionName).Trim(), ([string]$row.MessageId).Trim())] = $row.Id }
    }
    return @{ ByKey = $byKey; ByMessage = $byMessage; Count = $stored.Count }
}

# --- Read the export -----------------------------------------------------------------------
$ExportPath = (Resolve-Path $ExportPath).Path
$indexPath = Join-Path $ExportPath 'index.jsonl'
if (-not (Test-Path $indexPath)) { throw "No index.jsonl in $ExportPath" }

$entries = New-Object System.Collections.Generic.List[object]
foreach ($line in [IO.File]::ReadAllLines($indexPath, [Text.Encoding]::UTF8)) {
    if ([string]::IsNullOrWhiteSpace($line)) { continue }
    $entries.Add(($line | ConvertFrom-Json))
}

$summaryPath = Join-Path $ExportPath 'summary.json'
if (Test-Path $summaryPath) {
    $exportSummary = Get-Content $summaryPath -Raw -Encoding UTF8 | ConvertFrom-Json
    Write-Section ("Export of {0} (messages since {1}, whatsapp-web.js {2})" -f $exportSummary.exportedAtUtc, $exportSummary.since, $exportSummary.whatsappWebJs)
    foreach ($s in $exportSummary.sessions) {
        if ($s.error) { Write-Host ("  {0}  FAILED: {1}" -f $s.sessionId, $s.error); continue }
        $limited = @($s.chats | Where-Object { $_.limitReached }).Count
        $broken = @($s.chats | Where-Object { $_.error }).Count
        Write-Host ("  {0}  {1} kept from {2} chats; {3} chats hit --per-chat; {4} chats unreadable" -f $s.sessionId, $s.kept, @($s.chats).Count, $limited, $broken)
    }
}

# --- Classify ------------------------------------------------------------------------------
$stored = Get-StoredMessages
$plan = New-Object System.Collections.Generic.List[object]
foreach ($entry in $entries) {
    $key = Get-DeduplicationKey $entry.sessionId $entry.id
    $messageKey = '{0}|{1}' -f ([string]$entry.sessionId).Trim(), ([string]$entry.id).Trim()
    $chatIds = @($entry.chatId, $entry.from) | Where-Object { $_ }
    $isStatus = ($entry.isStatus -eq $true) -or (@($chatIds | Where-Object { $_.Trim() -ieq 'status@broadcast' }).Count -gt 0)

    $outcome = if ($entry.fromMe -eq $true) { 'skip:fromMe' }
        elseif ($isStatus) { 'skip:statusPost' }
        elseif ($stored.ByKey.ContainsKey($key)) { 'stored' }
        elseif ($stored.ByMessage.ContainsKey($messageKey)) { 'storedUnderOtherKey' }
        else { 'recoverable' }

    $occurred = [DateTimeOffset]::FromUnixTimeSeconds([long]$entry.timestamp).UtcDateTime
    $plan.Add([pscustomobject]@{
        Outcome     = $outcome
        File        = $entry.file
        DeliveryId  = $entry.deliveryId
        Key         = $key
        SessionId   = $entry.sessionId
        MessageId   = $entry.id
        From        = Get-Masked $entry.from
        IsGroup     = ([string]$entry.chatId).EndsWith('@g.us')
        OccurredUtc = $occurred.ToString('yyyy-MM-dd HH:mm:ss')
        Type        = $entry.type
        HasMedia    = $entry.hasMedia
        Bytes       = $entry.bytes
        Preview     = (([string]$entry.preview) -replace '[\r\n]+', ' ')
    })
}

$planPath = Join-Path $ExportPath 'backfill-plan.csv'
$plan | Sort-Object OccurredUtc | Export-Csv -Path $planPath -NoTypeInformation -Encoding UTF8

Write-Section 'Plan'
Write-Host ("  WhatsAppWebhookEvents holds {0} received rows now." -f $stored.Count)
Write-Host ("  {0} messages in the export." -f $plan.Count)
foreach ($group in $plan | Group-Object Outcome | Sort-Object Name) {
    Write-Host ("    {0,-22} {1,6}" -f $group.Name, $group.Count)
}
$recoverable = @($plan | Where-Object { $_.Outcome -eq 'recoverable' } | Sort-Object OccurredUtc)

if ($recoverable.Count -gt 0) {
    Write-Host ''
    Write-Host '  Recoverable by session and day (UTC):'
    foreach ($group in $recoverable | Group-Object { '{0}  {1}' -f $_.SessionId, $_.OccurredUtc.Substring(0, 10) } | Sort-Object Name) {
        Write-Host ("    {0}  {1,5}" -f $group.Name, $group.Count)
    }
    Write-Host ''
    Write-Host ("  Recoverable from {0} senders; {1} carry media; {2:N0} KB of payload." -f `
        @($recoverable | Group-Object From).Count, @($recoverable | Where-Object { $_.HasMedia -eq $true }).Count, (($recoverable | Measure-Object Bytes -Sum).Sum / 1KB))
    $oversized = @($recoverable | Where-Object { [long]$_.Bytes -gt $WebhookBodyLimitBytes }).Count
    if ($oversized -gt 0) {
        Write-Host ("  {0} are over the webhook's {1:N0} KB body limit and will be answered 413; -DropOversizedMedia sends them without the attachment." -f $oversized, ($WebhookBodyLimitBytes / 1KB)) -ForegroundColor Yellow
    }
}
Write-Host ''
Write-Host "  Every message and its outcome: $planPath"

if (-not $Apply) {
    Write-Host ''
    Write-Host '  Dry run: nothing was sent. Review the plan, then run again with -Apply.' -ForegroundColor Yellow
    return
}

# --- Apply ---------------------------------------------------------------------------------
if (-not $WebhookUrl) { throw 'No webhook URL: OpenWA__WebhookPublicUrl is not in web.config; pass -WebhookUrl.' }
if (-not $WebhookSecret) { throw 'No webhook secret: OpenWA__WebhookSecret is not in web.config; pass -WebhookSecret.' }

Write-Section ("Sending {0} messages to {1}" -f $recoverable.Count, $WebhookUrl)

# The exporter writes data.media as {"mimetype":..,"filename":..?,"data":..} in that order (as OpenWA
# builds it), so this removes exactly that object. Base64 holds no quote or brace.
$MediaPattern = ',"media":\{"mimetype":"(?:[^"\\]|\\.)*"(?:,"filename":"(?:[^"\\]|\\.)*")?,"data":"[^"]*"\}'

function Send-Payload($Item, [switch]$WithoutMedia) {
    $bytes = [IO.File]::ReadAllBytes((Join-Path $ExportPath $Item.File))
    if ($WithoutMedia) {
        $text = [Text.Encoding]::UTF8.GetString($bytes)
        $stripped = [regex]::Replace($text, $MediaPattern, '')
        if ($stripped.Length -eq $text.Length) { throw "No data.media to drop in $($Item.File)" }
        $bytes = (New-Object System.Text.UTF8Encoding($false)).GetBytes($stripped)
    }
    $headers = @{
        'X-OpenWA-Event'           = 'message.received'
        'X-OpenWA-Signature'       = Get-HmacSignature $WebhookSecret $bytes
        'X-OpenWA-Idempotency-Key' = $Item.Key
        'X-OpenWA-Delivery-Id'     = $Item.DeliveryId
        'X-OpenWA-Retry-Count'     = '0'
    }
    for ($attempt = 1; ; $attempt++) {
        $status = 0
        $content = $null
        $retryAfter = $null
        try {
            # Bytes, not a string: Windows PowerShell 5.1 would re-encode a string body, and the
            # signature is over these exact bytes.
            $response = Invoke-WebRequest -Method Post -Uri $WebhookUrl -Headers $headers -Body $bytes `
                -ContentType 'application/json; charset=utf-8' -UseBasicParsing -TimeoutSec 60
            $status = [int]$response.StatusCode
            $content = $response.Content
        } catch [System.Net.WebException] {
            $errorResponse = $_.Exception.Response
            if ($null -eq $errorResponse) {
                $content = $_.Exception.Message
            } else {
                $status = [int]$errorResponse.StatusCode
                $retryAfter = $errorResponse.Headers['Retry-After']
                $reader = New-Object System.IO.StreamReader($errorResponse.GetResponseStream())
                try { $content = $reader.ReadToEnd() } finally { $reader.Dispose() }
            }
        }

        $transient = $status -eq 0 -or $status -eq 409 -or $status -eq 429 -or $status -ge 500
        if (-not $transient -or $attempt -ge 5) {
            return [pscustomobject]@{ Status = $status; Content = $content; Attempts = $attempt }
        }
        $wait = if ($retryAfter -match '^\d+$') { [int]$retryAfter } else { [Math]::Min(60, 2 * $attempt * $attempt) }
        Start-Sleep -Seconds $wait
    }
}

$results = New-Object System.Collections.Generic.List[object]
$sent = 0
foreach ($item in $recoverable) {
    $result = Send-Payload $item
    $mediaDropped = $false
    if ($result.Status -eq 413 -and $DropOversizedMedia -and $item.HasMedia -eq $true) {
        $result = Send-Payload $item -WithoutMedia
        $mediaDropped = $true
    }
    $receiptId = $null
    if ($result.Status -eq 202 -and $result.Content) {
        try { $receiptId = ($result.Content | ConvertFrom-Json).id } catch { }
    }
    $results.Add([pscustomobject]@{
        File = $item.File; Key = $item.Key; OccurredUtc = $item.OccurredUtc
        Status = $result.Status; Attempts = $result.Attempts; ReceiptId = $receiptId; MediaDropped = $mediaDropped
        Error = $(if ($result.Status -eq 202) { $null } else { (([string]$result.Content) -replace '[\r\n]+', ' ') })
    })
    if ($result.Status -eq 202) { $sent++; if ($mediaDropped) { Write-Host ("  {0}  stored without its attachment (over the body limit)" -f $item.File) -ForegroundColor Yellow } }
    else { Write-Host ("  {0}  HTTP {1} after {2} attempts: {3}" -f $item.File, $result.Status, $result.Attempts, $result.Content) -ForegroundColor Red }

    # A wrong secret or a disabled integration fails every message alike; stop at the first.
    if ($result.Status -eq 401 -or $result.Status -eq 403 -or $result.Status -eq 404 -or $result.Status -eq 503) {
        Write-Host '  Stopping: this answer will be the same for every message.' -ForegroundColor Red
        break
    }
    if (($results.Count % 25) -eq 0) { Write-Host ("  {0} of {1} sent" -f $results.Count, $recoverable.Count) }
    Start-Sleep -Milliseconds $DelayMilliseconds
}

# --- Verify --------------------------------------------------------------------------------
Write-Section 'Verify: read the table again'
$after = Get-StoredMessages
$verified = 0
foreach ($r in $results) {
    $item = $recoverable | Where-Object { $_.File -eq $r.File } | Select-Object -First 1
    $messageKey = '{0}|{1}' -f ([string]$item.SessionId).Trim(), ([string]$item.MessageId).Trim()
    $rowId = if ($after.ByKey.ContainsKey($r.Key)) { $after.ByKey[$r.Key] } elseif ($after.ByMessage.ContainsKey($messageKey)) { $after.ByMessage[$messageKey] } else { $null }
    $r | Add-Member -NotePropertyName StoredRowId -NotePropertyValue $rowId
    $r | Add-Member -NotePropertyName StoredUnderKey -NotePropertyValue $after.ByKey.ContainsKey($r.Key)
    if ($rowId) { $verified++ }
}
$resultsPath = Join-Path $ExportPath ("backfill-results-{0:yyyyMMdd-HHmmss}.csv" -f (Get-Date))
$results | Export-Csv -Path $resultsPath -NoTypeInformation -Encoding UTF8

Write-Host ("  Received rows before {0}, after {1} (+{2})." -f $stored.Count, $after.Count, ($after.Count - $stored.Count))
Write-Host ("  {0} of {1} recoverable messages sent with 202; {2} now have a row." -f $sent, $recoverable.Count, $verified)
$wrongKey = @($results | Where-Object { $_.StoredRowId -and -not $_.StoredUnderKey }).Count
if ($wrongKey -gt 0) {
    Write-Host ("  !! {0} rows were stored under a different key than #762 builds. Check the API version." -f $wrongKey) -ForegroundColor Red
}
Write-Host "  Per message: $resultsPath"
if ($verified -ne $recoverable.Count) {
    Write-Host '  Not every message was stored. Run again: what is stored is skipped, the rest is retried.' -ForegroundColor Yellow
    exit 1
}
