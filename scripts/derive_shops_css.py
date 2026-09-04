"""Derive shops.css from van-sales.css.

The two pages are structurally the same object — a header, an inline edit strip, a
table and a chip per row — so the rules are taken from the sheet that already
dresses that shape rather than written again by eye. Everything is renamed vs- ->
shop- so the sheets stay independent, which van-sales.css's own header asks for.

Run from the repo root. Deterministic: re-running reproduces the file exactly.
"""

import re
import pathlib

SRC = pathlib.Path("ShopInventory.Web/wwwroot/css/van-sales.css")
DST = pathlib.Path("ShopInventory.Web/wwwroot/css/shops.css")

# The classes the Shops page actually uses. A rule is copied when its selector
# mentions one of these, so an unused rule cannot ride along.
WANTED = {
    "vs", "vs-head", "vs-title", "vs-subtitle", "vs-actions", "vs-btn",
    "vs-btn-accent", "vs-notice", "vs-filters", "vs-filter-label", "vs-field",
    "vs-meta", "vs-loading", "vs-empty", "vs-empty-title", "vs-table-wrap",
    "vs-table", "vs-primary", "vs-num", "vs-chip", "vs-chip-good",
    "vs-chip-neutral", "vs-chip-bad",
}

source = SRC.read_text(encoding="utf-8")

# Strip comments so a selector inside prose cannot match.
without_comments = re.sub(r"/\*.*?\*/", "", source, flags=re.S)

# Drop @media blocks whole. The flat rule matcher below cannot see the wrapper, so
# a rule lifted out of one stops being conditional — the first run of this script
# emitted the narrow-screen `.vs { padding: 16px }` as an unconditional rule that
# overrode the real padding. The breakpoints this page wants are added explicitly
# in the footer instead, where they can be read.
without_comments = re.sub(
    r"@media[^{]*\{(?:[^{}]*\{[^{}]*\})*[^{}]*\}", "", without_comments, flags=re.S)

# Top-level rules only: this sheet nests nothing but @media, which is handled below.
rule_pattern = re.compile(r"(?P<sel>[^{}@]+?)\{(?P<body>[^{}]*)\}", re.S)


def selector_wanted(selector: str) -> bool:
    """Every class must be one we want, not merely one of them.

    Nearly every rule in the source sheet is scoped under `.vs`, so testing for
    *any* match keeps the whole file — the first run of this script produced 314
    rules for a page that uses twenty. Requiring the whole set means `.vs .vsc-row`
    is rejected on `vsc-row` while `.vs .vs-table th` is kept.
    """
    classes = set(re.findall(r"\.([A-Za-z0-9_-]+)", selector))
    return bool(classes) and classes <= WANTED


kept: list[str] = []
for match in rule_pattern.finditer(without_comments):
    selector = match.group("sel").strip()
    if not selector or selector.startswith("@"):
        continue
    if selector_wanted(selector):
        body = match.group("body").strip()
        kept.append(f"{selector} {{\n    " + "\n    ".join(
            line.strip() for line in body.splitlines() if line.strip()
        ) + "\n}")

renamed = "\n\n".join(kept)
# Longest-first so `vs-btn-accent` is not half-renamed by the `vs-btn` rule.
for name in sorted(WANTED, key=len, reverse=True):
    renamed = renamed.replace(f".{name}", f".{name.replace('vs', 'shop', 1)}")
renamed = renamed.replace("--vs-", "--shop-")

HEADER = """/* ==========================================================================
   Shops — Nocturne

   /shops: the retail shop master. A shop names the business partner its sales
   are invoiced to, the warehouse its stock leaves and the cost centre its
   takings book against, and every till operator assigned to it inherits all
   three. `shop-` is not shared with any other sheet.

   Derived from van-sales.css, which already dresses this exact shape — a header,
   an inline edit strip, a table and a chip per row — rather than written again by
   eye, so the two stay visually identical without either importing the other.
   GENERATED FILE. Do not hand-edit: regenerate with

       python scripts/derive_shops_css.py

   run from the repo root. A rule this page needs that van-sales.css does not have
   goes in that script's footer, and a rule it should stop inheriting comes out of
   its WANTED set — either way the change survives the next regeneration, which a
   hand-edit here would not.

   Palette and spacing come from nocturne-tokens.css. The block below aliases
   them rather than restating them, so a colour is corrected in one place for the
   whole system, and there is no `.dark-theme .shop` block at all: these tokens
   flip at the root and every alias resolves against whichever scheme is in force.

   The one rule that is not inherited from van-sales.css is the `<select>` option
   block at the foot. A native select popup takes its background from the field's
   own fill, and this sheet's fields are a translucent wash which the browser
   composites onto white — so without an opaque colour there, the dark-theme
   options render near-white on near-white and the list is invisible. `--surface`
   rather than `--field-bg` because surface is the token that is opaque in both
   schemes. See .claude/skills/nocturne-dropdowns-and-dates.
   ========================================================================== */

"""

FOOTER = """

/* The native <select>s in the shop form. Two lines, and the reason they are here
   is in the header: without an opaque popup fill the dark-theme options are
   present, focusable and invisible. This never reproduces in light theme, so a
   light-mode screenshot proves nothing about it. */
.shop .shop-select option,
.shop .shop-select optgroup {
    color: var(--shop-ink);
    background-color: var(--shop-surface);
}

/* Narrow screens. Stated here rather than derived, because a rule lifted out of a
   media query stops being conditional — see the generator's note. */
@media (max-width: 768px) {
    .shop {
        padding: 16px;
    }

    .shop .shop-head {
        flex-direction: column;
        align-items: stretch;
        gap: 12px;
    }
}
"""

DST.write_text(HEADER + renamed + FOOTER, encoding="utf-8")
print(f"wrote {DST} — {len(kept)} rules, {len(DST.read_text(encoding='utf-8').splitlines())} lines")
