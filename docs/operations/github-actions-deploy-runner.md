# GitHub Actions Deploy Runner

This runbook sets up automatic production deployment on merge to `main`, gated behind a manual
approval. It covers the self-hosted runner the deployment needs, the credential it uses, and the
GitHub environment that holds the gate.

Once it is in place, merging to `main` runs the tests, then parks a deployment awaiting approval.
Nothing reaches production until someone approves it, and the job runs no steps at all while it
waits.

## Why a self-hosted runner

Production is `10.10.10.9` and `10.10.10.58`, both private addresses. GitHub-hosted runners run in
Azure and cannot route to either, so no cloud runner can deploy this application regardless of how
the workflow is written. The runner has to live on a machine that can already reach production.

`Update-Production.ps1` transfers over WinRM (`Copy-Item -ToSession`), not SMB, so the runner needs
WinRM to both nodes and HTTPS out to `github.com`. It does not need a file share.

## The machine

The runner lives on **`10.10.10.9`**, the primary production node. It is always on and always on
the LAN, so a merge never parks a deployment waiting for a machine to wake up.

Three consequences of that choice, all invisible until the first real deployment, so all checked
by `-ValidateOnly` before anything is installed:

- **`dotnet publish` runs on a box that is serving traffic.** The build competes with the live
  application for CPU and disk. Deploy outside peak hours if this proves noticeable.
- **`10.10.10.9` deploys itself over a loopback WinRM session.** That works, but it is the one
  place a *local* (non-domain) deploy account quietly fails: the token filter strips its
  administrator rights and `Invoke-Command` returns access denied. Use a domain account.
- **The final health check runs from inside the network.** It fetches the public URL, which has to
  hairpin back through NAT or resolve through split-horizon DNS. Where that does not work the
  check fails on every deployment while the site is perfectly healthy. It also means the check no
  longer proves the path an actual user takes — the weakness you accept for hosting on production.

`10.10.10.58` needs nothing installed. It is deployed *to*, over WinRM, like today.

## Prerequisites

- Windows with PowerShell 5.1 or later.
- **.NET 10 SDK** on `PATH`. Note this is the SDK, not the ASP.NET Core Hosting Bundle that
  production already has — publishing needs the full SDK, and installing it on a production box is
  a real change to that box.
- `git` on `PATH`.
- WinRM answering on `10.10.10.9` **and** `10.10.10.58`.
- One account with administrator rights on **both** nodes — the *deploy account*. A single account
  covering both is what lets one credential file serve the whole deployment; see
  [Both IIS nodes](#both-iis-nodes). Make it a domain account, per the loopback note above.
- An account for the runner service — the *service account*. It needs no rights on production at
  all, but you must be able to log in as it; see below.

## Two accounts, on purpose

The service account runs the runner process. The deploy account is the administrator on production.
They are deliberately different: the credential file is sealed with DPAPI under the service
account, and the administrator password lives *inside* that file. So the runner process itself is
unprivileged and merely holds a sealed secret, and the production password never enters GitHub.

The one hard rule: **the credential file must be created by the same account the runner service
runs as, on the same machine.** DPAPI will not decrypt it anywhere else.

## Install

`scripts/Install-DeployRunner.ps1` does the whole install. Check the machine first — this needs no
elevation and changes nothing, so it is safe to run before committing to anything:

```powershell
.\scripts\Install-DeployRunner.ps1 -ValidateOnly
```

Fix anything it reports, then install from an **elevated** PowerShell on `10.10.10.9`:

```powershell
.\scripts\Install-DeployRunner.ps1 -ServiceAccount "KEFALOS\<service-account>"
```

It downloads the runner and checks its SHA256 against the published release, registers it with the
`shopinventory-deploy` label, installs the service under that account, seals the deploy credential
*as* that account, and reads it back to prove the seal opens.

You are prompted for two things, both typed straight into Windows and neither written anywhere in
the clear:

1. The **service account's** password — for the service install.
2. The **deploy account's** credentials — the production administrator, which gets sealed.

The registration token is fetched through `gh` at run time rather than pasted in, so `gh` must be
authenticated with admin rights on the repository (`gh auth status`).

Rerunning is safe: it refuses to overwrite a configured runner, and asks before replacing an
existing credential file.

### If you would rather do it by hand

Use **Settings → Actions → Runners → New self-hosted runner** for the download and `config.cmd`
steps, adding `shopinventory-deploy` to the labels, and `--runasservice` with
`--windowslogonaccount` / `--windowslogonpassword` so the service runs as the service account —
not `NETWORK SERVICE`, which cannot create the seal. Then, **logged in as the service account**:

```powershell
New-Item -ItemType Directory -Force "C:\ProgramData\ShopInventory" | Out-Null
Get-Credential -Message "Deploy account: administrator on 10.10.10.9 and 10.10.10.58" | Export-Clixml -LiteralPath "C:\ProgramData\ShopInventory\deploy.credential.xml"
```

Lock it down, point the runner at it, and restart so the service sees the new variable:

```powershell
icacls "C:\ProgramData\ShopInventory\deploy.credential.xml" /inheritance:r /grant "KEFALOS\<service-account>:(R)"
[Environment]::SetEnvironmentVariable("SHOPINVENTORY_DEPLOY_CREDENTIAL", "C:\ProgramData\ShopInventory\deploy.credential.xml", "Machine")
Restart-Service actions.runner.*
```

The path is a machine variable rather than a GitHub secret deliberately. It is only a path, and the
credential it points at is useless off this machine, so nothing sensitive is stored in GitHub.

Verify the seal before relying on it — **as the service account**:

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

Three optional settings, all in the same environment:

- Secret `SHOPINVENTORY_WEB_SMTP_PASSWORD` — the POD report SMTP password. If it is absent the
  deployment still succeeds and production keeps the password it already has; the script only
  writes that setting when it is given one.
- Variable `PRODUCTION_HEALTH_URL` — overrides the post-deploy check, which defaults to
  `https://sis.kefaloscheese.com/health/ready`.
- Variable `ADDITIONAL_PRODUCTION_SERVERS` — the IIS nodes past the primary, comma separated.
  Defaults to `10.10.10.58`; set it to `none` to deploy `10.10.10.9` alone.

## First run

Trigger it by hand before trusting it on a merge. **Actions → Deploy to production → Run workflow**,
target `Both`. The run should stop at the approval gate; approve it and watch it through.

A healthy run:

1. Checks out the exact commit that passed tests.
2. Confirms the runner has the SDK and the credential file.
3. Publishes both projects, then for each node in turn: backs up, migrates, copies to the idle
   slot, warms it, cuts over. The log names each node as it starts and finishes.
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

Because the runner sits on `10.10.10.9`, rule out one thing first: the check runs from *inside* the
network and has to hairpin out and back. Confirm from the runner itself —

```powershell
Invoke-WebRequest https://sis.kefaloscheese.com/health/ready -UseBasicParsing
```

If that fails while the site is fine from outside, it is a NAT or split-horizon DNS problem, not a
deployment problem. Fix the resolution, or point `PRODUCTION_HEALTH_URL` at an address the runner
can actually reach — accepting that the check then proves less.

**"Access is denied" from `Invoke-Command` against `10.10.10.9` specifically, while `10.10.10.58`
works.** The primary deploys itself over a loopback WinRM session, and Windows strips administrator
rights from a *local* account arriving that way. Use a domain account for the deploy credential.
`-ValidateOnly` confirms the listener answers but cannot detect this — it only shows up once a real
credential is used.

**Tests pass but no deployment appears.** The deploy workflow triggers on the *Tests* workflow
completing. If the Tests run was skipped or cancelled rather than passing, there is nothing to
trigger from. Use **Run workflow** to deploy manually.

**"Deployment failed for 10.10.10.58".** The nodes deploy in order and the run stops at the first
failure, so `10.10.10.9` is already updated and `.58` is not — the two are serving different
builds. Fix the failing node and re-run; the primary simply redeploys the same commit. If it cannot
be fixed quickly, take `.58` out of the load balancer rather than leaving the mismatch in place.

**"Credential file not found for 10.10.10.58".** Only happens when
`-AdditionalSerializedCredentialPaths` is in use, and now means what it says: the path is wrong or
the file was moved. It no longer means the previous deployment ate it — the script re-seals a copy
for the child rather than handing over the original.

**"Credential file unusable for 10.10.10.58".** The file exists but does not decrypt or does not
hold a `PSCredential`. Almost always it was created by a different account than the one reading it;
`Export-Clixml` seals under the writing account. Re-create it as the runner's service account.

## Both IIS nodes

The deployment covers `10.10.10.9` and `10.10.10.58`. Both sit behind the load balancer, so both
have to move together or the two serve different builds to different users.

The node list is the repo variable `ADDITIONAL_PRODUCTION_SERVERS`, comma separated, defaulting to
`10.10.10.58`. Set it to `none` to deploy the primary alone.

They share one deploy account, and that is what lets a single credential file cover both. The
script deploys each node in turn by re-invoking itself as a child process, re-sealing a single-use
copy of the credential for each one. The unattended switches are passed down to those children too
— without that, a child would downgrade a failed health probe to a warning and the parent would
report the whole run as a success.

Per-node accounts are supported through `-AdditionalSerializedCredentialPaths`, one file per entry
in `-AdditionalProductionServers`. The script reads each one and re-seals a single-use copy for the
child, so the files survive and can be reused on every deployment.

Nodes are deployed **sequentially**, so for the couple of minutes between them the two are on
different builds. That is inherent to a rolling deploy and is why each node is individually
blue-green and health-checked before the next one starts.

## What this does not do

- **No automatic rollback.** Blue-green keeps the previous slot, but swapping back is manual. A
  failed run leaves production on whatever slot the cutover reached. With two nodes, a failure on
  the second leaves the first already updated.
- **No deploy on a red build.** By design — a failed or cancelled test run cannot reach the gate.
