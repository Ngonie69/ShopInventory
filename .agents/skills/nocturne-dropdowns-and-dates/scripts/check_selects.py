#!/usr/bin/env python3
"""Find native <select>s whose dark-theme popup will be unreadable.

The failure
-----------
A native <select> popup takes its background from the field's own
background-color. Nocturne page sheets paint their fields with a translucent
wash -- --noct-field-bg is rgba(243,245,254,.05) in dark theme -- and the
browser composites that 5%-white onto WHITE, then ignores `color-scheme: dark`
for the popup. The <option>s meanwhile inherit the field's light ink, so the
list renders near-white on near-white: present, focusable, invisible.

A bare <select> with no page-sheet styling is NOT affected: app.css:3120 gives
it an opaque `background-color: #1e293b` in dark theme, which yields a dark
popup with light options. That is why this script resolves the actual fill
rather than just asking whether a sheet mentions `option` -- a checker that
flags every page nobody trusts.

The fix, in the page sheet:

    .<page> .<page>-select option,
    .<page> .<page>-select optgroup {
        color: var(--<page>-ink);
        background-color: var(--<page>-surface);
    }

--surface rather than --field-bg because the popup needs an OPAQUE fill, and
surface is the token that is opaque in both themes.

Confidence
----------
This is static analysis over class names, so treat a hit as "go look at this
page in dark theme", not as a verdict. It reads the class list off each <select>
tag, resolves what background those classes are given (following custom-property
aliases into nocturne-tokens.css's .dark-theme block), and reports the ones that
land on a translucent colour. It cannot see runtime class expressions, and it
does not try to model the cascade.

Usage, from the repo root:

    python .claude/skills/nocturne-dropdowns-and-dates/scripts/check_selects.py
    python .../check_selects.py --page InventoryTransfers
    python .../check_selects.py --all          # include pages judged safe
"""

from __future__ import annotations

import argparse
import re
import sys
from pathlib import Path

COMMENT = re.compile(r"/\*.*?\*/", re.S)
# Razor `@* … *@` and HTML `<!-- … -->`. The pages document why they avoid a
# control as often as they use one — AuditTrail.razor has a comment explaining
# that it does NOT use <input type="date"> — so scanning raw text reports the
# opposite of the truth.
RAZOR_COMMENT = re.compile(r"@\*.*?\*@|<!--.*?-->", re.S)
SELECT_TAG = re.compile(r"<select\b([^>]*)>", re.I | re.S)
CLASS_ATTR = re.compile(r"""\bclass\s*=\s*["']([^"']*)["']""", re.I)
DATE_INPUT = re.compile(r"""<input[^>]*type\s*=\s*["']date["']""", re.I)
OPTION_RULE = re.compile(r"(^|[\s,>+~])option\s*[,{]", re.M)
RULE = re.compile(r"(?P<sel>[^{}]+)\{(?P<body>[^{}]*)\}")
BACKGROUND = re.compile(r"\bbackground(?:-color)?\s*:\s*(?P<val>[^;}]+)", re.I)
VAR_DECL = re.compile(r"(?P<name>--[\w-]+)\s*:\s*(?P<val>[^;}]+)")
VAR_REF = re.compile(r"var\(\s*(?P<name>--[\w-]+)\s*(?:,(?P<fallback>[^()]*(?:\([^()]*\))?[^()]*))?\)")
RGBA = re.compile(r"rgba?\(([^)]*)\)", re.I)
# A Razor class attribute is full of @(...) expressions; keep the literal words.
RAZOR_EXPR = re.compile(r"@\(?[^\s\"']*\)?")

OPAQUE = 1.0
# Below this the popup composites onto white and the bug bites.
TRANSLUCENT_BELOW = 0.9


def strip_comments(css: str) -> str:
    return COMMENT.sub("", css)


def collect_vars(css: str, dark_only: bool = False) -> dict[str, str]:
    """Custom-property declarations, last one wins.

    dark_only restricts to .dark-theme / [data-theme="dark"] blocks, which is
    where the translucent washes live -- the light values are opaque and would
    mask the problem.
    """
    out: dict[str, str] = {}
    for m in RULE.finditer(css):
        sel = m.group("sel")
        if dark_only and "dark" not in sel.lower():
            continue
        for d in VAR_DECL.finditer(m.group("body")):
            out[d.group("name")] = d.group("val").strip()
    return out


def resolve(value: str, variables: dict[str, str], depth: int = 0) -> str:
    """Expand var() references until a literal falls out."""
    if depth > 12:
        return value
    m = VAR_REF.search(value)
    if not m:
        return value.strip()
    name = m.group("name")
    if name in variables:
        repl = variables[name]
    elif m.group("fallback") is not None:
        repl = m.group("fallback")
    else:
        return value.strip()
    return resolve(value[: m.start()] + repl + value[m.end():], variables, depth + 1)


def alpha_of(value: str) -> float | None:
    """Alpha of a resolved colour, or None if it isn't one we can read."""
    m = RGBA.search(value)
    if not m:
        if re.search(r"\btransparent\b", value, re.I):
            return 0.0
        if re.search(r"#[0-9a-f]{8}\b", value, re.I):
            return int(re.search(r"#[0-9a-f]{6}([0-9a-f]{2})\b", value, re.I).group(1), 16) / 255
        return None
    parts = [p.strip() for p in m.group(1).replace("/", ",").split(",") if p.strip()]
    if len(parts) < 4:
        return OPAQUE
    a = parts[3].rstrip("%")
    try:
        val = float(a)
    except ValueError:
        return None
    return val / 100 if parts[3].endswith("%") else val


def classes_on_selects(razor_text: str) -> list[set[str]]:
    """The literal class tokens on each <select> tag."""
    found = []
    for m in SELECT_TAG.finditer(razor_text):
        attrs = m.group(1)
        c = CLASS_ATTR.search(attrs)
        tokens = set()
        if c:
            cleaned = RAZOR_EXPR.sub(" ", c.group(1))
            tokens = {t for t in cleaned.split() if re.fullmatch(r"[a-zA-Z][\w-]*", t)}
        found.append(tokens)
    return found


def field_fill(css: str, tokens: set[str], variables: dict[str, str]) -> tuple[str, str] | None:
    """The background a rule gives any of these classes, resolved.

    Returns (selector, resolved value) for the last matching rule, which is the
    best single-pass approximation of the cascade available without a real
    engine.
    """
    hit = None
    for m in RULE.finditer(css):
        sel = m.group("sel")
        sel_classes = set(re.findall(r"\.([a-zA-Z][\w-]*)", sel))
        if not (sel_classes & tokens):
            continue
        b = BACKGROUND.search(m.group("body"))
        if b:
            hit = (" ".join(sel.split()), resolve(b.group("val"), variables))
    return hit


def main() -> int:
    ap = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    ap.add_argument("--root", default=".", help="repo root (default: cwd)")
    ap.add_argument("--page", help="only this page, e.g. InventoryTransfers")
    ap.add_argument("--all", action="store_true", help="also list pages judged safe")
    args = ap.parse_args()

    root = Path(args.root).resolve()
    web = root / "ShopInventory.Web"
    pages_dir = web / "Components" / "Pages"
    css_dir = web / "wwwroot" / "css"

    if not pages_dir.is_dir() or not css_dir.is_dir():
        print(f"error: no ShopInventory.Web under {root}", file=sys.stderr)
        print("       run from the repo root, or pass --root", file=sys.stderr)
        return 2

    tokens_css = strip_comments((css_dir / "nocturne-tokens.css").read_text(encoding="utf-8", errors="replace"))
    dark_vars = collect_vars(tokens_css, dark_only=True)

    sheets = {p.stem.replace("-", "").lower(): p for p in css_dir.glob("*.css")}

    razors = sorted(pages_dir.glob("*.razor"))
    if args.page:
        want = args.page.replace(".razor", "").replace("-", "").lower()
        razors = [r for r in razors if r.stem.replace("-", "").lower() == want]
        if not razors:
            print(f"error: no page matching {args.page!r} in {pages_dir}", file=sys.stderr)
            return 2

    flagged: list[tuple[str, str, str, str]] = []
    safe: list[tuple[str, str]] = []
    native_dates: list[tuple[str, int]] = []

    for razor in razors:
        text = RAZOR_COMMENT.sub("", razor.read_text(encoding="utf-8", errors="replace"))

        n_dates = len(DATE_INPUT.findall(text))
        if n_dates:
            native_dates.append((razor.name, n_dates))

        select_classes = classes_on_selects(text)
        if not select_classes:
            continue

        sheet = sheets.get(razor.stem.replace("-", "").lower())
        if sheet is None:
            # No page sheet: app.css's opaque #1e293b applies. Not affected.
            safe.append((razor.name, "no page sheet - app.css opaque fill"))
            continue

        sheet_css = strip_comments(sheet.read_text(encoding="utf-8", errors="replace"))
        if OPTION_RULE.search(sheet_css):
            safe.append((razor.name, f"{sheet.name} styles option"))
            continue

        # The sheet's own aliases (--itx-field-bg: var(--noct-field-bg)) layer
        # over the token file; the dark --noct-* values then win, because the
        # light ones are opaque and would mask exactly what we're looking for.
        variables = {**dark_vars, **collect_vars(sheet_css), **dark_vars}

        worst: tuple[str, str, float] | None = None
        for tokens in select_classes:
            if not tokens:
                continue
            hit = field_fill(sheet_css, tokens, variables)
            if not hit:
                continue
            sel, value = hit
            a = alpha_of(value)
            if a is not None and a < TRANSLUCENT_BELOW:
                if worst is None or a < worst[2]:
                    worst = (sel, value, a)

        if worst:
            flagged.append((razor.name, sheet.name, worst[0], f"{worst[1]}  (alpha {worst[2]:g})"))
        else:
            safe.append((razor.name, f"{sheet.name} - select fill is opaque"))

    if flagged:
        print(f"Unreadable in dark theme -- translucent field fill, no `option` rule ({len(flagged)}):\n")
        for name, sheet, sel, value in flagged:
            print(f"  {name}  ->  {sheet}")
            print(f"      {sel}")
            print(f"      background resolves to {value}")
        print("\n  Add the option/optgroup rule to each sheet, then confirm in dark theme.")
    else:
        print("No page sheet leaves a native <select> on a translucent fill.")

    if native_dates:
        print(f"\nPages still using <input type=\"date\"> ({len(native_dates)}):")
        for name, count in native_dates:
            print(f"  {name}  ({count})")
        print("  Prefer <NocturneDateField> -- see references/datefield.md.")

    if args.all and safe:
        print(f"\nJudged safe ({len(safe)}):")
        for name, why in safe:
            print(f"  {name:<44} {why}")

    return 1 if (flagged or native_dates) else 0


if __name__ == "__main__":
    raise SystemExit(main())
