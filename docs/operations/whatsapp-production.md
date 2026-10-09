# WhatsApp in production

How the WhatsApp operator console is turned on for the live estate, and what to check when it stops
working. Sending invoices to customers rides on the same gateway; see
[Customer documents](#customer-documents). For starting OpenWA on a developer machine, see
[openwa-windows-startup.md](openwa-windows-startup.md).

## What has to be true

Five things, and the console only reports the first two. The other three fail silently, which is why
each has a check of its own below.

| | Must be true | What it looks like when it is not |
|---|---|---|
| 1 | OpenWA is running and reachable from the API | Gateway reads **Unreachable** |
| 2 | `OpenWA__*` is set on the API | Every action fails with *WhatsApp integration is disabled* |
| 3 | A session is paired to the business handset | Session sits at `qr_ready` or `disconnected` |
| 4 | OpenWA holds a webhook aimed at this API | **Session looks perfectly healthy and the inbox stays empty** |
| 5 | That webhook's secret matches `OpenWA__WebhookSecret` | Same as 4, plus `WhatsApp.InvalidWebhookSignature` in the API log |

Rows 4 and 5 are the ones that cost time. A paired session shows connected, the gateway shows ok,
the console shows no error anywhere, and not one message arrives — because OpenWA posts events only
to webhooks registered against that session, and it will not tell anybody it has none.

The API now registers that webhook itself whenever a session is created or started, and the console
shows a **Delivery** row beside the selected session saying whether OpenWA is actually holding it.

## Topology

OpenWA runs on **10.10.10.9 only**.

WhatsApp Web allows one linked browser session per number. A second OpenWA instance paired to the
same number fights the first for it: both flap between connected and disconnected and inbound
messages land on whichever won the race. So there is one gateway, and only .9's API talks to it:

```
  10.10.10.9                                  10.10.10.58
  ┌──────────────────────────────┐            ┌──────────────────────────┐
  │ IIS  ShopInventory-API :5106 │            │ No OpenWA__* settings.   │
  │ IIS  ShopInventory-Web  :5107│            │ A WhatsApp request it    │
  │                              │            │ serves answers "WhatsApp │
  │ OpenWA (Node + Chrome) :2785 │            │ integration is disabled" │
  └──────────────────────────────┘            └──────────────────────────┘
            ▲        │
            │        └─ webhook → http://10.10.10.9:5106/api/whatsapp/webhook/openwa
            └────────── OpenWA__BaseUrl = http://10.10.10.9:2785 (on .9 only)
```

.58 was left unconfigured on purpose on 2026-10-07. If it is ever configured, point it at
`http://10.10.10.9:2785` and pin its `-WebhookPublicUrl` to .9's URL as well: OpenWA matches webhooks
by URL alone, so a per-node URL registers a second webhook and delivers every message twice. If .9 is
down, WhatsApp is down; the rest of the app is not.

## Install

Once, on 10.10.10.9, in an elevated PowerShell.

OpenWA is a git submodule and a plain clone leaves it empty:

```bash
git -C C:\path\to\ShopInventory submodule update --init --recursive
```

Then:

```bash
.\scripts\Install-OpenWAProduction.ps1
```

That checks Node 20+ and Chrome, builds `dist\main.js`, writes a production `OpenWA\.env`, starts
the service, registers the `ShopInventory-OpenWA` boot task, and firewalls port 2785 to the two API
nodes. It is re-runnable: an existing install keeps its data directory, its API key and its paired
session.

Note the API key it prints — OpenWA mints a random one on first start under `NODE_ENV=production`
and writes it to `OpenWA\data\.api-key`.

### Upgrading OpenWA

On 10.10.10.9, after the ShopInventory commit that moves the `OpenWA` pointer is on the checkout
there, run these in an elevated PowerShell:

```bash
git -C C:\path\to\ShopInventory submodule update --init OpenWA
```

```bash
.\scripts\Install-OpenWAProduction.ps1
```

The installer keeps stamps of the commit it built and the `package-lock.json` it installed from,
and does only what changed:

- **A new commit.** It rebuilds while the gateway keeps running, then restarts it.
- **A new `package-lock.json`.** It stops the gateway, because `npm ci` replaces files it holds
  open, then installs, builds and starts it.
- **A changed `.env`.** It restarts the gateway.
- **Nothing changed.** It leaves the gateway running.

A restart goes through the `ShopInventory-OpenWA` boot task, so the gateway runs as SYSTEM and
outlives your session. Every session that was running is started again by the gateway itself, three
seconds apart, and a paired one reconnects without a new QR code. A session someone pressed **Stop**
on stays stopped. (Before Ngonie69/OpenWA#3 the gateway started nothing: every restart left every
session `disconnected` until someone pressed **Start**.)

The first run on an install made before the stamps adopts its dependencies when `npm ls` finds
them complete, so that upgrade is a rebuild and a restart, not a reinstall.

Confirm it took:

1. `GET http://127.0.0.1:2785/api/health` answers 200.
2. `fiscal-alerts` reads `ready` on `/whatsapp-inbox` within a few minutes, without anyone pressing
   **Start**.
3. `scripts/Test-WhatsAppDeliveryPath.ps1` passes.

## Point the API at it

On **each** API node, elevated. Generate one webhook secret and use the same string on both — the
load balancer sends a delivery to either node, and each verifies the signature with its own copy:

```bash
.\scripts\Set-OpenWAApiConfig.ps1 -OpenWaBaseUrl 'http://10.10.10.9:2785' -OpenWaApiKey '<from data\.api-key>' -WebhookSecret '<the same secret on both nodes>' -IncludeSlots -RestartAppPool
```

These are written to the API's `web.config`, not `appsettings.json`, because a publish overwrites
`appsettings.json` and does not touch `web.config`. They survive future deployments without further
action: `Update-Production.ps1` seeds each blue/green slot's `web.config` from the live site, so the
values carry across a cutover the same way the connection string does.

## Pair the handset

Needs the business phone in hand. It cannot be scripted.

1. Open the console at **https://sis.kefaloscheese.com/whatsapp-inbox** as an Admin.
2. **New** → name the session (letters, numbers and hyphens only) → **Create + start**.
3. On the phone: WhatsApp → Linked devices → Link a device → scan the QR.
4. The session moves to `ready`, and **Delivery** reads *Registered*.

If Delivery reads *Not registered*, the note under it says why and **Repair delivery** re-registers.

## Prove it works

```bash
.\scripts\Test-WhatsAppDeliveryPath.ps1 -ApiBaseUrl 'http://10.10.10.9:5106' -OpenWaBaseUrl 'http://127.0.0.1:2785' -OpenWaApiKey '<key>' -WebhookSecret '<secret>' -Username '<admin>' -Password '<password>'
```

It creates a throwaway session, confirms OpenWA holds a webhook aimed at this API, posts a signed
delivery and reads it back out of the inbox, checks a wrongly-signed one is refused, and deletes the
session it made. Run it after deploying, after rotating the secret, and after any OpenWA reinstall.

The last check matters: without it, a positive result would also be what you get from an API that
verifies no signature at all.

## When it breaks

**Gateway Unreachable.** OpenWA is not running, or the firewall is not letting this node through.
On 10.10.10.9: `Invoke-WebRequest http://127.0.0.1:2785/api/health`. If that answers and the API
still cannot reach it, the rule is the problem. Logs are in `OpenWA\logs\`.

**Every action says "WhatsApp integration is disabled".** `OpenWA__Enabled` is missing from that
node's `web.config`. Note *that node's* — with two nodes behind a balancer, a half-configured pair
fails for about half of requests, which reads as intermittent rather than as missing configuration.

**Session connected, inbox empty.** This is rows 4 and 5. Look at the Delivery row for the session.
Then run the delivery-path check — it distinguishes "no webhook" from "wrong secret", which the
console cannot.

**`WhatsApp.InvalidWebhookSignature` in the API log.** OpenWA is delivering, and the secret it signs
with is not the one this node verifies against. Usually a secret rotated on the API without the
sessions being restarted. Press **Repair delivery** on each session, or restart them.

**Session dropped to `disconnected` after a reboot.** The boot task did not run, or Chrome could not
start under SYSTEM. `Get-ScheduledTask ShopInventory-OpenWA`, then the newest `OpenWA\logs\*.err.log`.
The stored session survives a restart, so this does not need a fresh QR scan. A session that had been
stopped by hand before the reboot stays stopped. That is deliberate, so press **Start**.

**Session stuck in `authenticating` or `initializing`.** WhatsApp Web loaded but never finished.
whatsapp-web.js can accept the stored login and then never report ready. The gateway restarts an
engine that is still in either state after five minutes (`readyTimeoutMs` in the session's config),
up to five times with a growing gap. After that it marks the session `failed`, so a session in
either state for more than about half an hour means the gateway is older than Ngonie69/OpenWA#3.
On that older gateway, press **Stop** and then **Start**: Start alone answers "Session is already
started".

**Session reads `failed`.** Its retries are used up. Press **Start**. If it shows a QR code, the
phone has dropped the linked device: scan it from WhatsApp → Linked devices. If it fails again,
the reason is in the newest `OpenWA\logs\*.log`. *Execution context was destroyed* is whatsapp-web.js
losing a race with WhatsApp Web reloading itself. It is usually transient, and the gateway already
retries it.

## Customer documents

Invoices sent to customers on WhatsApp, as the Fiscal Tax Invoice PDF, from a **dedicated number**
paired as its own session. Staff send from the invoice drawer on `/invoices`, numbers are kept with
the customer's consent on `/customers`, and administrators run it from `/whatsapp-deliveries`.

Every send is a row in `CustomerDocumentDeliveries`. One clustered Quartz job,
`customer-document-delivery`, sends them one at a time with a randomised gap, and only once the
invoice's fiscal receipt is confirmed. No web request calls WhatsApp directly.

### Before the first send

1. **OpenWA must accept a request over 100 KB.** Express's default JSON limit is 100 KB, and the
   invoice PDF carries a 117 KB logo before any lines. OpenWA from Ngonie69/OpenWA#2 on reads
   `API_BODY_LIMIT` (16mb) and also answers `503`, not `500`, while a session is not ready. A gateway
   older than that refuses every send with `413`: the row is marked **Failed** and administrators get
   a *document too large* alert. Bring .9 up to date as described in
   [Upgrading OpenWA](#upgrading-openwa).
2. **Pair the dedicated number.** Use a SIM and phone that are used for nothing else, with WhatsApp
   Business, the profile name *Kefalos Cheese — Invoices* and the office number in the description.
   On `/whatsapp-inbox`: **New** → `customer-documents` → **Create + start**, then scan the QR code.
   Its webhook registers itself, so customers' replies land in the inbox. Keep the phone charged and
   online.
3. **Warm it up.** Have five to ten staff message the number first. A new number that only sends is
   the pattern WhatsApp bans.
4. **Choose it.** On `/whatsapp-deliveries`, set **Send documents from** to `customer-documents`.
   Leave **Send new invoices automatically** off for the first week, and the automatic cap at 20.

### Where its settings live

| Setting | Where | Changed by |
|---|---|---|
| `CustomerDocuments:Enabled`, the send window, gaps, hourly/daily/per-number caps, caption | `appsettings.json` | A deploy. The same on every node, and it decides whether the job is scheduled at all |
| The session, automatic sending on/off, the automatic daily cap | `SystemConfigs` | `/whatsapp-deliveries`. Every node reads them on its next pass |
| `OpenWA__*`, including `OpenWA:DocumentTimeoutSeconds` (60) | `web.config` | `Set-OpenWAApiConfig.ps1`. A node without them sends nothing and claims nothing |

The job is declared from `appsettings.json` alone, never from `web.config`. `QuartzStoredJobReconciler`
deletes a job that the starting node does not declare, so a per-node setting would let .58 delete .9's
job. A node without the gateway settings runs the job, finds it cannot send, and leaves every row for a
node that can.

### Switching it off

| To | Do |
|---|---|
| Stop automatic sends at once | `/whatsapp-deliveries` → turn **Send new invoices automatically** off |
| Stop every send at once | `/whatsapp-deliveries` → **Send documents from** → *None*. Queued sends wait |
| Remove the job | `CustomerDocuments:Enabled = false` in `appsettings.json`, then deploy |

### Proving it works

Judge by what arrives on the phone, never by the row's status:

1. Save a staff member's number on a test customer and send one invoice from its drawer.
2. On the phone: the PDF opens, the file name is `Kefalos-Invoice-<number>.pdf`, the caption names
   the right customer and total, and the QR code verifies on ZIMRA's page.
3. Compare `CustomerDocumentDeliveries` with OpenWA's own message log for the session. Every **Sent**
   and **Sent (unconfirmed)** row should have an outgoing message to that chat with that file name.

**Sent (unconfirmed)** is normal. Current WhatsApp Web builds often accept a message without returning
its id. It is finished and is never sent again by itself.

### When it goes wrong

`/whatsapp-deliveries` → **Needs a person** lists the three states that wait for someone:

- **Needs attention (Held).** The fiscal receipt could not be confirmed. Either the receipt's
  customer, total or date disagreed with the invoice, or it waited longer than
  `MaxFiscalWaitHours` (24). Check the invoice on `/fiscalisation` before resending. A receipt that
  disagrees is exactly the stale-repost link the check exists to stop.
- **Checking whether it went (Uncertain).** The call to OpenWA was cut off: a timeout, a 5xx, or a
  restart mid-send. After five minutes the job settles it from OpenWA's own message log. If the log
  shows the message, the row becomes Sent. If the log has no record of it, the row is queued again,
  because OpenWA writes its log row before it sends.
- **Failed.** OpenWA refused the request, or five attempts in a row could not reach it. The reason
  is on the row, quoted as the gateway gave it.

**Send again** creates a new row and never repeats the old one. For a send that may already have
arrived, it asks for a tick confirming the customer did not receive it.

Administrators get at most one notification per condition every six hours, never one per document.
The conditions are:

- the session is not ready, or the gateway cannot be reached;
- the gateway refused the job (`401`, `403` or `404`);
- a document is too large (`413`);
- a pass held sends or left them uncertain;
- the hourly or daily cap stopped sends that were due;
- more than `BacklogAlertThreshold` (50) sends are waiting.

## Rotating the webhook secret

1. `Set-OpenWAApiConfig.ps1 ... -WebhookSecret '<new>' -IncludeSlots -RestartAppPool` on both nodes.
2. Press **Repair delivery** on every session, or restart each one — the registration OpenWA already
   holds still carries the old secret, and nothing rewrites it on its own.
3. Re-run `Test-WhatsAppDeliveryPath.ps1`.

Doing step 1 alone drops every inbound message with a signature failure while the console keeps
showing a healthy, registered session.

## Turning it off

```bash
.\scripts\Set-OpenWAApiConfig.ps1 -Disable -IncludeSlots -RestartAppPool
```

The console then refuses operator actions and says why, instead of hanging against a dead gateway.
Stop the gateway itself with `.\scripts\Stop-OpenWA.ps1`.
