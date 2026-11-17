<#
.SYNOPSIS
    Proves the item stock $crossjoin returns what the STK_ITEMS SQL queries did.

.DESCRIPTION
    GetStockQuantitiesForItemsInWarehouseAsync used to read SQLQueries('STK_ITEMS_<hash>')/List, one stored
    query per warehouse and item-code family. It now reads
    $crossjoin(Items,Items/ItemWarehouseInfoCollection) filtered on the same warehouse and family.

    This lists the STK_ITEMS query objects SAP already holds, reads a sample of them the old way and the
    matching $crossjoin straight after, and compares every row: item code, name, barcode, warehouse,
    InStock, Committed, Ordered and unit. Exits 1 on any difference.

    Read-only: GETs only, and no SQLQueries object is created. The old read is the one that can leave a
    Service Layer worker stuck, so keep -Sample small and run it once, not in a loop.

.EXAMPLE
    $env:SAP_PASSWORD = '...'; pwsh ./scripts/Compare-ItemStockReads.ps1 -UserName manager -CompanyDB KEFALOS_USD_NEW2
#>
[CmdletBinding()]
param(
    [string] $BaseUrl = 'https://10.10.10.6:50000/b1s/v1',
    [Parameter(Mandatory)] [string] $UserName,
    [Parameter(Mandatory)] [string] $CompanyDB,
    # Password from SAP_PASSWORD, or asked for; never printed.
    [int] $Sample = 12,
    # STK_ITEMS_<hash> codes to compare as well as the sample.
    [string[]] $Include = @(),
    [int] $TimeoutSec = 60
)

$ErrorActionPreference = 'Stop'
$BaseUrl = $BaseUrl.TrimEnd('/')
$password = $env:SAP_PASSWORD
if (-not $password) { $password = [Net.NetworkCredential]::new('', (Read-Host "SAP password for $UserName" -AsSecureString)).Password }

$login = Invoke-RestMethod "$BaseUrl/Login" -Method Post -SkipCertificateCheck -ContentType 'application/json' `
    -Body (@{ UserName = $UserName; Password = $password; CompanyDB = $CompanyDB } | ConvertTo-Json)
$headers = @{ Cookie = "B1SESSION=$($login.SessionId)"; Prefer = 'odata.maxpagesize=500' }

function Read-All([string] $path) {
    $rows = [Collections.Generic.List[object]]::new(); $skip = 0
    while ($true) {
        $sep = if ($path.Contains('?')) { '&' } else { '?' }
        $url = if ($skip -eq 0) { "$BaseUrl/$path" } else { "$BaseUrl/$path$sep`$skip=$skip" }
        $page = Invoke-RestMethod $url -Headers $headers -SkipCertificateCheck -TimeoutSec $TimeoutSec
        $got = @($page.value); foreach ($r in $got) { $rows.Add($r) }
        $skip += $got.Count
        if ($got.Count -eq 0 -or -not ($page.'odata.nextLink' -or $page.'@odata.nextLink')) { return $rows }
    }
}

# Mirrors SAPServiceLayerClient.BuildItemStockCrossJoin with UseCustomFields off.
function Get-CrossJoinPath([string] $warehouse, [string] $prefix) {
    $line = 'Items/ItemWarehouseInfoCollection'
    $esc = { param($v) $v.Replace("'", "''") }
    $filter = "Items/ItemCode eq $line/ItemCode and $line/WarehouseCode eq '$(& $esc $warehouse)' and (startswith(Items/ItemCode,'$(& $esc $prefix)'))"
    "`$crossjoin(Items,$line)?`$expand=" + [Uri]::EscapeDataString("Items(`$select=ItemCode,ItemName,BarCode,InventoryUOM),$line(`$select=WarehouseCode,InStock,Committed,Ordered)") +
        "&`$filter=" + [Uri]::EscapeDataString($filter) + "&`$orderby=" + [Uri]::EscapeDataString('Items/ItemCode')
}

function Line($code, $name, $bar, $whs, $in, $com, $ord, $uom) {
    '{0}|{1}|{2}|{3}|{4:R}|{5:R}|{6:R}|{7}' -f $code, $name, $bar, $whs, [double]$in, [double]$com, [double]$ord, $uom
}

$failures = 0; $pairs = 0; $oldMs = 0L; $newMs = 0L; $rowsCompared = 0
try {
    $objects = @(Read-All ("SQLQueries?`$select=SqlCode,SqlName&`$filter=" + [Uri]::EscapeDataString("startswith(SqlCode,'STK_ITEMS_')")))
    $parsed = $objects | ForEach-Object {
        # The name only groups the sample: SAP cuts it short ("in CORMAC" for CORMACH2), so the warehouse
        # compared is the one the old read's own rows name.
        if ($_.SqlName -match '^Stock validation items (\S+) in (.+?) [0-9A-F]{12}$|^Stock validation items (\S+) in (.+)$') {
            [pscustomobject]@{ Code = $_.SqlCode; Prefix = $Matches[1] ?? $Matches[3]; Warehouse = $Matches[2] ?? $Matches[4] }
        }
    }
    # A spread of warehouses rather than the first N codes, which cluster on one.
    $Include = @($Include | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
    $chosen = @($parsed | Where-Object { $_.Code -in $Include }) +
        @($parsed | Where-Object { $_.Code -notin $Include } | Group-Object Warehouse | ForEach-Object { $_.Group | Get-Random -Count ([Math]::Min(2, $_.Count)) } |
            Get-Random -Count ([Math]::Min($Sample, @($parsed).Count)))
    Write-Host "$(@($parsed).Count) STK_ITEMS objects in SAP; comparing $(@($chosen).Count)"

    foreach ($q in $chosen) {
        $pairs++
        $sw = [Diagnostics.Stopwatch]::StartNew()
        try { $old = Read-All "SQLQueries('$($q.Code)')/List" }
        catch { Write-Host ("  SKIP {0,-24} {1,-6} {2,-8} old read failed after {3} ms: {4}" -f $q.Code, $q.Prefix, $q.Warehouse, $sw.ElapsedMilliseconds, $_.Exception.Message); continue }
        $oldMs += $sw.ElapsedMilliseconds; $t1 = $sw.ElapsedMilliseconds
        $warehouse = @($old)[0].WarehouseCode
        if (-not $warehouse) { Write-Host ("  SKIP {0,-24} {1,-6} {2,-8} the old read returned no rows to compare" -f $q.Code, $q.Prefix, $q.Warehouse); continue }
        $q.Warehouse = $warehouse
        $sw.Restart()
        $new = Read-All (Get-CrossJoinPath $q.Warehouse $q.Prefix)
        $newMs += $sw.ElapsedMilliseconds

        $a = $old | ForEach-Object { Line $_.ItemCode $_.ItemName $_.BarCode $_.WarehouseCode $_.InStock $_.Committed $_.Ordered $_.UoM }
        $b = $new | ForEach-Object { $i = $_.Items; $w = $_.'Items/ItemWarehouseInfoCollection'; Line $i.ItemCode $i.ItemName $i.BarCode $w.WarehouseCode $w.InStock $w.Committed $w.Ordered $i.InventoryUOM }
        $diff = @(Compare-Object @($a) @($b))
        $rowsCompared += @($a).Count
        $status = if ($diff.Count -eq 0) { 'same' } else { $failures++; 'DIFF' }
        Write-Host ("  {0,-4} {1,-24} {2,-6} {3,-8} rows {4,4}/{5,-4} old {6,6} ms  new {7,5} ms" -f $status, $q.Code, $q.Prefix, $q.Warehouse, @($a).Count, @($b).Count, $t1, $sw.ElapsedMilliseconds)
        $diff | Select-Object -First 6 | ForEach-Object { Write-Host "         $($_.SideIndicator) $($_.InputObject)" }
    }
}
finally {
    try { Invoke-WebRequest "$BaseUrl/Logout" -Method Post -Headers $headers -SkipCertificateCheck | Out-Null } catch { }
}

Write-Host ("Compared {0} pairs, {1} rows. Old reads {2:n0} ms in all, new {3:n0} ms." -f $pairs, $rowsCompared, $oldMs, $newMs)
if ($failures -gt 0) { Write-Host "FAIL: $failures pair(s) differ" -ForegroundColor Red; exit 1 }
Write-Host 'PASS: identical' -ForegroundColor Green
