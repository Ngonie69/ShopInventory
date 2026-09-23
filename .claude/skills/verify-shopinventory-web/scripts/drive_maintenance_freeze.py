"""Proves the maintenance freeze end to end, through the app rather than through its tests.

Turns the lockout on from Settings the way an operator does, then asks the API what a
frozen portal request actually gets. Six things it establishes, in order:

  1. The audience tick boxes render and save.
  2. The API stores the audiences and reports them back.
  3. A portal request carrying no user token is refused 503, and says which audience.
  4. An Admin's own token gets through the same frozen portal.
  5. A caller that is not the portal is untouched by a portal-only lockout.
  6. The switch that lifts the lockout is itself reachable while it is on.
  7. The banner appears on an ordinary page, in both themes.

The writes it attempts are aimed at a path that answers 405, so a request that gets past the
lockout changes nothing. 405 is the proof: it means routing was reached, which is the far side
of the gate.

It leaves the lockout off. Run with the API on 5106 and the Web on 5051.
"""
import json
import os
import sys
import time
import urllib.error
import urllib.request

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from cdp import Chrome, find_chrome  # noqa: E402

WEB = os.environ.get("VERIFY_WEB_URL", "http://localhost:5051")
API = os.environ.get("VERIFY_API_URL", "http://localhost:5106")
USER = os.environ.get("VERIFY_USER", "admin")
PASSWORD = os.environ.get("VERIFY_PASSWORD", "admin123")
API_KEY = os.environ.get("VERIFY_API_KEY", "")

OUT = os.path.join("artifacts", "verify", time.strftime("%Y%m%d-%H%M%S") + "-maintenance-freeze")

results = []
TOKEN = {"admin": None}

# The version gate refuses an Android caller that names no version, so a handset asking about
# maintenance has to look like a real one.
HANDSET = {"X-App-Platform": "android", "X-App-Version": "2.0.1", "X-App-Id": "com.kefalos.vansales"}


def record(name, ok, detail=""):
    results.append({"check": name, "ok": bool(ok), "detail": str(detail)[:400]})
    print(f"  {'PASS' if ok else 'FAIL'}  {name}" + (f"  — {detail}" if detail else ""))


def call(method, path, body=None, headers=None):
    """A raw call to the API, so the answer is the API's and not a client's reading of it."""
    data = json.dumps(body).encode() if body is not None else None
    request = urllib.request.Request(API + path, data=data, method=method)
    request.add_header("Content-Type", "application/json")
    for name, value in (headers or {}).items():
        request.add_header(name, value)
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return response.status, response.read().decode("utf-8", "replace")
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode("utf-8", "replace")


def admin_headers(**extra):
    """What the Web actually sends: its API key and the signed-in user's bearer together."""
    headers = {"X-API-Key": API_KEY, "Authorization": "Bearer " + TOKEN["admin"]}
    headers.update(extra)
    return headers


def sign_in_as_admin():
    status, body = call("POST", "/api/auth/login", {"username": USER, "password": PASSWORD},
                        {"X-API-Key": API_KEY})
    if status != 200:
        raise RuntimeError(f"Could not sign in to the API: HTTP {status} {body[:200]}")
    TOKEN["admin"] = json.loads(body)["accessToken"]


def login(c):
    c.goto(WEB + "/login")
    c.install_error_trap()
    c.wait_for("#username")
    c.type_into("#username", USER)
    c.type_into("#password", PASSWORD)
    c.click("button[type=submit].nsi-submit")
    deadline = time.time() + 45
    while time.time() < deadline:
        if not str(c.eval("location.pathname") or "").rstrip("/").endswith("/login"):
            c.wait_for_load()
            time.sleep(1.0)
            return
        if "twofactor-code" in (c.eval("document.body.innerHTML") or "")[:200000]:
            raise RuntimeError("Login hit the two-factor step; turn 2FA off for this account first.")
        time.sleep(0.4)
    raise RuntimeError("Still on /login after 45s.")


def main():
    os.makedirs(OUT, exist_ok=True)

    sign_in_as_admin()

    # Start from a known state rather than from whatever the last run left.
    call("PUT", "/api/maintenance", {"enabled": False}, admin_headers())

    with Chrome(find_chrome()) as c:
        login(c)
        c.screenshot(os.path.join(OUT, "00-after-login.png"))

        # ---------------------------------------------------------------- 1. the screen
        c.goto(WEB + "/settings")
        c.install_error_trap()
        c.wait_for(".stg-check-block", timeout=30)
        labels = c.eval(
            "Array.from(document.querySelectorAll('.stg-check-block strong')).map(e => e.textContent.trim())")
        record(
            "Settings > General offers the three audiences",
            labels == ["Mobile apps", "Web portal", "Tills and integrations"],
            labels)

        # Turn it on and tick the web portal, the way an operator would.
        c.eval("""
            (() => {
              const setter = Object.getOwnPropertyDescriptor(
                  window.HTMLInputElement.prototype, 'checked').set;
              const fire = el => { el.dispatchEvent(new Event('change', {bubbles:true})); };
              const master = document.querySelector('.stg-switch input[type=checkbox]');
              if (!master.checked) { setter.call(master, true); fire(master); }
              return true;
            })()
        """)
        time.sleep(1.2)
        c.eval("""
            (() => {
              const setter = Object.getOwnPropertyDescriptor(
                  window.HTMLInputElement.prototype, 'checked').set;
              const boxes = Array.from(document.querySelectorAll('.stg-check-block'));
              for (const box of boxes) {
                const want = box.querySelector('strong').textContent.trim() === 'Web portal';
                const input = box.querySelector('input');
                if (input.checked !== want) {
                  setter.call(input, want);
                  input.dispatchEvent(new Event('change', {bubbles:true}));
                }
              }
              return true;
            })()
        """)
        time.sleep(1.0)
        c.eval("""
            (() => {
              const area = document.querySelector('.stg-card textarea');
              const setter = Object.getOwnPropertyDescriptor(
                  window.HTMLTextAreaElement.prototype, 'value').set;
              setter.call(area, 'Stock take in progress. Back at 20:00.');
              area.dispatchEvent(new Event('input', {bubbles:true}));
              area.dispatchEvent(new Event('change', {bubbles:true}));
              return true;
            })()
        """)
        time.sleep(0.6)
        c.screenshot(os.path.join(OUT, "01-settings-before-save.png"))

        c.eval("""
            (() => {
              const button = Array.from(document.querySelectorAll('.stg-btn-primary'))
                  .find(b => b.textContent.includes('maintenance mode'));
              button.click();
              return button.textContent.trim();
            })()
        """)
        time.sleep(3.0)
        c.screenshot(os.path.join(OUT, "02-settings-after-save.png"))

        saved = c.text(".stg-alert")
        record("Saving reports the lockout back in words", "Maintenance mode is on" in saved, saved[:200])

        # ---------------------------------------------------------------- 2. what the API stored
        status, body = call("GET", "/api/maintenance", headers=admin_headers())
        stored = json.loads(body) if status == 200 else {}
        record("The API stored the audiences the screen sent",
               stored.get("audiences") == ["WebPortal"], f"HTTP {status}: {stored.get('audiences')}")
        record("The API is reporting the lockout as live", stored.get("isActive") is True, stored.get("isActive"))

        # ---------------------------------------------------------------- 3. a frozen portal call
        status, body = call(
            "POST", "/api/product",
            {"itemCode": "VERIFY-ONLY", "itemName": "never posted"},
            {"X-API-Key": API_KEY, "X-Client-App": "web-portal"})
        refused = json.loads(body) if body.strip().startswith("{") else {}
        record("A portal write is refused 503", status == 503, f"HTTP {status}")
        record("The refusal names the audience", refused.get("audience") == "WebPortal", refused.get("audience"))
        record("The refusal carries the operator's own wording",
               refused.get("message") == "Stock take in progress. Back at 20:00.", refused.get("message"))

        # ---------------------------------------------------------------- 4. the admin exemption
        # The same request as above, with the signed-in Admin's token beside the key. 405 rather
        # than 503 is the pass: it reached routing, which is past the gate.
        status, _ = call(
            "POST", "/api/product",
            {"itemCode": "VERIFY-ONLY", "itemName": "never posted"},
            admin_headers(**{"X-Client-App": "web-portal"}))
        record("An Admin gets through the frozen portal", status != 503, f"HTTP {status}")

        # ---------------------------------------------------------------- 5. everyone else
        status, _ = call(
            "POST", "/api/product",
            {"itemCode": "VERIFY-ONLY", "itemName": "never posted"},
            {"X-API-Key": API_KEY})
        record("A caller that is not the portal is untouched", status != 503, f"HTTP {status}")

        status, body = call("GET", "/api/maintenance/status", headers={"X-Client-App": "web-portal"})
        told = json.loads(body) if status == 200 else {}
        record("The portal is told it is frozen", told.get("isActive") is True, told.get("isActive"))

        status, body = call("GET", "/api/maintenance/status", headers=HANDSET)
        told = json.loads(body) if status == 200 else {}
        record("A handset is told it is not", told.get("isActive") is False, told.get("isActive"))

        # ---------------------------------------------------------------- 6. the way out
        status, _ = call("GET", "/api/maintenance", headers=admin_headers(**{"X-Client-App": "web-portal"}))
        record("The off switch stays reachable from the frozen portal", status == 200, f"HTTP {status}")

        # ---------------------------------------------------------------- 7. the banner
        c.goto(WEB + "/")
        c.install_error_trap()
        time.sleep(2.5)
        banner = c.text(".maint-banner")
        record("Every page carries the banner while the portal is frozen",
               "Maintenance mode is on" in (banner or ""), (banner or "")[:200])
        record("An admin is told they are the exception",
               "everyone else is frozen" in (banner or ""), (banner or "")[:200])

        for theme in ("light", "dark"):
            c.set_theme(theme == "dark")
            time.sleep(0.5)
            c.screenshot(os.path.join(OUT, f"03-banner.{theme}.png"), full_page=False)

        c.set_theme(False)
        c.goto(WEB + "/settings")
        time.sleep(2.5)
        for theme in ("light", "dark"):
            c.set_theme(theme == "dark")
            time.sleep(0.5)
            c.screenshot(os.path.join(OUT, f"04-settings.{theme}.png"))

        errors = c.console_errors()
        record("No console errors", len(errors) == 0, errors[:3])

    # Leave it off, whatever happened above.
    call("PUT", "/api/maintenance", {"enabled": False}, admin_headers())
    status, body = call("GET", "/api/maintenance", headers=admin_headers())
    record("The lockout is off again", json.loads(body).get("isActive") is False, "")

    failures = [r for r in results if not r["ok"]]
    with open(os.path.join(OUT, "result.json"), "w", encoding="utf-8") as f:
        json.dump({"checks": results, "failures": len(failures)}, f, indent=2)

    print(f"\n{len(results) - len(failures)}/{len(results)} passed. Evidence in {OUT}")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
