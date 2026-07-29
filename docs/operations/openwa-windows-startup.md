# OpenWA Windows Startup

This runbook starts OpenWA outside IIS on the same Windows host as ShopInventory and makes it restart automatically after reboot by using a built-in Scheduled Task.

## Prerequisites

- OpenWA repository is present at `OpenWA/` under the workspace root.
- Node 20.20.2 is installed at `C:\Users\ngoni\.config\herd\bin\nvm\v20.20.2`.
- Google Chrome is installed at `C:\Program Files\Google\Chrome\Application\chrome.exe`.
- `npm install` has already completed successfully in `OpenWA/`.
- Local ShopInventory API webhook is reachable at `http://127.0.0.1:5106/api/whatsapp/webhook/openwa`.

## Scripts

- `scripts/Run-OpenWA.ps1`: foreground runner intended for Task Scheduler.
- `scripts/Start-OpenWA.ps1`: starts the runner in a detached PowerShell process.
- `scripts/Stop-OpenWA.ps1`: stops active OpenWA Node processes.
- `scripts/Register-OpenWAStartupTask.ps1`: registers a startup task as `NT AUTHORITY\SYSTEM`.

## Validate Prerequisites

```powershell
.\scripts\Run-OpenWA.ps1 -ValidateOnly
```

## Start OpenWA Now

```powershell
.\scripts\Start-OpenWA.ps1
```

## Stop OpenWA

```powershell
.\scripts\Stop-OpenWA.ps1
```

## Register Automatic Startup

```powershell
.\scripts\Register-OpenWAStartupTask.ps1
```

To register and immediately launch the task:

```powershell
.\scripts\Register-OpenWAStartupTask.ps1 -StartNow
```

To remove the task later:

```powershell
Unregister-ScheduledTask -TaskName 'ShopInventory-OpenWA' -Confirm:$false
```

## Non-Admin Fallback

If task registration is blocked by Windows elevation policy, install a current-user Startup entry instead. This starts OpenWA when that user signs in.

```powershell
.\scripts\Install-OpenWAUserStartup.ps1
```

To remove the Startup entry later:

```powershell
.\scripts\Remove-OpenWAUserStartup.ps1
```

## Logs

`Run-OpenWA.ps1` writes timestamped logs under `OpenWA/logs/`:

- `openwa-*.out.log`
- `openwa-*.err.log`

## Notes

- The runner uses `node .\dist\main.js` because that startup path was more reliable on this Windows host than `npm run start`.
- The runner sets `PUPPETEER_SKIP_CHROMIUM_DOWNLOAD=true` and points Puppeteer to the locally installed Chrome binary.
- The runner will build OpenWA automatically only if `dist\main.js` is missing. Use `-SkipBuild` when you want startup to fail fast instead.
- Local webhook registration should use `127.0.0.1`, not `localhost`, because OpenWA rejects `localhost` for webhook URLs.
- `Register-OpenWAStartupTask.ps1` needs an elevated PowerShell session. Use the Startup-folder fallback when elevation is not available.