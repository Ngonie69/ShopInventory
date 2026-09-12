"""Drives /fiscalisation through all four tabs, in both themes.

Written for the redesign that turned the console's four stacked sections into a
summary band plus four tabs. A screenshot of the default tab would prove only
that the page loads; the thing worth proving is that each panel renders its own
content, that switching between them works, and that the work queue's new
checkbox column and rows-per-page control are really there.

Run with both services up and doctor passing:

    python .claude/skills/verify-shopinventory-web/scripts/drive_fiscal_console.py
"""
import json
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from cdp import Chrome  # noqa: E402
from verify import EVIDENCE, WEB, _login  # noqa: E402


def _outdir(tag):
    out = os.path.join(EVIDENCE, "%s-%s" % (time.strftime("%Y%m%d-%H%M%S"), tag))
    os.makedirs(out, exist_ok=True)
    return out

# Label, and a selector that is present only when that panel has rendered.
TABS = [
    ("Work queue", "section[aria-label='Work queue']"),
    ("Compliance evidence", "section[aria-label='Compliance evidence']"),
    ("Devices", "section[aria-label='Devices']"),
    ("Fiscal days", "section[aria-label='Fiscal days']"),
]


def click_tab(c, label):
    """Clicks the tab by its visible text, the way a person picks it."""
    clicked = c.eval(
        "(() => { const t = [...document.querySelectorAll('.fcx-tab')]"
        ".find(b => b.innerText.trim().startsWith(%s));"
        " if (!t) return false; t.click(); return true; })()" % json.dumps(label)
    )
    if not clicked:
        raise RuntimeError("no tab button reading %r" % label)
    time.sleep(0.9)


def fonts_ready(c, seconds=15.0):
    """Waits for the webfonts, Bootstrap Icons included.

    Without this the first shot of a run catches the icon font mid-flight and
    every `<i class="bi ...">` photographs as an empty box — which looks exactly
    like an icon name that does not exist. The font comes from jsDelivr, so how
    long it takes is a property of the network, not of the page.
    """
    deadline = time.time() + seconds
    while time.time() < deadline:
        if c.eval("document.fonts.status === 'loaded'"
                  " && document.fonts.check('16px \"bootstrap-icons\"')"):
            return True
        time.sleep(0.25)
    return False


def settle(c, seconds=20.0):
    """Waits out any in-panel spinner, so a shot is of content and not of a spinner.

    Capped rather than open-ended: locally the REVMax device is unreachable and
    that panel legitimately stays on its spinner until the call times out. The
    cap is what stops this waiting for a device that is not on this network.
    """
    deadline = time.time() + seconds
    while time.time() < deadline:
        if not c.eval("!!document.querySelector('.fcx-loading')"):
            return True
        time.sleep(0.4)
    return False


def main():
    outdir = _outdir("fiscal-console")
    results = []
    failures = 0

    with Chrome(headless=True, width=1600, height=1000) as c:
        c.install_error_trap()
        _login(c)
        c.screenshot(os.path.join(outdir, "00-after-login.png"))

        c.goto(WEB + "/fiscalisation")
        c.wait_for(".fcx", timeout=30)

        landed = c.eval("location.href")
        if str(landed).rstrip("/").endswith("/login"):
            print("FAIL bounced to /login")
            return 1

        # The band is above the tabs and has to be true on every one of them, so
        # it is checked once here and then again on the last tab.
        band = c.text(".fcx-band")
        results.append({"check": "band rendered", "ok": ".fcx-band" and bool(band.strip()),
                        "text": band[:400]})

        for label, panel in TABS:
            click_tab(c, label)
            loaded = settle(c)
            fonts_ready(c)

            for dark in (True, False):
                c.set_theme(dark)
                time.sleep(0.35)
                theme = "dark" if dark else "light"
                slug = label.lower().replace(" ", "-")
                c.screenshot(os.path.join(outdir, "fiscalisation.%s.%s.png" % (slug, theme)))

            present = c.eval("!!document.querySelector(%s)" % json.dumps(panel))
            selected = c.eval(
                "(() => { const t = [...document.querySelectorAll('.fcx-tab')]"
                ".find(b => b.innerText.trim().startsWith(%s));"
                " return t ? t.getAttribute('aria-selected') : null; })()" % json.dumps(label)
            )
            body = c.text(panel) if present else ""

            ok = bool(present) and selected == "true" and len(body.strip()) > 40
            failures += 0 if ok else 1
            results.append({
                "tab": label,
                "ok": ok,
                "panel_present": bool(present),
                "aria_selected": selected,
                "finished_loading": loaded,
                "text": body[:400],
            })

        # Back to the queue for the two controls the redesign added.
        click_tab(c, "Work queue")
        c.set_theme(True)
        time.sleep(0.4)

        checkboxes = c.eval("document.querySelectorAll('.fcx-check').length")
        header_box = c.eval("!!document.querySelector('th.fcx-col-check .fcx-check')")
        size_picker = c.eval("!!document.querySelector('.fcx-pager-size')")
        band_still_there = c.eval("!!document.querySelector('.fcx-band-count')")
        tab_count = c.eval("document.querySelectorAll('.fcx-tab').length")

        results.append({
            "check": "queue controls",
            "ok": bool(header_box) and bool(size_picker) and tab_count == 4 and bool(band_still_there),
            "checkbox_count": checkboxes,
            "header_select_all": bool(header_box),
            "rows_per_page": bool(size_picker),
            "band_visible_on_queue_tab": bool(band_still_there),
            "tab_count": tab_count,
        })
        if not (header_box and size_picker and tab_count == 4 and band_still_there):
            failures += 1

        c.screenshot(os.path.join(outdir, "fiscalisation.queue-controls.dark.png"))

        errors = c.console_errors()

    payload = {
        "route": "/fiscalisation",
        "results": results,
        "console_errors": errors,
        "failures": failures + len(errors),
    }
    with open(os.path.join(outdir, "result.json"), "w", encoding="utf-8") as fh:
        json.dump(payload, fh, indent=2)

    print(json.dumps(payload, indent=2)[:4000])
    print("\nartifacts: " + outdir)
    return 1 if payload["failures"] else 0


if __name__ == "__main__":
    raise SystemExit(main())
