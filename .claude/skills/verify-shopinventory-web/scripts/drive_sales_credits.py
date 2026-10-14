"""Drive the Shop & Vending sales pages and prove a credited sale reads as a correction.

Run against an API whose database holds today's till and vending sales with fiscalised credits
against some of them (see features/shop-vending-credits.md for the seed). VERIFY_WEB_URL picks the
Web. For each page, in light and dark: the sold figure is still on screen, and what was credited
and the net are beside it.

  /desktop-sales                  the day card's credited line, the row marks, the drawer's lines
  /reports/desktop-sales          the headline, the tender and breakdown rows, the best sellers
  /reports/management-sales       the takings figure, the breakdown and item rows
  /reports/desktop-sales/review   the headline and the "credited back" finding (This week: the
                                  review opens on last week, and the seed is today's)
  /vending/sales                  the settlement row's mark
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

stamp = time.strftime("%Y%m%d-%H%M%S")
outdir = os.path.join("artifacts", "verify", f"{stamp}-sales-credits")
os.makedirs(outdir, exist_ok=True)
results = {"web": WEB, "checks": [], "console_errors": {}}


def check(name, ok, **detail):
    results["checks"].append({"check": name, "ok": bool(ok), **detail})
    print(("PASS " if ok else "FAIL ") + name + " " + json.dumps(detail)[:300])


def wait_text(c, needle, timeout=60):
    deadline = time.time() + timeout
    while time.time() < deadline:
        if needle in (c.text() or ""):
            return True
        time.sleep(0.4)
    return False


def settle_fonts(c):
    deadline = time.time() + 10
    while time.time() < deadline and c.eval("document.fonts.status") != "loaded":
        time.sleep(0.2)


def pick_period(c, label):
    """Press a period button by its words, as a person would."""
    return c.eval(f"""(() => {{ const b = [...document.querySelectorAll('button')]
        .find(x => x.textContent.trim() === {json.dumps(label)}); if (!b) return false; b.click(); return true; }})()""")


PAGES = [
    ("desktop-sales", "/desktop-sales", "Credited back",
     ["Net of credits", "Fully credited", "Credited −"]),
    ("analysis", "/reports/desktop-sales", "Credited back",
     ["Net of credits", "credited"]),
    ("management", "/reports/management-sales", "credited",
     ["net"]),
    ("review", "/reports/desktop-sales/review", "Credited back",
     ["Net of credits", "credited back on"]),
    ("vending", "/vending/sales", "credited",
     []),
]

with Chrome() as c:
    c.goto(WEB + "/login")
    c.install_error_trap()
    c.wait_for("#username")
    c.type_into("#username", USER)
    c.type_into("#password", PASSWORD)
    c.click("button[type=submit].nsi-submit")
    deadline = time.time() + 45
    while time.time() < deadline and str(c.eval("location.pathname")).rstrip("/").endswith("/login"):
        time.sleep(0.4)
    check("logged in", not str(c.eval("location.pathname")).rstrip("/").endswith("/login"))
    c.screenshot(os.path.join(outdir, "00-after-login.png"), full_page=False)

    for tag, route, ready, expected in PAGES:
        for dark in (False, True):
            theme = "dark" if dark else "light"
            c.goto(WEB + route)
            c.install_error_trap()
            c.set_theme(dark)
            if tag == "review":
                wait_text(c, "This week", timeout=30)
                pick_period(c, "This week")
            found = wait_text(c, ready)
            time.sleep(1.0)
            settle_fonts(c)
            body = c.text() or ""
            missing = [needle for needle in expected if needle not in body]
            check(f"{tag} ({theme}): credits stated", found and not missing,
                  path=c.eval("location.pathname"), missing=missing)

            if tag == "desktop-sales" and not dark:
                # The drawer: a credited line says how many came back.
                # The partly credited till sale: its drawer must mark the feta line, and only it.
                opened = c.eval("""(() => { const row = [...document.querySelectorAll('table tbody tr')]
                    .find(tr => tr.innerText.includes('TILL-CR-1')); if (!row) return false;
                    row.click(); return true; })()""")
                marks = []
                deadline = time.time() + 20
                while opened and time.time() < deadline and not marks:
                    marks = c.eval("""[...document.querySelectorAll('.ops-overlay td .ops-ds-credited')]
                        .map(el => el.closest('tr').querySelector('.ops-code')?.innerText.trim() + ': ' + el.innerText.trim())""") or []
                    time.sleep(0.4)
                check("desktop-sales: drawer marks the credited line", marks == ["FET-500: 2 credited"], marks=marks)
                time.sleep(0.6)
                c.screenshot(os.path.join(outdir, f"{tag}-drawer.{theme}.png"), full_page=False)
                # A fresh load takes the app's stored theme, so the shot below needs this one again.
                c.goto(WEB + route)
                c.set_theme(dark)
                wait_text(c, ready)
                time.sleep(1.0)

            if tag == "analysis" and not dark:
                # The credit lands on the item the receipt line names, and on no other.
                rows = c.eval("""[...document.querySelectorAll('.ops-sbd-items tbody tr')].map(r => ({
                    item: r.querySelector('.ops-code')?.innerText.trim(),
                    credited: !!r.querySelector('.ops-sbd-credited') }))""") or []
                credited_items = sorted(row["item"] for row in rows if row["credited"])
                check("analysis: only the credited item is marked", credited_items == ["FET-500"], items=credited_items)

            if tag == "analysis" and dark:
                # Credit colour is a token: it must differ from the body ink in each theme.
                colours = c.eval("""(() => { const el = document.querySelector('.ops-sbd-credited');
                    return el ? [getComputedStyle(el).color, getComputedStyle(document.body).color] : null; })()""")
                check("analysis (dark): credited ink is themed", bool(colours) and colours[0] != colours[1], colours=colours)

            shot = os.path.join(outdir, f"{tag}.{theme}.png")
            c.screenshot(shot)
            errors = c.console_errors()
            results["console_errors"][f"{tag}.{theme}"] = errors
            check(f"{tag} ({theme}): no console errors", not errors, errors=errors[:3])

results["failures"] = sum(1 for item in results["checks"] if not item["ok"])
with open(os.path.join(outdir, "result.json"), "w", encoding="utf-8") as f:
    json.dump(results, f, indent=2)
print(f"\n{outdir}  failures={results['failures']}")
sys.exit(1 if results["failures"] else 0)
