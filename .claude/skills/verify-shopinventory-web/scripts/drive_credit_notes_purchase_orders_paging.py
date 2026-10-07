"""Drive Credit Notes and Purchase Orders against a real API and prove they page on the server.

Run against an API whose database holds credit-note projection rows and local purchase orders
(see features/credit-notes.md and features/purchase-orders.md). VERIFY_WEB_URL picks the Web.

Credit Notes: first page of the month, the fiscal filter, the customer filter, a sort, Next.
Purchase Orders: the SAP source (whatever SAP answers is recorded), the local source, a status
filter with its figures, Next.
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
outdir = os.path.join("artifacts", "verify", f"{stamp}-credit-notes-purchase-orders-paging")
os.makedirs(outdir, exist_ok=True)
results = {"web": WEB, "checks": [], "observed": {}}


def check(name, ok, **detail):
    results["checks"].append({"check": name, "ok": bool(ok), **detail})
    print(("PASS " if ok else "FAIL ") + name + " " + json.dumps(detail)[:400])


def js(c, expr):
    return c.eval(expr)


def text_of(c, selector):
    return js(c, f"document.querySelector({json.dumps(selector)})?.innerText.trim() || ''") or ""


def click_text(c, selector, text):
    ok = js(c, f"""(() => {{
        const el = [...document.querySelectorAll({json.dumps(selector)})]
            .find(e => e.textContent.trim().toLowerCase().startsWith({json.dumps(text.lower())}));
        if (!el) return false; el.click(); return true; }})()""")
    if not ok:
        raise RuntimeError(f"no {selector} reading {text!r}")


def wait_change(c, read, before, timeout=30):
    deadline = time.time() + timeout
    while time.time() < deadline:
        now = read()
        if now and now != before:
            time.sleep(0.5)
            return read()
        time.sleep(0.25)
    return read()


def pick(c, button_selector, option_text):
    js(c, f"document.querySelector({json.dumps(button_selector)}).click()")
    time.sleep(0.4)
    click_text(c, ".nsel-open .nsel-item", option_text)
    time.sleep(0.3)


def number(s):
    m = re.search(r"([\d,]+)", s or "")
    return int(m.group(1).replace(",", "")) if m else None


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

    # ── Credit Notes ──────────────────────────────────────────────────────
    cn_rows = lambda: js(c, """[...document.querySelectorAll('table tbody tr')].filter(r => r.querySelector('.cnx-fiscal'))
        .map(r => ({fiscal: r.querySelector('.cnx-fiscal').innerText.trim(),
                    customer: r.querySelector('.cnx-cust-name')?.innerText.trim(),
                    total: parseFloat((r.querySelectorAll('td.cnx-num')[0]?.innerText || '0').replace(/,/g, ''))}))""") or []
    cn_range = lambda: text_of(c, ".cnx-range")

    c.goto(WEB + "/credit-notes")
    c.install_error_trap()
    c.wait_for("td.cnx-fiscal", timeout=60)
    time.sleep(1.0)
    first = cn_rows()
    label = cn_range()
    month_total = number(text_of(c, ".cnx-count"))
    results["observed"]["credit_notes_month_total"] = month_total
    m = re.match(r"1–(\d+) of", label)
    page_size = int(m.group(1)) if m else 0
    check("Credit Notes: first page of the month", page_size > 0 and len(first) == page_size and (month_total or 0) > page_size,
          label=label, total=month_total, rows=len(first))
    check("Credit Notes: the page carries fiscal states", {r["fiscal"] for r in first} >= {"Fiscalised", "Not Fiscalised"},
          states=sorted({r["fiscal"] for r in first}))
    c.screenshot(os.path.join(outdir, "01-credit-notes.png"))

    pick(c, "#cnx-f-fiscal", "Not fiscalised")
    before = cn_range()
    click_text(c, "button.cnx-btn-secondary", "Apply")
    label = wait_change(c, cn_range, before)
    rows = cn_rows()
    not_fiscalised_total = number(text_of(c, ".cnx-count"))
    results["observed"]["credit_notes_not_fiscalised"] = not_fiscalised_total
    check("Credit Notes: fiscal filter narrows and every row reads Not Fiscalised",
          rows and all(r["fiscal"] == "Not Fiscalised" for r in rows) and not_fiscalised_total < month_total,
          label=label, total=not_fiscalised_total)

    click_text(c, "button.cnx-btn-ghost", "Clear")
    label = wait_change(c, cn_range, label)
    c.type_into("#cnx-f-customer", "mbare")
    click_text(c, "button.cnx-btn-secondary", "Apply")
    label = wait_change(c, cn_range, label)
    rows = cn_rows()
    mbare_total = number(text_of(c, ".cnx-count"))
    results["observed"]["credit_notes_mbare"] = mbare_total
    check("Credit Notes: customer filter matches anywhere, ignoring case",
          rows and all("mbare" in (r["customer"] or "").lower() for r in rows), label=label, total=mbare_total)

    click_text(c, "button.cnx-btn-ghost", "Clear")
    label = wait_change(c, cn_range, label)
    before_rows = cn_rows()
    click_text(c, "th.cnx-th-sort", "Total")
    desc = wait_change(c, cn_rows, before_rows)
    totals = [r["total"] for r in desc]
    check("Credit Notes: Total sorts the whole month descending", totals == sorted(totals, reverse=True), first=totals[:5])

    label = cn_range()
    js(c, "document.querySelector('button[aria-label=\"Next page\"]').click()")
    label2 = wait_change(c, cn_range, label)
    second = cn_rows()
    check("Credit Notes: Next shows the second page, still sorted",
          label2.startswith(f"{page_size + 1}–{2 * page_size} of") and second and second[0]["total"] <= totals[-1],
          label=label2)

    # ── Purchase Orders ───────────────────────────────────────────────────
    po_rows = lambda: js(c, """[...document.querySelectorAll('.pdx-row')].map(r => ({
        number: r.querySelector('.pdx-docnum')?.innerText.trim(),
        status: r.querySelector('.pdx-cell-status')?.innerText.trim()}))""") or []
    po_range = lambda: text_of(c, ".pdx-foot-info") or text_of(c, ".pdx-count")
    figures = lambda: js(c, "[...document.querySelectorAll('.pdx-stat-num')].map(e => e.innerText.trim())") or []

    c.goto(WEB + "/purchase-orders")
    c.install_error_trap()
    deadline = time.time() + 90
    while time.time() < deadline and js(c, "!!document.querySelector('.pdx-spin')"):
        time.sleep(0.5)
    time.sleep(1.0)
    results["observed"]["purchase_orders_sap"] = {
        "count": text_of(c, ".pdx-count"), "range": text_of(c, ".pdx-foot-info"), "figures": figures(),
        "rows": len(po_rows()), "alert": text_of(c, ".pdx-alert") or text_of(c, "[role=alert]")}
    print("SAP source:", json.dumps(results["observed"]["purchase_orders_sap"]))
    c.screenshot(os.path.join(outdir, "02-purchase-orders-sap.png"))

    before = text_of(c, ".pdx-count")
    click_text(c, ".pdx-seg-opt", "Local")
    count = wait_change(c, lambda: text_of(c, ".pdx-count"), before)
    rows = po_rows()
    local_total = number(count)
    figs = figures()
    m = re.match(r"Showing 1–(\d+) of", text_of(c, ".pdx-foot-info"))
    po_page = int(m.group(1)) if m else 0
    results["observed"]["purchase_orders_local"] = {"count": count, "figures": figs}
    check("Purchase Orders: local source shows one page and the figures for every match",
          local_total and len(rows) == po_page and po_page < local_total and number(figs[0]) == local_total,
          count=count, figures=figs, rows=len(rows))
    c.screenshot(os.path.join(outdir, "03-purchase-orders-local.png"))

    pick(c, ".pdx-filters .nsel-btn", "Pending")
    before = text_of(c, ".pdx-count")
    click_text(c, "button.pdx-btn-primary", "Apply")
    count = wait_change(c, lambda: text_of(c, ".pdx-count"), before)
    rows = po_rows()
    figs = figures()
    check("Purchase Orders: status filter narrows; Pending is the whole total and Drafts none",
          rows and all(r["status"] == "Pending" for r in rows) and number(figs[2]) == number(count) and number(figs[1]) == 0,
          count=count, figures=figs, statuses=sorted({r["status"] for r in rows}))

    click_text(c, "button.pdx-btn-quiet", "Clear")
    count = wait_change(c, lambda: text_of(c, ".pdx-count"), count)
    label = text_of(c, ".pdx-foot-info")
    click_text(c, "button.pdx-step", "Next")
    label2 = wait_change(c, lambda: text_of(c, ".pdx-foot-info"), label)
    check("Purchase Orders: Next shows the second page", label2.startswith(f"Showing {po_page + 1}–"), label=label2)

    errors = c.console_errors()
    check("no console errors", not errors, errors=errors[:5])

results["failures"] = sum(1 for x in results["checks"] if not x["ok"])
with open(os.path.join(outdir, "result.json"), "w", encoding="utf-8") as f:
    json.dump(results, f, indent=2)
print(f"{results['failures']} failure(s); evidence in {outdir}")
sys.exit(1 if results["failures"] else 0)
