# WhatsApp in production

How the WhatsApp operator console is turned on for the live estate, and what to check when it stops
working. For starting OpenWA on a developer machine, see
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
messages land on whichever won the race. So the load-balanced pair shares one gateway:

```
  10.10.10.9                                  10.10.10.58
  ┌──────────────────────────────┐            ┌──────────────────────┐
  │ IIS  ShopInventory-API :5106 │◀───────────│ (no OpenWA here)     │
  │ IIS  ShopInventory-Web  :5107│            │ IIS  API :5106       │
  │                              │            │ IIS  Web :5107       │
  │ OpenWA (Node + Chrome) :2785 │◀───────────│ OpenWA__BaseUrl      │
  └──────────────────────────────┘            └──────────────────────┘
            ▲        │
            │        └─ webhook → http://10.10.10.9:5106/api/whatsapp/webhook/openwa
            └────────── OpenWA__BaseUrl = http://10.10.10.9:2785
```

Both nodes read the same Postgres, so a message delivered to .9 is in the inbox .58 serves. If .9 is
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
The stored session survives a restart, so this does not need a fresh QR scan.

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
