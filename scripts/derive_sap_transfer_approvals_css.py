"""Derive sap-transfer-approvals.css from credit-note-approvals.css.

/inventory-transfers/sap-approvals is the same kind of page as /credit-notes/approvals:
a queue SAP's approval procedure holds, read live, a right-hand drawer for one
request, and a confirm dialog for the add. credit-note-approvals.css already
dresses every one of those on the shared nocturne-tokens.css palette, in both
themes, so none of it is written again by eye. This copies it whole and renames
the `cna` prefix to `sta`, so the two sheets stay independent, then appends what
this page has and that one does not: the warehouse route, each line's batch
picks, and the mark on a line SAP would refuse for want of one.

The copy is whole rather than filtered: a filter that does not understand nesting
is how a narrow-screen rule once escaped its media query (see
derive_daily_payments_css.py). A rule this page never matches costs a few bytes.

Run from the repo root. Deterministic: re-running reproduces the file exactly.
Edit this script, never the generated sheet.
"""

import pathlib
import re

CSS = pathlib.Path("ShopInventory.Web/wwwroot/css")
SRC = CSS / "credit-note-approvals.css"
DST = CSS / "sap-transfer-approvals.css"

HEADER = """/* ==========================================================================
   SAP-held transfers — Nocturne

   /inventory-transfers/sap-approvals: inventory transfers keyed in the SAP
   client and approved by SAP's approval procedure that nobody has added yet,
   and the add that turns one into the real transfer. `sta-` is not shared
   with any other sheet.

   GENERATED FILE. Do not hand-edit: regenerate with

       python scripts/derive_sap_transfer_approvals_css.py

   run from the repo root. Everything above the page footer is
   credit-note-approvals.css with its prefix renamed, so it follows that
   sheet's palette and both themes through nocturne-tokens.css and has no
   `.dark-theme` block of its own.
   ========================================================================== */

"""

FOOTER = """


/* ── This page ───────────────────────────────────────────────────────────
   What the credit memo queue does not have. Every colour resolves through
   the --sta-* aliases above, so both themes follow from nocturne-tokens.css.
   The drawer sits outside `.sta` on `.sta-overlay`, so every rule it uses is
   written for that root too.
   ────────────────────────────────────────────────────────────────────────── */

/* The route a transfer takes, source first, as one unit that does not wrap. */
.sta .sta-route,
.sta-overlay .sta-route {
    display: inline-flex;
    align-items: center;
    gap: var(--sta-s2);
    white-space: nowrap;
}

/* The route is the row's control, and MudBlazor's sheet loads last with a bare
   `button` rule that centres a button's contents; the column flex box then
   stretches it across the cell. Both together put the route in the middle of
   the cell over its left-aligned numbers. */
.sta .sta-cust-name.sta-route {
    justify-content: flex-start;
    align-self: flex-start;
}

.sta .sta-route-arrow,
.sta-overlay .sta-route-arrow {
    color: var(--sta-mute-2);
    font-size: 12px;
}

.sta .sta-wh,
.sta-overlay .sta-wh {
    font-family: 'JetBrains Mono', ui-monospace, monospace;
    font-size: 12px;
    letter-spacing: .01em;
    color: var(--sta-ink);
}

/* A depot controller's queue is limited to its own warehouses; say which. */
.sta .sta-scope {
    display: inline-flex;
    align-items: center;
    gap: var(--sta-s2);
    flex-wrap: wrap;
    font-size: 12.5px;
    color: var(--sta-mute);
}

/* The batch picks on a draft line, one per row, inside the lines table. */
.sta-overlay .sta-batches {
    display: flex;
    flex-direction: column;
    gap: 2px;
    font-size: 12px;
}

.sta-overlay .sta-batch {
    display: inline-flex;
    gap: var(--sta-s2);
    white-space: nowrap;
}

.sta-overlay .sta-batch-no {
    font-family: 'JetBrains Mono', ui-monospace, monospace;
    color: var(--sta-ink-2);
}

.sta-overlay .sta-batch-qty {
    color: var(--sta-mute);
    font-variant-numeric: tabular-nums;
}

/* What a used-up batch holds now, beside what the draft picks from it. */
.sta-overlay .sta-batch-left {
    color: var(--sta-warn-ink);
    font-size: 11.5px;
    font-weight: 500;
    font-variant-numeric: tabular-nums;
}

/* A line SAP would refuse for want of a batch or serial pick. The warn family,
   on the line itself as well as in the note, so it is found in a long draft. */
.sta-overlay .sta-lines-missing td {
    background: var(--sta-warn-bg);
}

.sta-overlay .sta-missing {
    display: inline-flex;
    align-items: center;
    gap: 4px;
    font-size: 11.5px;
    font-weight: 500;
    color: var(--sta-warn-ink);
}

.sta-overlay .sta-copied {
    font-size: 11px;
    color: var(--sta-mute);
}

/* The transfer requests a draft was copied from, and whether each is open. */
.sta-overlay .sta-linked {
    display: flex;
    flex-direction: column;
    gap: var(--sta-s2);
}

.sta-overlay .sta-linked-item {
    display: flex;
    align-items: center;
    gap: var(--sta-s3);
    padding: var(--sta-s2) var(--sta-s3);
    border-radius: var(--sta-r-md);
    box-shadow: var(--sta-shadow-sm);
    font-size: 13px;
}

.sta-overlay .sta-linked-item .sta-tag {
    margin-left: auto;
}
"""


def main() -> None:
    source = SRC.read_text(encoding="utf-8")
    # The source's own header describes the credit memo page; this sheet's replaces it.
    body = re.sub(r"\A/\* =+.*?=+ \*/\s*", "", source, count=1, flags=re.S)
    # `cna` only ever appears as the prefix (verified: never inside another word), so a token rename is exact.
    renamed = re.sub(r"(?<![A-Za-z0-9])cna(?![A-Za-z0-9])", "sta", body)
    if re.search(r"(?<![A-Za-z0-9])cna(?![A-Za-z0-9])", renamed):
        raise SystemExit("a cna token survived the rename")
    DST.write_text(HEADER + renamed.rstrip() + FOOTER, encoding="utf-8", newline="\n")
    print(f"wrote {DST} ({len((HEADER + renamed + FOOTER).splitlines())} lines)")


if __name__ == "__main__":
    main()
