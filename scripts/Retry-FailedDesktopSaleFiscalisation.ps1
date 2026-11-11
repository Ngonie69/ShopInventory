<#
.SYNOPSIS
    Retries every till and vending sale whose fiscalisation failed, one after another, through the API's own
    retry endpoint (the Retry button on /desktop-sales).

.DESCRIPTION
    For use after an FDMS outage, once ZIMRA answers again. The sweep gives a failed sale 5 attempts 30 s apart,
    so an outage of more than a few minutes leaves every sale it touched Failed for a person to retry. This does
    that retry for all of them.

    It signs in as you (username, password and, if your account has it, the two-factor code), lists the Failed
    sales of the chosen sources and period, then posts each one to
    POST /api/DesktopIntegration/sales/{externalReference}/fiscalise, oldest first.

    Safe to run more than once. The endpoint asks the fiscal device whether it already holds a receipt for the
    sale before submitting anything, so a sale filed by an earlier attempt is adopted (AlreadyFiscalised), never
    signed twice.

    It stops after -StopAfterConsecutiveFailures failures in a row: that is FDMS still not answering, and every
    further retry would fail the same way. A sale the endpoint refuses outright (a van sale, one flagged for
    reconciliation, one still being fiscalised by its till) is reported and skipped; it does not count as a
    failure.

    Each fiscalised sale posts to SAP with the next pass of the posting job, as it would have had it fiscalised
    first time.

.PARAMETER BaseUrl
    The ShopInventory API. Defaults to production.

.PARAMETER FromDate
    First sale date (DocDate) to include. Defaults to two days ago.

.PARAMETER ToDate
    Last sale date to include. Defaults to today.

.PARAMETER SourceSystems
    Which sales to retry. Defaults to shop tills and vending, the two sources the retry endpoint accepts.

.PARAMETER StopAfterConsecutiveFailures
    Stop once this many sales in a row fail at the device. Defaults to 3.

.PARAMETER DelayMilliseconds
    Pause between sales. Defaults to 250 ms.

.PARAMETER Credential
    Your ShopInventory sign-in. Asked for when not given.

.PARAMETER TwoFactorCode
    The two-factor code, when your account has two-factor sign-in. Asked for when needed and not given.

.PARAMETER ReportPath
    Where to write the CSV of what happened to each sale. Defaults to a timestamped file beside this script.

.EXAMPLE
    ./scripts/Retry-FailedDesktopSaleFiscalisation.ps1 -WhatIf
    Signs in and lists the sales it would retry, without retrying any.

.EXAMPLE
    ./scripts/Retry-FailedDesktopSaleFiscalisation.ps1 -FromDate 2026-10-17 -ToDate 2026-10-17
    Retries the Failed till and vending sales sold on 17 October.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$BaseUrl = 'https://sis.kefaloscheese.com',
    [datetime]$FromDate = (Get-Date).Date.AddDays(-2),
    [datetime]$ToDate = (Get-Date).Date,
    [string[]]$SourceSystems = @('KefalosShopTill', 'KefalosVending'),
    [ValidateRange(1, 100)]
    [int]$StopAfterConsecutiveFailures = 3,
    [ValidateRange(0, 10000)]
    [int]$DelayMilliseconds = 250,
    [pscredential]$Credential,
    [string]$TwoFactorCode,
    [string]$ReportPath = (Join-Path $PSScriptRoot "fiscal-retry-$(Get-Date -Format 'yyyyMMdd-HHmmss').csv")
)

$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw 'Run this with PowerShell 7 (pwsh): it reads error responses with -SkipHttpErrorCheck.'
}
$BaseUrl = $BaseUrl.TrimEnd('/')

function Invoke-Api {
    param(
        [Parameter(Mandatory)][string]$Method,
        [Parameter(Mandatory)][string]$Path,
        [object]$Body,
        [string]$Token
    )
    $request = @{
        Method             = $Method
        Uri                = "$BaseUrl$Path"
        SkipHttpErrorCheck = $true
        TimeoutSec         = 180
        Headers            = @{ Accept = 'application/json' }
    }
    if ($Token) { $request.Headers.Authorization = "Bearer $Token" }
    if ($null -ne $Body) {
        $request.ContentType = 'application/json'
        $request.Body = $Body | ConvertTo-Json -Depth 5 -Compress
    }
    $response = Invoke-WebRequest @request
    $json = $null
    if ($response.Content) {
        try { $json = $response.Content | ConvertFrom-Json } catch { $json = $null }
    }
    [pscustomobject]@{
        Status     = [int]$response.StatusCode
        Json       = $json
        Raw        = [string]$response.Content
        RetryAfter = $response.Headers['Retry-After'] | Select-Object -First 1
    }
}

# The reason a refusal gives: the ErrorOr description carries the sale and the device's own words.
function Get-ProblemText {
    param($Response)
    $detail = $Response.Json.errorDetails | Select-Object -First 1
    if ($detail.description) { return [string]$detail.description }
    if ($Response.Json.detail) { return [string]$Response.Json.detail }
    if ($Response.Json.title) { return [string]$Response.Json.title }
    return "HTTP $($Response.Status) $($Response.Raw)".Trim()
}

# --- Sign in --------------------------------------------------------------------------------------------
$credential = if ($Credential) { $Credential } else { Get-Credential -Message "ShopInventory sign-in for $BaseUrl" }
$login = Invoke-Api -Method Post -Path '/api/Auth/login' -Body @{
    username = $credential.UserName
    password = $credential.GetNetworkCredential().Password
}
if ($login.Status -ne 200) { throw "Sign-in refused: $(Get-ProblemText $login)" }

if ($login.Json.requiresTwoFactor) {
    $code = if ($TwoFactorCode) { $TwoFactorCode } else { Read-Host 'Two-factor code' }
    $login = Invoke-Api -Method Post -Path '/api/Auth/login/two-factor' -Body @{
        twoFactorToken = $login.Json.twoFactorToken
        code           = $code.Trim()
        isBackupCode   = $false
    }
    if ($login.Status -ne 200) { throw "Two-factor sign-in refused: $(Get-ProblemText $login)" }
}
$token = $login.Json.accessToken
if (-not $token) { throw 'Sign-in answered without an access token.' }
Write-Host "Signed in as $($login.Json.user.username) ($($login.Json.user.role))." -ForegroundColor Green

# --- List every Failed sale first -----------------------------------------------------------------------
# All pages are read before the first retry: a retried sale leaves the Failed filter, so paging while
# retrying would skip a page's worth of sales.
$query = @(
    'fiscalizationStatuses=Failed'
    'sort=oldest'
    'pageSize=100'
    "fromDate=$($FromDate.ToString('yyyy-MM-dd'))"
    "toDate=$($ToDate.ToString('yyyy-MM-dd'))"
) + ($SourceSystems | ForEach-Object { "sourceSystems=$([uri]::EscapeDataString($_))" })

$sales = [System.Collections.Generic.List[object]]::new()
$page = 1
do {
    $list = Invoke-Api -Method Get -Path "/api/DesktopIntegration/sales?$($query -join '&')&page=$page" -Token $token
    if ($list.Status -ne 200) { throw "Could not list failed sales: $(Get-ProblemText $list)" }
    foreach ($sale in $list.Json.sales) { $sales.Add($sale) }
    $page++
} while ($list.Json.hasMore)

$period = "$($FromDate.ToString('yyyy-MM-dd')) to $($ToDate.ToString('yyyy-MM-dd'))"
Write-Host "$($sales.Count) Failed sale(s) from $($SourceSystems -join ', '), sold $period." -ForegroundColor Cyan
if ($sales.Count -eq 0) { return }

$sales | Format-Table -AutoSize @(
    @{ Label = 'Reference'; Expression = { $_.externalReferenceId } }
    @{ Label = 'Sale'; Expression = { $_.saleNumber } }
    @{ Label = 'Source'; Expression = { $_.sourceSystem } }
    @{ Label = 'Date'; Expression = { ([datetime]$_.docDate).ToString('yyyy-MM-dd') } }
    @{ Label = 'Total'; Expression = { '{0} {1:N2}' -f $_.currency, [decimal]$_.totalAmount } }
    @{ Label = 'Tries'; Expression = { $_.fiscalizationAttempts } }
    @{ Label = 'Last error'; Expression = { $e = [string]$_.fiscalError; if ($e.Length -gt 60) { $e.Substring(0, 60) + '...' } else { $e } } }
) | Out-Host

# --- Retry them, oldest first ---------------------------------------------------------------------------
$results = [System.Collections.Generic.List[object]]::new()
$consecutiveFailures = 0
$stoppedBecause = $null

foreach ($sale in $sales) {
    $reference = [string]$sale.externalReferenceId
    if (-not $PSCmdlet.ShouldProcess($reference, 'Retry fiscalisation')) { continue }

    $attempt = 0
    do {
        $attempt++
        $response = Invoke-Api -Method Post -Token $token `
            -Path "/api/DesktopIntegration/sales/$([uri]::EscapeDataString($reference))/fiscalise"
        $rateLimited = $response.Status -eq 429 -and $attempt -lt 5
        if ($rateLimited) {
            $wait = if ($response.RetryAfter -as [int]) { [int]$response.RetryAfter } else { 10 }
            Write-Host "  rate limited; waiting $wait s" -ForegroundColor DarkGray
            Start-Sleep -Seconds $wait
        }
    } while ($rateLimited)

    $code = [string]$response.Json.code
    $outcome, $message = switch ($true) {
        ($response.Status -eq 200) {
            [string]$response.Json.outcome, [string]$response.Json.message
            break
        }
        ($response.Status -eq 401) {
            'SignedOut', 'The sign-in expired or was revoked.'
            break
        }
        ($code -eq 'DesktopSales.SaleNotFiscalisable' -or $response.Status -in 403, 404) {
            'Skipped', (Get-ProblemText $response)
            break
        }
        default {
            'Failed', (Get-ProblemText $response)
        }
    }

    $results.Add([pscustomobject]@{
        ExternalReference = $reference
        SaleNumber        = $sale.saleNumber
        Source            = $sale.sourceSystem
        DocDate           = ([datetime]$sale.docDate).ToString('yyyy-MM-dd')
        Total             = $sale.totalAmount
        Currency          = $sale.currency
        Outcome           = $outcome
        ReceiptNumber     = $response.Json.fiscalReceiptNumber
        HttpStatus        = $response.Status
        ErrorCode         = $code
        Message           = $message
    })

    $colour = switch ($outcome) { 'Fiscalised' { 'Green' } 'AlreadyFiscalised' { 'Green' } 'Skipped' { 'Yellow' } default { 'Red' } }
    Write-Host ("{0,-18} {1}  {2}" -f $outcome, $reference, $message) -ForegroundColor $colour

    if ($outcome -eq 'SignedOut') {
        $stoppedBecause = 'the sign-in expired; run the script again to carry on'
        break
    }
    if ($outcome -eq 'Failed') {
        $consecutiveFailures++
        if ($consecutiveFailures -ge $StopAfterConsecutiveFailures) {
            $stoppedBecause = "$consecutiveFailures sales in a row failed at the device; FDMS is probably still not answering"
            break
        }
    }
    elseif ($outcome -ne 'Skipped') {
        $consecutiveFailures = 0
    }

    if ($DelayMilliseconds -gt 0) { Start-Sleep -Milliseconds $DelayMilliseconds }
}

# --- Report ---------------------------------------------------------------------------------------------
if ($results.Count -gt 0) {
    $results | Export-Csv -LiteralPath $ReportPath -NoTypeInformation -Encoding utf8
}

$fiscalised = @($results | Where-Object Outcome -in 'Fiscalised', 'AlreadyFiscalised').Count
$skipped = @($results | Where-Object Outcome -eq 'Skipped').Count
$failed = @($results | Where-Object Outcome -eq 'Failed').Count
$notTried = $sales.Count - $results.Count

Write-Host ''
Write-Host "Fiscalised: $fiscalised   Skipped: $skipped   Failed: $failed   Not tried: $notTried" -ForegroundColor Cyan
if ($stoppedBecause) { Write-Host "Stopped early: $stoppedBecause." -ForegroundColor Yellow }
if ($results.Count -gt 0) { Write-Host "Report: $ReportPath" }
