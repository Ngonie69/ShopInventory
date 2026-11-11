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

   /inventory-transfers/sap-approvals: inventory transfers and transfer
   requests keyed in the SAP client and approved by SAP's approval procedure
   that nobody has added yet, and the add that turns one into the real
   document. `sta-` is not shared with any other sheet.

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


/* ── The queue (2026-11 redesign) ────────────────────────────────────────
   From the "SAP-Held Transfers Redesign" canvas: rows grouped by the day they
   were raised, the route as each row's headline, a filter panel whose every
   choice SAP applies, and a pager that says where you are. `sta-q-` is the
   list, `sta-f-` the filters, `sta-d-` the drawer's additions.

   MudBlazor's sheet loads last with a bare `button` rule that centres a
   button's contents and sets position: relative; every button below states
   its own alignment at class weight, which outranks it.
   ────────────────────────────────────────────────────────────────────────── */

.sta .sta-sr {
    position: absolute;
    width: 1px;
    height: 1px;
    overflow: hidden;
    clip: rect(0 0 0 0);
    white-space: nowrap;
}

/* 40px controls: the queue is worked on tablets at depots as well as desks. */
.sta .sta-btn-lg,
.sta-overlay .sta-btn-lg {
    height: 40px;
    padding: 0 14px;
    font-weight: 600;
}

.sta .sta-btn-icon-lg,
.sta-overlay .sta-btn-icon-lg {
    width: 40px;
    height: 40px;
    padding: 0;
}

.sta-shell {
    align-items: center;
}

.sta .sta-q-mark {
    width: 44px;
    height: 44px;
    flex-shrink: 0;
    display: grid;
    place-items: center;
    border-radius: 12px;
    background: rgba(var(--sta-accent-rgb), .16);
    color: var(--sta-accent-text);
    font-size: 22px;
}

.sta .sta-sub {
    max-width: 640px;
}

.sta .sta-head-actions {
    margin-left: auto;
}

/* A warehouse code, wherever it is the thing being read. */
.sta .sta-q-code,
.sta-overlay .sta-q-code {
    display: inline-block;
    padding: 2px 8px;
    border-radius: 6px;
    background: var(--noct-field-bg);
    border: 1px solid var(--sta-field-edge);
    color: var(--sta-ink);
    font-family: 'JetBrains Mono', ui-monospace, monospace;
    font-size: 13px;
    font-weight: 600;
    letter-spacing: .01em;
    white-space: nowrap;
}

/* ── Toolbar ── */

.sta-q-bar {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: var(--sta-s4) var(--sta-s6);
    padding: var(--sta-s6);
}

.sta .sta-q-search {
    flex: 1 1 260px;
    max-width: 380px;
    height: 40px;
    display: flex;
    align-items: center;
    gap: var(--sta-s3);
    padding: 0 6px 0 12px;
    border-radius: var(--sta-r-md);
    border: 1px solid var(--sta-field-edge);
    background: var(--noct-field-bg);
    color: var(--sta-mute-3);
}

.sta .sta-q-search:focus-within {
    border-color: var(--sta-accent);
    box-shadow: 0 0 0 3px rgba(var(--sta-accent-rgb), .22);
}

/* (0,2,1): app.css paints every dark-theme input slate at (0,1,1). */
.sta .sta-q-search input {
    flex: 1;
    min-width: 0;
    height: 100%;
    padding: 0;
    border: 0;
    outline: none;
    background-color: transparent;
    color: var(--sta-ink);
    font: inherit;
    font-size: 14px;
}

.sta .sta-q-search input::placeholder {
    color: var(--sta-mute-3);
    -webkit-text-fill-color: var(--sta-mute-3);
    opacity: 1;
}

.sta .sta-q-search input:focus-visible {
    outline: none;
}

.sta .sta-q-search-clear {
    width: 28px;
    height: 28px;
    display: grid;
    place-items: center;
    padding: 0;
    border: 0;
    border-radius: 6px;
    background: transparent;
    color: var(--sta-mute-2);
    cursor: pointer;
}

.sta .sta-q-search-clear:hover {
    background: var(--sta-hover);
    color: var(--sta-ink);
}

.sta .sta-q-search-clear svg {
    width: 12px;
    height: 12px;
    fill: currentColor;
}

.sta .sta-q-filterbtn.is-open {
    background: rgba(var(--sta-accent-rgb), .14);
    border-color: rgba(var(--sta-accent-rgb), .5);
    color: var(--sta-accent-text);
}

.sta .sta-q-badge {
    min-width: 20px;
    height: 20px;
    padding: 0 6px;
    display: inline-grid;
    place-items: center;
    border-radius: 999px;
    background: var(--sta-accent);
    color: var(--noct-on-accent);
    font-size: 11.5px;
    font-weight: 700;
}

.sta .sta-q-range {
    margin-left: auto;
    color: var(--sta-mute-2);
    font-size: 13px;
    white-space: nowrap;
}

.sta .sta-q-range strong {
    color: var(--sta-ink);
    font-weight: 600;
    font-variant-numeric: tabular-nums;
}

.sta .sta-q-scope {
    display: inline-flex;
    align-items: center;
    flex-wrap: wrap;
    gap: var(--sta-s2);
    padding-left: var(--sta-s6);
    border-left: 1px solid var(--sta-divider);
    color: var(--sta-mute-2);
    font-size: 13px;
}

.sta .sta-q-scope .sta-q-code {
    font-size: 12px;
    padding: 1px 7px;
}

/* ── Filter panel ── */

.sta-f {
    border-top: 1px solid var(--sta-rowline);
    background: var(--noct-field-bg);
}

.sta-f-grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(240px, 1fr));
    gap: var(--sta-s8) 32px;
    padding: var(--sta-s6) var(--sta-s6) var(--sta-s3);
}

.sta .sta-f-group {
    min-width: 0;
    margin: 0;
    padding: 0;
    border: 0;
    display: flex;
    flex-direction: column;
    gap: var(--sta-s3);
}

.sta .sta-f-group legend {
    padding: 0;
    margin-bottom: var(--sta-s3);
    color: var(--sta-mute-2);
    font-size: 11.5px;
    font-weight: 600;
    letter-spacing: .08em;
    text-transform: uppercase;
}

.sta-f-chips {
    display: flex;
    flex-wrap: wrap;
    gap: 6px;
}

.sta .sta-f-chip {
    height: 34px;
    padding: 0 12px;
    display: inline-flex;
    align-items: center;
    justify-content: flex-start;
    gap: 6px;
    border-radius: 999px;
    border: 1px solid var(--sta-field-edge);
    background: transparent;
    color: var(--sta-mute);
    font: inherit;
    font-size: 13px;
    font-weight: 500;
    white-space: nowrap;
    cursor: pointer;
    transition: background-color .15s, border-color .15s, color .15s;
}

.sta .sta-f-chip:hover {
    border-color: var(--sta-field-edge-hi);
    color: var(--sta-ink);
}

.sta .sta-f-chip.is-on {
    background: rgba(var(--sta-accent-rgb), .18);
    border-color: rgba(var(--sta-accent-rgb), .55);
    color: var(--sta-accent-text);
}

.sta .sta-f-chip.is-code {
    font-family: 'JetBrains Mono', ui-monospace, monospace;
    font-size: 12.5px;
    font-weight: 600;
}

.sta .sta-f-chip.is-sm {
    height: 30px;
    padding: 0 10px 0 12px;
    font-size: 12.5px;
    font-weight: 600;
}

.sta .sta-f-chip .ph-x {
    font-size: 11px;
}

.sta .sta-f-chip .sta-f-tick {
    display: none;
    font-size: 12px;
}

.sta .sta-f-chip.is-on .sta-f-tick {
    display: inline;
}

.sta-f-foot {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: var(--sta-s3) var(--sta-s6);
    padding: var(--sta-s4) var(--sta-s6) var(--sta-s6);
}

.sta-f-count {
    color: var(--sta-mute-2);
    font-size: 13px;
}

.sta-f-actions {
    margin-left: auto;
    display: flex;
    gap: var(--sta-s3);
}

.sta-f-active {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: var(--sta-s3);
    padding: var(--sta-s4) var(--sta-s6);
    border-top: 1px solid var(--sta-rowline);
}

.sta-f-active-label {
    margin-right: var(--sta-s1);
    color: var(--sta-mute-2);
    font-size: 13px;
}

.sta .sta-f-clear {
    height: 30px;
    padding: 0 8px;
    display: inline-flex;
    align-items: center;
    border: 0;
    background: transparent;
    color: var(--sta-mute-2);
    font: inherit;
    font-size: 12.5px;
    font-weight: 600;
    text-decoration: underline;
    text-underline-offset: 3px;
    cursor: pointer;
}

.sta .sta-f-clear:hover {
    color: var(--sta-ink);
}

/* ── Rows ── */

.sta-q-row {
    display: grid;
    grid-template-columns:
        minmax(210px, 1.25fr) minmax(150px, 1.3fr) minmax(150px, 1fr) minmax(170px, 1.05fr) 230px;
    column-gap: 20px;
    align-items: center;
}

.sta-q-colhead {
    padding: var(--sta-s3) var(--sta-s6);
    border-top: 1px solid var(--sta-rowline);
    color: var(--sta-mute-2);
    font-size: 11.5px;
    font-weight: 600;
    letter-spacing: .08em;
    text-transform: uppercase;
}

.sta-q-end {
    text-align: right;
}

.sta-q-day {
    display: flex;
    align-items: baseline;
    gap: var(--sta-s3);
    padding: var(--sta-s6) var(--sta-s6) var(--sta-s2);
    border-top: 1px solid var(--sta-divider);
}

.sta-q-day-rel {
    font-size: 13px;
    font-weight: 650;
    color: var(--sta-ink);
}

.sta-q-day-date,
.sta-q-day-count {
    font-size: 13px;
    color: var(--sta-mute-2);
}

.sta-q-day-count {
    margin-left: auto;
}

.sta-q-item {
    padding: var(--sta-s4) var(--sta-s6);
    border-top: 1px solid var(--sta-rowline);
    transition: background-color .12s;
}

.sta-q-item:hover {
    background: var(--sta-hover);
}

.sta-q-item.is-open {
    background: rgba(var(--sta-accent-rgb), .10);
}

.sta-q-route-cell {
    display: flex;
    flex-direction: column;
    gap: 6px;
    min-width: 0;
}

.sta-q-route {
    display: flex;
    align-items: center;
    flex-wrap: wrap;
    gap: var(--sta-s3);
}

.sta-q-route .ph-arrow-right,
.sta-d-route .ph-arrow-right {
    color: var(--sta-mute-3);
}

.sta-q-refs {
    display: flex;
    align-items: center;
    gap: 6px;
    font-family: 'JetBrains Mono', ui-monospace, monospace;
    font-size: 12px;
    color: var(--sta-mute-2);
}

.sta-q-comment {
    min-width: 0;
}

.sta-q-comment-text {
    display: -webkit-box;
    -webkit-line-clamp: 2;
    -webkit-box-orient: vertical;
    overflow: hidden;
    color: var(--sta-ink-2);
}

.sta-q-none {
    color: var(--sta-mute-3);
    font-style: italic;
}

.sta-q-stack {
    display: flex;
    flex-direction: column;
    gap: 2px;
    min-width: 0;
}

.sta-q-main {
    color: var(--sta-ink);
    font-weight: 500;
}

.sta-q-sub {
    color: var(--sta-mute-2);
    font-size: 12.5px;
}

.sta-q-act {
    display: flex;
    align-items: center;
    justify-content: flex-end;
    gap: var(--sta-s4);
}

.sta .sta-q-pill,
.sta-overlay .sta-q-pill {
    height: 26px;
    padding: 0 10px;
    display: inline-flex;
    align-items: center;
    gap: 6px;
    border-radius: 999px;
    font-size: 12.5px;
    font-weight: 600;
    white-space: nowrap;
}

.sta .sta-q-pill.is-ready,
.sta-overlay .sta-q-pill.is-ready {
    background: rgba(var(--sta-approved-rgb), .13);
    color: var(--sta-approved);
}

.sta .sta-q-pill.is-held,
.sta-overlay .sta-q-pill.is-held {
    background: rgba(var(--sta-pending-rgb), .15);
    color: var(--sta-pending);
}

.sta .sta-q-review {
    color: var(--sta-accent-text);
}

.sta-q-note {
    margin-top: var(--sta-s3);
    display: flex;
    align-items: flex-start;
    gap: var(--sta-s3);
    padding: var(--sta-s3) var(--sta-s4);
    border-radius: var(--sta-r-md);
    background: var(--sta-warn-bg);
    border: 1px solid var(--sta-warn-edge);
    color: var(--sta-warn-ink);
    font-size: 13px;
}

.sta-q-note .ph {
    margin-top: 3px;
}

.sta .sta-empty .sta-btn {
    margin-top: var(--sta-s3);
}

/* ── Footer and pager ── */

.sta-q-foot {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    justify-content: space-between;
    gap: var(--sta-s4) var(--sta-s8);
    padding: var(--sta-s4) var(--sta-s6);
    border-top: 1px solid var(--sta-divider);
}

.sta-q-foot-range {
    color: var(--sta-mute-2);
    font-size: 13px;
}

.sta-q-foot-range strong {
    color: var(--sta-ink);
    font-weight: 600;
    font-variant-numeric: tabular-nums;
}

.sta-q-pager {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: var(--sta-s3) var(--sta-s6);
}

.sta .sta-q-pager .sta-pager {
    gap: 4px;
}

.sta .sta-q-pagenum {
    min-width: 40px;
    height: 40px;
    padding: 0 8px;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    border: 1px solid transparent;
    border-radius: var(--sta-r-md);
    background: transparent;
    color: var(--sta-mute-2);
    font: inherit;
    font-variant-numeric: tabular-nums;
    cursor: pointer;
}

.sta .sta-q-pagenum:hover:not(:disabled) {
    background: var(--sta-hover);
    color: var(--sta-ink);
}

.sta .sta-q-pagenum.is-current {
    background: rgba(var(--sta-accent-rgb), .18);
    color: var(--sta-accent-text);
    font-weight: 600;
    cursor: default;
}

.sta-q-gap {
    min-width: 24px;
    text-align: center;
    color: var(--sta-mute-3);
}

.sta-q-pagepill {
    height: 28px;
    padding: 0 10px;
    display: inline-flex;
    align-items: center;
    gap: 4px;
    border-radius: 999px;
    background: var(--noct-field-bg);
    border: 1px solid var(--sta-field-edge);
    color: var(--sta-mute-2);
    font-size: 12.5px;
    white-space: nowrap;
}

.sta-q-pagepill strong {
    color: var(--sta-ink);
    font-weight: 600;
    font-variant-numeric: tabular-nums;
}

/* ── Drawer ── */

.sta-overlay .sta-drawer {
    width: min(560px, 96vw);
}

.sta-d-head {
    flex-shrink: 0;
    display: flex;
    flex-direction: column;
    gap: var(--sta-s4);
    padding: var(--sta-s6) var(--sta-s6) var(--sta-s4);
    border-bottom: 1px solid var(--sta-divider);
}

.sta-d-titlerow {
    display: flex;
    align-items: center;
    gap: var(--sta-s4);
}

.sta-overlay .sta-d-titlerow .sta-drawer-title {
    margin: 0;
    font-size: 20px;
    font-weight: 650;
}

.sta-overlay .sta-d-close {
    margin-left: auto;
}

.sta-d-route {
    display: flex;
    align-items: center;
    flex-wrap: wrap;
    gap: var(--sta-s3);
}

.sta-overlay .sta-d-route .sta-q-code {
    padding: 5px 12px;
    font-size: 15px;
}

.sta-overlay .sta-d-facts {
    margin: 0;
}

.sta-overlay .sta-d-facts dd {
    margin: 0;
}

.sta-overlay .sta-d-sub {
    margin-top: 2px;
    color: var(--sta-mute-2);
    font-size: 12.5px;
}

.sta-overlay .sta-d-count {
    margin-left: var(--sta-s2);
    color: var(--sta-mute-2);
    font-weight: 400;
    text-transform: none;
    letter-spacing: 0;
}

.sta-d-lines {
    list-style: none;
    margin: 0;
    padding: 0;
    border: 1px solid var(--sta-rowline);
    border-radius: var(--sta-r-md);
    overflow: hidden;
}

.sta-d-line {
    padding: var(--sta-s4);
}

.sta-d-line + .sta-d-line {
    border-top: 1px solid var(--sta-rowline);
}

.sta-d-line.is-missing {
    background: var(--sta-warn-bg);
}

.sta-d-line-top {
    display: flex;
    align-items: flex-start;
    gap: var(--sta-s4);
}

.sta-d-line-item {
    flex: 1;
    min-width: 0;
    display: flex;
    flex-direction: column;
    gap: 1px;
}

.sta-d-line-code {
    font-family: 'JetBrains Mono', ui-monospace, monospace;
    font-size: 12.5px;
    font-weight: 600;
    color: var(--sta-ink);
}

.sta-d-line-desc {
    color: var(--sta-mute-2);
    font-size: 13px;
}

.sta-d-line-qty {
    font-weight: 600;
    font-variant-numeric: tabular-nums;
    white-space: nowrap;
}

.sta-d-batches {
    display: flex;
    flex-wrap: wrap;
    gap: 6px;
    margin-top: var(--sta-s3);
    font-size: 12px;
}

.sta-d-batch {
    height: 24px;
    padding: 0 8px;
    display: inline-flex;
    align-items: center;
    gap: 6px;
    border-radius: 6px;
    background: var(--noct-field-bg);
    border: 1px solid var(--sta-field-edge);
}

.sta-d-missing {
    height: 24px;
    padding: 0 8px;
    display: inline-flex;
    align-items: center;
    gap: 6px;
    border-radius: 6px;
    background: rgba(var(--sta-pending-rgb), .15);
    color: var(--sta-pending);
    font-weight: 600;
}

.sta-overlay .sta-drawer-foot {
    align-items: center;
}

.sta-overlay .sta-drawer-foot.is-confirming {
    display: block;
}

.sta-d-confirm {
    display: flex;
    flex-direction: column;
    gap: var(--sta-s2);
}

.sta-overlay .sta-d-confirm .sta-confirm-copy {
    font-size: 14px;
    font-weight: 600;
}

.sta-d-confirm-actions {
    display: flex;
    justify-content: flex-end;
    gap: var(--sta-s3);
    margin-top: var(--sta-s3);
}

.sta-d-foot-note {
    flex: 1;
    color: var(--sta-mute-2);
    font-size: 13px;
}

/* ── Document kind: transfers and transfer requests ───────────────────── */
/* SAP holds both kinds in one draft set. The switch picks which the queue
   reads, and each row and the drawer name their kind, because adding one
   moves stock and adding the other does not. */

.sta .sta-kind {
    display: inline-flex;
    align-items: center;
    gap: 2px;
    padding: 3px;
    border-radius: var(--sta-r-md);
    border: 1px solid var(--sta-field-edge);
    background: var(--noct-field-bg);
}

.sta .sta-kind-opt {
    height: 32px;
    padding: 0 10px 0 12px;
    display: inline-flex;
    align-items: center;
    gap: 8px;
    border: 0;
    border-radius: calc(var(--sta-r-md) - 3px);
    background: transparent;
    color: var(--sta-mute);
    font: inherit;
    font-size: 13px;
    font-weight: 600;
    white-space: nowrap;
    cursor: pointer;
    transition: background-color .15s, color .15s;
}

.sta .sta-kind-opt:hover {
    background: var(--sta-hover);
    color: var(--sta-ink);
}

.sta .sta-kind-opt.is-on {
    background: rgba(var(--sta-accent-rgb), .18);
    color: var(--sta-accent-text);
}

.sta .sta-kind-n {
    min-width: 22px;
    height: 20px;
    padding: 0 6px;
    display: inline-grid;
    place-items: center;
    border-radius: 999px;
    background: var(--sta-tag-bg);
    color: var(--sta-ink-2);
    font-size: 11.5px;
    font-weight: 700;
    font-variant-numeric: tabular-nums;
}

.sta .sta-kind-opt.is-on .sta-kind-n {
    background: var(--sta-accent);
    color: var(--noct-on-accent);
}

/* The kind on a row and in the drawer's title. A request is tinted from the
   info family so it reads apart from the accent and the status pills. */
.sta .sta-q-kind,
.sta-overlay .sta-q-kind {
    height: 20px;
    padding: 0 7px;
    display: inline-flex;
    align-items: center;
    border-radius: 5px;
    background: var(--sta-tag-bg);
    color: var(--sta-ink-2);
    font-family: 'Inter', system-ui, sans-serif;
    font-size: 10.5px;
    font-weight: 700;
    letter-spacing: .06em;
    text-transform: uppercase;
}

.sta .sta-q-kind.is-request,
.sta-overlay .sta-q-kind.is-request {
    background: rgba(var(--noct-fam-info-rgb), .16);
    color: var(--noct-fam-info);
}

/* With the kind tag the references outgrow the route column: each keeps its
   words together and the line wraps between them, never inside "Draft 80412". */
.sta .sta-q-refs {
    flex-wrap: wrap;
    row-gap: 4px;
}

.sta .sta-q-refs > span {
    white-space: nowrap;
}

/* What adding a request does, said before the person adds it. */
.sta-overlay .sta-info {
    display: flex;
    align-items: flex-start;
    gap: var(--sta-s3);
    padding: var(--sta-s3) var(--sta-s4);
    border-radius: var(--sta-r-md);
    border: 1px solid rgba(var(--noct-fam-info-rgb), .35);
    background: rgba(var(--noct-fam-info-rgb), .10);
    color: var(--sta-ink);
    font-size: 13px;
}

.sta-overlay .sta-info > .ph {
    margin-top: 3px;
    color: var(--noct-fam-info);
}

/* The way on to where an added request is converted. */
.sta .sta-added-link {
    margin-top: var(--sta-s2);
    display: inline-flex;
    align-items: center;
    gap: 6px;
    color: var(--sta-accent-text);
    font-size: 13px;
    font-weight: 600;
    text-decoration: underline;
    text-underline-offset: 3px;
}

/* ── Narrow screens ── */

@media (max-width: 1080px) {
    .sta-q-row {
        grid-template-columns: minmax(0, 1fr) minmax(0, 1fr);
        row-gap: var(--sta-s4);
    }

    .sta-q-colhead {
        display: none;
    }

    .sta-q-act {
        grid-column: 1 / -1;
        justify-content: space-between;
    }
}

@media (max-width: 640px) {
    .sta-shell {
        flex-wrap: wrap;
        padding: var(--sta-s6) 16px;
    }

    .sta-body {
        padding: var(--sta-s6) 16px 64px;
    }

    .sta-q-row {
        grid-template-columns: minmax(0, 1fr);
    }

    .sta .sta-q-range {
        margin-left: 0;
    }

    .sta .sta-q-scope {
        padding-left: 0;
        border-left: 0;
    }

    /* The switch takes the row, its three choices sharing it evenly. */
    .sta .sta-kind {
        flex: 1 1 100%;
    }

    .sta .sta-kind-opt {
        flex: 1 1 0;
        justify-content: center;
    }
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
