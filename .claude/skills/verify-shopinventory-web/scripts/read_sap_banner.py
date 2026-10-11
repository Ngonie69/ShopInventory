"""Read the SAP banner off the running ShopInventory.Web page, as a signed-in member of staff sees it.

    python .claude/skills/verify-shopinventory-web/scripts/read_sap_banner.py <tag>
        [--shots] [--wait-kind down|slow|back|none] [--timeout 150]

Signs in through /login, lands on the home page, and reads the `.sapo-banner` element: its kind
(down / slow / back), the headline, the "why" sentence and the detail. With --wait-kind it reloads
the page every few seconds until the banner is of that kind (or absent, for `none`) and reports how
long that took, so the moment a bar appears or goes is observed rather than assumed. With --shots it
saves the page in light and in dark. Appends one JSON line per reading to result.jsonl under
artifacts/verify/<date>-sap-banner/.

A reading of "no banner" is only as good as the wait behind it: the banner asks the API when the
layout first renders, so an absent bar is read for 15 seconds before it is believed. Prefer
--wait-kind for anything that matters. See features/sap-outage-banner.md for the stack this needs.
"""
import json
import os
import sys
import time

sys.path.insert(0, os.path.dirname(__file__))
from cdp import Chrome  # noqa: E402

WEB = os.environ.get("VERIFY_WEB_URL", "http://localhost:5051").rstrip("/")
USER = os.environ.get("VERIFY_USER", "admin")
PASSWORD = os.environ.get("VERIFY_PASSWORD", "admin123")
OUT = os.path.join("artifacts", "verify", time.strftime("%Y%m%d") + "-sap-banner")

READ = """(() => {
  const b = document.querySelector('.sapo-banner');
  if (!b) return {present: false, shell: !!document.querySelector('main')};
  const kind = ['down', 'slow', 'back'].find(k => b.classList.contains('sapo-banner-' + k)) || 'unknown';
  const spans = [...b.querySelectorAll('.sapo-banner-text > span')];
  const why = spans.find(s => s.classList.contains('sapo-banner-why'));
  const detail = spans.find(s => !s.classList.contains('sapo-banner-why'));
  const cs = getComputedStyle(b);
  return {present: true, kind: kind,
          headline: b.querySelector('strong')?.innerText.trim() || '',
          why: why ? why.innerText.trim() : null,
          detail: detail ? detail.innerText.trim() : '',
          icon: [...(b.querySelector('i')?.classList || [])].filter(c => c !== 'ph').join(' '),
          color: cs.color, background: cs.backgroundColor};
})()"""


def login(c):
    c.goto(WEB + "/login")
    c.wait_for("#username")
    c.type_into("#username", USER)
    c.type_into("#password", PASSWORD)
    c.click("button[type=submit].nsi-submit")
    deadline = time.time() + 60
    while time.time() < deadline:
        path = c.eval("location.pathname") or ""
        if not path.rstrip("/").endswith("/login"):
            c.wait_for_load()
            return
        time.sleep(0.4)
    raise RuntimeError("Still on /login after 60s: " + (c.text() or "")[:300].replace("\n", " "))


def read(c, settle=15.0):
    """Reload the home page and read the banner once the layout has had time to fetch it."""
    c.goto(WEB + "/")
    c.wait_for("main", timeout=40)
    deadline = time.time() + settle
    last = None
    while time.time() < deadline:
        last = c.eval(READ)
        if last and last.get("present"):
            return last
        time.sleep(0.5)
    return last or {"present": False}


def set_theme(c, dark):
    c.eval("""(on => { for (const el of [document.documentElement, document.body]) {
        el.classList.toggle('dark-theme', on); el.classList.toggle('light-theme', !on);
        el.setAttribute('data-theme', on ? 'dark' : 'light'); } })(%s)""" % ("true" if dark else "false"))
    time.sleep(0.6)


def main():
    args = sys.argv[1:]
    tag = args[0]
    shots = "--shots" in args
    wait_kind = args[args.index("--wait-kind") + 1] if "--wait-kind" in args else None
    timeout = float(args[args.index("--timeout") + 1]) if "--timeout" in args else 150.0
    os.makedirs(OUT, exist_ok=True)

    with Chrome(height=1000) as c:
        login(c)
        started = time.time()
        reading = read(c)
        if wait_kind:
            want = (lambda r: not r.get("present")) if wait_kind == "none" else (lambda r: r.get("present") and r.get("kind") == wait_kind)
            while not want(reading) and time.time() - started < timeout:
                time.sleep(4)
                reading = read(c, settle=4.0)
            reading["waited_for"] = wait_kind
            reading["reached"] = bool(want(reading))
            reading["after_seconds"] = round(time.time() - started, 1)
        reading["tag"] = tag
        reading["at_utc"] = time.strftime("%H:%M:%SZ", time.gmtime())
        if shots and reading.get("present"):
            for theme in ("light", "dark"):
                set_theme(c, theme == "dark")
                probe = c.eval("(() => { const cs = getComputedStyle(document.querySelector('.sapo-banner')); return cs.color + ' on ' + cs.backgroundColor; })()")
                reading["computed_" + theme] = probe
                c.screenshot(os.path.join(OUT, f"{tag}.{theme}.png"), full_page=False)
        with open(os.path.join(OUT, "result.jsonl"), "a", encoding="utf-8") as f:
            f.write(json.dumps(reading) + "\n")
        print(json.dumps(reading, indent=1))


if __name__ == "__main__":
    main()
