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

From outside the checkout, launch it with `-File` instead:

```bash
powershell -ExecutionPolicy Bypass -File C:\ShopInventory\scripts\Install-OpenWAProduction.ps1
```

Before ShopInventory#763 that form failed at once under Windows PowerShell 5.1 with
`Split-Path : Cannot bind argument to parameter 'Path' because it is an empty string`. 5.1 leaves
`$PSScriptRoot` empty in a parameter's default value under `-File`, and every OpenWA script used it to
find the checkout. On an older checkout, run the script in-process and name the checkout yourself:
`& C:\ShopInventory\scripts\Install-OpenWAProduction.ps1 -RepositoryRoot C:\ShopInventory`.

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

The boot task runs `scripts\Run-OpenWA.ps1` from this checkout, and a running task keeps the copy
it started with. A change to that script takes effect the next time the task starts. That happens
on an installer run that restarts the gateway, on a reboot, or with `.\scripts\Stop-OpenWA.ps1`
followed by `Start-ScheduledTask ShopInventory-OpenWA`. A re-run with nothing changed leaves the
old runner going.

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

The boot task's runner, `scripts\Run-OpenWA.ps1`, supervises node. When node exits with any code
but 0 it is started again after 5 seconds, then 10, 20 and so on up to 5 minutes between tries. A
run that lasted 10 minutes counts as healthy, and the next crash starts again at 5 seconds. Before
each start the runner ends any Chrome still holding one of OpenWA's session profiles, which a crash
can leave behind.

The runner also restarts a node that is still running but has stopped answering, for example with a
wedged event loop or a puppeteer deadlock. No exit code reports that. Once node has answered
`http://127.0.0.1:<port>/api/health` with 200, the runner asks again every 30 seconds, with a
10-second timeout. After 4 failures in a row, about 2 minutes, it ends node and any Chrome on
OpenWA's session profiles, then restarts node with the same backoff as a crash. Until the first 200
nothing is counted, because a restart on .9 took over a minute to answer. A gateway that is slow to
start is never ended for it. The port is taken from `PORT` in the environment, then `OpenWA\.env`,
then `data\.env.generated`, then 2785. `-Port` overrides all of these, and `-HealthCheckSeconds 0`
turns the check off. `/api/health` shows only that node's HTTP server is answering. A session whose
Chrome has hung while node still answers is not covered here. That is the session's own
`readyTimeoutMs` retry, below.

Every start, first answer, failed health check, exit code and restart is a line in
`OpenWA\logs\openwa-supervisor.txt`, and each run has its own `openwa-<time>.out.log` and `.err.log`. So:

- **The supervisor log shows restarts, and the gateway answers again.** It crashed and came back.
  The reason is at the end of the `.err.log` named in the line before the restart. Sessions that
  were running start again by themselves.
- **The supervisor log shows `Health check 1 of 4 failed` … `Ending it`, then a restart.** node was
  alive but stopped answering, and the runner ended it. The `.err.log` and `.out.log` named in the
  restart line end where it hung, with no crash message, because node never exited by itself. A
  `Health check` line followed by `answered … again` is a slow moment that passed. Nothing was ended.
- **The supervisor log shows a restart every few minutes.** The gateway crashes as soon as it starts.
  The gap has grown to 5 minutes, and it keeps trying. Read the newest `.err.log`. Each run has the
  same failure until the cause is fixed.
- **Nothing listens on 2785 and `Get-ScheduledTask ShopInventory-OpenWA` reads `Ready`, not
  `Running`.** The supervisor itself is gone. The last line of the supervisor log says why: a stop,
  or node exiting with code 0. If the log is missing, or has no line from that day, the runner on
  this host is older than the supervisor and ran node only once. That is how the gateway stayed down
  for two days from 14 Oct 2026. Start it with `Start-ScheduledTask ShopInventory-OpenWA`.
- **node is running and `/api/health` does not answer, and the supervisor log has no `Health check`
  lines.** Either node has not answered once since it started, which counts as still starting, or
  the runner on this host is older than the health check, which reaches .9 only when the task is
  restarted. The log's `Supervisor started` line names the URL it checks. If it has no such line,
  or names the wrong port, restart the task.

To stop the gateway on purpose, use `.\scripts\Stop-OpenWA.ps1`. It writes `OpenWA\logs\stop-requested`
first, so the supervisor exits and does not restart node, then ends node and waits for the supervisor
to go. If you end `node.exe` any other way, for example from Task Manager, the supervisor treats it as
a crash and node is back within 5 seconds. Starting the task clears the marker.

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

**Stop fails with "OpenWA did not answer POST /api/sessions/…/stop within 30 seconds".** The gateway
is older than Ngonie69/OpenWA#4. To stop a session it closes that session's Chrome, and the older
gateway waits for Chrome to exit for as long as that takes, while the API gives up after 30 seconds
(`OpenWA:TimeoutSeconds`). The stop was not cancelled: it carries on, and the session reads `ready`
until Chrome has gone. From #4 on the gateway allows Chrome 15 seconds and then ends its process, so
a stop always answers. On the older gateway:

1. Wait a minute and look at the row again. `disconnected` means the stop went through.
2. Still `ready` after a few minutes means Chrome never exited. Stop the gateway with
   `.\scripts\Stop-OpenWA.ps1`, then start the `ShopInventory-OpenWA` task. A healthy Chrome exits
   by itself within half a minute of the gateway stopping. A hung one does not, and while it runs
   the session cannot start again. The supervising runner ends any Chrome still holding a session
   profile before it starts node. An older runner does not, so end it with the command below, using
   the session's name, before you start the task.
3. Either way, press **Start** when the session should be running. The stop marked it as stopped on
   purpose before it did anything else, so no restart brings it back by itself.

```powershell
Get-CimInstance Win32_Process -Filter "name = 'chrome.exe'" |
    Where-Object CommandLine -like '*session-fiscal-alerts*' |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
```

While it is stopped nothing is sent through it and nothing it receives reaches the inbox.

**Gateway Unreachable straight after a Stop, and every session with it.** The gateway is older than
Ngonie69/OpenWA#5. Pressing **Stop** while a session read `initializing` could close its Chrome
while WhatsApp Web was still downloading. whatsapp-web.js was reading that page with nothing waiting
on the result, the read was rejected, and Node ends a process on a rejection nobody handles. The
newest `OpenWA\logs\*.err.log` ends with
`TargetCloseError: Protocol error (Network.getResponseBody): Target closed`. The supervising runner
restarts it within seconds (see **Gateway Unreachable** above). An older runner ran the gateway only
once, and on that runner no session sends or receives until someone restarts it:

```powershell
Start-ScheduledTask ShopInventory-OpenWA
```

Sessions that were running start again by themselves. The one that was being stopped stays stopped.
From #5 on the gateway logs the rejection and carries on: look for
`Unhandled promise rejection` with context `Process` in the same log. One of those lines after a
Stop is this race and needs nothing done. Any other is a fault the gateway survived and is worth
reading.

**Start fails after a Stop, and the log says "The browser is already running".** The gateway is
older than Ngonie69/OpenWA#6. A start takes about a second to bring Chrome up, and a **Stop** or a
delete that arrived in that second closed nothing, because there was no Chrome to close yet. The
stop answered, the row read `disconnected`, and Chrome came up anyway with nothing holding it. It
keeps the session's profile locked, so every later **Start** answers "Internal server error" and the
newest `OpenWA\logs\*.log` has `The browser is already running for …\session-<name>`. The
five-minute restart of a stuck session could leave a Chrome behind the same way. On the older
gateway, end that Chrome with the command above (with the session's name), then press **Start**.

From #6 on a stop waits for Chrome to come up and closes it, so it can take a few seconds longer.
The start it overtook answers "Session '…' was stopped, deleted or restarted before it finished
starting". That message is the stop working and needs nothing done: press **Start** again when the
session should be running. A second **Start** pressed while the first is still getting going
answers "Session is already started".

## Customer documents

Invoices sent to customers on WhatsApp from a **dedicated number** paired as its own session: as the
Fiscal Tax Invoice PDF, or as the till slip the van's printer gives when the sale was filed with ZIMRA
as a 48 mm receipt (the partner's document type on Settings → Fiscalisation). Staff send from the
invoice drawer on `/invoices`, numbers are kept with the customer's consent on `/customers`, and
administrators run it from `/whatsapp-deliveries`.

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
4. **Nothing to choose.** Once the session reads `ready` the delivery job takes it within a minute,
   and `/whatsapp-deliveries` shows *Sending from customer-documents*, changed by *Chosen
   automatically*. With one ready session that one is used whatever it is called; with several, the one
   named `customer-documents` (`CustomerDocuments:PreferredSessionName`). Only when several are ready
   and none has that name does an administrator have to pick it under **Send documents from**, and they
   are sent a *no number to go from* alert if documents are waiting on it.
5. **Mind the warm-up.** Automatic sending is on unless someone has switched it off, held to the
   automatic cap (20 a day until raised) and the send window. For a number WhatsApp has never seen,
   switch **Send new invoices automatically** off for its first week, or keep the cap low: a new
   number that suddenly sends many documents to people who never wrote to it is the pattern WhatsApp
   restricts. Sends a person or a van rep asks for are not held to that cap.

### Van sales

After a sale the van app asks the API to send the invoice to the shop's saved number, without the
rep doing anything; the confirmation screen says where it went. A shop with no number yet shows
**Send the invoice on WhatsApp**: the rep types the number the customer gives and reads it back, once.
That number is saved on the shop, marked for automatic invoices, with the rep recorded as having taken
the consent, so no later sale asks again. The API queues each send against the sale
(`trigger: Counter`). It waits
as *Waiting for fiscal receipt*, reason "Waiting for the van sale to reach SAP", until the office has
posted the sale, then goes as that SAP invoice with the shop as the buyer, not the van. Counter sends
keep to the hourly, daily and per-number caps but not the automatic window. Each rep may type
`MaxVanSaleSendsPerUserPerDay` (80) numbers a day; sends to a saved number are not counted. A number
is changed, set to only-when-asked, opted out or removed on **Route Customers** on the Web; a shop
holds at most `MaxContactsPerOwner` (3), and a fourth given at the van is sent to without being saved.

A sale that never reaches SAP is held for a person after `MaxFiscalWaitHours` (24), like any other.

**Shops that want every invoice.** Save the shop's number on **Route Customers** with *Send their new invoices
to it automatically* ticked. The invoice scan then sends each of that shop's van invoices once SAP has it,
naming the shop, not the van. It reads the van invoice's sale reference to find the shop. If the rep
already sent the invoice to that number at the van, or someone sent it by hand, it is not sent again.
`/van-sales/invoices` shows what was sent in each invoice's drawer, with **Send via WhatsApp** for
staff who may send.

### Automatic sends

A second clustered job, `customer-invoice-scan`, looks for new SAP invoices every two minutes and
queues one for each number marked **Send new invoices automatically** on the invoice's own customer.
It reads past a DocEntry watermark kept in `SystemConfigs` as `CustomerDocuments.InvoiceScanCheckpoint`,
re-reading the last 20 in case two posts finished out of order. It sees every invoice, including
ones keyed straight into B1. It sends nothing itself: its rows go through the same fiscal check,
window and caps as a manual send, and only within the automatic cap.

- **The first pass sends nothing.** It records the newest invoice, so nothing from before the scan
  ran is ever sent. With automatic sending off it still moves the watermark, so turning it on later
  never sends the backlog.
- **What it leaves alone.** No row for cancelled or consolidated invoices, or for anything on a
  selling account (shop tills, cart vendors), whatever numbers are saved. A van invoice whose shop is
  found goes to the shop's numbers instead (see *Van sales*); one whose shop cannot be found, or whose
  sale row names another invoice, goes to no one automatically. A **Skipped** row
  with the reason for a reposted invoice, or one dated more than `AutoMaxDocumentAgeDays` (7) back.
  Either can still be sent by hand.
- **Is it running?** Under the automatic switch, `/whatsapp-deliveries` says when SAP was last
  checked. During an SAP outage it waits and starts again from the same invoice.
- **To start again from now**, delete the `CustomerDocuments.InvoiceScanCheckpoint` row. The next
  pass records the newest invoice and sends nothing older.

### Where its settings live

| Setting | Where | Changed by |
|---|---|---|
| `CustomerDocuments:Enabled`, the send window, gaps, hourly/daily/per-number caps, caption, the scan's interval and page size | `appsettings.json` | A deploy. The same on every node, and it decides whether the job is scheduled at all |
| The session, automatic sending on/off, the automatic daily cap | `SystemConfigs` | `/whatsapp-deliveries`, or nobody: with nothing saved the session is chosen automatically and automatic sending is on. Every node reads them on its next pass |
| `OpenWA__*`, including `OpenWA:DocumentTimeoutSeconds` (60) | `web.config` | `Set-OpenWAApiConfig.ps1`. A node without them sends nothing and claims nothing |

The job is declared from `appsettings.json` alone, never from `web.config`. `QuartzStoredJobReconciler`
deletes a job that the starting node does not declare, so a per-node setting would let .58 delete .9's
job. A node without the gateway settings runs the job, finds it cannot send, and leaves every row for a
node that can.

### Switching it off

| To | Do |
|---|---|
| Stop automatic sends at once | `/whatsapp-deliveries` → turn **Send new invoices automatically** off |
| Stop every send at once | `/whatsapp-deliveries` → **Send documents from** → *None — stop all sending*. Queued sends wait, and no session is chosen automatically until it is put back to *Automatic* or a number |
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
