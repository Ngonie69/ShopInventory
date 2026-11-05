<#
.SYNOPSIS
    Gives the ShopInventory API the Windows account it signs in to the SAP attachments share with
    (\\kfldb\b1_shf), after proving the account can open the attachments folder.

.DESCRIPTION
    The API reads SAP document attachments straight off the share, because SAP's Service Layer
    cannot serve them (its Linux host has no mount for the attachments folder). When the share asks
    for a login, the API signs in with SAP:AttachmentsUsername and SAP:AttachmentsPassword. Those
    live in each API web.config on the server, never in the repository, which is public.

    Run this on the API server, as an administrator. It:

      1. Asks for the share account (user name and password) in a Windows credential prompt. The
         password is never printed.
      2. Signs in to the share with that account using the same Windows call the API makes, and
         reads a file from the attachments folder. With -TestWrite it also writes, reads back and
         deletes a small test file there. If the sign-in or the read fails, it stops and writes
         nothing.
      3. Writes SAP__AttachmentsUsername, SAP__AttachmentsPassword and SAP__AttachmentsPath into
         every ShopInventory API web.config (the live site and the blue and green slots), and removes
         SAP__AttachmentsDomain: the user name carries the domain, if it has one. Each file is backed
         up next to itself first. A file that already holds these values is left alone.
      4. Waits for the API to answer /health/ready again.

    Saving a web.config restarts that site's app, which takes a few seconds and drops till and van
    requests in flight. Between 07:00 and 19:00 the script asks first, unless -Force is given.

    Deployments carry the values forward: each one copies the new slot's web.config from the live
    site. SAP__AttachmentsPath is also overwritten by each deployment from the package, which is why
    the repository's web.config must name the same folder (ShippedAttachmentsPathTests).

.PARAMETER Credential
    The share account. Leave it out to be prompted. Write the user name the way the file server
    expects it: KFLDB\user for an account on the file server itself, DOMAIN\user or user@domain for
    a domain account.

.PARAMETER AttachmentsPath
    The SAP attachments folder, as the API should open it.

.PARAMETER TestWrite
    Also prove the account can write: create, read back and delete a file named
    ShopInventory-write-test-<guid>.txt in the attachments folder.

.PARAMETER TestOnly
    Run the sign-in test and stop. Writes nothing and restarts nothing, so it is safe in trading
    hours, and it does not need IIS.

.PARAMETER SkipTest
    Write the values without testing them first.

.PARAMETER Force
    Do not ask before restarting the API between 07:00 and 19:00.

.EXAMPLE
    .\scripts\Set-SapAttachmentsShareLogin.ps1 -TestOnly -TestWrite

    Proves the account can read and write the folder, and changes nothing.

.EXAMPLE
    .\scripts\Set-SapAttachmentsShareLogin.ps1 -TestWrite

    Proves the account, then gives it to the API.

.EXAMPLE
    .\scripts\Set-SapAttachmentsShareLogin.ps1 -WhatIf

    Tests the account and shows which web.config files would change.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [System.Management.Automation.PSCredential]$Credential,
    [string]$AttachmentsPath = '\\kfldb\b1_shf\Paths\Attachments\',
    [switch]$TestWrite,
    [switch]$TestOnly,
    [switch]$SkipTest,
    [switch]$Force,
    [string[]]$ApiSiteNames = @('ShopInventory-API', 'ShopInventory-API-Blue', 'ShopInventory-API-Green'),
    [int]$ApiPort = 5106
)

$ErrorActionPreference = 'Stop'

$EnvironmentVariablesXPath = '/configuration/location/system.webServer/aspNetCore/environmentVariables'

# ── The share ──────────────────────────────────────────────────────────────────────────────────

function Get-ShareRoot {
    param([string]$Path)

    if (-not $Path.StartsWith('\\')) { throw "'$Path' is not a UNC path (\\server\share\...)." }
    $parts = @($Path.TrimStart('\').Split('\') | Where-Object { $_ })
    if ($parts.Count -lt 2) { throw "'$Path' does not name a share (\\server\share\...)." }
    return "\\$($parts[0])\$($parts[1])"
}

function Add-ShareLoginType {
    if ('ShopInventoryShareLogin' -as [type]) { return }

    # The same call, flags and resource type as ShopInventory's NetworkShareConnections, so a pass
    # here means the API's own sign-in will pass with the same values. C# 5: Windows PowerShell's
    # compiler.
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class ShopInventoryShareLogin
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public int Scope;
        public int Type;
        public int DisplayType;
        public int Usage;
        public string LocalName;
        public string RemoteName;
        public string Comment;
        public string Provider;
    }

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetAddConnection2(ref NetResource netResource, string password, string username, int flags);

    [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
    private static extern int WNetCancelConnection2(string name, int flags, bool force);

    public static int Connect(string shareRoot, string username, string password)
    {
        NetResource resource = new NetResource();
        resource.Type = 1;
        resource.RemoteName = shareRoot;
        return WNetAddConnection2(ref resource, password, username, 4);
    }

    public static int Disconnect(string shareRoot)
    {
        return WNetCancelConnection2(shareRoot, 0, false);
    }
}
'@
}

function Get-Win32Message {
    param([int]$Code)

    $hint = switch ($Code) {
        5 { ' The account signed in but is not allowed into the share.' }
        53 { ' The server name is wrong or the server cannot be reached.' }
        67 { ' The share name is wrong.' }
        86 { ' Wrong password.' }
        1326 { ' Wrong user name or password. For an account on the file server itself, write KFLDB\user.' }
        1909 { ' The account is locked out.' }
        default { '' }
    }
    return "$((New-Object System.ComponentModel.Win32Exception $Code).Message) (Windows error $Code).$hint"
}

function Connect-ShareForTest {
    param([string]$ShareRoot, [string]$UserName, [string]$Password)

    Add-ShareLoginType
    $result = [ShopInventoryShareLogin]::Connect($ShareRoot, $UserName, $Password)

    # 1219: this Windows session already holds a connection to the server under another account,
    # perhaps this operator's own Explorer window. Windows treats the server's address as a different
    # server, so the account can still be tested through it. The API runs in a session of its own and
    # does not meet this.
    if ($result -eq 1219) {
        $server = $ShareRoot.TrimStart('\').Split('\')[0]
        $address = [System.Net.Dns]::GetHostAddresses($server) |
            Where-Object { $_.AddressFamily -eq 'InterNetwork' } |
            Select-Object -First 1
        if ($null -ne $address) {
            $byAddress = $ShareRoot -replace [regex]::Escape("\\$server\"), "\\$($address.IPAddressToString)\"
            Write-Host "  This session is already connected to $server under another account; testing through $byAddress instead." -ForegroundColor Yellow
            $ShareRoot = $byAddress
            $result = [ShopInventoryShareLogin]::Connect($ShareRoot, $UserName, $Password)
        }
    }

    if ($result -ne 0 -and $result -ne 85) {
        throw "Signing in to $ShareRoot as '$UserName' failed: $(Get-Win32Message -Code $result)"
    }

    return [pscustomobject]@{ Root = $ShareRoot; OwnedHere = ($result -eq 0) }
}

function Disconnect-ShareForTest {
    param([object]$Connection)

    if ($null -ne $Connection -and $Connection.OwnedHere) {
        [void][ShopInventoryShareLogin]::Disconnect($Connection.Root)
    }
}

function Test-FolderAccess {
    param([string]$Folder, [switch]$Write)

    if (-not [System.IO.Directory]::Exists($Folder)) {
        throw "Signed in, but the folder $Folder does not exist or cannot be listed."
    }

    # The folder holds about 100,000 files: take the first one, never the whole listing.
    $sample = $null
    foreach ($file in [System.IO.Directory]::EnumerateFiles($Folder)) { $sample = $file; break }
    if ($null -eq $sample) {
        Write-Host "  Opened $Folder (it holds no files to read)." -ForegroundColor Green
    }
    else {
        $buffer = New-Object byte[] 4096
        $stream = [System.IO.File]::Open($sample, 'Open', 'Read', 'ReadWrite')
        try { $read = $stream.Read($buffer, 0, $buffer.Length) } finally { $stream.Dispose() }
        Write-Host "  Read $read bytes of '$([System.IO.Path]::GetFileName($sample))' from $Folder." -ForegroundColor Green
    }

    if ($Write) {
        $probe = Join-Path $Folder "ShopInventory-write-test-$([guid]::NewGuid().ToString('N')).txt"
        $text = "ShopInventory write test from $env:COMPUTERNAME at $((Get-Date).ToString('o')). Safe to delete."
        try {
            [System.IO.File]::WriteAllText($probe, $text)
            if ([System.IO.File]::ReadAllText($probe) -ne $text) { throw "The test file $probe read back different from what was written." }
            Write-Host "  Wrote and read back '$([System.IO.Path]::GetFileName($probe))'." -ForegroundColor Green
        }
        finally {
            if ([System.IO.File]::Exists($probe)) {
                [System.IO.File]::Delete($probe)
                Write-Host '  Deleted the test file.' -ForegroundColor Green
            }
        }
    }
}

# ── The API's web.config files ─────────────────────────────────────────────────────────────────

function Get-SiteWebConfigPath {
    param([string]$Name)

    $site = Get-Item "IIS:\Sites\$Name" -ErrorAction SilentlyContinue
    if ($null -eq $site) { return $null }

    $path = Join-Path ([Environment]::ExpandEnvironmentVariables([string]$site.physicalPath)) 'web.config'
    if (Test-Path -LiteralPath $path) { return $path }
    return $null
}

function Get-EnvironmentVariableValue {
    param([xml]$Config, [string]$Name)

    $node = $Config.SelectSingleNode("$EnvironmentVariablesXPath/environmentVariable[@name='$Name']")
    if ($null -eq $node) { return $null }
    return $node.GetAttribute('value')
}

function Set-EnvironmentVariableValue {
    param([xml]$Config, [string]$Name, [string]$Value)

    $variables = $Config.SelectSingleNode($EnvironmentVariablesXPath)
    if ($null -eq $variables) {
        $aspNetCore = $Config.SelectSingleNode('/configuration/location/system.webServer/aspNetCore')
        if ($null -eq $aspNetCore) { throw 'web.config is missing /configuration/location/system.webServer/aspNetCore.' }
        $variables = $Config.CreateElement('environmentVariables')
        [void]$aspNetCore.AppendChild($variables)
    }

    $node = $variables.SelectSingleNode("environmentVariable[@name='$Name']")
    if ($null -eq $node) {
        $node = $Config.CreateElement('environmentVariable')
        $node.SetAttribute('name', $Name)
        [void]$variables.AppendChild($node)
    }
    # SetAttribute escapes the value, so a password with & < > or quotes cannot break the file.
    $node.SetAttribute('value', $Value)
}

function Remove-EnvironmentVariable {
    param([xml]$Config, [string]$Name)

    $node = $Config.SelectSingleNode("$EnvironmentVariablesXPath/environmentVariable[@name='$Name']")
    if ($null -ne $node) { [void]$node.ParentNode.RemoveChild($node) }
}

function Wait-ApiReady {
    param([int]$Port)

    $url = "http://localhost:$Port/health/ready"
    $deadline = (Get-Date).AddMinutes(3)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 5
        try {
            $response = Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 30
            if ($response.StatusCode -eq 200) { return "$url answered: $($response.Content)" }
        }
        catch {
            # Still starting.
        }
    }
    return $null
}

# ── Main ───────────────────────────────────────────────────────────────────────────────────────

if (-not $AttachmentsPath.EndsWith('\')) { $AttachmentsPath += '\' }
$shareRoot = Get-ShareRoot -Path $AttachmentsPath

if ($null -eq $Credential) {
    $Credential = Get-Credential -Message "The Windows account ShopInventory signs in to $shareRoot with (KFLDB\user, DOMAIN\user or user@domain)"
    if ($null -eq $Credential) { Write-Host 'Cancelled. Nothing written.'; return }
}

# The credential prompt turns a bare 'user' into '\user'.
$userName = $Credential.UserName.Trim().TrimStart('\')
$password = $Credential.GetNetworkCredential().Password
if ([string]::IsNullOrWhiteSpace($userName)) { throw 'The user name is empty.' }
if ([string]::IsNullOrEmpty($password)) { throw 'The password is empty.' }

# 1. Prove the account before anything depends on it.
if (-not $SkipTest) {
    Write-Host "Testing $shareRoot as '$userName'..." -ForegroundColor Cyan
    $connection = Connect-ShareForTest -ShareRoot $shareRoot -UserName $userName -Password $password
    try {
        $folder = $connection.Root + $AttachmentsPath.Substring($shareRoot.Length)
        Test-FolderAccess -Folder $folder -Write:$TestWrite
    }
    finally {
        Disconnect-ShareForTest -Connection $connection
    }
    Write-Host "  The account works." -ForegroundColor Green
}

if ($TestOnly) {
    Write-Host 'Test only: nothing written.' -ForegroundColor Cyan
    return
}

# 2. Find the API's web.config files.
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Writing the API web.config needs an administrator PowerShell. The test passed; run again as administrator.'
}
Import-Module WebAdministration -ErrorAction Stop

$wanted = [ordered]@{
    'SAP__AttachmentsPath'     = $AttachmentsPath
    'SAP__AttachmentsUsername' = $userName
    'SAP__AttachmentsPassword' = $password
}

$apiConfigs = @()
foreach ($siteName in $ApiSiteNames) {
    $path = Get-SiteWebConfigPath -Name $siteName
    if ($null -eq $path -or ($apiConfigs | Where-Object { $_.Path -eq $path })) { continue }

    [xml]$config = Get-Content -LiteralPath $path -Raw
    $aspNetCore = $config.SelectSingleNode('/configuration/location/system.webServer/aspNetCore')
    if ($null -eq $aspNetCore -or $aspNetCore.GetAttribute('arguments') -notlike '*ShopInventory.dll*') {
        Write-Warning "Skipping $siteName ($path): it does not run ShopInventory.dll."
        continue
    }

    $changes = @()
    foreach ($name in $wanted.Keys) {
        if ((Get-EnvironmentVariableValue -Config $config -Name $name) -cne $wanted[$name]) { $changes += $name }
    }
    if ($null -ne (Get-EnvironmentVariableValue -Config $config -Name 'SAP__AttachmentsDomain')) { $changes += 'SAP__AttachmentsDomain (removed)' }

    $apiConfigs += [pscustomobject]@{ Site = $siteName; Path = $path; Config = $config; Changes = $changes }
}

if ($apiConfigs.Count -eq 0) {
    throw "No ShopInventory API web.config found under the IIS sites: $($ApiSiteNames -join ', ')."
}

Write-Host ''
Write-Host "Folder      $AttachmentsPath" -ForegroundColor Cyan
Write-Host "User name   $userName" -ForegroundColor Cyan
Write-Host "Password    (hidden, $($password.Length) characters)" -ForegroundColor Cyan
foreach ($api in $apiConfigs) {
    $summary = if ($api.Changes.Count) { $api.Changes -join ', ' } else { 'already set' }
    Write-Host ("  {0,-24} {1}" -f $api.Site, $summary) -ForegroundColor DarkGray
}

# A foreach, not Where-Object: under -WhatIf, Windows PowerShell 5.1 also skips ForEach-Object's
# property form (see Set-SapCertThumbprints.ps1).
$toWrite = @()
foreach ($api in $apiConfigs) { if ($api.Changes.Count -gt 0) { $toWrite += $api } }
if ($toWrite.Count -eq 0) {
    Write-Host 'Every API web.config already holds these values. Nothing written, nothing restarted.' -ForegroundColor Green
    return
}

$sites = @($toWrite | ForEach-Object { $_.Site }) -join ', '
if (-not $PSCmdlet.ShouldProcess("$($toWrite.Count) API web.config file(s): $sites", 'Set the SAP attachments share sign-in (restarts each site)')) {
    return
}

$now = Get-Date
if (-not $Force -and $now.Hour -ge 7 -and $now.Hour -lt 19) {
    $question = "Saving these files restarts the API ($sites). The live one drops till and van requests in flight for a few seconds. It is $($now.ToString('HH:mm')), inside trading hours. Restart now?"
    if (-not $PSCmdlet.ShouldContinue($question, 'Restart the API during trading hours')) {
        Write-Host 'Nothing written. Run again after 19:00, or with -Force.' -ForegroundColor Yellow
        return
    }
}

# 3. Write.
foreach ($api in $toWrite) {
    foreach ($name in $wanted.Keys) { Set-EnvironmentVariableValue -Config $api.Config -Name $name -Value $wanted[$name] }
    Remove-EnvironmentVariable -Config $api.Config -Name 'SAP__AttachmentsDomain'

    $backupPath = "$($api.Path).bak-$(Get-Date -Format 'yyyyMMdd-HHmmss')"
    Copy-Item -LiteralPath $api.Path -Destination $backupPath -Force
    $api.Config.Save($api.Path)
    Write-Host "  Updated $($api.Path)" -ForegroundColor Green
    Write-Host "  Backup  $backupPath" -ForegroundColor DarkGray
}

# 4. The API is back.
$ready = Wait-ApiReady -Port $ApiPort
if ($null -eq $ready) {
    Write-Warning "The API did not answer http://localhost:$ApiPort/health/ready within three minutes. The values are written; check the site, and recycle its app pool if IIS logs 503 with substatus 1255."
}
else {
    Write-Host $ready -ForegroundColor Green
}

Write-Host ''
Write-Host 'To see the API read through the new sign-in: open a credit note approval that has an attachment and click View.' -ForegroundColor Cyan
Write-Host "If it fails, the message names the reason (wrong password, locked out, share not found). Search the API log for 'SAP attachments share'." -ForegroundColor DarkGray
