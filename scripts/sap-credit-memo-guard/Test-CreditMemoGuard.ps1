<#
.SYNOPSIS
    Proves the SAP credit memo guard is live: adds a credit memo draft that over-credits an invoice
    (must be refused with "KEF:") and one that does not (must be accepted), then deletes what it added.

.DESCRIPTION
    Drafts go through SBO_SP_TransactionNotification exactly as the B1 client's "Add" or "send for
    approval" does (object 112), so a refusal here is the refusal the wash bay will see. Drafts post
    nothing: no stock, no ledger, no fiscal receipt.

    Test companies only. It refuses a CompanyDB that does not contain "TEST", so it cannot add drafts
    in production.

    Run before installing the guard to see SAP accept the over-credit (the "before" picture), and
    after installing to see it refused.

.EXAMPLE
    .\Test-CreditMemoGuard.ps1 -ServiceLayerUrl https://10.10.10.6:50000/b1s/v1 -CompanyDB KEFALOS_TEST_3 -UserName manager
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ServiceLayerUrl,
    [Parameter(Mandatory)][string]$CompanyDB,
    [Parameter(Mandatory)][string]$UserName,
    [securestring]$Password,
    [int]$LookbackDays = 365
)

$ErrorActionPreference = 'Stop'
if ($CompanyDB -notmatch 'TEST') { throw "Refusing to add drafts in '$CompanyDB': this test runs only against a test company." }
if (-not $Password) { $Password = Read-Host -AsSecureString "SAP password for $UserName in $CompanyDB" }
$base = $ServiceLayerUrl.TrimEnd('/')
$plain = [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($Password))
$login = @{ UserName = $UserName; Password = $plain; CompanyDB = $CompanyDB } | ConvertTo-Json
Invoke-RestMethod -Method Post -Uri "$base/Login" -Body $login -ContentType 'application/json' -SkipCertificateCheck -SessionVariable web | Out-Null
$plain = $null

function Invoke-Sap([string]$method, [string]$path, $body) {
    $request = @{ Method = $method; Uri = "$base/$path"; WebSession = $web; SkipCertificateCheck = $true; SkipHttpErrorCheck = $true; StatusCodeVariable = 'status'; ContentType = 'application/json' }
    if ($body) { $request.Body = ($body | ConvertTo-Json -Depth 6) }
    $response = Invoke-RestMethod @request
    [pscustomobject]@{ Status = $status; Body = $response }
}

$added = @()
$failures = 0
try {
    $since = (Get-Date).AddDays(-$LookbackDays).ToString('yyyy-MM-dd')
    $page = Invoke-Sap Get "Invoices?`$filter=DocDate ge '$since' and Cancelled eq 'tNO'&`$select=DocEntry,DocNum,DocDate,CardCode,DocTotal,DocCurrency,DocumentLines&`$orderby=DocEntry desc&`$top=40"
    # An invoice nothing has been credited against yet, so "left" is simply its total. A paid invoice
    # reads bost_Close at the header while its lines stay open, so only the lines are judged.
    $invoice = $page.Body.value | Where-Object {
        $lines = @($_.DocumentLines)
        $lines.Count -gt 0 -and -not ($lines | Where-Object { $_.RemainingOpenQuantity -ne $_.Quantity -or $_.Quantity -lt 1 })
    } | Select-Object -First 1
    if (-not $invoice) { throw "No open, uncredited invoice since $since in $CompanyDB to test against." }
    $line = $invoice.DocumentLines[0]
    "Testing against invoice $($invoice.DocNum) (DocEntry $($invoice.DocEntry), $($invoice.CardCode), total $($invoice.DocTotal) $($invoice.DocCurrency))"

    $copied = @{ BaseType = 13; BaseEntry = $invoice.DocEntry; BaseLine = $line.LineNum; Quantity = $line.Quantity }
    # The wash bay's shape: the invoice line copied in full, plus a hand-typed line of the same item.
    $typed = @{ ItemCode = $line.ItemCode; Quantity = 1; UnitPrice = [Math]::Max([decimal]$invoice.DocTotal, 1); WarehouseCode = $line.WarehouseCode }
    if ($line.VatGroup) { $typed.VatGroup = $line.VatGroup }

    # Nothing copied: the platform files these against the invoice named in Customer Ref. No.
    $typedSmall = $typed.Clone(); $typedSmall.UnitPrice = 0.01

    $cases = @(
        @{ Name = 'over-credit (copied line + typed line worth more than the invoice)'; Lines = @($copied, $typed); ExpectRefused = $true },
        @{ Name = 'within the invoice (one unit copied)'; Lines = @(@{ BaseType = 13; BaseEntry = $invoice.DocEntry; BaseLine = $line.LineNum; Quantity = 1 }); ExpectRefused = $false },
        @{ Name = 'nothing copied, Customer Ref. No. = invoice, worth more than it'; Lines = @($typed); NumAtCard = "$($invoice.DocNum)"; ExpectRefused = $true },
        @{ Name = 'nothing copied, Customer Ref. No. = invoice, within it'; Lines = @($typedSmall); NumAtCard = "$($invoice.DocNum)"; ExpectRefused = $false },
        @{ Name = 'nothing copied, Customer Ref. No. not an invoice'; Lines = @($typedSmall); NumAtCard = 'RETURN CRATES'; ExpectRefused = $true }
    )

    foreach ($case in $cases) {
        # Dated on the invoice's day: an unlinked draft needs that day's exchange rate, which a test company may lack for today.
        $draft = @{ DocObjectCode = 'oCreditNotes'; CardCode = $invoice.CardCode; DocCurrency = $invoice.DocCurrency; DocDate = ([datetime]$invoice.DocDate).ToString('yyyy-MM-dd'); Comments = 'KEF credit memo guard test - safe to delete'; DocumentLines = $case.Lines }
        if ($case.NumAtCard) { $draft.NumAtCard = $case.NumAtCard }
        $result = Invoke-Sap Post 'Drafts' $draft
        $refused = $result.Status -ge 400
        $message = if ($refused) { $result.Body.error.message.value ?? $result.Body.error.message } else { "added draft $($result.Body.DocEntry), total $($result.Body.DocTotal)" }
        if (-not $refused) { $added += $result.Body.DocEntry }
        $byGuard = $refused -and "$message" -match 'KEF:'
        $ok = if ($case.ExpectRefused) { $byGuard } else { -not $refused }
        if (-not $ok) { $failures++ }
        '{0}  {1}: HTTP {2} - {3}' -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $case.Name, $result.Status, $message
    }
}
finally {
    foreach ($entry in $added) {
        $deleted = Invoke-Sap Delete "Drafts($entry)"
        "Removed test draft $entry (HTTP $($deleted.Status))"
    }
    try { Invoke-Sap Post 'Logout' $null | Out-Null } catch {}
}

if ($failures -gt 0) { "$failures check(s) failed. Before the guard is installed, the over-credit case is expected to FAIL (SAP accepts it)."; exit 1 }
'Guard is live: every over-credit and unlinked draft was refused, and the valid ones accepted.'
