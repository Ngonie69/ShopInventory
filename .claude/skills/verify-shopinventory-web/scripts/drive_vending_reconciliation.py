"""Drives /vending/reconciliation as four people and records what each sees.

Run against an API + Web pair whose database holds vending cashiers, their sales and some cash-ups
(the scratch-database recipe: API on 5116, Web on 5071). It establishes, in order:

  1. A supervisor (Cashier) sees the week: a day with sales and no count reads "Not cashed up" with
     a dash, not a variance; a short count with no reason is flagged; ZiG stays on its own line.
  2. The supervisor keys today's count by notes and coins, balanced, and it saves as awaiting approval.
  3. An Admin who did not key it approves it from the same dialog.
  4. An Admin is shown why they cannot approve a count with no reason, and one they keyed themselves.
  5. A Manager sees the page with no Open cash-up and a read-only dialog.
  6. Both themes, with no console errors.

The day and cashier names it looks for are the scratch seed's (scratch_seed.py / scratch_history.py
in the session that wrote this); pass VERIFY_TODAY_LABEL etc. to point it at other data.
Evidence lands in artifacts/verify/<timestamp>-vending-reconciliation/.
"""
import json
import os
import sys
import time

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from cdp import Chrome, find_chrome  # noqa: E402

WEB = os.environ.get("VERIFY_WEB_URL", "http://localhost:5071")
PASSWORD = os.environ.get("VERIFY_PASSWORD", "admin123")
OUT = os.path.join("artifacts", "verify", time.strftime("%Y%m%d-%H%M%S") + "-vending-reconciliation")
results = []

# Sets a form field the way Blazor binds it: the native setter, then input and change.
SET_JS = """
(function (el, value) {
  const proto = el.tagName === 'TEXTAREA' ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
  Object.getOwnPropertyDescriptor(proto, 'value').set.call(el, value);
  el.dispatchEvent(new Event('input', { bubbles: true }));
  el.dispatchEvent(new Event('change', { bubbles: true }));
  return true;
})"""

EXPECTED_USD_JS = """(() => {
  const head = document.querySelectorAll('.vend-rec-modal .vend-rec-currency-head')[0];
  const m = head && head.innerText.match(/USD ([0-9,]+\\.[0-9]{2}) expected/);
  return m ? m[1].replace(/,/g, '') : null;
})()"""


def record(name, ok, detail=""):
    results.append({"check": name, "ok": bool(ok), "detail": str(detail)[:500]})
    print(f"  {'PASS' if ok else 'FAIL'}  {name}" + (f"  — {detail}" if detail else ""), flush=True)


def wait(c, js, seconds=30.0):
    deadline = time.time() + seconds
    while time.time() < deadline:
        if c.eval(js):
            return True
        time.sleep(0.3)
    return False


def fonts_ready(c):
    wait(c, "document.fonts.status === 'loaded' && document.fonts.check('16px \"bootstrap-icons\"')", 20)


def login(c, user):
    c.goto(WEB + "/login")
    c.install_error_trap()
    c.wait_for("#username")
    c.type_into("#username", user)
    c.type_into("#password", PASSWORD)
    c.click("button[type=submit].nsi-submit")
    if not wait(c, "!location.pathname.replace(/\\/$/, '').endsWith('/login')", 45):
        raise RuntimeError(f"{user} is still on /login after 45s")
    c.wait_for_load()
    time.sleep(1.0)


def open_page(c):
    c.goto(WEB + "/vending/reconciliation")
    c.install_error_trap()
    if not wait(c, "!!document.querySelector('.vend-rec-table') || !!document.querySelector('.vend-rec .shop-empty')", 40):
        raise RuntimeError("The reconciliation did not render")
    fonts_ready(c)
    time.sleep(0.6)


def shoot(c, name):
    """Both themes. With the dialog open, a full-page shot stops at the viewport (it is fixed), so
    the dialog is shot at its top and again scrolled to its foot instead."""
    dialog = bool(c.eval("!!document.querySelector('.vend-rec-overlay')"))
    for dark in (False, True):
        c.set_theme(dark)
        time.sleep(0.5)
        theme = 'dark' if dark else 'light'
        if not dialog:
            c.screenshot(os.path.join(OUT, f"{name}.{theme}.png"))
            continue
        c.eval("document.querySelector('.vend-rec-overlay').scrollTop = 0")
        time.sleep(0.3)
        c.screenshot(os.path.join(OUT, f"{name}.{theme}.png"), full_page=False)
        c.eval("(() => { const o = document.querySelector('.vend-rec-overlay'); o.scrollTop = o.scrollHeight; return true; })()")
        time.sleep(0.3)
        c.screenshot(os.path.join(OUT, f"{name}.{theme}.foot.png"), full_page=False)
        c.eval("document.querySelector('.vend-rec-overlay').scrollTop = 0")
    c.set_theme(False)
    time.sleep(0.3)


def set_field(c, section, label, value):
    js = f"""(() => {{
      const sec = document.querySelectorAll('.vend-rec-modal .vend-rec-currency')[{section}];
      const f = sec && [...sec.querySelectorAll('.shop-form-field')]
        .find(x => (x.querySelector('label')?.textContent || '').trim().startsWith({json.dumps(label)}));
      const el = f && f.querySelector('input');
      return el ? {SET_JS}(el, {json.dumps(str(value))}) : false;
    }})()"""
    ok = c.eval(js)
    time.sleep(0.25)
    return ok


def set_aria(c, aria, value):
    ok = c.eval(f"(() => {{ const el = document.querySelector('.vend-rec-modal input[aria-label={json.dumps(aria)}]'); "
                f"return el ? {SET_JS}(el, {json.dumps(str(value))}) : false; }})()")
    time.sleep(0.2)
    return ok


def click_text(c, selector, text):
    ok = c.eval(f"(() => {{ const el = [...document.querySelectorAll({json.dumps(selector)})]"
                f".find(e => e.textContent.trim().includes({json.dumps(text)}) && !e.disabled); "
                f"if (!el) return false; el.click(); return true; }})()")
    time.sleep(0.6)
    return ok


ROW_JS = """(day, who) => [...document.querySelectorAll('.vend-rec-table tbody tr')]
  .find(r => r.textContent.includes(day) && r.textContent.includes(who))"""


def row_text(c, day, who):
    return c.eval(f"(() => {{ const r = ({ROW_JS})({json.dumps(day)}, {json.dumps(who)}); "
                  f"return r ? r.innerText.replace(/\\s+/g, ' ') : null; }})()")


def open_row(c, day, who):
    ok = c.eval(f"(() => {{ const r = ({ROW_JS})({json.dumps(day)}, {json.dumps(who)}); if (!r) return false; r.click(); return true; }})()")
    if ok:
        wait(c, "!!document.querySelector('.vend-rec-modal .vend-rec-currency')", 20)
        time.sleep(0.6)
    return ok


def modal_text(c):
    return c.eval("document.querySelector('.vend-rec-modal')?.innerText || ''") or ""


def hint(c):
    return c.eval("document.querySelector('.vend-rec-modal .vend-rec-hint')?.innerText || ''") or ""


def button_state(c, text):
    return c.eval(f"(() => {{ const b = [...document.querySelectorAll('.vend-rec-modal .shop-modal-footer button')]"
                  f".find(e => e.textContent.trim() === {json.dumps(text)}); "
                  f"return b ? (b.disabled ? 'disabled' : 'enabled') : 'absent'; }})()")


def close_modal(c):
    c.eval("document.querySelector('.vend-rec-modal .shop-modal-close')?.click()")
    wait(c, "!document.querySelector('.vend-rec-modal')", 10)
    time.sleep(1.0)


def main():
    os.makedirs(OUT, exist_ok=True)
    errors = []

    # ── 1-2. The supervisor ────────────────────────────────────────────────
    with Chrome(find_chrome()) as c:
        login(c, "supervisor")
        open_page(c)
        c.screenshot(os.path.join(OUT, "00-after-login.png"))
        shoot(c, "01-supervisor-week")

        not_counted = row_text(c, "Thu 12 Nov", "Rudo Moyo")
        record("A day with sales and no count reads Not cashed up, with no variance",
               not_counted is not None and "Not cashed up" in not_counted and "Not counted" in not_counted
               and "Balanced" not in not_counted, not_counted)
        short = row_text(c, "Tue 10 Nov", "Rudo Moyo")
        record("A short count with no reason is flagged",
               short is not None and "No reason given" in short and "− USD 12.00" in short, short)
        queried = row_text(c, "Tue 10 Nov", "Tafadzwa Ncube")
        record("A queried count shows as queried", queried is not None and "Queried" in queried, queried)
        zig = row_text(c, "Mon 9 Nov", "Blessing Dube")
        record("A ZiG shortfall is its own line, not netted into USD",
               zig is not None and "− ZiG 30.00" in zig and "Balanced" in zig, zig)

        record("Open cash-up is offered to a Cashier", click_text(c, ".shop-header-actions button", "Open cash-up"))
        wait(c, "!!document.querySelector('.vend-rec-modal .nsel-btn')", 10)
        c.eval("document.querySelector('.vend-rec-modal .nsel-btn').click()")
        time.sleep(0.5)
        click_text(c, ".vend-rec-modal .nsel-item", "Rudo Moyo")
        wait(c, "!!document.querySelector('.vend-rec-modal .vend-rec-currency')", 20)
        time.sleep(0.6)
        expected = c.eval(EXPECTED_USD_JS)
        record("Today opens on the cashier's expected cash, not counted",
               expected is not None and "not counted yet" in modal_text(c), f"USD expected {expected}")

        # Float 20, then notes for the dollars and coins for the cents, so the drawer balances.
        total = round(float(expected) + 20.0, 2)
        set_field(c, 0, "Opening float", "20")
        click_text(c, ".vend-rec-modal .vend-rec-mode .shop-seg-btn", "Notes")
        time.sleep(0.4)
        remaining, notes = total, {}
        for face in (100, 50, 20, 10, 5, 2, 1):
            quantity = int(remaining // face)
            if quantity:
                notes[face] = quantity
                remaining = round(remaining - quantity * face, 2)
                set_aria(c, f"USD {face} notes", quantity)
        set_aria(c, "USD coins, total", f"{remaining:.2f}")
        set_field(c, 0, "Banked", f"{float(expected):.2f}")
        set_field(c, 0, "Deposit reference", "DS-20261113-N")
        set_field(c, 1, "Counted in the drawer", "0")
        time.sleep(0.6)
        preview = c.eval("document.querySelectorAll('.vend-rec-modal .vend-rec-sum')[0].innerText.replace(/\\s+/g,' ')")
        record("Notes and coins add up and the preview balances", "Balanced" in (preview or ""),
               f"{notes} + coins {remaining:.2f}; {preview}")
        shoot(c, "02-supervisor-keying")

        record("Save count is enabled once every currency is counted", button_state(c, "Save count") == "enabled")
        click_text(c, ".vend-rec-modal .shop-modal-footer button", "Save count")
        wait(c, "(document.querySelector('.vend-rec-modal')?.innerText || '').includes('Awaiting approval')", 20)
        text = modal_text(c)
        record("The count saves as awaiting approval, keyed by the supervisor",
               "Awaiting approval" in text and "counted by supervisor" in text,
               [line for line in text.splitlines() if "counted by" in line][:1])
        record("A Cashier is not offered Approve", button_state(c, "Approve") == "absent")
        close_modal(c)
        today = row_text(c, "Fri 13 Nov", "Rudo Moyo")
        record("Today's row now shows the count, balanced",
               today is not None and "Balanced" in today and "Awaiting approval" in today, today)
        errors += [("supervisor", e) for e in c.console_errors()]

    # ── 3-4. An Admin ──────────────────────────────────────────────────────
    with Chrome(find_chrome()) as c:
        login(c, "admin")
        open_page(c)
        shoot(c, "03-admin-week")
        review = c.eval("document.querySelector('.vend-rec-review')?.innerText || ''") or ""
        record("The review list has today's count", "Rudo Moyo" in review, review.replace("\n", " | ")[:300])

        open_row(c, "Tue 10 Nov", "Rudo Moyo")
        record("Approve is unavailable on a variance with no reason, and says why",
               button_state(c, "Approve") == "disabled" and "no reason" in hint(c), hint(c))
        close_modal(c)

        open_row(c, "Fri 13 Nov", "Rudo Moyo")
        record("Approve is available to an Admin who did not key the count", button_state(c, "Approve") == "enabled")
        shoot(c, "04-admin-review")
        click_text(c, ".vend-rec-modal .shop-modal-footer button", "Approve")
        wait(c, "(document.querySelector('.vend-rec-modal')?.innerText || '').includes('approved by admin')", 20)
        text = modal_text(c)
        record("The count is approved, by the Admin, and the form locks",
               "Approved" in text and "approved by admin" in text and button_state(c, "Save recount") == "absent"
               and c.eval("[...document.querySelectorAll('.vend-rec-modal input')].every(i => i.disabled)"))
        close_modal(c)

        # The Admin keys a count themselves, and is then told another Admin must approve it.
        open_row(c, "Thu 12 Nov", "Tafadzwa Ncube")
        own = c.eval(EXPECTED_USD_JS)
        set_field(c, 0, "Counted in the drawer", own)
        set_field(c, 1, "Counted in the drawer", "0")
        click_text(c, ".vend-rec-modal .shop-modal-footer button", "Save count")
        wait(c, "(document.querySelector('.vend-rec-modal')?.innerText || '').includes('Awaiting approval')", 20)
        record("An Admin who keyed a count is told another Admin approves it",
               button_state(c, "Approve") == "disabled" and "another Admin" in hint(c), hint(c))
        shoot(c, "05-admin-own-count")
        close_modal(c)
        errors += [("admin", e) for e in c.console_errors()]

    # ── 5. A Manager ───────────────────────────────────────────────────────
    with Chrome(find_chrome()) as c:
        login(c, "manager")
        open_page(c)
        record("A Manager is not offered Open cash-up",
               not c.eval("[...document.querySelectorAll('.shop-header-actions button')].some(b => b.textContent.includes('Open cash-up'))"))
        open_row(c, "Fri 13 Nov", "Rudo Moyo")
        record("A Manager's dialog is read-only",
               bool(c.eval("[...document.querySelectorAll('.vend-rec-modal input')].every(i => i.disabled)"))
               and button_state(c, "Approve") == "absent")
        shoot(c, "06-manager-readonly")
        close_modal(c)
        errors += [("manager", e) for e in c.console_errors()]

    record("No console errors", not errors, errors[:5])
    failures = sum(1 for r in results if not r["ok"])
    with open(os.path.join(OUT, "result.json"), "w", encoding="utf-8") as f:
        json.dump({"web": WEB, "checks": results, "failures": failures}, f, indent=2)
    print(f"{failures} failure(s); evidence in {OUT}")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
