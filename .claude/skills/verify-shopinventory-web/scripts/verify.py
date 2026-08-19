#!/usr/bin/env python3
"""Drive ShopInventory.Web and capture evidence. Run from the repo root.

    python .claude/skills/verify-shopinventory-web/scripts/verify.py doctor
    python .claude/skills/verify-shopinventory-web/scripts/verify.py smoke
    python .claude/skills/verify-shopinventory-web/scripts/verify.py shot /dashboard /customers
    python .claude/skills/verify-shopinventory-web/scripts/verify.py shot /crates --light-only

`doctor` is read-only and never opens a browser. Everything else logs in as the
Development seed user and writes PNGs plus a result.json under artifacts/verify/.
"""

from __future__ import annotations

import argparse
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
EVIDENCE = os.environ.get("VERIFY_EVIDENCE", os.path.join("artifacts", "verify"))

# Routes that render for an Admin with no SAP connection. Keep in step with
# features/README.md; a route that needs live SAP belongs in the map's Gotchas,
# not here.
SMOKE_ROUTES = ["/dashboard", "/customers", "/user-management", "/settings"]


def _http_status(url: str, timeout: float = 5.0) -> int | str:
    try:
        req = urllib.request.Request(url, method="GET")
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return r.status
    except urllib.error.HTTPError as e:
        return e.code
    except Exception as e:
        return f"down ({type(e).__name__})"


def cmd_doctor(_args) -> int:
    rows = []
    web = _http_status(WEB + "/login")
    api = _http_status(API + "/")
    rows.append(("Web  " + WEB + "/login", web, isinstance(web, int) and web < 500))
    rows.append(("API  " + API + "/", api, isinstance(api, int) and api < 500))
    try:
        chrome = find_chrome()
        rows.append(("Chrome", chrome, True))
    except RuntimeError as e:
        rows.append(("Chrome", str(e), False))

    ok = all(r[2] for r in rows)
    width = max(len(r[0]) for r in rows)
    for name, detail, good in rows:
        print(f"  {'PASS' if good else 'FAIL'}  {name.ljust(width)}  {detail}")
    if not ok:
        print("\nNot worth driving. Start the services first - see SKILL.md 'Launch'.")
    return 0 if ok else 1


def _login(c: Chrome) -> None:
    c.goto(WEB + "/login")
    c.install_error_trap()
    c.wait_for("#username")
    c.type_into("#username", USER)
    c.type_into("#password", PASSWORD)
    c.click("button[type=submit].nsi-submit")
    deadline = time.time() + 45
    while time.time() < deadline:
        url = c.eval("location.pathname") or ""
        if not url.rstrip("/").endswith("/login"):
            c.wait_for_load()
            time.sleep(0.8)
            return
        if "twofactor-code" in (c.eval("document.body.innerHTML") or "")[:200000]:
            raise RuntimeError(
                "Login hit the two-factor step. Use an account with 2FA off, "
                "or set VERIFY_USER / VERIFY_PASSWORD."
            )
        time.sleep(0.4)
    body = (c.text() or "")[:400].replace("\n", " ")
    raise RuntimeError(f"Still on /login after 45s. Page said: {body}")


def _shoot_route(c: Chrome, route: str, outdir: str, themes) -> list:
    results = []
    c.goto(WEB + route)
    c.install_error_trap()
    landed = c.eval("location.pathname")
    if str(landed).rstrip("/").endswith("/login"):
        results.append({"route": route, "ok": False, "reason": "bounced to /login"})
        return results
    slug = route.strip("/").replace("/", "-") or "root"
    for theme in themes:
        c.set_theme(theme == "dark")
        path = os.path.join(outdir, f"{slug}.{theme}.png")
        c.screenshot(path)
        results.append({
            "route": route,
            "theme": theme,
            "ok": True,
            "shot": path,
            "title": c.eval("document.title"),
            "console_errors": c.console_errors(),
        })
    return results


def _run(routes, themes, tag: str) -> int:
    stamp = time.strftime("%Y%m%d-%H%M%S")
    outdir = os.path.join(EVIDENCE, f"{stamp}-{tag}")
    os.makedirs(outdir, exist_ok=True)
    results, failures = [], 0
    with Chrome(headless=True) as c:
        _login(c)
        results.extend([{"step": "login", "ok": True, "url": c.eval("location.href")}])
        c.screenshot(os.path.join(outdir, "00-after-login.png"))
        for route in routes:
            try:
                results.extend(_shoot_route(c, route, outdir, themes))
            except Exception as e:
                failures += 1
                results.append({"route": route, "ok": False, "reason": f"{type(e).__name__}: {e}"})
    for r in results:
        if r.get("ok") is False:
            failures += 1
        if r.get("console_errors"):
            failures += 1
    summary = {"when": stamp, "web": WEB, "user": USER, "routes": routes,
               "themes": themes, "failures": failures, "results": results}
    with open(os.path.join(outdir, "result.json"), "w", encoding="utf-8") as f:
        json.dump(summary, f, indent=2)

    print(f"\nEvidence: {outdir}")
    for r in results:
        if "route" not in r:
            continue
        if r.get("ok"):
            errs = r.get("console_errors") or []
            flag = f"  console: {errs}" if errs else ""
            print(f"  PASS  {r['route']:<24} {r.get('theme','')}{flag}")
        else:
            print(f"  FAIL  {r['route']:<24} {r.get('reason')}")
    print(f"\n{'FAILED' if failures else 'OK'} - {failures} problem(s)")
    return 1 if failures else 0


def cmd_smoke(args) -> int:
    themes = ["light"] if args.light_only else ["light", "dark"]
    return _run(SMOKE_ROUTES, themes, "smoke")


def cmd_shot(args) -> int:
    themes = ["light"] if args.light_only else ["light", "dark"]
    return _run(args.routes, themes, "shot")


def main() -> int:
    p = argparse.ArgumentParser(prog="verify", description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = p.add_subparsers(dest="cmd", required=True)
    sub.add_parser("doctor", help="read-only: is this instance worth driving?")
    s = sub.add_parser("smoke", help="log in and shoot the core routes in both themes")
    s.add_argument("--light-only", action="store_true")
    t = sub.add_parser("shot", help="log in and shoot the named routes")
    t.add_argument("routes", nargs="+")
    t.add_argument("--light-only", action="store_true")
    args = p.parse_args()
    return {"doctor": cmd_doctor, "smoke": cmd_smoke, "shot": cmd_shot}[args.cmd](args)


if __name__ == "__main__":
    sys.exit(main())
