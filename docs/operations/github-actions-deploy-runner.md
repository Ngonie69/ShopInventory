# GitHub Actions Deploy Runner

This runbook sets up automatic production deployment on merge to `main`, gated behind a manual
approval. It covers the self-hosted runner the deployment needs, the credential it uses, and the
GitHub environment that holds the gate.

Once it is in place, merging to `main` runs the tests, then parks a deployment awaiting approval.
Nothing reaches production until someone approves it, and the job runs no steps at all while it
waits.

## Why a self-hosted runner

Production is `10.10.10.9`, a private address. GitHub-hosted runners run in Azure and cannot route
to it, so no cloud runner can deploy this application regardless of how the workflow is written.
The runner has to live on a machine that can already reach production.

`Update-Production.ps1` transfers over WinRM (`Copy-Item -ToSession`), not SMB, so the runner needs
WinRM to `10.10.10.9` and HTTPS out to `github.com`. It does not need a file share.

## Choosing the machine

Any Windows machine on the LAN that can reach both. Two reasonable choices:

- **A separate build box.** Preferred. `dotnet publish` runs on the runner, so the build stays off
  the production server, and the deployment has exactly the shape it has today: one machine
  publishing and pushing to another over WinRM.
- **The production server itself.** Works, and avoids provisioning a machine, but the build then
  competes with the live application for CPU and disk, and the remoting session becomes a loopback
  to itself.

The rest of this runbook says *the runner* for whichever you picked.

## Prerequisites

- Windows with PowerShell 5.1 or later.
- .NET 10 SDK on `PATH`. The workflow refuses to start without it.
- `git` on `PATH`.
- WinRM reachable from the runner to `10.10.10.9`.
- A domain or local account with administrator rights on `10.10.10.9` — the *deploy account*.
- A second account for the runner service — the *service account*. It needs no rights on
  production at all; see below.

## Two accounts, on purpose

The service account runs the runner process. The deploy account is the administrator on production.
They are deliberately different: the credential file is sealed with DPAPI under the service
account, and the administrator password lives *inside* that file. So the runner process itself is
unprivileged and merely holds a sealed secret, and the production password never enters GitHub.

The one hard rule: **the credential file must be created by the same account the runner service
runs as, on the same machine.** DPAPI will not decrypt it anywhere else.

## Install the runner

In the repository, go to **Settings → Actions → Runners → New self-hosted runner**, pick Windows,
and follow the download and `config.cmd` steps shown there — they include a registration token that
is generated per runner and expires.

When `config.cmd` asks for labels, add `shopinventory-deploy`. The workflow selects the runner with
`[self-hosted, windows, shopinventory-deploy]`, and `self-hosted` and `windows` are applied
automatically.

Install it as a service running as the service account:

```powershell
.\config.cmd --runasservice --windowslogonaccount "DOMAIN\svc-shopinventory-runner" --windowslogonpassword "<password>"
```

Confirm it came up:

```powershell
Get-Service actions.runner.* | Format-Table Name, Status, StartName
```

`StartName` must show the service account. If it shows `NT AUTHORITY\NETWORK SERVICE` the next step
cannot be completed — reconfigure the service before continuing.

## Create the credential file

Log in to the runner **as the service account** and run this. It prompts for the deploy account's
username and password and seals them to disk:

```powershell
New-Item -ItemType Directory -Force "C:\ProgramData\ShopInventory" | Out-Null
Get-Credential -Message "Deploy account: administrator on 10.10.10.9" | Export-Clixml -LiteralPath "C:\ProgramData\ShopInventory\deploy.credential.xml"
```

Lock it down to the service account:

```powershell
icacls "C:\ProgramData\ShopInventory\deploy.credential.xml" /inheritance:r /grant "DOMAIN\svc-shopinventory-runner:(R)"
```

Point the runner at it with a machine-scoped variable, then restart the service so it picks the
variable up:

```powershell
[Environment]::SetEnvironmentVariable("SHOPINVENTORY_DEPLOY_CREDENTIAL", "C:\ProgramData\ShopInventory\deploy.credential.xml", "Machine")
Restart-Service actions.runner.*
```

The path is a machine variable rather than a GitHub secret deliberately. It is only a path, and the
credential it points at is useless off this machine, so nothing sensitive is stored in GitHub.

Verify the seal works before relying on it — run this **as the service account**:

```powershell
(Import-Clixml -LiteralPath $env:SHOPINVENTORY_DEPLOY_CREDENTIAL).UserName
```

It should print the deploy account. Running the same line as any other account is expected to fail;
that failure is the protection working.

## Create the approval gate

In the repository, go to **Settings → Environments → New environment** and name it `production` —
the name must match exactly, or the job will run with no gate at all.

In that environment:

- Tick **Required reviewers** and add whoever may release. This is the gate: the job stays pending
  until one of them approves.
- Optionally set **Deployment branches** to `main` only.

Two optional settings, both in the same environment:

- Secret `SHOPINVENTORY_WEB_SMTP_PASSWORD` — the POD report SMTP password. If it is absent the
  deployment still succeeds and production keeps the password it already has; the script only
  writes that setting when it is given one.
- Variable `PRODUCTION_HEALTH_URL` — overrides the post-deploy check, which defaults to
  `https://sis.kefaloscheese.com/health/ready`.

## First run

Trigger it by hand before trusting it on a merge. **Actions → Deploy to production → Run workflow**,
target `Both`. The run should stop at the approval gate; approve it and watch it through.

A healthy run:

1. Checks out the exact commit that passed tests.
2. Confirms the runner has the SDK and the credential file.
3. Publishes both projects, backs up, migrates, copies to the idle slot, warms it, cuts over.
4. Verifies `https://sis.kefaloscheese.com/health/ready` from the runner, over the public address.

Step 4 exists because the script's own probe runs on the production box against `localhost`, so it
cannot see a broken binding, an expired certificate or a reverse-proxy rule — every way the site can
be down while the box insists it is up.

## What the workflow passes

The workflow calls the same script used by hand, with four switches added for unattended use:

| Switch | Why |
| --- | --- |
| `-CredentialPath` | Reads the sealed credential instead of prompting. |
| `-NonInteractive` | Fails immediately rather than prompting a console nobody is watching. |
| `-SuppressExitPrompt` | Skips the `Press Enter to exit` that would otherwise hang the job. |
| `-FailOnVerificationError` | Turns a failed post-cutover probe into a failed deployment. Interactively this stays a warning, because a person is there to judge it. |

The git safety check is **not** skipped. It still refuses to publish a dirty tree or a commit behind
`origin/main`.

## Troubleshooting

**Job never starts, stays queued.** The runner is offline or its labels do not match. Check
**Settings → Actions → Runners** and `Get-Service actions.runner.*`.

**"This runner is not provisioned for deployment."** Either the .NET 10 SDK is missing from the
service's `PATH`, or `SHOPINVENTORY_DEPLOY_CREDENTIAL` is unset or points at nothing. The step names
which. Note that a variable set after the service started is invisible to it until a restart.

**"Could not read the credential file."** The file was created by a different account than the one
the service runs as. Re-create it while logged in as the service account.

**"No deployment credential was supplied and -NonInteractive is set."** The credential path was
empty when the script ran. Same cause as above, one step earlier.

**Deployment succeeds, public verification fails.** The cutover worked and the application answers
on the box but not on its public address — look at the reverse proxy, the certificate and DNS
before touching the application. The site may genuinely be down; this is the check refusing to
report green over it.

**Tests pass but no deployment appears.** The deploy workflow triggers on the *Tests* workflow
completing. If the Tests run was skipped or cancelled rather than passing, there is nothing to
trigger from. Use **Run workflow** to deploy manually.

## What this does not do

- **No automatic rollback.** Blue-green keeps the previous slot, but swapping back is manual. A
  failed run leaves production on whatever slot the cutover reached.
- **One node only.** It deploys `10.10.10.9`. If `10.10.10.58` is also serving traffic, it needs
  `-AdditionalProductionServers` and a second credential file, and the workflow does not currently
  pass either.
- **No deploy on a red build.** By design — a failed or cancelled test run cannot reach the gate.
