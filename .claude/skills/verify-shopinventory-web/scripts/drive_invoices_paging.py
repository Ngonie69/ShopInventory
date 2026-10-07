"""Drive Invoices against a real API and SAP and prove it pages on the server.

The API must have SAP on (the list refuses otherwise) and, for the fiscal checks, fiscal records seeded
for one customer's invoices (see features/invoices.md). VERIFY_WEB_URL picks the Web; EXPECT is a JSON
file of the counts SAP gives directly (all_count, kefalos_count, spa011_count, spa011_fiscalised, ...).

Checks: the unfiltered first page and SAP-totalled tiles, Next, the case-blind quick search, the customer
filter with each fiscal state, "Select all not fiscalised" across pages, no console errors.
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
EXPECT = json.load(open(os.environ["EXPECT"])) if os.environ.get("EXPECT") else {}
CUSTOMER = os.environ.get("VERIFY_INVOICE_CUSTOMER", "SPA011")

stamp = time.strftime("%Y%m%d-%H%M%S")
outdir = os.path.join("artifacts", "verify", f"{stamp}-invoices-paging")
os.makedirs(outdir, exist_ok=True)
results = {"web": WEB, "expect": EXPECT, "checks": []}


def check(name, ok, **detail):
    results["checks"].append({"check": name, "ok": bool(ok), **detail})
    print(("PASS " if ok else "FAIL ") + name + " " + json.dumps(detail)[:400])


def text_of(c, selector):
    return c.eval(f"document.querySelector({json.dumps(selector)})?.innerText.trim() || ''") or ""


def number(s):
    m = re.search(r"([\d,]+)", s or "")
    return int(m.group(1).replace(",", "")) if m else None


def total_in(label):
    m = re.search(r"of ([\d,]+)", label or "")
    return int(m.group(1).replace(",", "")) if m else None


def click_text(c, selector, text):
    ok = c.eval(f"""(() => {{
        const el = [...document.querySelectorAll({json.dumps(selector)})]
            .find(e => e.textContent.trim().toLowerCase().startsWith({json.dumps(text.lower())}));
        if (!el) return false; el.click(); return true; }})()""")
    if not ok:
        raise RuntimeError(f"no {selector} reading {text!r}")


def wait_change(read, before, timeout=120):
    deadline = time.time() + timeout
    while time.time() < deadline:
        now = read()
        if now and now != before:
            time.sleep(0.6)
            return read()
        time.sleep(0.3)
    return read()


def rows(c):
    return c.eval("""[...document.querySelectorAll('table.ivx-table tbody tr')].filter(r => r.querySelector('.ivx-link'))
        .map(r => ({doc: r.querySelector('.ivx-link').innerText.trim(),
                    name: r.querySelector('.ivx-cust-name')?.innerText.trim(),
                    code: r.querySelector('.ivx-cust-code')?.innerText.trim(),
                    fiscal: r.querySelector('.ivx-fiscal-text')?.innerText.trim()}))""") or []


foot = lambda c: text_of(c, ".ivx-foot-range")

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

    c.goto(WEB + "/invoices")
    c.install_error_trap()
    c.wait_for(".ivx-link", timeout=120)
    time.sleep(1.5)
    label = foot(c)
    first = rows(c)
    tiles = c.eval("[...document.querySelectorAll('.ivx-stat-value')].map(e => e.innerText.trim())")
    m = re.match(r"1–([\d,]+) of", label)
    page_size = int(m.group(1).replace(",", "")) if m else 0
    check("unfiltered: one page of every invoice, tiles added up by SAP",
          page_size == len(first) > 0 and total_in(label) == EXPECT.get("all_count", total_in(label))
          and number(tiles[0]) == EXPECT.get("all_count", number(tiles[0]))
          and number(tiles[1]) == round(EXPECT.get("all_total", number(tiles[1]) or 0)),
          label=label, tiles=tiles, rows=len(first))
    c.screenshot(os.path.join(outdir, "01-invoices.png"))

    c.eval("document.querySelector('button[aria-label=\"Next page\"]').click()")
    label2 = wait_change(lambda: foot(c), label)
    second = rows(c)
    check("Next shows the second page", label2.startswith(f"{page_size + 1:,}–") and second and second[0]["doc"] != first[0]["doc"],
          label=label2)

    c.type_into(".ivx-quick input", "kefalos")
    label3 = wait_change(lambda: foot(c), label2)
    found = rows(c)
    check("quick search ignores case and covers every invoice",
          found and all("kefalos" in ((r["name"] or "") + (r["code"] or "")).lower() for r in found)
          and total_in(label3) == EXPECT.get("kefalos_count", total_in(label3)) and label3.startswith("1–"),
          label=label3)
    c.type_into(".ivx-quick input", "")
    label = wait_change(lambda: foot(c), label3)

    c.type_into("#ivx-f-customer", CUSTOMER)
    label = wait_change(lambda: foot(c), label)
    check(f"customer filter {CUSTOMER}", total_in(label) == EXPECT.get("spa011_count", total_in(label))
          and all(r["code"] == CUSTOMER for r in rows(c)), label=label)

    for state, key in [("Fiscalised", "spa011_fiscalised"), ("Not fiscalised", "spa011_not_fiscalised"), ("Unknown", "spa011_unknown")]:
        c.eval("document.querySelector('#ivx-f-fiscal').click()")
        time.sleep(0.4)
        click_text(c, ".nsel-open .nsel-item", state)
        new = wait_change(lambda: foot(c), label)
        r = rows(c)
        check(f"fiscal filter {state}: every match across pages, every row in that state",
              total_in(new) == EXPECT.get(key, total_in(new)) and r and all((x["fiscal"] or "").lower() == state.lower() for x in r),
              label=new, states=sorted({x["fiscal"] for x in r}))
        label = new

    # Under "Unknown": every match is fiscalisable, across more than one page.
    expected_unknown = total_in(label)
    click_text(c, ".ivx-toolbar button", "Select all")
    deadline = time.time() + 120
    while time.time() < deadline and not re.search(r"\d[\d,]* selected", text_of(c, ".ivx-toolbar")):
        time.sleep(0.5)
    selected = number(re.search(r"([\d,]+) selected", text_of(c, ".ivx-toolbar")).group(1)) if re.search(r"([\d,]+) selected", text_of(c, ".ivx-toolbar")) else None
    check("Select all not fiscalised takes every match, not just the page",
          selected == expected_unknown and selected > page_size, selected=selected, expected=expected_unknown, page=page_size)
    before = foot(c)
    c.eval("document.querySelector('button[aria-label=\"Next page\"]').click()")
    after = wait_change(lambda: foot(c), before)
    still = re.search(r"([\d,]+) selected", text_of(c, ".ivx-toolbar"))
    check("the selection survives a page turn", still and number(still.group(1)) == selected, label=after)
    c.screenshot(os.path.join(outdir, "02-unknown-selected.png"))

    errors = c.console_errors()
    check("no console errors", not errors, errors=errors[:5])

results["failures"] = sum(1 for x in results["checks"] if not x["ok"])
with open(os.path.join(outdir, "result.json"), "w", encoding="utf-8") as f:
    json.dump(results, f, indent=2)
print(f"{results['failures']} failure(s); evidence in {outdir}")
sys.exit(1 if results["failures"] else 0)
