<#
.SYNOPSIS
Asks the REVMax device, from the machine this runs on, whether it holds a receipt for an invoice.

.DESCRIPTION
Read-only: one GET to /api/RevmaxAPI/GetInvoice/{n} per number, which files nothing.

Run it on the production API server. A credit note against an invoice REVMax fiscalised is held back
with "REVMax could not be asked" when this same call times out, cannot connect, or gets an answer
other than a receipt or "Invoice not Found". This shows which of those the API server is getting, and
how long the device takes to answer (the API gives up after Revmax:TimeoutSeconds, 90 s).

.PARAMETER InvoiceNumber
One or more numbers to look up: the original invoice's SAP DocNum, or a till/van sale's reference.

.PARAMETER BaseUrl
The REVMax device, as Revmax:BaseUrl in appsettings.json.

.EXAMPLE
.\Test-RevmaxReachable.ps1 -InvoiceNumber 771485
#>
param(
    [Parameter(Mandatory)] [string[]] $InvoiceNumber,
    [string] $BaseUrl = 'http://172.16.16.201:8001',
    [int] $TimeoutSeconds = 90
)

$uri = [Uri] $BaseUrl
Write-Host "TCP $($uri.Host):$($uri.Port) ..." -NoNewline
$tcp = Test-NetConnection -ComputerName $uri.Host -Port $uri.Port -WarningAction SilentlyContinue
Write-Host $(if ($tcp.TcpTestSucceeded) { ' open' } else { ' NOT reachable' })

foreach ($number in $InvoiceNumber) {
    $url = '{0}/api/RevmaxAPI/GetInvoice/{1}' -f $BaseUrl.TrimEnd('/'), [Uri]::EscapeDataString($number)
    $clock = [Diagnostics.Stopwatch]::StartNew()
    try {
        $response = Invoke-RestMethod -Uri $url -Method Get -TimeoutSec $TimeoutSeconds
        $clock.Stop()
        $data = $response.Data
        [pscustomobject]@{
            Number      = $number
            Seconds     = [math]::Round($clock.Elapsed.TotalSeconds, 1)
            Code        = $response.Code
            Message     = $response.Message
            DeviceID    = $response.DeviceID
            ReceiptType = $data.receiptType
            InvoiceNo   = $data.invoiceNo
            GlobalNo    = $data.receiptGlobalNo
            Verdict     = if ($response.Code -eq '1' -and "$($response.DeviceID)" -eq '22862') { 'Held by our device (22862): credit goes to REVMax' }
                          elseif ($response.Code -eq '1') { 'Held by ANOTHER device: not ours' }
                          elseif ("$($response.Message)" -match 'not found') { 'Not on REVMax' }
                          else { 'No straight answer: the API holds the credit note back' }
        }
    }
    catch {
        $clock.Stop()
        [pscustomobject]@{
            Number  = $number
            Seconds = [math]::Round($clock.Elapsed.TotalSeconds, 1)
            Code    = $null
            Message = $_.Exception.Message
            Verdict = 'No answer: the API holds the credit note back'
        }
    }
}
