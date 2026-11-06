"""Drive /customer-portal/credit-notes on the real Web (against stub_api.py) and keep evidence.

Env: VERIFY_WEB_URL, STUB_URL, PORTAL_CARD_CODE, PORTAL_PASSWORD, SKILL_SCRIPTS, OUT_DIR.
"""
import json
import os
import sys
import time
import urllib.request

sys.path.insert(0, os.environ["SKILL_SCRIPTS"])
from cdp import Chrome  # noqa: E402

WEB = os.environ["VERIFY_WEB_URL"].rstrip("/")
STUB = os.environ["STUB_URL"].rstrip("/")
CARD = os.environ["PORTAL_CARD_CODE"]
PASSWORD = os.environ["PORTAL_PASSWORD"]
OUT = os.environ["OUT_DIR"]
os.makedirs(OUT, exist_ok=True)
results = {"web": WEB, "checks": [], "console_errors": {}}


def check(name, ok, **detail):
    results["checks"].append({"check": name, "ok": bool(ok), **detail})
    print(("PASS " if ok else "FAIL ") + name + " " + json.dumps(detail, default=str)[:500])


def stub_log():
    return json.loads(urllib.request.urlopen(STUB + "/__requests", timeout=5).read())


def wait(c, js, timeout=30):
    deadline = time.time() + timeout
    while time.time() < deadline:
        value = c.eval(js)
        if value:
            return value
        time.sleep(0.25)
    return c.eval(js)


def txt(c, selector):
    return c.eval(f"document.querySelector({json.dumps(selector)})?.innerText.trim() || ''") or ""


def rows(c):
    return c.eval("[...document.querySelectorAll('.cpd-ledger table.cpd-table tbody tr')]"
                  ".map(r => r.querySelector('.cpd-doc-id')?.innerText.trim())") or []


def click_text(c, selector, text):
    ok = c.eval(f"""(() => {{ const el = [...document.querySelectorAll({json.dumps(selector)})]
        .find(e => e.innerText.trim().startsWith({json.dumps(text)})); if (!el) return false; el.click(); return true; }})()""")
    if not ok:
        raise RuntimeError(f"no {selector} starting {text!r}")
    time.sleep(0.5)


def fonts_ready(c):
    wait(c, "document.fonts.status === 'loaded' && document.fonts.check('16px \"Phosphor\"')", 20)
    time.sleep(0.4)


def sign_in(c):
    c.goto(WEB + "/customer-portal/login")
    c.wait_for("#cardCode", timeout=60)
    c.type_into("#cardCode", CARD)
    c.type_into("#password", PASSWORD)
    c.click("button[type=submit].signin-submit")
    return wait(c, "location.pathname.startsWith('/customer-portal/dashboard') && location.pathname", 60)


def load_period(c):
    c.wait_for("#ccn-f-from", timeout=60)
    c.install_error_trap()
    c.type_into("#ccn-f-from", "01/10/2026")
    c.type_into("#ccn-f-to", "06/11/2026")
    time.sleep(0.6)
    c.click(".cpd-field-action .btn")
    return wait(c, "document.querySelectorAll('.cpd-ledger table.cpd-table tbody tr').length", 60)


def hook_download(c):
    c.eval("""(() => { window.__downloads = [];
        window.downloadPdfFromBase64 = (b64, name) => window.__downloads.push(
            {name, bytes: Math.floor(b64.length * 3 / 4), head: atob(b64.slice(0, 8)).slice(0, 4)}); return true; })()""")


# ---------------------------------------------------------------- desktop
with Chrome(width=1440, height=1420) as c:
    landed = sign_in(c)
    check("portal sign-in lands on the dashboard", landed, path=landed)

    c.wait_for("a.cpx-navlink[href=\"/customer-portal/credit-notes\"]", timeout=30)
    link = c.eval("document.querySelector('a.cpx-navlink[href=\"/customer-portal/credit-notes\"]')?.innerText.trim()")
    check("sidebar carries a Credit notes link", link == "Credit notes", link=link)
    c.click("a.cpx-navlink[href=\"/customer-portal/credit-notes\"]")
    path = wait(c, "location.pathname === '/customer-portal/credit-notes' && location.pathname", 30)
    c.wait_for("#ccn-f-from", timeout=60)
    c.install_error_trap()
    fonts_ready(c)
    check("the link opens /customer-portal/credit-notes", path, path=path)
    check("top bar names the section and the link is active",
          txt(c, ".cpx-screen-title") == "Credit notes" and txt(c, "a.cpx-navlink.active") == "Credit notes",
          title=txt(c, ".cpx-screen-title"), active=txt(c, "a.cpx-navlink.active"))
    blank = c.eval("document.querySelector('.cpd-field-action .btn').disabled")
    check("Load waits for a period", blank and "Choose a period" in c.text(), disabled=blank)
    c.set_theme(False)
    c.screenshot(os.path.join(OUT, "01-before-load.light.png"), full_page=False)

    before = len(stub_log())
    count = load_period(c)
    fonts_ready(c)
    asked = [r["path"] for r in stub_log()[before:] if "creditnote/customers" in r["path"].lower()]
    check("one API read for the account and period",
          len(asked) == 1 and f"cardCodes={CARD}" in asked[0] and "fromDate=2026-10-01" in asked[0] and "toDate=2026-11-06" in asked[0],
          asked=asked)
    check("all eight credit notes listed newest first", count == 8 and rows(c)[0] == "30418", rows=rows(c))

    kpis = c.eval("[...document.querySelectorAll('.cpd-kpi .cpd-kpi-num')].map(e => e.innerText.trim())")
    check("figures: credited, count, credit left, latest", kpis == ["$2,137.15", "8", "$574.50", "$186.40"], kpis=kpis)
    strip = txt(c, ".cpd-credit-note")
    check("credit-left strip states the figure and the notes", "$574.50" in strip and "3 credit notes" in strip, strip=strip)
    credit_tile = c.eval("document.querySelectorAll('.cpd-kpi')[2].classList.contains('is-credit')")
    check("credit-left figure is marked", credit_tile)
    statuses = c.eval("[...document.querySelectorAll('.cpd-ledger tbody .cpd-status')].map(e => e.innerText.trim())")
    aligns = c.eval("[...document.querySelectorAll('.cpd-ledger thead th')].map(t => t.innerText.trim() + ':' + getComputedStyle(t).textAlign)")
    check("money and status headings sit over their right-aligned figures",
          all(a.endswith(":right") for a in aligns if a.split(":")[0] in ("AMOUNT", "APPLIED", "CREDIT LEFT", "STATUS", "DOCUMENT")),
          aligns=aligns)
    check("statuses read Not applied / Part applied / Applied",
          statuses[:4] == ["Not applied", "Not applied", "Applied", "Part applied"], statuses=statuses)
    c.set_theme(False)
    c.screenshot(os.path.join(OUT, "02-list.light.png"), full_page=False)

    click_text(c, ".cpd-seg-btn", "Credit left")
    check("Credit left shows the three with credit", rows(c) == ["30418", "30402", "30377"], rows=rows(c))
    click_text(c, ".cpd-seg-btn", "Applied")
    check("Applied shows the five used up", len(rows(c)) == 5 and "30377" not in rows(c), rows=rows(c))
    click_text(c, ".cpd-seg-btn", "All")
    c.type_into("#ccn-f-find", "10431")
    time.sleep(0.5)
    check("Find matches the credited invoice number", rows(c) == ["30377"], rows=rows(c))
    c.type_into("#ccn-f-find", "")
    time.sleep(0.4)
    click_text(c, ".cpd-text-btn", "Show them")
    check("Show them filters to credit left", rows(c) == ["30418", "30402", "30377"], rows=rows(c))
    click_text(c, ".cpd-seg-btn", "All")

    c.set_theme(True)
    c.screenshot(os.path.join(OUT, "02-list.dark.png"), full_page=False)
    c.set_theme(False)

    hook_download(c)
    pdf_before = len(stub_log())
    c.eval("document.querySelector('button[aria-label=\"View credit note 30377\"]').click()")
    wait(c, "document.querySelector('.cpd-sheet')", 15)
    time.sleep(0.5)
    sheet = txt(c, ".cpd-sheet")
    check("sheet: number, status, invoice, credit left, items",
          "Credit note 30377" in sheet and "Part applied" in sheet and "Invoice 10431" in sheet
          and "USD 162.00" in sheet and "Mozzarella 2 kg" in sheet and "Feta 400 g" in sheet and "No. 47902" in sheet,
          sheet=sheet[:400])
    verify = c.eval("document.querySelector('.cpd-sheet a.cpd-ext')?.getAttribute('href')")
    check("fiscal receipt links to its verification address", verify == "https://fdms.zimra.co.zw/verify/47902", href=verify)
    c.screenshot(os.path.join(OUT, "03-sheet.light.png"), full_page=False)
    c.set_theme(True)
    c.screenshot(os.path.join(OUT, "03-sheet.dark.png"), full_page=False)
    c.set_theme(False)

    click_text(c, ".cpd-sheet-foot .btn-primary", "Download PDF")
    got = wait(c, "window.__downloads.length && window.__downloads[0]", 30)
    pdf_calls = [r["path"] for r in stub_log()[pdf_before:] if "/pdf?" in r["path"].lower()]
    check("sheet Download PDF hands the browser the credit note PDF",
          got and got["name"] == "CreditNote_30377.pdf" and got["head"] == "%PDF" and got["bytes"] > 10000,
          download=got, api=pdf_calls)
    check("the PDF is asked for once, by DocEntry, for the note's own account",
          pdf_calls == [f"/api/creditnote/61377/pdf?cardCode={CARD}"], api=pdf_calls)

    click_text(c, ".cpd-sheet-foot .btn-outline-primary", "Close")
    closed = not c.eval("!!document.querySelector('.cpd-sheet')")
    check("Close shuts the sheet", closed)

    c.eval("document.querySelector('button[aria-label=\"Download credit note 30418 as PDF\"]').click()")
    second = wait(c, "window.__downloads.length > 1 && window.__downloads[1]", 30)
    check("row download button downloads that note", second and second["name"] == "CreditNote_30418.pdf", download=second)

    errors = c.console_errors()
    results["console_errors"]["desktop"] = errors
    check("no console errors on desktop", not errors, errors=errors)

# ---------------------------------------------------------------- phone
with Chrome(width=390, height=844) as c:
    sign_in(c)
    c.goto(WEB + "/customer-portal/credit-notes")
    count = load_period(c)
    fonts_ready(c)
    c.set_theme(True)
    cards = c.eval("getComputedStyle(document.querySelector('.cpd-ledger tbody tr')).display")
    label = c.eval("getComputedStyle(document.querySelector('.cpd-ledger .cpd-icon-only-label')).display")
    overflow = c.eval("document.documentElement.scrollWidth - document.documentElement.clientWidth")
    check("phone: rows are cards, download says PDF, no sideways scroll",
          count == 8 and cards == "grid" and label != "none" and overflow <= 0,
          rows=count, row_display=cards, label_display=label, overflow=overflow)
    c.eval("document.querySelector('.cpd-ledger').scrollIntoView()")
    time.sleep(0.4)
    c.screenshot(os.path.join(OUT, "04-phone-list.dark.png"), full_page=False)
    c.eval("document.querySelector('button[aria-label=\"View credit note 30418\"]').click()")
    wait(c, "document.querySelector('.cpd-sheet')", 15)
    time.sleep(0.5)
    foot_visible = c.eval("(() => { const b = document.querySelector('.cpd-sheet-foot .btn-primary').getBoundingClientRect();"
                          " return b.bottom <= innerHeight && b.top >= 0; })()")
    check("phone: Download PDF is on screen with the sheet open", foot_visible)
    on_top = c.eval("(() => { const b = document.querySelector('.cpd-sheet-close'); const r = b.getBoundingClientRect();"
                    " const hit = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);"
                    " return !!hit && b.contains(hit); })()")
    zs = c.eval("[getComputedStyle(document.querySelector('.cpd-overlay')).zIndex, getComputedStyle(document.querySelector('.customer-appbar')).zIndex]")
    check("phone: the sheet's close button is above the top bar", on_top, z_overlay_appbar=zs)
    c.screenshot(os.path.join(OUT, "05-phone-sheet.dark.png"), full_page=False)
    errors = c.console_errors()
    results["console_errors"]["phone"] = errors
    check("no console errors on phone", not errors, errors=errors)

results["failures"] = sum(1 for x in results["checks"] if not x["ok"])
results["requests"] = stub_log()
with open(os.path.join(OUT, "result.json"), "w", encoding="utf-8") as f:
    json.dump(results, f, indent=2)
print(f"\n{len(results['checks']) - results['failures']}/{len(results['checks'])} checks passed -> {OUT}")
sys.exit(1 if results["failures"] else 0)
