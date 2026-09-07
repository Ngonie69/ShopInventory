<#
.SYNOPSIS
    Proves the WhatsApp delivery path end to end: API -> OpenWA -> webhook -> inbox.

.DESCRIPTION
    A paired WhatsApp session and a healthy gateway prove nothing on their own. Messages reach the
    inbox only if OpenWA holds a webhook aimed at this API AND that webhook is signed with the
    secret this API verifies against. Both of those are invisible from the console's session list,
    and when either is wrong the symptom is an inbox that stays empty with no error anywhere.

    This walks the whole path against a running pair:

      1. The API answers /api/whatsapp/health, so OpenWA is reachable from the API.
      2. Creating a session leaves OpenWA holding a webhook aimed at OpenWA:WebhookPublicUrl.
      3. A delivery signed with OpenWA:WebhookSecret is accepted (202).
      4. A delivery signed with the wrong secret is rejected, so step 3 proved something.
      5. The accepted delivery is readable back from /api/whatsapp/messages.

    Then it deletes the session it made. Nothing else is touched.

    Run it after deploying, after rotating the webhook secret, and after any OpenWA reinstall -
    a reinstall that starts OpenWA on an empty data directory drops every webhook it held.

.PARAMETER ApiBaseUrl
    The ShopInventory API. Production is http://10.10.10.9:5106.

.PARAMETER OpenWaBaseUrl
    The OpenWA gateway. Must match the API's OpenWA:BaseUrl.

.PARAMETER OpenWaApiKey
    An OpenWA key with at least the operator role. Must match the API's OpenWA:ApiKey.
    On the OpenWA host this is the contents of data\.api-key.

.PARAMETER WebhookSecret
    Must match the API's OpenWA:WebhookSecret. Step 4 fails if this is wrong in a way that makes
    step 3 pass, which is the one combination a single positive test cannot catch.

.PARAMETER Username
.PARAMETER Password
    An Admin account on the API. Every /api/whatsapp route except the inbound webhook is AdminOnly.

.EXAMPLE
    ./scripts/Test-WhatsAppDeliveryPath.ps1 -ApiBaseUrl http://127.0.0.1:5106 `
        -OpenWaApiKey (Get-Content OpenWA/data/.api-key -Raw).Trim() `
        -WebhookSecret 'the-configured-secret' -Username admin -Password admin123
#>
[CmdletBinding()]
param(
    [string]$ApiBaseUrl = 'http://127.0.0.1:5106',
    [string]$OpenWaBaseUrl = 'http://127.0.0.1:2785',
    [Parameter(Mandatory = $true)][string]$OpenWaApiKey,
    [Parameter(Mandatory = $true)][string]$WebhookSecret,
    [Parameter(Mandatory = $true)][string]$Username,
    [Parameter(Mandatory = $true)][string]$Password,
    [string]$SessionNamePrefix = 'delivery-check'
)

$ErrorActionPreference = 'Stop'

$script:Failures = @()
$script:StepNumber = 0

function Write-Step {
    param([string]$Name)
    $script:StepNumber++
    Write-Host ""
    Write-Host "[$script:StepNumber] $Name" -ForegroundColor Cyan
}

function Assert-That {
    param([string]$Claim, [bool]$Condition, [string]$Detail)

    if ($Condition) {
        Write-Host "    PASS  $Claim" -ForegroundColor Green
        if ($Detail) { Write-Host "          $Detail" -ForegroundColor DarkGray }
    }
    else {
        Write-Host "    FAIL  $Claim" -ForegroundColor Red
        if ($Detail) { Write-Host "          $Detail" -ForegroundColor Yellow }
        $script:Failures += $Claim
    }
}

function Get-HmacSignature {
    param([string]$Secret, [string]$Payload)

    $hmac = [System.Security.Cryptography.HMACSHA256]::new([Text.Encoding]::UTF8.GetBytes($Secret))
    try {
        $hash = $hmac.ComputeHash([Text.Encoding]::UTF8.GetBytes($Payload))
        return 'sha256=' + ([BitConverter]::ToString($hash) -replace '-', '').ToLowerInvariant()
    }
    finally {
        $hmac.Dispose()
    }
}

# Invoke-RestMethod throws on any non-2xx, and a rejection is a result here rather than an error -
# step 4 is built on getting a 401 back. This returns the status either way.
function Invoke-Api {
    param(
        [string]$Method,
        [string]$Uri,
        [hashtable]$Headers = @{},
        [string]$Body
    )

    $arguments = @{
        Method             = $Method
        Uri                = $Uri
        Headers            = $Headers
        SkipHttpErrorCheck = $true
        TimeoutSec         = 60
    }

    if ($PSBoundParameters.ContainsKey('Body')) {
        $arguments.Body = $Body
        $arguments.ContentType = 'application/json'
    }

    $response = Invoke-WebRequest @arguments
    $parsed = $null
    if ($response.Content) {
        try { $parsed = $response.Content | ConvertFrom-Json } catch { $parsed = $null }
    }

    return [pscustomobject]@{
        StatusCode = [int]$response.StatusCode
        Content    = $response.Content
        Json       = $parsed
    }
}

$ApiBaseUrl = $ApiBaseUrl.TrimEnd('/')
$OpenWaBaseUrl = $OpenWaBaseUrl.TrimEnd('/')
$sessionName = "$SessionNamePrefix-$(Get-Random -Minimum 10000 -Maximum 99999)"
$createdSessionId = $null

Write-Host "WhatsApp delivery path" -ForegroundColor White
Write-Host "  API     $ApiBaseUrl"
Write-Host "  OpenWA  $OpenWaBaseUrl"

try {
    Write-Step "Sign in to the API"
    $login = Invoke-Api -Method Post -Uri "$ApiBaseUrl/api/auth/login" `
        -Body (@{ username = $Username; password = $Password } | ConvertTo-Json)

    # The token key is accessToken, not token - the other spelling silently yields a null bearer
    # and every later step comes back 401 looking like a permissions problem.
    $token = $login.Json.accessToken
    Assert-That "The API issued an access token" ([bool]$token) "HTTP $($login.StatusCode)"
    if (-not $token) { throw "Cannot continue without a token. Body: $($login.Content)" }

    $authHeaders = @{ Authorization = "Bearer $token" }
    $openWaHeaders = @{ 'X-API-Key' = $OpenWaApiKey }

    Write-Step "The API can reach OpenWA"
    $health = Invoke-Api -Method Get -Uri "$ApiBaseUrl/api/whatsapp/health" -Headers $authHeaders
    Assert-That "GET /api/whatsapp/health answers 200" ($health.StatusCode -eq 200) `
        "HTTP $($health.StatusCode). $($health.Content)"
    Assert-That "OpenWA reports a status" ([bool]$health.Json.status) `
        "status=$($health.Json.status) via $($health.Json.sourcePath)"

    Write-Step "Creating a session registers the inbound webhook"
    $create = Invoke-Api -Method Post -Uri "$ApiBaseUrl/api/whatsapp/sessions" -Headers $authHeaders `
        -Body (@{ name = $sessionName } | ConvertTo-Json)
    Assert-That "POST /api/whatsapp/sessions answers 201" ($create.StatusCode -eq 201) `
        "HTTP $($create.StatusCode). $($create.Content)"
    $createdSessionId = $create.Json.id
    if (-not $createdSessionId) { throw "No session id was returned. Body: $($create.Content)" }

    # Read it from OpenWA rather than from the API's own status endpoint: the point is that OpenWA
    # is holding the registration, and asking the API to confirm its own claim proves nothing.
    $webhooks = Invoke-Api -Method Get `
        -Uri "$OpenWaBaseUrl/api/sessions/$createdSessionId/webhooks" -Headers $openWaHeaders
    # Never $webhooks.Content: OpenWA returns the webhook entity raw, and that includes the shared
    # secret in clear. Report the shape instead, and let the assertions below read the fields.
    Assert-That "OpenWA lists webhooks for the new session" ($webhooks.StatusCode -eq 200) `
        "HTTP $($webhooks.StatusCode), $(@($webhooks.Json).Count) webhook(s)"

    $status = Invoke-Api -Method Get `
        -Uri "$ApiBaseUrl/api/whatsapp/sessions/$createdSessionId/webhook" -Headers $authHeaders
    $expectedUrl = $status.Json.expectedUrl
    Assert-That "The API knows which URL OpenWA should deliver to" ([bool]$expectedUrl) `
        "OpenWA:WebhookPublicUrl = $expectedUrl"

    $matching = @($webhooks.Json | Where-Object { $_.url.TrimEnd('/') -eq $expectedUrl.TrimEnd('/') })
    Assert-That "OpenWA holds a webhook aimed at that URL" ($matching.Count -ge 1) `
        "OpenWA holds $(@($webhooks.Json).Count) webhook(s): $(@($webhooks.Json | ForEach-Object { $_.url }) -join ', ')"
    if ($matching.Count -ge 1) {
        Assert-That "That webhook is active" ([bool]$matching[0].active) "id=$($matching[0].id)"
        Assert-That "It subscribes to message.received" ($matching[0].events -contains 'message.received') `
            "events: $($matching[0].events -join ', ')"
    }

    Write-Step "A correctly signed delivery is accepted"
    $marker = "delivery-check-$([guid]::NewGuid().ToString('N'))"
    $payload = @{
        event   = 'message.received'
        session = $sessionName
        data    = @{
            id        = $marker
            from      = '263000000000@c.us'
            body      = "Delivery path check $marker"
            timestamp = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
            fromMe    = $false
        }
    } | ConvertTo-Json -Depth 6 -Compress

    $accepted = Invoke-Api -Method Post -Uri "$ApiBaseUrl/api/whatsapp/webhook/openwa" -Body $payload `
        -Headers @{
            'X-OpenWA-Signature'       = Get-HmacSignature -Secret $WebhookSecret -Payload $payload
            'X-Webhook-Event'          = 'message.received'
            'X-OpenWA-Idempotency-Key' = $marker
        }
    Assert-That "A signed delivery answers 202" ($accepted.StatusCode -eq 202) `
        "HTTP $($accepted.StatusCode). $($accepted.Content)"

    Write-Step "A wrongly signed delivery is rejected"
    # Without this, step 3 would also pass against an API that verifies nothing at all.
    $forged = Invoke-Api -Method Post -Uri "$ApiBaseUrl/api/whatsapp/webhook/openwa" -Body $payload `
        -Headers @{
            'X-OpenWA-Signature' = Get-HmacSignature -Secret "$WebhookSecret-wrong" -Payload $payload
            'X-Webhook-Event'    = 'message.received'
        }
    Assert-That "An unsigned-for-this-API delivery is refused" ($forged.StatusCode -ge 400) `
        "HTTP $($forged.StatusCode)"

    Write-Step "The accepted delivery is readable from the inbox"
    $inbox = Invoke-Api -Method Get -Uri "$ApiBaseUrl/api/whatsapp/messages?page=1&pageSize=50" -Headers $authHeaders
    Assert-That "GET /api/whatsapp/messages answers 200" ($inbox.StatusCode -eq 200) `
        "HTTP $($inbox.StatusCode)"
    Assert-That "The delivery is in the inbox" ($inbox.Content -like "*$marker*") `
        "Looked for $marker in $($inbox.Json.totalCount) captured event(s)"
}
finally {
    if ($createdSessionId) {
        Write-Step "Clean up the session this check created"
        try {
            $delete = Invoke-Api -Method Delete `
                -Uri "$OpenWaBaseUrl/api/sessions/$createdSessionId" -Headers @{ 'X-API-Key' = $OpenWaApiKey }
            if ($delete.StatusCode -lt 300) {
                Write-Host "    Removed $sessionName ($createdSessionId)" -ForegroundColor DarkGray
            }
            else {
                Write-Host "    Could not remove $sessionName ($createdSessionId): HTTP $($delete.StatusCode). Delete it from the console." -ForegroundColor Yellow
            }
        }
        catch {
            Write-Host "    Could not remove $sessionName ($createdSessionId): $($_.Exception.Message)" -ForegroundColor Yellow
        }
    }
}

Write-Host ""
if ($script:Failures.Count -eq 0) {
    Write-Host "Delivery path is intact. A message sent to the paired number reaches the inbox." -ForegroundColor Green
    exit 0
}

Write-Host "$($script:Failures.Count) check(s) failed:" -ForegroundColor Red
$script:Failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
exit 1
