#!/usr/bin/env python3
"""Assert every prefixed class a Razor page uses is defined in its stylesheet.

Nothing in the test suite reads a stylesheet, so a page whose CSS block never
landed -- a failed append, a bad merge, a renamed prefix -- builds clean, passes
every test and renders with no styling at all. This is the check that catches
that, and it is cheap enough to run after any page work.

    python scripts/check_page_css.py                          # every known pair
    python scripts/check_page_css.py route-assignments        # one pair
    python scripts/check_page_css.py --unused                 # also list dead rules

Exit code is 1 when a used class has no definition, so it can gate a commit.
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PAGES = ROOT / "ShopInventory.Web" / "Components"
SHEETS = ROOT / "ShopInventory.Web" / "wwwroot" / "css"

# Classes that come from somewhere other than the page's own sheet.
SHARED_PREFIXES = ("nsel-", "ndf-", "ph-", "ph", "visually-hidden", "dark-theme")

# A page sheet's prefix is its root class: the sheet's first selector.
CLASS_IN_MARKUP = re.compile(r'class="([^"]*)"')
CLASS_IN_CSS = re.compile(r'\.([A-Za-z_][\w-]*)')
# Razor interpolation inside a class attribute: class="ras-x @(cond ? "is-on" : null)"
RAZOR_BITS = re.compile(r'@\([^)]*\)|@[\w.]+')


def sheet_prefix(sheet: Path) -> str:
    """The stylesheet's own namespace, taken from its first class selector."""
    for line in sheet.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if line.startswith(".") and "{" in line:
            name = CLASS_IN_CSS.match(line)
            if name:
                return name.group(1).split("-")[0] + "-"
    return ""


def classes_used(page: Path) -> set[str]:
    src = page.read_text(encoding="utf-8")
    used: set[str] = set()
    for attr in CLASS_IN_MARKUP.findall(src):
        # Keep the string literals inside a Razor ternary -- "is-on" and friends
        # are real classes -- but drop the expression scaffolding around them.
        for chunk in RAZOR_BITS.split(attr):
            used.update(token for token in chunk.split() if token and token.isascii())
        for literal in re.findall(r'"([^"]+)"', attr):
            used.update(literal.split())
    return {c for c in used if re.fullmatch(r"[A-Za-z_][\w-]*", c or "")}


def classes_defined(sheet: Path) -> set[str]:
    css = sheet.read_text(encoding="utf-8")
    css = re.sub(r"/\*.*?\*/", " ", css, flags=re.S)
    return set(CLASS_IN_CSS.findall(css))


def check(page: Path, sheet: Path, show_unused: bool) -> int:
    if not page.exists():
        print(f"  !! no page at {page}")
        return 1
    if not sheet.exists():
        print(f"  !! no sheet at {sheet}")
        return 1

    prefix = sheet_prefix(sheet)
    used = classes_used(page)
    defined = classes_defined(sheet)

    mine = {c for c in used
            if (c.startswith(prefix) or c.startswith("is-"))
            and not c.startswith(SHARED_PREFIXES)
            # a trailing hyphen is the stub of a class built by concatenation
            # ("uac-hour-@level"); the real name is only known at render time
            and not c.endswith("-")}
    missing = sorted(mine - defined)

    print(f"  {page.name} -> {sheet.name}   prefix {prefix!r}   "
          f"{len(mine)} used, {len(defined)} defined")
    if missing:
        print(f"  !! {len(missing)} used but NOT defined:")
        for c in missing:
            print(f"       .{c}")
    if show_unused:
        unused = sorted(c for c in defined
                        if c.startswith(prefix) and c not in used)
        if unused:
            print(f"  -- {len(unused)} defined but unused: {', '.join('.' + c for c in unused)}")
    return 1 if missing else 0


def pairs() -> list[tuple[Path, Path]]:
    """A page and its sheet, matched by the sheet's name appearing in App.razor."""
    found = []
    for sheet in sorted(SHEETS.glob("*.css")):
        stem = sheet.stem
        candidates = [p for p in PAGES.rglob("*.razor")
                      if p.stem.lower() == stem.replace("-", "")]
        if candidates:
            found.append((candidates[0], sheet))
    return found


def main() -> int:
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    show_unused = "--unused" in sys.argv

    if args:
        wanted = []
        for name in args:
            sheet = SHEETS / f"{name}.css"
            page = next((p for p in PAGES.rglob("*.razor")
                         if p.stem.lower() == name.replace("-", "")), None)
            if page is None:
                print(f"no page found for {name}")
                return 1
            wanted.append((page, sheet))
    else:
        wanted = pairs()

    print(f"checking {len(wanted)} page/sheet pair(s)")
    bad = 0
    for page, sheet in wanted:
        bad += check(page, sheet, show_unused)
    print("\nOK" if bad == 0 else f"\n{bad} pair(s) with undefined classes")
    return 1 if bad else 0


if __name__ == "__main__":
    raise SystemExit(main())
