#Requires -Version 5.1
<#
.SYNOPSIS
    Regenerates the API Explorer's endpoint catalogue from the controllers.

.DESCRIPTION
    ApiExplorer.razor's `_apiCategories` was hand-written, and for a long time it advertised
    endpoints no route served. Test-RouteCatalogues.ps1 now catches that, but catching drift is not
    the same as not having any: with 500+ routes, keeping the list correct by hand is a losing game.

    This rewrites the `_apiCategories` initializer in place from `[Route]` plus the `[Http*]`
    attributes, so the explorer is derived rather than remembered. Run it after adding or renaming a
    route, then read the diff.

    **Curation survives.** Every endpoint description, and every module name, description, icon and
    colour already in the file, is carried over and reapplied - matched on verb+path for endpoints
    and on base path for modules. Only genuinely new entries get a generated description, taken from
    the action name ("GetPagedInvoices" -> "Get paged invoices"). So editing a description in the
    .razor is safe: the next run keeps it.

    What is NOT carried over is an entry for a route that no longer exists. That is the point.

.PARAMETER OutFile
    Write the generated initializer here instead of rewriting the .razor. For inspecting the output
    without touching the page.

.EXAMPLE
    powershell -File scripts/Update-ApiExplorerCatalogue.ps1
    git diff ShopInventory.Web/Components/Pages/ApiExplorer.razor

.EXAMPLE
    powershell -File scripts/Update-ApiExplorerCatalogue.ps1 -OutFile out.txt -Verbose

.NOTES
    Run scripts/Test-RouteCatalogues.ps1 afterwards, and build the Web project - this writes C#
    into a .razor file and nothing here type-checks it.

    Categories are assigned by the $categoryOf table below. A controller missing from it lands in
    "Other" rather than being dropped, so a new one shows up as misfiled rather than invisible.
#>
[CmdletBinding()]
param([string]$OutFile)

$ErrorActionPreference = 'Stop'
$repoRoot = (& git rev-parse --show-toplevel).Trim()
$explorer = Join-Path $repoRoot 'ShopInventory.Web/Components/Pages/ApiExplorer.razor'
$dir = Join-Path $repoRoot 'ShopInventory/Controllers'

# ---- what the explorer already says, so curation is not thrown away -------------------------
$existingEndpoint = @{}   # "VERB path" (lowercased) -> description
$existingModule = @{}     # basepath (lowercased) -> @{ Name; Description; Icon; Color }

$text = Get-Content -LiteralPath $explorer -Raw
foreach ($m in [regex]::Matches($text, 'new\(\s*"([A-Z]+)"\s*,\s*"([^"]+)"\s*,\s*"((?:[^"\\]|\\.)*)"\s*\)')) {
    $key = ($m.Groups[1].Value + ' ' + $m.Groups[2].Value.Trim('/')).ToLowerInvariant()
    $existingEndpoint[$key] = $m.Groups[3].Value
}
foreach ($m in [regex]::Matches($text,
    'Name\s*=\s*"([^"]+)",\s*BasePath\s*=\s*"([^"]+)",\s*Description\s*=\s*"((?:[^"\\]|\\.)*)",\s*Icon\s*=\s*([A-Za-z0-9_\.]+),\s*Color\s*=\s*([A-Za-z0-9_\.]+)')) {
    $existingModule[$m.Groups[2].Value.Trim('/').ToLowerInvariant()] = @{
        Name = $m.Groups[1].Value; Description = $m.Groups[3].Value
        Icon = $m.Groups[4].Value; Color = $m.Groups[5].Value
    }
}

Write-Verbose "carried over: $($existingEndpoint.Count) endpoint descriptions, $($existingModule.Count) modules"

# ---- the controllers -------------------------------------------------------------------------
$verbPattern = 'Get|Post|Put|Delete|Patch|Head|Options'
$controllers = @{}   # controller name -> @{ Base; Routes = @(@{Verb;Path;Action}) }

foreach ($file in (Get-ChildItem -LiteralPath $dir -Filter *.cs | Sort-Object Name)) {
    $lines = Get-Content -LiteralPath $file.FullName
    $base = $null; $className = $null; $pendingRoute = $null
    $verbs = @()

    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]

        if ($line -match '^\s*\[Route\("([^"]+)"\)\]') { $pendingRoute = $Matches[1]; continue }

        if ($line -match '(?:^|\s)class\s+([A-Za-z0-9_]+)') {
            if ($Matches[1] -like '*Controller' -and $pendingRoute) {
                $className = $Matches[1]
                $base = $pendingRoute -replace '\[controller\]', ($className -replace 'Controller$', '')
                $pendingRoute = $null
                if (-not $controllers.ContainsKey($className)) {
                    $controllers[$className] = @{ Base = $base; Routes = @() }
                }
            }
            continue
        }

        if ($line -match "^\s*\[Http($verbPattern)(?:\(""([^""]*)""\))?\]") {
            $verbs += @{ Verb = $Matches[1].ToUpperInvariant(); Template = $Matches[2] }
            continue
        }

        if ($verbs.Count -gt 0 -and
            $line -match '^\s*(?:public|internal)\s+(?:static\s+)?(?:async\s+)?[A-Za-z0-9_<>,\[\]\?\. ]+?\s+([A-Za-z0-9_]+)\s*\(') {
            $action = $Matches[1]
            foreach ($v in $verbs) {
                $t = $v.Template
                if ($t -and ($t.StartsWith('/') -or $t.StartsWith('~/'))) { $full = $t.TrimStart('~') }
                elseif ($t) { $full = "$base/$t" }
                else { $full = $base }
                $full = '/' + ($full -replace '\{([A-Za-z0-9_]+)(:[^}]+)?\}', '{$1}').Trim('/')
                $controllers[$className].Routes += @{ Verb = $v.Verb; Path = $full; Action = $action }
            }
            $verbs = @()
        }
    }
}

# ---- shaping ---------------------------------------------------------------------------------
$acronyms = @{
    'sap'='SAP'; 'pdf'='PDF'; 'gl'='GL'; 'pod'='POD'; 'pods'='PODs'; 'qr'='QR'; 'grv'='GRV'
    'grvs'='GRVs'; 'vat'='VAT'; 'id'='ID'; 'api'='API'; 'url'='URL'; 'db'='DB'; 'wa'='WA'
    'grpo'='GRPO'; 'po'='PO'; 'rti'='RTI'; 'uom'='UoM'; 'csv'='CSV'; 'json'='JSON'; 'sql'='SQL'
    'two'='two'; 'fa'='FA'
}

function ConvertTo-Sentence {
    param([string]$Action)

    $words = [regex]::Matches($Action, '([A-Z]+(?![a-z])|[A-Z][a-z0-9]*|[a-z0-9]+)') |
        ForEach-Object { $_.Value }

    $out = @()
    foreach ($w in $words) {
        $lower = $w.ToLowerInvariant()
        if ($acronyms.ContainsKey($lower)) { $out += $acronyms[$lower] }
        else { $out += $lower }
    }
    if ($out.Count -eq 0) { return $Action }

    $first = $out[0]
    if ($first -cne $first.ToUpperInvariant()) {
        $first = $first.Substring(0,1).ToUpperInvariant() + $first.Substring(1)
    }
    # $out[1..0] does NOT yield an empty range in PowerShell - it counts backwards and repeats the
    # only element, which turned every single-word action into "Search search".
    if ($out.Count -eq 1) { return $first }
    return (@($first) + @($out[1..($out.Count-1)])) -join ' '
}

# Controller -> category. Anything unlisted lands in "Other" so a new controller is visible rather
# than silently absent.
$categoryOf = @{
    'InvoiceController'='Core Business'; 'SalesOrderController'='Core Business'
    'CreditNoteController'='Core Business'; 'QuotationController'='Core Business'
    'PaymentController'='Core Business'; 'IncomingPaymentController'='Core Business'
    'BatchController'='Core Business'

    'ProductController'='Inventory'; 'StockController'='Inventory'
    'InventoryTransferController'='Inventory'; 'PriceController'='Inventory'
    'CratesController'='Inventory'; 'ItemVolumeConversionController'='Inventory'

    'PurchaseOrderController'='Purchasing'; 'PurchaseRequestController'='Purchasing'
    'PurchaseQuotationController'='Purchasing'; 'PurchaseInvoiceController'='Purchasing'
    'GoodsReceiptPurchaseOrderController'='Purchasing'

    'BusinessPartnerController'='Partners & Customers'; 'CustomerPortalController'='Partners & Customers'
    'StatementController'='Partners & Customers'; 'RouteCustomersController'='Partners & Customers'
    'CreditControlController'='Partners & Customers'

    'ExchangeRateController'='Finance'; 'GLAccountController'='Finance'; 'CostCentreController'='Finance'

    'ReportController'='Reports & Documents'; 'DocumentController'='Reports & Documents'
    'BackupController'='Reports & Documents'

    'VanSalesReportController'='Field Operations'; 'VanSalesAttendanceController'='Field Operations'
    'VanSalesCompatibilityController'='Field Operations'; 'TimesheetController'='Field Operations'
    'MerchandiserController'='Field Operations'

    'AuthController'='System & Security'; 'TwoFactorController'='System & Security'
    'PasswordController'='System & Security'; 'UserController'='System & Security'
    'UserManagementController'='System & Security'; 'UserActivityController'='System & Security'
    'HealthController'='System & Security'; 'RateLimitController'='System & Security'
    'SAPSettingsController'='System & Security'; 'AppVersionController'='System & Security'
    'ExceptionCenterController'='System & Security'; 'SyncController'='System & Security'
    'ApprovalProcessController'='System & Security'

    'WebhookController'='Integrations'; 'NotificationController'='Integrations'
    'PushNotificationController'='Integrations'; 'EmailController'='Integrations'
    'WhatsAppController'='Integrations'; 'DesktopIntegrationController'='Integrations'
    'FiscalisationSettingsController'='Integrations'; 'FiscalDeviceOfflineLeaseController'='Integrations'
}

$categoryOrder = @(
    'Core Business', 'Inventory', 'Purchasing', 'Partners & Customers', 'Finance',
    'Reports & Documents', 'Field Operations', 'System & Security', 'Integrations', 'Other'
)

$categoryIcon = @{
    'Core Business'='Icons.Material.Filled.Business'; 'Inventory'='Icons.Material.Filled.Inventory'
    'Purchasing'='Icons.Material.Filled.ShoppingBasket'; 'Partners & Customers'='Icons.Material.Filled.People'
    'Finance'='Icons.Material.Filled.AccountBalance'; 'Reports & Documents'='Icons.Material.Filled.Assessment'
    'Field Operations'='Icons.Material.Filled.LocalShipping'; 'System & Security'='Icons.Material.Filled.Security'
    'Integrations'='Icons.Material.Filled.IntegrationInstructions'; 'Other'='Icons.Material.Filled.Api'
}

$fallbackIcons = @('Icons.Material.Filled.Api', 'Icons.Material.Filled.Dns', 'Icons.Material.Filled.Bolt')
$fallbackColors = @('Color.Primary', 'Color.Secondary', 'Color.Info', 'Color.Success', 'Color.Warning')

# Splitting the class name reads badly for a handful, and pluralising algorithmically would break
# Stock, Sync and Health. Name those by hand.
$moduleNameOf = @{
    'WhatsAppController'='WhatsApp'; 'PushNotificationController'='Push Notifications'
    'QuotationController'='Quotations'; 'TimesheetController'='Timesheets'
    'BatchController'='Batches'; 'PurchaseOrderController'='Purchase Orders'
    'PurchaseInvoiceController'='Purchase Invoices'; 'PurchaseQuotationController'='Purchase Quotations'
    'PurchaseRequestController'='Purchase Requests'; 'GoodsReceiptPurchaseOrderController'='Goods Receipt POs'
    'FiscalDeviceOfflineLeaseController'='Fiscal Device Leases'
}

function Get-ModuleName {
    param([string]$Controller)
    if ($moduleNameOf.ContainsKey($Controller)) { return $moduleNameOf[$Controller] }
    $n = $Controller -replace 'Controller$', ''
    $n = [regex]::Replace($n, '(?<!^)([A-Z][a-z])', ' $1')
    $n = [regex]::Replace($n, '(?<=[a-z])([A-Z])', ' $1')
    return $n.Trim()
}

function Escape-CSharp { param([string]$S) return $S.Replace('\', '\\').Replace('"', '\"') }

# ---- emit ------------------------------------------------------------------------------------
$sb = New-Object System.Text.StringBuilder
$null = $sb.AppendLine('        _apiCategories = new List<ApiCategory>')
$null = $sb.AppendLine('{')

$grouped = @{}
foreach ($name in $controllers.Keys) {
    if ($controllers[$name].Routes.Count -eq 0) { continue }
    $cat = if ($categoryOf.ContainsKey($name)) { $categoryOf[$name] } else { 'Other' }
    if (-not $grouped.ContainsKey($cat)) { $grouped[$cat] = @() }
    $grouped[$cat] += $name
}

$catIndex = 0
$emittedCats = @($categoryOrder | Where-Object { $grouped.ContainsKey($_) })
foreach ($cat in $emittedCats) {
    $catIndex++
    $null = $sb.AppendLine('new ApiCategory')
    $null = $sb.AppendLine('{')
    $null = $sb.AppendLine("Name = `"$(Escape-CSharp $cat)`",")
    $null = $sb.AppendLine("Icon = $($categoryIcon[$cat]),")
    $null = $sb.AppendLine('Modules = new List<ApiModule>')
    $null = $sb.AppendLine('{')

    $mods = @($grouped[$cat] | Sort-Object)
    $modIndex = 0
    foreach ($controller in $mods) {
        $modIndex++
        $info = $controllers[$controller]
        $baseKey = $info.Base.Trim('/').ToLowerInvariant()
        $prior = $existingModule[$baseKey]

        $basePath = '/' + $info.Base.Trim('/')
        $modName = if ($prior) { $prior.Name } else { Get-ModuleName $controller }
        $routeCount = $info.Routes.Count
        $plural = if ($routeCount -ne 1) { 's' } else { '' }
        $modDesc = if ($prior) { $prior.Description } else {
            "$routeCount endpoint$plural under $basePath."
        }
        $icon = if ($prior) { $prior.Icon } else { $fallbackIcons[$modIndex % $fallbackIcons.Count] }
        $color = if ($prior) { $prior.Color } else { $fallbackColors[$modIndex % $fallbackColors.Count] }

        $verbs = @($info.Routes | ForEach-Object { $_.Verb } | Sort-Object -Unique)

        $null = $sb.AppendLine('new ApiModule')
        $null = $sb.AppendLine('{')
        $null = $sb.AppendLine("Name = `"$(Escape-CSharp $modName)`",")
        $null = $sb.AppendLine("BasePath = `"$(Escape-CSharp $basePath)`",")
        $null = $sb.AppendLine("Description = `"$(Escape-CSharp $modDesc)`",")
        $null = $sb.AppendLine("Icon = $icon,")
        $null = $sb.AppendLine("Color = $color,")
        $quotedVerbs = ($verbs | ForEach-Object { '"' + $_ + '"' }) -join ', '
        $null = $sb.AppendLine("Methods = new[] { $quotedVerbs },")
        $null = $sb.AppendLine('Endpoints = new List<ApiEndpoint>')
        $null = $sb.AppendLine('{')

        # GETs first so the tester seeds on something safe, then by path.
        $ordered = @($info.Routes |
            Sort-Object @{ Expression = { if ($_.Verb -eq 'GET') { 0 } else { 1 } } }, @{ Expression = { $_.Path } }, @{ Expression = { $_.Verb } })

        for ($r = 0; $r -lt $ordered.Count; $r++) {
            $route = $ordered[$r]
            $key = ($route.Verb + ' ' + $route.Path.Trim('/')).ToLowerInvariant()
            $desc = if ($existingEndpoint.ContainsKey($key)) { $existingEndpoint[$key] } else { ConvertTo-Sentence $route.Action }
            $comma = if ($r -lt $ordered.Count - 1) { ',' } else { '' }
            $null = $sb.AppendLine("new(`"$($route.Verb)`", `"$(Escape-CSharp $route.Path)`", `"$(Escape-CSharp $desc)`")$comma")
        }

        $null = $sb.AppendLine('}')
        $null = $sb.AppendLine("}$(if ($modIndex -lt $mods.Count) { ',' })")
    }

    $null = $sb.AppendLine('}')
    $null = $sb.AppendLine("}$(if ($catIndex -lt $emittedCats.Count) { ',' })")
}

$null = $sb.AppendLine('};')

$total = ($controllers.Values | ForEach-Object { $_.Routes.Count } | Measure-Object -Sum).Sum
Write-Verbose "emitted $total endpoints across $($emittedCats.Count) categories"

if ($OutFile) {
    $sb.ToString() | Set-Content -LiteralPath $OutFile -Encoding UTF8
    Write-Host "Wrote the generated initializer to $OutFile ($total endpoints)."
    return
}

# ---- splice it back into the page ------------------------------------------------------------
# Bounded by the `_apiCategories = new List<ApiCategory>` line and the first `};` that closes it,
# so everything else on the page - markup, the tester, the nested classes - is left alone.
$lines = Get-Content -LiteralPath $explorer
$start = ($lines | Select-String -Pattern '^\s*_apiCategories = new List<ApiCategory>$' |
    Select-Object -First 1).LineNumber
if (-not $start) { throw "Cannot find the _apiCategories initializer in $explorer." }

$end = $null
for ($i = $start; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^\s*\};\s*$') { $end = $i + 1; break }
}
if (-not $end) { throw "Cannot find the end of the _apiCategories initializer in $explorer." }

$updated = @()
$updated += $lines[0..($start - 2)]
$updated += ($sb.ToString() -split "`r?`n" | Select-Object -SkipLast 1)
$updated += $lines[$end..($lines.Count - 1)]

Set-Content -LiteralPath $explorer -Value $updated -Encoding UTF8

$moduleCount = ($grouped.Values | ForEach-Object { $_.Count } | Measure-Object -Sum).Sum
Write-Host ("Rewrote {0} ({1} endpoints, {2} modules, {3} categories)." -f
    (Split-Path $explorer -Leaf), $total, $moduleCount, $emittedCats.Count)
Write-Host 'Now run scripts/Test-RouteCatalogues.ps1 and build ShopInventory.Web.'
