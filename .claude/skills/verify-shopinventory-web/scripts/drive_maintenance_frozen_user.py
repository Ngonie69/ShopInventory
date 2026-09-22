"""What an ordinary user meets while the web portal is frozen.

The companion to drive_maintenance_freeze.py, which drives the operator's side and can only
ever see the Admin's view of a lockout — the exemption applies to the account that turns the
switch on. This one signs in as a non-admin and establishes the half that actually matters:

  1. They are shown the amber banner, not the Admin's slate one.
  2. Their own token is refused 503 by the API, while the Admin's is not.
  3. Reads keep working under the Transactions scope, so pages still load.

Needs a non-admin account. Create one with the admin's password hash so no hashing is involved:

  INSERT INTO "Users" (...) SELECT gen_random_uuid(), 'verifyclerk', ..., "PasswordHash",
    'Cashier', ... FROM "Users" WHERE "Username" = 'admin';

and drop it again afterwards. Set VERIFY_CLERK_USER if it is called something else.
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
ADMIN = os.environ.get("VERIFY_USER", "admin")
CLERK = os.environ.get("VERIFY_CLERK_USER", "verifyclerk")
PASSWORD = os.environ.get("VERIFY_PASSWORD", "admin123")
API_KEY = os.environ.get("VERIFY_API_KEY", "")

OUT = os.path.join("artifacts", "verify", time.strftime("%Y%m%d-%H%M%S") + "-maintenance-frozen-user")

results = []


def record(name, ok, detail=""):
    results.append({"check": name, "ok": bool(ok), "detail": str(detail)[:400]})
    print(f"  {'PASS' if ok else 'FAIL'}  {name}" + (f"  - {detail}" if detail else ""))


def call(method, path, body=None, headers=None):
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


def token_for(username):
    status, body = call("POST", "/api/auth/login", {"username": username, "password": PASSWORD},
                        {"X-API-Key": API_KEY})
    if status != 200:
        raise RuntimeError(f"Could not sign {username} in: HTTP {status} {body[:200]}")
    return json.loads(body)["accessToken"]


def sign_in(c, username):
    c.goto(WEB + "/login")
    c.install_error_trap()
    c.wait_for("#username")
    c.type_into("#username", username)
    c.type_into("#password", PASSWORD)
    c.click("button[type=submit].nsi-submit")
    deadline = time.time() + 45
    while time.time() < deadline:
        if not str(c.eval("location.pathname") or "").rstrip("/").endswith("/login"):
            c.wait_for_load()
            time.sleep(1.2)
            return
        if "twofactor-code" in (c.eval("document.body.innerHTML") or "")[:200000]:
            raise RuntimeError(f"{username} hit the two-factor step.")
        time.sleep(0.4)
    raise RuntimeError(f"{username} is still on /login after 45s.")


def portal(token):
    return {"X-API-Key": API_KEY, "Authorization": "Bearer " + token, "X-Client-App": "web-portal"}


def main():
    os.makedirs(OUT, exist_ok=True)
    admin_token = token_for(ADMIN)
    admin_only = {"X-API-Key": API_KEY, "Authorization": "Bearer " + admin_token}

    call("PUT", "/api/maintenance", {
        "enabled": True,
        "scope": "Transactions",
        "audiences": ["WebPortal"],
        "message": "Stock take in progress. Back at 20:00."
    }, admin_only)

    try:
        clerk_token = token_for(CLERK)

        # ------------------------------------------------------------ the API, per identity
        # Same request, same key, same portal header. The only difference is whose token it is.
        # A path that answers 405 when it gets through, so nothing is ever written.
        body = {"itemCode": "VERIFY-ONLY", "itemName": "never posted"}

        status, refusal = call("POST", "/api/product", body, portal(clerk_token))
        record("A cashier's write is refused 503", status == 503, f"HTTP {status}")

        parsed = json.loads(refusal) if refusal.strip().startswith("{") else {}
        record("They are told why, in the operator's words",
               parsed.get("message") == "Stock take in progress. Back at 20:00.", parsed.get("message"))

        status, _ = call("POST", "/api/product", body, portal(admin_token))
        record("The Admin's write is not", status != 503, f"HTTP {status}")

        # A read the API answers from its own database. /api/product would do as well but goes
        # out to SAP, and a verification that hangs on a slow Service Layer is proving the wrong
        # thing — what is under test here is the gate, not the endpoint behind it.
        status, _ = call("GET", "/api/notification", headers=portal(clerk_token))
        record("A cashier can still read under the Transactions scope", status != 503, f"HTTP {status}")

        # ------------------------------------------------------------ the banner they see
        with Chrome(find_chrome()) as c:
            sign_in(c, CLERK)
            c.goto(WEB + "/")
            c.install_error_trap()
            time.sleep(2.5)

            banner = c.text(".maint-banner") or ""
            classes = c.eval("(document.querySelector('.maint-banner') || {}).className || ''")

            record("A cashier gets the frozen banner, not the Admin's",
                   "maint-banner-frozen" in str(classes), classes)
            record("It says what they can and cannot do",
                   "you can look, but not save" in banner, banner[:160])
            record("It does not tell them they are the exception",
                   "everyone else is frozen" not in banner, banner[:160])

            for theme in ("light", "dark"):
                c.set_theme(theme == "dark")
                time.sleep(0.5)
                c.screenshot(os.path.join(OUT, f"01-frozen-banner.{theme}.png"), full_page=False)

            record("No console errors", len(c.console_errors()) == 0, c.console_errors()[:3])
    finally:
        call("PUT", "/api/maintenance", {"enabled": False}, admin_only)

    status, body = call("GET", "/api/maintenance", headers=admin_only)
    record("The lockout is off again", json.loads(body).get("isActive") is False, "")

    failures = [r for r in results if not r["ok"]]
    with open(os.path.join(OUT, "result.json"), "w", encoding="utf-8") as f:
        json.dump({"checks": results, "failures": len(failures)}, f, indent=2)

    print(f"\n{len(results) - len(failures)}/{len(results)} passed. Evidence in {OUT}")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
