"""Drive /route-assignments and prove the staged-edit flow actually works.

Screenshots alone would not show it: the point of this page is that a move is
held unsaved until Save, so the proof has to click something and read what the
page then says. Run with both services up (see SKILL.md 'Launch').

    python .claude/skills/verify-shopinventory-web/scripts/verify_route_assignments.py
"""
import json
import os
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from cdp import Chrome  # noqa: E402
from verify import _login, WEB  # noqa: E402

ROOT = Path(__file__).resolve().parents[3]
OUT = ROOT / "artifacts" / "verify" / (time.strftime("%Y%m%d-%H%M%S") + "-route-assignments")

checks: list[tuple[str, bool, str]] = []


def check(name: str, ok: bool, detail: str = "") -> None:
    checks.append((name, bool(ok), detail))
    print(f"  {'PASS' if ok else 'FAIL'}  {name}" + (f"   {detail}" if detail else ""))


def q(c: Chrome, js: str):
    return c.eval(js)


def main() -> int:
    OUT.mkdir(parents=True, exist_ok=True)
    with Chrome(headless=True, width=1600, height=1000) as c:
        _login(c)
        c.screenshot(str(OUT / "00-after-login.png"))

        c.goto(WEB + "/route-assignments")
        c.wait_for(".ras")
        time.sleep(1.5)

        # --- the page is the page, not a redirect to /login ---
        check("route stayed on /route-assignments",
              (c.eval("location.pathname") or "") == "/route-assignments",
              c.eval("location.pathname") or "")

        # --- the three panes and the change log are all there ---
        panes = q(c, "document.querySelectorAll('.ras-panes > .ras-pane').length")
        check("three panes rendered", panes == 3, f"{panes} panes")
        check("change log rendered", bool(q(c, "!!document.querySelector('.ras-panel')")))

        # --- the sheet actually loaded (an unstyled page is the failure mode) ---
        styled = q(c, """(() => {
            const p = document.querySelector('.ras-pane');
            if (!p) return null;
            const s = getComputedStyle(p);
            return s.borderRadius + '|' + s.overflow;
        })()""")
        check("pane picks up route-assignments.css",
              bool(styled) and styled.split("|")[0] not in ("0px", ""), str(styled))

        # --- the hero figures read real numbers ---
        figures = q(c, """Array.from(document.querySelectorAll('.ras-figure')).map(
            f => f.querySelector('.ras-figure-label').textContent.trim() + '=' +
                 f.querySelector('.ras-figure-n').textContent.trim())""") or []
        check("four hero figures", len(figures) == 4, ", ".join(figures))
        routes_fig = next((f for f in figures if f.startswith("Routes=")), "")
        check("routes figure is non-zero",
              routes_fig.split("=")[-1].isdigit() and int(routes_fig.split("=")[-1]) > 0,
              routes_fig)

        # --- day chips built from the routes' own days ---
        chips = q(c, "Array.from(document.querySelectorAll('.ras-chip')).map(b=>b.textContent.trim())") or []
        check("day chips present", len(chips) >= 2, ", ".join(chips))

        # --- retired ZWL codes: hidden by default, but never silently ---
        # Read from the shops pane specifically; the unassigned pane has a
        # .ras-note of its own and matching that instead is how this check
        # passed while the line it was meant to prove was missing.
        def shops_pane(js):
            return q(c, "(() => { const p = document.querySelectorAll("
                        "'.ras-panes > .ras-pane')[1]; return %s; })()" % js)

        note = shops_pane("(() => { const n = p.querySelector('.ras-note');"
                          " return n ? n.textContent.replace(/\\s+/g,' ').trim() : ''; })()") or ""
        rows_hidden = shops_pane("p.querySelectorAll('.ras-stop').length")
        print(f"  info  retired note on load: {note[:120] or '(none)'}")

        # The first route on the list has retired codes in both the test and the
        # production company, so the note has to be there on first paint -- it
        # was not, until Reproject ran again after the route was chosen.
        check("the retired note is on screen without clicking a route",
              "retired" in note and "Show them" in note, note[:100])

        if "Show them" in note:
            shops_pane("(() => { const b = p.querySelector('.ras-note .ras-link');"
                       " if (b) b.click(); return true; })()")
            time.sleep(1.2)
            rows_shown = shops_pane("p.querySelectorAll('.ras-stop').length")
            tags = shops_pane("p.querySelectorAll('.ras-tag.is-bad').length")
            count = shops_pane("p.querySelector('.ras-count').textContent.trim()")
            check("showing retired codes adds rows",
                  rows_shown > rows_hidden, f"{rows_hidden} -> {rows_shown}")
            check("every added row is tagged retired",
                  tags == rows_shown - rows_hidden, f"{tags} tags for {rows_shown - rows_hidden} added")
            check("the header count agrees with the rows",
                  count.startswith(str(rows_shown)), f"header {count!r} vs {rows_shown} rows")
            shops_pane("(() => { const b = p.querySelector('.ras-note .ras-link');"
                       " if (b) b.click(); return true; })()")
            time.sleep(1.0)

        c.set_theme(False)
        time.sleep(0.5)
        c.screenshot(str(OUT / "route-assignments.light.png"))
        c.set_theme(True)
        time.sleep(0.5)
        c.screenshot(str(OUT / "route-assignments.dark.png"))
        c.set_theme(False)
        time.sleep(0.4)

        # ------------------------------------------------------------------
        # the staged-edit flow: unassign one shop, then revert it
        # ------------------------------------------------------------------
        save_before = q(c, "document.querySelector('.ras-btn-primary').textContent.trim()")
        disabled_before = q(c, "document.querySelector('.ras-btn-primary').disabled")
        check("Save starts disabled and says so",
              disabled_before is True and "No changes" in (save_before or ""), save_before)

        stops_before = shops_pane("p.querySelectorAll('.ras-stop').length")
        first_shop = q(c, """(() => { const s = document.querySelector('.ras-stop .ras-shop-name');
                                      return s ? s.textContent.trim() : ''; })()""")
        print(f"  info  first route has {stops_before} shops, first is {first_shop!r}")

        # Unassign is the last button in the first shop row.
        clicked = q(c, """(() => {
            const row = document.querySelectorAll('.ras-panes > .ras-pane')[1].querySelector('.ras-stop');
            if (!row) return false;
            const b = Array.from(row.querySelectorAll('button'))
                           .find(x => x.textContent.trim() === 'Unassign');
            if (!b) return false; b.click(); return true; })()""")
        check("clicked Unassign on the first shop", clicked is True)
        time.sleep(1.2)

        save_after = q(c, "document.querySelector('.ras-btn-primary').textContent.trim()")
        disabled_after = q(c, "document.querySelector('.ras-btn-primary').disabled")
        check("Save became live and counts the change",
              disabled_after is False and "Save 1 change" in (save_after or ""), save_after)

        change_rows = q(c, "document.querySelectorAll('.ras-change').length")
        change_text = q(c, """(() => { const r = document.querySelector('.ras-change');
            return r ? r.textContent.replace(/\\s+/g,' ').trim() : ''; })()""") or ""
        check("the change log gained a row", change_rows == 1, f"{change_rows} row(s)")
        check("the row reads as a move to Unassigned",
              "Unassigned" in change_text, change_text[:110])
        check("the row is marked unsaved", "unsaved" in change_text, change_text[:110])

        stops_after = shops_pane("p.querySelectorAll('.ras-stop').length")
        check("the shop left the route straight away",
              stops_after == stops_before - 1, f"{stops_before} -> {stops_after}")

        c.screenshot(str(OUT / "route-assignments.staged.png"))

        # --- revert puts it back and leaves nothing to save ---
        reverted = q(c, """(() => {
            const b = Array.from(document.querySelectorAll('.ras-change button'))
                           .find(x => x.textContent.trim() === 'Revert');
            if (!b) return false; b.click(); return true; })()""")
        check("clicked Revert", reverted is True)
        time.sleep(1.2)

        save_final = q(c, "document.querySelector('.ras-btn-primary').textContent.trim()")
        disabled_final = q(c, "document.querySelector('.ras-btn-primary').disabled")
        stops_final = shops_pane("p.querySelectorAll('.ras-stop').length")
        check("Revert left nothing to save",
              disabled_final is True and "No changes" in (save_final or ""), save_final)
        check("the shop came back", stops_final == stops_before,
              f"{stops_after} -> {stops_final}")
        check("the change log emptied",
              q(c, "document.querySelectorAll('.ras-change').length") == 0)

        # --- nothing was written: the page reloads clean ---
        c.goto(WEB + "/route-assignments")
        c.wait_for(".ras")
        time.sleep(1.2)
        check("a reload shows no saved overrides",
              q(c, "document.querySelectorAll('.ras-change').length") == 0)

        errors = c.console_errors()
        check("no console errors", len(errors) == 0,
              "; ".join(str(e)[:120] for e in errors[:3]))

        result = {
            "route": "/route-assignments",
            "checks": [{"name": n, "ok": o, "detail": d} for n, o, d in checks],
            "figures": figures,
            "chips": chips,
            "frozenNote": note,
            "consoleErrors": errors,
            "failures": sum(1 for _, o, _ in checks if not o),
        }
        (OUT / "result.json").write_text(json.dumps(result, indent=2), encoding="utf-8")

    failed = sum(1 for _, o, _ in checks if not o)
    print(f"\n{len(checks) - failed}/{len(checks)} checks passed")
    print(f"evidence: {OUT}")
    return 1 if failed else 0


if __name__ == "__main__":
    raise SystemExit(main())
