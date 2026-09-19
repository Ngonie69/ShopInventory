"""Derive daily-payments.css from shops.css.

/reports/desktop-sales/payments is the same kind of page as /shops: a header,
a filter bar, a segmented control, a table with chips, and a modal holding a
form of pickers. shops.css already dresses every one of those, on the shared
nocturne-tokens.css palette and in both themes, so none of it is written again
by eye. This copies it whole and renames the `shop` prefix to `dpy`, so the two
sheets stay independent, then appends what this page has and /shops does not:
the payment totals strip, the expandable invoice rows, the textarea and the
account/reference cells.

The copy is whole rather than filtered (derive_shops_css.py filters): shops.css
is small, it carries its own @media and @keyframes blocks, and a filter that
does not understand nesting is how a narrow-screen rule once escaped its media
query. A rule this page never matches costs a few bytes; a rule that escapes its
query costs a layout.

Run from the repo root. Deterministic: re-running reproduces the file exactly.
Edit this script, never the generated sheet.
"""

import pathlib
import re

CSS = pathlib.Path("ShopInventory.Web/wwwroot/css")
SRC = CSS / "shops.css"
DST = CSS / "daily-payments.css"

HEADER = """/* ==========================================================================
   Daily incoming payments — Nocturne

   /reports/desktop-sales/payments: the one incoming payment each business
   partner gets per day for its till, vending and van invoices, the invoices
   each one settles, and the G/L accounts each partner's payment posts to.
   `dpy-` is not shared with any other sheet.

   GENERATED FILE. Do not hand-edit: regenerate with

       python scripts/derive_daily_payments_css.py

   run from the repo root. Everything above the page footer is shops.css with
   its prefix renamed, so it follows that sheet's palette and both themes
   through nocturne-tokens.css and has no `.dark-theme` block of its own.
   ========================================================================== */

"""

FOOTER = """

/* ── This page ───────────────────────────────────────────────────────────
   What /shops does not have. Every colour resolves through the --dpy-*
   aliases above, so both themes follow from nocturne-tokens.css.
   ────────────────────────────────────────────────────────────────────────── */

.dpy .dpy-tabs {
    align-self: flex-start;
}

.dpy .dpy-filter-bar .ndf,
.dpy .dpy-filter-bar .nsel {
    min-width: 150px;
}

.dpy .dpy-filter-field {
    display: flex;
    flex-direction: column;
    gap: 4px;
    font-size: 11px;
    color: var(--dpy-mute);
    text-transform: uppercase;
    letter-spacing: .04em;
}

.dpy .dpy-totals {
    display: flex;
    flex-wrap: wrap;
    gap: var(--dpy-s4);
}

.dpy .dpy-total {
    flex: 1 1 160px;
    padding: var(--dpy-s3) var(--dpy-s4);
    border: 1px solid var(--dpy-divider);
    border-radius: var(--dpy-r-md);
    background: var(--dpy-surface);
}

.dpy .dpy-total-label {
    font-size: 11px;
    color: var(--dpy-mute);
    text-transform: uppercase;
    letter-spacing: .04em;
}

.dpy .dpy-total-value {
    font-size: 20px;
    font-weight: 600;
    font-variant-numeric: tabular-nums;
    color: var(--dpy-ink);
}

/* Dates, references, money and account codes never wrap: a wrapped day or amount reads as two
   values. Only the partner name may, and it gets enough room not to break mid-word. */
.dpy .dpy-table th,
.dpy .dpy-table td {
    white-space: nowrap;
}

.dpy .dpy-table td.dpy-wrap {
    white-space: normal;
    min-width: 180px;
    max-width: 260px;
}

.dpy .dpy-table .dpy-num {
    text-align: right;
    font-variant-numeric: tabular-nums;
}

.dpy .dpy-mono {
    font-family: var(--dpy-mono);
    font-size: 12.5px;
}

.dpy .dpy-acct {
    font-family: var(--dpy-mono);
    font-size: 12px;
    color: var(--dpy-ink-2);
}

.dpy .dpy-sub {
    display: block;
    font-size: 12px;
    color: var(--dpy-mute);
}

.dpy .dpy-row-open td {
    background: var(--dpy-hover);
}

.dpy .dpy-expand {
    background: none;
    border: 0;
    color: var(--dpy-mute);
    cursor: pointer;
    padding: 2px 6px;
    border-radius: var(--dpy-r-sm);
}

.dpy .dpy-expand:hover {
    color: var(--dpy-ink);
    background: var(--dpy-hover);
}

.dpy .dpy-detail-cell {
    padding: 0 !important;
    background: var(--dpy-wash);
}

.dpy .dpy-detail {
    padding: var(--dpy-s3) var(--dpy-s6) var(--dpy-s4);
    display: flex;
    flex-direction: column;
    gap: var(--dpy-s3);
}

.dpy .dpy-detail-error {
    color: rgb(var(--dpy-fam-bad-rgb));
    font-size: 13px;
}

.dpy .dpy-lines {
    width: 100%;
    border-collapse: collapse;
    font-size: 13px;
}

.dpy .dpy-lines th {
    text-align: left;
    font-weight: 500;
    font-size: 11px;
    text-transform: uppercase;
    letter-spacing: .04em;
    color: var(--dpy-mute);
    padding: 6px 8px;
    border-bottom: 1px solid var(--dpy-divider);
}

.dpy .dpy-lines td {
    padding: 6px 8px;
    border-bottom: 1px solid var(--dpy-rule);
    color: var(--dpy-ink-2);
}

.dpy .dpy-lines .dpy-num,
.dpy .dpy-lines th.dpy-num {
    text-align: right;
}

.dpy .dpy-chip-warn {
    color: var(--dpy-fam-warn);
    background: rgba(var(--dpy-fam-warn-rgb), .12);
}

.dpy .dpy-chip-info {
    color: var(--dpy-fam-info);
    background: rgba(var(--dpy-fam-info-rgb), .12);
}

.dpy .dpy-chip-accent {
    color: var(--dpy-fam-accent);
    background: rgba(var(--dpy-fam-accent-rgb), .12);
}

.dpy .dpy-mail-ok {
    color: var(--dpy-fam-good);
}

.dpy .dpy-mail-bad {
    color: var(--dpy-fam-warn);
}

.dpy-overlay .dpy-textarea {
    min-height: 84px;
    resize: vertical;
    font-family: inherit;
    line-height: 1.5;
}

.dpy-overlay .dpy-check {
    display: flex;
    align-items: center;
    gap: 8px;
    font-size: 14px;
    color: var(--dpy-ink);
}

.dpy-overlay .dpy-check input {
    accent-color: var(--dpy-accent);
    width: 16px;
    height: 16px;
}
"""


def main() -> None:
    text = SRC.read_text(encoding="utf-8")

    # Drop the source's prose; its header describes /shops, not this page.
    text = re.sub(r"/\*.*?\*/\s*", "", text, flags=re.S)

    # Class names, custom properties and keyframe names: `.shop`, `.shop-x`,
    # `--shop-x`, `shop-fade`. A word boundary on both sides so nothing that
    # merely contains the letters is touched.
    renamed = re.sub(r"(?<![A-Za-z0-9_])shop(?=[-\s{,:.)\[>+~]|$)", "dpy", text)

    sheet = HEADER + renamed.strip() + "\n" + FOOTER
    DST.write_text(sheet, encoding="utf-8", newline="\n")

    left = re.findall(r"[.-]shop\b|--shop-", renamed)
    if left:
        raise SystemExit(f"unrenamed: {sorted(set(left))}")

    body = re.sub(r"/\*.*?\*/", "", sheet, flags=re.S)
    have = set(re.findall(r"(--[a-z0-9-]+)\s*:", body))
    want = set(re.findall(r"var\((--dpy-[a-z0-9-]+)", body))
    dangling = sorted(want - have)
    if dangling:
        raise SystemExit(f"dangling tokens: {dangling}")

    print(f"wrote {DST} ({len(sheet.splitlines())} lines)")


if __name__ == "__main__":
    main()
