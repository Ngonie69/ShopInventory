---
name: verify-shopinventory-web
description: >-
  Drive the running ShopInventory.Web Blazor Server app the way a user does and
  capture proof: launch the API and Web together, log in, visit real routes in
  both light and dark themes, screenshot them, and collect console errors. Use
  this whenever a change to ShopInventory.Web needs to be shown working rather
  than asserted — before reporting a UI or workflow change done, when a page is
  reported broken, after a Nocturne restyle, or when a claim about what a page
  renders needs evidence. Also use it to check whether a running instance is
  worth driving at all (`doctor`).
---

# Verify ShopInventory.Web

The deliverable of this skill is a screenshot and a `result.json` under
`artifacts/verify/`, produced by driving the real app. Not a build log, not a
description of what the code should do.

## Why the usual tools do not work here

Three properties of this app defeat the obvious approaches. Know them before
reaching for something else.

- **Both browser MCP tools load the page in an iframe.** The app sends
  `frame-ancestors 'self'` and `X-Frame-Options: SAMEORIGIN`, so the frame
  refuses and you get an error page instead of the app.
- **`curl` returns the loading shell.** Blazor Server renders with
  `prerender: false`, so the HTTP response contains no page content.
- **Headless Chrome navigating directly is not framed,** so the CSP never
  applies and the page renders normally. That is what `scripts/cdp.py` uses.

There is no `package.json` at the repo root and Chrome is already installed, so
the driver is a dependency-free CDP client in the Python standard library rather
than Playwright or Puppeteer.

## Launch

Two services. The Web app authenticates through the API, so the API must be up
first or login fails with a connection error.

```bash
# 1. API on 5106
ASPNETCORE_ENVIRONMENT=Development dotnet run \
  --project ShopInventory/ShopInventory.csproj --urls "http://localhost:5106"

# 2. Web on 5051
ASPNETCORE_ENVIRONMENT=Development dotnet run \
  --project ShopInventory.Web/ShopInventory.Web.csproj --urls "http://localhost:5051"
```

Run both in the background and keep their task ids — cleanup needs them.

**Ready when** `curl -s -o /dev/null -w "%{http_code}" http://localhost:5051/login`
returns `200`. The API logs `Now listening on:` and takes roughly 25 seconds on a
cold start because it provisions the Quartz schema. Do not start driving before
`doctor` passes.

### Read this before starting the API

The API is not inert. It registers Quartz jobs including `invoice-posting`,
`inventory-transfer-posting` and `incoming-payment-posting`, each on a
**10-second** interval (`Configuration/QuartzConfiguration.cs:56-58`).

- These three are registered **unconditionally**. `SAP:Enabled=false` does *not*
  gate them — it gates the desktop-sale and credit-limit jobs only (line 151).
  Setting it drops the job count from 14 to 10 and leaves all three posting jobs
  scheduled.
- What actually keeps them harmless is an empty queue. `InvoicePostingJob`
  returns immediately when nothing is pending (`Services/Jobs/InvoicePostingJob.cs:48`).
  On a clean local database they log nothing and never open a SAP connection.
- **The blast radius is whatever is pending in your local database, not a flag.**
  `SAP:ServiceLayerUrl` points at the real Service Layer host
  (`https://10.10.10.6:50000/b1s/v1/`) with `SAP:CompanyDB = KEFALOS_TEST_3`, a
  test company. A pending row will be posted to a real SAP server in a test
  company, so check before running the API if that matters for your change:

```bash
psql -U postgres -d ShopInventory -c \
  'SELECT status, count(*) FROM "InvoiceQueue" GROUP BY status;'
```

If you only need to look at pages and not exercise posting, this is still the
correct way to run it — just know what is scheduled.

## Doctor

Read-only, opens no browser, mutates nothing. Run it first whenever anything
looks off.

```bash
python .claude/skills/verify-shopinventory-web/scripts/verify.py doctor
```

It checks that `/login` answers on 5051, that the API answers on 5106, and that
Chrome is where the driver expects. Any `FAIL` means the instance is not worth
driving; fix that before interpreting anything else.

## Drive

```bash
# core routes, both themes
python .claude/skills/verify-shopinventory-web/scripts/verify.py smoke

# specific routes
python .claude/skills/verify-shopinventory-web/scripts/verify.py shot /credit-notes /reports

# skip dark (see the type-size caveat below)
python .claude/skills/verify-shopinventory-web/scripts/verify.py shot /settings --light-only
```

Login handles are stable and live in `Components/Pages/Login.razor`:

| What | Selector |
|---|---|
| Username field | `#username` |
| Password field | `#password` |
| Submit | `button[type=submit].nsi-submit` |
| Two-factor code | `#twofactor-code` |

Credentials default to `admin` / `admin123` and are overridable with
`VERIFY_USER` and `VERIFY_PASSWORD`. `VERIFY_WEB_URL` and `VERIFY_API_URL`
override the ports.

**Login requires two-factor to be off for the driven account.** The local
database ships exactly one user, `admin`, with `TwoFactorEnabled = true`. The
TOTP secret is encrypted with ASP.NET Data Protection, so the harness cannot
compute a code. The password check passes — a `Login hit the two-factor step`
error means the credentials were right and only the second factor stopped it.
To drive the app, turn 2FA off for the account on your **local development**
database, and turn it back on in cleanup:

```bash
# verification scaffolding - local dev DB only, restore in cleanup
psql -U postgres -d ShopInventory -c \
  'UPDATE "Users" SET "TwoFactorEnabled" = false WHERE "Username" = ''admin'';'
```

For anything beyond a route screenshot, use `cdp.py` as a library. It exposes
`goto`, `wait_for`, `type_into`, `click`, `text`, `set_theme`, `screenshot`,
`install_error_trap` and `console_errors`. `type_into` sets the value through
the native setter and dispatches `input` and `change`, which is what Blazor
binds on — assigning `.value` directly does not update the model.

Prefer stable handles: route paths, `id` attributes, and the Nocturne class
prefixes (`nsi-`, `um-`, and the per-page prefix). Never drive by coordinates or
tab order.

## Evidence

Everything lands in `artifacts/verify/<timestamp>-<tag>/`:

- `00-after-login.png` — proof the session was established
- `<route>.light.png` / `<route>.dark.png` — one per route per theme
- `result.json` — routes, themes, page titles, console errors, failure count

Proof standards for this app:

- Drive the real user path. Logging in through `/login` is the path; setting a
  cookie or calling the API directly is not.
- Capture the resulting state, not just the final screen. A route that silently
  bounced to `/login` is recorded as a failure, not a passing screenshot of the
  login page.
- A console error fails the run even when the screenshot looks right.
- For a data-dependent page, confirm the row you expect is on screen with
  `c.text(...)` rather than trusting that the page rendered.

**Two things a headless screenshot lies about**, both inherited from
`.claude/skills/nocturne-dropdowns-and-dates/references/verifying.md`:

- **Type size in dark mode.** An anti-zoom rule in `app.css` keys on
  `max-device-width: 1024px`, which reads the screen rather than the window, and
  headless reports a small screen. Every dark-mode control measures 16px.
  Compare type sizes in light.
- **`backdrop-filter`.** Headless does not composite it, so frosted surfaces
  render flat. Never call a blur effect broken from a headless shot.
- **Fixed elements in a full-page shot.** `captureBeyondViewport` extends the
  page but leaves position-fixed chrome at viewport height, so on a long page
  the left nav stops partway down and the strip below it renders bare. That is
  the capture, not a layout bug. To check the sidebar itself, shoot a short page
  or pass `full_page=False`.

A native `<select>` popup is a platform window drawn outside the page, so it
never appears in any screenshot. To verify option contrast, use the computed
style probe in that same skill, not this one.

## Cleanup

Kill **what you started**, by task id or PID. Never `taskkill /IM dotnet.exe` —
that kills whatever else the user is running.

```bash
# restore the 2FA flag if you cleared it
psql -U postgres -d ShopInventory -c \
  'UPDATE "Users" SET "TwoFactorEnabled" = true WHERE "Username" = ''admin'';'
```

The driver removes its own Chrome profile and process on exit, including on
failure. Evidence under `artifacts/verify/` **survives cleanup** — never delete
it as part of teardown.

## Helpers

| File | What it is | Invocation |
|---|---|---|
| `scripts/cdp.py` | Stdlib CDP client. Chrome launch, WebSocket, page ops. | imported by `verify.py`, or `from cdp import Chrome` |
| `scripts/verify.py` | CLI: `doctor`, `smoke`, `shot`. | `python .claude/skills/verify-shopinventory-web/scripts/verify.py <cmd>` |

## Feature map

`features/README.md` indexes what a user can actually do here, one file per
feature. A proof that drives one convenient route is incomplete when the map
lists others for the same change. Keep it in step with the app; drift is what
`/maintain-verification-skill` is for.
