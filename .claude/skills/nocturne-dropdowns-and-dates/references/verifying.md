# Seeing the control before you call it done

The running app cannot be screenshotted by either browser tool. It sends
`frame-ancestors 'self'` plus `X-Frame-Options: SAMEORIGIN`, so the in-app
Browser pane refuses to navigate and the Chrome tab lands on an error page. And
Blazor Server renders with `prerender: false`, so `curl` returns only the loading
shell — there is no non-browser way to see a page's real output either.

What works: a static harness plus headless Chrome.

## The harness

Copy the sheets into the scratchpad and hand-write the markup the Razor page
renders:

```
harness/
├── bootstrap.min.css        from wwwroot/lib/bootstrap/dist/css/
├── app.css                  from wwwroot/
├── nocturne-tokens.css      from wwwroot/css/
├── <page>.css               from wwwroot/css/
└── filters.html
```

Link them in that order — it is the order `App.razor` uses, and cascade order is
often the thing under test. Bootstrap Icons and Inter come from the jsDelivr and
Google Fonts CDNs, which the app's CSP already allows.

Build one panel per state you need to see. For a dropdown that means at minimum:
dark with the menu open, dark with a filter set, and light with the menu open.
Render the menu markup inline with the `open`/`selected` classes hard-coded
rather than trying to script a click.

Three harness-specific overrides, and only these three:

```css
/* Page roots are min-height:100vh, so each panel would eat a full viewport. */
.um-page { min-height: 0 !important; padding: 26px 24px 40px !important; }

/* The catch veil is fixed/inset-0 and would cover the panels below it. */
.um-dd-catch { display: none !important; }
```

Wrap each panel in `.dark-theme` or leave it bare for light. Put **one** page
root class per panel and use plain divs inside — the custom properties inherit
down, and a root class on every cell brings `min-height: 100vh` with it.

## Shooting it

```powershell
$h = "<scratchpad>"
$p = Start-Process -FilePath "C:\Program Files\Google\Chrome\Application\chrome.exe" -ArgumentList @(
  "--headless=new", "--disable-gpu",
  "--no-first-run", "--no-default-browser-check",
  "--user-data-dir=$h\chrome-profile",
  "--virtual-time-budget=4000",
  "--window-size=1280,1500",
  "--screenshot=$h\filters.png",
  "file:///$($h -replace '\\','/')/harness/filters.html"
) -PassThru -NoNewWindow
Wait-Process -Id $p.Id -Timeout 90
```

Then `Read` the PNG in a **separate** call — Chrome writes the file after the
tool call returns, so reading it in the same command shows the previous shot.

The flags that matter:

- `--user-data-dir=<fresh dir>` — without its own profile Chrome attaches to the
  user's running instance, never exits, and writes no file at all. The call just
  hangs to its timeout.
- `--virtual-time-budget=4000` — without it the shot catches CSS entry animations
  mid-fade and the whole page looks washed out, which reads as a contrast bug
  that isn't there.
- `--screenshot=` needs an **absolute Windows path**. A bare filename fails with
  "Access is denied" and exit code 0, so it looks like it worked.
- Launch from **PowerShell**, not Bash — Bash mangles the backslashes.

The `persistent_cache` / `crx_installer` errors on a fresh profile are noise.

## What the harness lies about

- **Type size in dark mode.** `app.css` has an iOS anti-zoom rule keyed on
  `max-device-width: 1024px`, which reads the *screen*, not the window — and
  headless reports a small screen. So the query matches at any `--window-size`
  and every dark-mode control measures 16px while its light twin measures the
  real value. Compare type sizes **in light** unless you're specifically testing
  the mobile rule.
- **`backdrop-filter`.** Headless doesn't composite it, though
  `getComputedStyle` still reports the blur. Frosted surfaces render as flat
  translucency. Never call a blur-dependent effect broken from a headless shot.
- **Phone widths.** Chrome enforces a minimum window width around 500px, so
  `--window-size=390,...` silently crops a wider layout instead of laying out at
  390. Put the page in a fixed-width `<iframe>` to test real phone widths.
- **`--dump-dom`** returns nothing under `--headless=new`. To verify markup, add
  a temporary `outline` and re-shoot.

## A screenshot cannot verify the `option` fix

The native popup is a platform window drawn outside the page — which is the
entire reason the bug exists — so it never appears in a screenshot, headless or
otherwise. Shooting the page proves nothing about the fix.

Assert on computed styles instead. Build a probe that mounts a `<select>` under
each page root in both themes and prints the numbers **into the page**, then
screenshot the numbers:

```js
const os = getComputedStyle(option);
// alpha < 1 means the popup will composite onto white -> the bug is present
// contrast < 4.5:1 means the fix picked an unreadable pair
```

Two things make this trustworthy rather than decorative:

- **Check alpha before contrast.** A fully transparent background computes its
  contrast against black and scores ~17:1 — a great number for an invisible
  list. Opacity is the real assertion; contrast is the secondary one.
- **Run a negative control.** Restore the pre-fix sheet into the harness
  (`git show HEAD:path/to/sheet.css`) and confirm the probe reports
  `rgba(0, 0, 0, 0)` / alpha 0. A probe that has never failed hasn't been shown
  to work. This one flagged the unfixed sheet and passed the six fixed ones in
  the same run, which is what makes the PASS row mean something.

## Cheap structural check

A bad merge can leak selectors into a block and silently delete whole rules —
CSS reports nothing. After editing a sheet:

```powershell
$t = Get-Content "ShopInventory.Web\wwwroot\css\<page>.css" -Raw
$noC = [regex]::Replace($t, '/\*.*?\*/', '', 'Singleline')
"open=$(([regex]::Matches($noC,'\{')).Count) close=$(([regex]::Matches($noC,'\}')).Count)"
"orphan-comments=$((([regex]::Matches($t,'/\*')).Count) - (([regex]::Matches($t,'\*/')).Count))"
```

Both numbers should balance. Note that the Grep tool renders `/*` as `\*` in its
output — that is a display artifact, not breakage. Confirm with `Read` before
chasing it.
