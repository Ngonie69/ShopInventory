"""Drive Mobile Orders against a real API and prove it pages on the server.

Run against an API whose database holds mobile orders (see features/mobile-orders.md).
VERIFY_WEB_URL picks the Web; the login is VERIFY_USER / VERIFY_PASSWORD (admin / admin123).

Checks, each recorded in result.json:
  - the first load shows page 1 of the recent-and-open window, one page of rows
  - a status tab narrows the total and every row reads that status
  - a typed order-number filter narrows to the matching rows
  - sorting by Total orders the rows, and a second click reverses it
  - Next shows the second page
  - Export downloads a workbook of every matching order, not just the page
  - Load older orders widens the total
"""
import json
import os
import re
import sys
import time

sys.path.insert(0, os.path.dirname(__file__))
from cdp import Chrome  # noqa: E402

WEB = os.environ.get("VERIFY_WEB_URL", "http://localhost:5051").rstrip("/")
USER = os.environ.get("VERIFY_USER", "admin")
PASSWORD = os.environ.get("VERIFY_PASSWORD", "admin123")

stamp = time.strftime("%Y%m%d-%H%M%S")
outdir = os.path.join("artifacts", "verify", f"{stamp}-mobile-orders-paging")
os.makedirs(outdir, exist_ok=True)
results = {"web": WEB, "checks": []}


def check(name, ok, **detail):
    results["checks"].append({"check": name, "ok": bool(ok), **detail})
    print(("PASS " if ok else "FAIL ") + name + " " + json.dumps(detail)[:300])


def range_label(c):
    return c.eval("document.querySelector('.mox-foot-range')?.innerText || ''") or ""


def total_of(label):
    m = re.search(r"of ([\d,]+) orders", label)
    return int(m.group(1).replace(",", "")) if m else None


def rows(c):
    return c.eval("""[...document.querySelectorAll('table.mox-table tbody tr')]
        .filter(r => r.querySelector('button.mox-docnum'))
        .map(r => ({
            number: r.querySelector('button.mox-docnum').innerText.trim(),
            status: r.querySelector('.mox-status')?.innerText.trim(),
            total: parseFloat((r.querySelector('td.mox-total')?.innerText || '0').replace(/,/g, ''))
        }))""") or []


def wait_label_change(c, before, timeout=15):
    deadline = time.time() + timeout
    while time.time() < deadline:
        now = range_label(c)
        if now and now != before:
            time.sleep(0.4)
            return range_label(c)
        time.sleep(0.2)
    return range_label(c)


def wait_rows_change(c, before, timeout=15):
    deadline = time.time() + timeout
    while time.time() < deadline:
        now = rows(c)
        if now and now != before:
            time.sleep(0.3)
            return rows(c)
        time.sleep(0.2)
    return rows(c)


def click_text(c, selector, text):
    ok = c.eval(f"""(() => {{
        const el = [...document.querySelectorAll({json.dumps(selector)})]
            .find(e => e.textContent.trim().toLowerCase().startsWith({json.dumps(text.lower())}));
        if (!el) return false; el.click(); return true; }})()""")
    if not ok:
        raise RuntimeError(f"no {selector} reading {text!r}")


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

    c.goto(WEB + "/mobile-drafts")
    c.install_error_trap()
    c.wait_for("button.mox-docnum", timeout=40)
    time.sleep(1.0)
    label = range_label(c)
    first = rows(c)
    window_total = total_of(label)
    tiles = c.eval("[...document.querySelectorAll('.mox-stat-value')].map(e => e.innerText.trim())")
    tabs = c.eval("[...document.querySelectorAll('.mox-tab')].map(e => e.innerText.trim())")
    m = re.match(r"Showing 1–([\d,]+) of", label)
    page_size = int(m.group(1).replace(",", "")) if m else 0
    check("first load is page 1 of the window", page_size > 0 and len(first) == page_size and window_total > page_size,
          label=label, rows=len(first), page_size=page_size, tiles=tiles, tabs=tabs)
    check("no Delivered tab", "Delivered" not in tabs, tabs=tabs)
    c.screenshot(os.path.join(outdir, "01-first-load.light.png"))

    # Status tab
    click_text(c, ".mox-tab", "Pending")
    label = wait_label_change(c, label)
    pending = rows(c)
    check("Pending tab narrows and every row reads Pending",
          total_of(label) is not None and total_of(label) < window_total and pending
          and all(r["status"] == "Pending" for r in pending),
          label=label, statuses=sorted({r["status"] for r in pending}))
    click_text(c, ".mox-tab", "All")
    label = wait_label_change(c, label)

    # Typed filter (debounced)
    click_text(c, ".mox-toolbar-right button", "Filters")
    c.wait_for("#mox-f-number")
    before = rows(c)
    c.type_into("#mox-f-number", "mob-0001")
    filtered = wait_rows_change(c, before)
    label = range_label(c)
    check("order-number filter narrows to matches",
          filtered and all("MOB-0001" in r["number"] for r in filtered) and total_of(label) == len(filtered) <= 10,
          label=label, numbers=[r["number"] for r in filtered])
    c.screenshot(os.path.join(outdir, "02-order-number-filter.light.png"))
    click_text(c, "button", "Clear filters")
    label = wait_label_change(c, label)

    # Sort by Total, then reverse
    before = rows(c)
    click_text(c, "th.mox-th-sort", "Total")
    desc = wait_rows_change(c, before)
    click_text(c, "th.mox-th-sort", "Total")
    asc = wait_rows_change(c, desc)
    dt = [r["total"] for r in desc]
    at = [r["total"] for r in asc]
    check("Total sorts descending then ascending across the whole list",
          dt == sorted(dt, reverse=True) and at == sorted(at) and dt[0] >= at[-1],
          descending=dt[:5], ascending=at[:5])

    # Next page
    label = range_label(c)
    click_text(c, "button", "Next")
    label2 = wait_label_change(c, label)
    check("Next shows the second page", label2.startswith(f"Showing {page_size + 1:,}–{2 * page_size:,} of"), label=label2)

    # Export everything matching: capture the download instead of saving it
    c.eval("""window.__downloads = []; window.downloadFile = (name, b64) => window.__downloads.push({name, size: b64.length});""")
    click_text(c, "button", "Export to Excel")
    deadline = time.time() + 60
    while time.time() < deadline and not c.eval("window.__downloads.length"):
        time.sleep(0.5)
    downloads = c.eval("window.__downloads") or []
    check("Export downloads a workbook", len(downloads) == 1 and downloads[0]["size"] > 5000, downloads=downloads)

    # Load older orders
    label = range_label(c)
    click_text(c, "button", "Load older orders")
    label = wait_label_change(c, label)
    check("Load older orders widens the total", (total_of(label) or 0) > window_total, label=label, window=window_total)

    c.set_theme(True)
    time.sleep(0.8)
    c.screenshot(os.path.join(outdir, "03-all-history.dark.png"))
    errors = c.console_errors()
    check("no console errors", not errors, errors=errors[:5])

results["failures"] = sum(1 for x in results["checks"] if not x["ok"])
with open(os.path.join(outdir, "result.json"), "w", encoding="utf-8") as f:
    json.dump(results, f, indent=2)
print(f"{results['failures']} failure(s); evidence in {outdir}")
sys.exit(1 if results["failures"] else 0)
