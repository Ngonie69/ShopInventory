<#
.SYNOPSIS
    Who saved a customer's WhatsApp number, and its history. Read-only.

.DESCRIPTION
    Run on the production API server. Finds every saved WhatsApp contact whose number ends in the
    given digits and prints, for each: the customer it is on, who recorded the customer's consent,
    where (web app, van handset or till), when, the audit-trail rows written for it, and the
    documents sent to it.

    The session is read-only (default_transaction_read_only=on), so nothing can be changed. The
    connection string is taken from the API site's web.config, as the site itself uses it, and the
    password is cleared from the environment afterwards.

    Note: renewing consent on a number (saving it again on the same customer) overwrites "recorded
    by". The audit rows below are the full history of who saved it from the web app; van-handset
    and till saves are not audited, only recorded on the contact itself.

.PARAMETER LastDigits
    The end of the number, as the delivery log shows it: 1567.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Find-WhatsAppContact.ps1 -LastDigits 1567
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$LastDigits,

    [string]$SiteRoot = 'C:\inetpub\ShopInventory-API'
)

$ErrorActionPreference = 'Stop'

$digits = ($LastDigits -replace '[^0-9]', '')
if ($digits.Length -lt 3) {
    throw "Give at least three digits of the number, e.g. -LastDigits 1567."
}

function Get-ConnectionString {
    param([string]$Root)

    $candidates = @($Root, "$Root-Blue", "$Root-Green") | Where-Object { Test-Path $_ }
    foreach ($dir in $candidates) {
        $config = Join-Path $dir 'web.config'
        if (Test-Path $config) {
            [xml]$xml = Get-Content -Path $config -Raw
            $node = $xml.SelectNodes('//environmentVariable') |
                Where-Object { $_.name -eq 'ConnectionStrings__DefaultConnection' } |
                Select-Object -First 1
            if ($node -and $node.value) { return $node.value }
        }

        $json = Join-Path $dir 'appsettings.Production.json'
        if (Test-Path $json) {
            $settings = Get-Content -Path $json -Raw | ConvertFrom-Json
            if ($settings.ConnectionStrings.DefaultConnection) { return $settings.ConnectionStrings.DefaultConnection }
        }
    }

    throw "No connection string found under $Root (web.config or appsettings.Production.json)."
}

function Find-Psql {
    $onPath = Get-Command psql -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    $installed = Get-ChildItem 'C:\Program Files\PostgreSQL\*\bin\psql.exe' -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
    if ($installed) { return $installed.FullName }

    throw "psql.exe not found on PATH or under C:\Program Files\PostgreSQL."
}

$parts = @{}
foreach ($pair in (Get-ConnectionString -Root $SiteRoot).Split(';')) {
    $kv = $pair.Split('=', 2)
    if ($kv.Count -eq 2) { $parts[$kv[0].Trim().ToLowerInvariant()] = $kv[1].Trim() }
}

$pgHost = if ($parts['host']) { $parts['host'] } elseif ($parts['server']) { $parts['server'] } else { 'localhost' }
$pgPort = if ($parts['port']) { $parts['port'] } else { '5432' }
$pgDb = $parts['database']
$pgUser = if ($parts['username']) { $parts['username'] } elseif ($parts['user id']) { $parts['user id'] } else { $parts['user'] }
$pgPassword = $parts['password']

# Every table and column here is PascalCase, so the SQL goes through a file: PowerShell 5.1 strips
# the double quotes from a -c argument and Postgres then folds the names to lower case.
$sql = @"
\echo
\echo '=== Saved WhatsApp numbers ending in $digits ==='
\x on
SELECT c."Id"                                   AS contact_id,
       c."PhoneE164"                            AS number,
       c."ContactName"                          AS who_answers,
       c."OwnerName"                            AS customer,
       c."CardCode"                             AS card_code,
       rc."Code"                                AS route_customer,
       c."ConsentRecordedBy"                    AS consent_recorded_by,
       c."ConsentSource"                        AS recorded_on,
       c."ConsentNote"                          AS how_they_agreed,
       c."ConsentRecordedAtUtc" AT TIME ZONE 'Africa/Harare' AS consent_recorded_cat,
       c."CreatedAtUtc" AT TIME ZONE 'Africa/Harare'    AS first_saved_cat,
       c."AutoSendInvoices"                     AS automatic_invoices,
       c."OptedOutAtUtc" AT TIME ZONE 'Africa/Harare'   AS opted_out_cat,
       c."OptedOutBy"                           AS opted_out_by,
       c."RemovedAtUtc" AT TIME ZONE 'Africa/Harare'    AS removed_cat,
       c."RemovedBy"                            AS removed_by,
       c."UpdatedBy"                            AS last_changed_by
FROM "CustomerWhatsAppContacts" c
LEFT JOIN "RouteCustomers" rc ON rc."Id" = c."RouteCustomerId"
WHERE regexp_replace(c."PhoneE164", '[^0-9]', '', 'g') LIKE '%$digits'
ORDER BY c."CreatedAtUtc";
\x off

\echo
\echo '=== Audit trail for those numbers (web app saves, changes, opt-outs, removals) ==='
SELECT a."Timestamp" AT TIME ZONE 'Africa/Harare' AS at_cat,
       a."Username",
       a."UserRole",
       a."Action",
       a."EntityId"                       AS contact_id,
       a."Details"
FROM "AuditLogs" a
WHERE a."EntityType" = 'CustomerWhatsAppContact'
  AND a."EntityId" IN (
      SELECT c."Id"::text FROM "CustomerWhatsAppContacts" c
      WHERE regexp_replace(c."PhoneE164", '[^0-9]', '', 'g') LIKE '%$digits')
ORDER BY a."Timestamp";

\echo
\echo '=== Documents sent to those numbers, by status ==='
SELECT d."ContactId" AS contact_id,
       d."Status",
       count(*)       AS documents,
       min(d."CreatedAtUtc") AT TIME ZONE 'Africa/Harare' AS first_cat,
       max(d."CreatedAtUtc") AT TIME ZONE 'Africa/Harare' AS last_cat
FROM "CustomerDocumentDeliveries" d
WHERE regexp_replace(d."RecipientE164", '[^0-9]', '', 'g') LIKE '%$digits'
GROUP BY d."ContactId", d."Status"
ORDER BY d."ContactId", d."Status";
"@

$sqlFile = Join-Path $env:TEMP "find-whatsapp-contact-$digits.sql"
Set-Content -Path $sqlFile -Value $sql -Encoding ASCII   # ASCII: PowerShell 5.1's UTF8 writes a BOM psql chokes on

$env:PGPASSWORD = $pgPassword
$env:PGOPTIONS = '-c default_transaction_read_only=on'
try {
    & (Find-Psql) -h $pgHost -p $pgPort -U $pgUser -d $pgDb -P pager=off -v ON_ERROR_STOP=1 -f $sqlFile
}
finally {
    Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
    Remove-Item Env:\PGOPTIONS -ErrorAction SilentlyContinue
    Remove-Item $sqlFile -ErrorAction SilentlyContinue
}
