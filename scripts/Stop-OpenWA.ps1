[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param()

$ErrorActionPreference = 'Stop'

$processes = Get-CimInstance Win32_Process -Filter "name = 'node.exe'" -ErrorAction SilentlyContinue |
    Where-Object {
        $commandLine = $_.CommandLine
        $commandLine -and (
            $commandLine -like "*\OpenWA\dist\main*" -or
            $commandLine -like "* .\dist\main.js*" -or
            $commandLine -like "*\OpenWA\node_modules\.bin\*nest.js*" -or
            $commandLine -like "*npm-cli.js*run start*OpenWA*"
        )
    }

if (-not $processes) {
    Write-Host 'OpenWA is not running.'
    return
}

foreach ($process in $processes) {
    if ($PSCmdlet.ShouldProcess("PID $($process.ProcessId)", 'Stop OpenWA process')) {
        Stop-Process -Id $process.ProcessId -Force
        Write-Host "Stopped OpenWA process PID $($process.ProcessId)."
    }
}