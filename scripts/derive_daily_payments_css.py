"""Derive daily-payments.css from shops.css.

/reports/desktop-sales/payments is the same kind of page as /shops: a header,
a filter bar, a segmented control, a table with chips, and a modal holding a
form of pickers. shops.css already dresses every one of those, on the shared
nocturne-tokens.css palette and in both themes, so none of it is written again
by eye. This copies it whole and renames the `shop` prefix to `dpy`, so the two
sheets stay independent, then appends what this page has and /shops does not:
the run-status strip, the attention cards, the totals, the ledger grouped by
day and run, the account cards, and the right-hand drawers.

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

   Built from the "Daily Incoming Payments redesign" canvas: a run-status
   strip, the payments that need a person, totals, a ledger grouped by day
   and run, and a right-hand drawer for one payment or one partner's
   accounts. The drawers sit outside `.dpy` on `.dpy-overlay`, so every rule
   they use is written for that root too.
   ────────────────────────────────────────────────────────────────────────── */

.dpy,
.dpy-overlay {
    --dpy-warn-bg: var(--noct-warn-bg);
    --dpy-warn-edge: var(--noct-warn-edge);
    --dpy-warn-ink: var(--noct-warn-ink);
    --dpy-shadow-lg: var(--noct-shadow-lg);
}

/* ── Shared bits the drawers need too ──────────────────────────────────── */

.dpy-overlay .dpy-chip {
    display: inline-flex;
    align-items: center;
    gap: 5px;
    padding: 2px 9px;
    border-radius: 999px;
    font-size: 0.71rem;
    font-weight: 600;
    letter-spacing: 0.02em;
    white-space: nowrap;
}

.dpy-overlay .dpy-chip-good {
    background: rgba(var(--dpy-fam-good-rgb), .13);
    color: var(--dpy-fam-good);
}

.dpy-overlay .dpy-chip-bad {
    background: rgba(var(--dpy-fam-bad-rgb), .13);
    color: var(--dpy-fam-bad);
}

.dpy-overlay .dpy-chip-neutral {
    background: rgba(var(--dpy-fam-neutral-rgb), .12);
    color: var(--dpy-fam-neutral);
}

.dpy .dpy-chip-warn,
.dpy-overlay .dpy-chip-warn {
    color: var(--dpy-fam-warn);
    background: rgba(var(--dpy-fam-warn-rgb), .12);
}

.dpy-overlay .dpy-num {
    font-variant-numeric: tabular-nums;
    white-space: nowrap;
}

.dpy-overlay .dpy-primary {
    font-weight: 500;
}

.dpy .dpy-mono,
.dpy-overlay .dpy-mono {
    font-family: var(--dpy-mono);
    font-size: 12.5px;
}

.dpy .dpy-acct,
.dpy-overlay .dpy-acct {
    font-family: var(--dpy-mono);
    font-size: 12px;
    color: var(--dpy-ink-2);
}

.dpy .dpy-sub,
.dpy-overlay .dpy-sub {
    display: block;
    font-size: 12px;
    color: var(--dpy-mute);
}

.dpy .dpy-faint,
.dpy-overlay .dpy-faint {
    color: var(--dpy-faint);
}

.dpy .dpy-strong {
    font-weight: 600;
}

.dpy .dpy-mail-ok {
    color: var(--dpy-fam-good);
}

.dpy .dpy-mail-bad {
    color: var(--dpy-fam-bad);
}

/* A status dot: posting on or paused, an email sent or failed. */
.dpy .dpy-dot,
.dpy-overlay .dpy-dot {
    display: inline-block;
    flex: none;
    width: 8px;
    height: 8px;
    border-radius: 50%;
    background: var(--dpy-fam-neutral);
}

.dpy .dpy-dot.is-good,
.dpy-overlay .dpy-dot.is-good {
    background: var(--dpy-fam-good);
}

.dpy .dpy-dot.is-warn,
.dpy-overlay .dpy-dot.is-warn {
    background: var(--dpy-fam-warn);
}

.dpy .dpy-dot.is-bad,
.dpy-overlay .dpy-dot.is-bad {
    background: var(--dpy-fam-bad);
}

/* The cash and electronic keys. Cash is the accent and electronic the info
   blue, which differ in lightness as well as hue in both schemes. */
.dpy .dpy-key,
.dpy-overlay .dpy-key {
    display: inline-block;
    flex: none;
    width: 8px;
    height: 8px;
    margin-right: 7px;
    border-radius: 2px;
    vertical-align: 1px;
}

.dpy .dpy-key-cash,
.dpy-overlay .dpy-key-cash {
    background: var(--dpy-accent);
}

.dpy .dpy-key-el,
.dpy-overlay .dpy-key-el {
    background: var(--dpy-fam-info);
}

.dpy-overlay .dpy-key-mixed {
    background: linear-gradient(90deg, var(--dpy-accent) 50%, var(--dpy-fam-info) 50%);
}

.dpy .dpy-split,
.dpy-overlay .dpy-split {
    display: flex;
    height: 6px;
    border-radius: 3px;
    overflow: hidden;
    background: var(--dpy-field-bg);
}

.dpy-overlay .dpy-split-lg {
    height: 8px;
    border-radius: 4px;
}

.dpy .dpy-split-cash,
.dpy-overlay .dpy-split-cash {
    background: var(--dpy-accent);
}

.dpy .dpy-split-el,
.dpy-overlay .dpy-split-el {
    flex: 1;
    background: var(--dpy-fam-info);
}

/* Tones for the attention icons and the drawer's problem box. */
.dpy .dpy-tone-bad,
.dpy-overlay .dpy-tone-bad {
    --dpy-tone: var(--dpy-fam-bad);
    --dpy-tone-rgb: var(--dpy-fam-bad-rgb);
}

.dpy .dpy-tone-warn,
.dpy-overlay .dpy-tone-warn {
    --dpy-tone: var(--dpy-fam-warn);
    --dpy-tone-rgb: var(--dpy-fam-warn-rgb);
}

.dpy .dpy-tone-info,
.dpy-overlay .dpy-tone-info {
    --dpy-tone: var(--dpy-fam-info);
    --dpy-tone-rgb: var(--dpy-fam-info-rgb);
}

.dpy .dpy-tone-neutral,
.dpy-overlay .dpy-tone-neutral {
    --dpy-tone: var(--dpy-fam-neutral);
    --dpy-tone-rgb: var(--dpy-fam-neutral-rgb);
}

/* ── Notices: posting paused, partners held for accounts ──────────────── */

.dpy .dpy-paused,
.dpy .dpy-held {
    display: flex;
    align-items: center;
    gap: var(--dpy-s4);
    padding: 13px 18px;
    border-radius: var(--dpy-r-lg);
    background: var(--dpy-warn-bg);
    border: 1px solid var(--dpy-warn-edge);
    color: var(--dpy-warn-ink);
}

.dpy .dpy-paused > i,
.dpy .dpy-held > i {
    font-size: 18px;
    flex: none;
}

.dpy .dpy-paused-text {
    display: flex;
    flex-direction: column;
    gap: 2px;
    /* Wide enough to hold its line: on a phone the link or buttons wrap below it instead. */
    flex: 1 1 240px;
    min-width: 0;
    font-size: 13px;
    text-wrap: pretty;
}

.dpy .dpy-paused-text strong {
    font-weight: 600;
    font-size: 14px;
}

.dpy .dpy-paused-link {
    color: var(--dpy-warn-ink);
    font-weight: 600;
    white-space: nowrap;
    text-decoration: underline;
}

.dpy .dpy-held-actions {
    display: flex;
    flex-wrap: wrap;
    gap: var(--dpy-s2);
}

.dpy .dpy-held .dpy-btn {
    color: var(--dpy-warn-ink);
    border-color: var(--dpy-warn-edge);
}

/* ── Run status ────────────────────────────────────────────────────────── */

.dpy .dpy-runs {
    display: grid;
    grid-template-columns: repeat(3, minmax(0, 1fr));
    border: 1px solid var(--dpy-rule);
    border-radius: var(--dpy-r-lg);
    background: var(--dpy-wash);
}

.dpy .dpy-run {
    display: flex;
    flex-direction: column;
    gap: 3px;
    padding: 14px 20px;
    min-width: 0;
}

.dpy .dpy-run + .dpy-run {
    border-left: 1px solid var(--dpy-rule);
}

.dpy .dpy-run-label {
    font-size: 12px;
    font-weight: 500;
    color: var(--dpy-mute-3);
}

.dpy .dpy-run-value {
    display: flex;
    align-items: center;
    gap: 8px;
    font-size: 15px;
    font-weight: 600;
    color: var(--dpy-ink);
}

.dpy .dpy-run-meta {
    font-size: 12px;
    color: var(--dpy-mute-2);
}

.dpy .dpy-run-meta a {
    margin-left: 6px;
    color: var(--dpy-accent-text);
}

/* ── Tabs ──────────────────────────────────────────────────────────────── */

.dpy .dpy-tabbar {
    display: flex;
    gap: 4px;
    border-bottom: 1px solid var(--dpy-rule);
}

.dpy .dpy-tab {
    display: inline-flex;
    align-items: center;
    height: 40px;
    margin-bottom: -1px;
    padding: 0 14px;
    border: 0;
    border-bottom: 2px solid transparent;
    background: transparent;
    font: 500 13.5px 'Inter', system-ui, sans-serif;
    color: var(--dpy-mute-2);
    cursor: pointer;
}

.dpy .dpy-tab:hover:not(.is-on) {
    color: var(--dpy-ink-2);
}

.dpy .dpy-tab.is-on {
    color: var(--dpy-ink);
    font-weight: 600;
    border-bottom-color: var(--dpy-accent);
}

.dpy .dpy-section-head {
    display: flex;
    align-items: baseline;
    flex-wrap: wrap;
    gap: 4px 10px;
}

.dpy .dpy-section-head h3 {
    font-size: 15px;
    font-weight: 600;
    color: var(--dpy-ink);
}

.dpy .dpy-section-head span {
    font-size: 12px;
    color: var(--dpy-mute-3);
}

/* ── Needs attention ───────────────────────────────────────────────────── */

.dpy .dpy-attn,
.dpy .dpy-lands {
    display: flex;
    flex-direction: column;
    gap: var(--dpy-s3);
}

.dpy .dpy-attn-grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(320px, 1fr));
    gap: var(--dpy-s4);
}

.dpy .dpy-attn-card {
    display: flex;
    align-items: center;
    gap: 12px;
    padding: 12px 14px;
    border: 1px solid var(--dpy-rule);
    border-radius: var(--dpy-r-lg);
    background: var(--dpy-surface);
}

.dpy .dpy-attn-icon {
    display: grid;
    place-items: center;
    flex: none;
    width: 32px;
    height: 32px;
    border-radius: var(--dpy-r-md);
    color: var(--dpy-tone);
    background: rgba(var(--dpy-tone-rgb), .14);
}

.dpy .dpy-attn-text {
    display: flex;
    flex-direction: column;
    gap: 1px;
    flex: 1;
    min-width: 0;
}

.dpy .dpy-attn-title {
    font-weight: 600;
    color: var(--dpy-ink);
}

.dpy .dpy-attn-meta {
    font-size: 12px;
    color: var(--dpy-mute-2);
}

.dpy .dpy-attn-card .dpy-btn {
    border-color: var(--dpy-field-edge);
    flex: none;
}

.dpy .dpy-attn-more {
    font-size: 12px;
    color: var(--dpy-mute-3);
}

/* ── Filters and totals ────────────────────────────────────────────────── */

.dpy .dpy-filter-bar {
    align-items: flex-end;
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

.dpy .dpy-filter-field .dpy-seg-btn {
    text-transform: none;
    letter-spacing: 0;
}

.dpy .dpy-result {
    padding-bottom: 9px;
}

.dpy .dpy-kpis {
    display: grid;
    grid-template-columns: 1.6fr repeat(4, minmax(0, 1fr));
    gap: var(--dpy-s4);
}

.dpy .dpy-kpi {
    display: flex;
    flex-direction: column;
    gap: 5px;
    padding: 14px 18px;
    border: 1px solid var(--dpy-rule);
    border-radius: var(--dpy-r-lg);
    background: var(--dpy-surface);
    min-width: 0;
}

.dpy .dpy-kpi-label {
    display: flex;
    align-items: baseline;
    font-size: 12px;
    font-weight: 500;
    color: var(--dpy-mute-3);
}

.dpy .dpy-kpi-value {
    font-size: 20px;
    font-weight: 600;
    font-variant-numeric: tabular-nums;
    color: var(--dpy-ink);
}

.dpy .dpy-kpi-lead .dpy-kpi-value {
    font-size: 26px;
    letter-spacing: -.01em;
}

.dpy .dpy-kpi-value.is-bad {
    color: var(--dpy-fam-bad);
}

.dpy .dpy-kpi-meta {
    font-size: 12px;
    color: var(--dpy-mute-2);
}

/* ── The ledger ────────────────────────────────────────────────────────── */

/* Dates, references, money and account codes never wrap: a wrapped day or amount reads as two
   values. Only the partner name may, and it gets enough room not to break mid-word. */
.dpy .dpy-table th,
.dpy .dpy-table td {
    white-space: nowrap;
}

.dpy .dpy-table td.dpy-wrap {
    white-space: normal;
    min-width: 180px;
    max-width: 280px;
}

.dpy .dpy-table .dpy-num {
    text-align: right;
    font-variant-numeric: tabular-nums;
}

.dpy .dpy-table td .dpy-chip-warn {
    margin-left: 6px;
}

.dpy .dpy-ledger tbody + tbody .dpy-day-row td {
    border-top: 1px solid var(--dpy-divider);
}

.dpy .dpy-table .dpy-day-row td {
    padding: 0;
}

.dpy .dpy-table .dpy-day-row:hover td,
.dpy .dpy-table .dpy-run-row:hover td {
    background: transparent;
}

.dpy .dpy-day {
    display: flex;
    align-items: center;
    gap: 12px;
    width: 100%;
    min-height: 52px;
    padding: 0 16px;
    border: 0;
    background: transparent;
    font: inherit;
    color: var(--dpy-ink);
    text-align: left;
    cursor: pointer;
}

.dpy .dpy-day:hover {
    background: var(--dpy-hover);
}

.dpy .dpy-day-caret {
    width: 16px;
    color: var(--dpy-mute-3);
    transition: transform .15s ease;
}

.dpy .dpy-day-caret.is-open {
    transform: rotate(90deg);
}

.dpy .dpy-day-label {
    min-width: 110px;
    font-size: 15px;
    font-weight: 600;
}

.dpy .dpy-day-count,
.dpy .dpy-day-inv {
    font-size: 13px;
    color: var(--dpy-mute-2);
    font-variant-numeric: tabular-nums;
}

.dpy .dpy-day-fill {
    flex: 1;
}

.dpy .dpy-day-total {
    min-width: 110px;
    text-align: right;
    font-weight: 600;
    font-variant-numeric: tabular-nums;
}

.dpy .dpy-table .dpy-run-row td {
    padding: 7px 16px 7px 44px;
    font-size: 12px;
    color: var(--dpy-mute-3);
    background: var(--dpy-hover);
}

.dpy .dpy-run-row-label {
    margin-right: 10px;
    font-weight: 600;
    letter-spacing: .06em;
    text-transform: uppercase;
    color: var(--dpy-mute);
}

.dpy .dpy-ledger .dpy-pay-row {
    cursor: pointer;
}

.dpy .dpy-ledger .dpy-pay-row td:first-child {
    padding-left: 44px;
}

.dpy .dpy-table tr.is-selected td,
.dpy .dpy-table tr.is-selected:hover td {
    background: rgba(var(--dpy-accent-rgb), .12);
}

.dpy .dpy-pay-open {
    display: block;
    padding: 0;
    border: 0;
    background: none;
    font: inherit;
    color: inherit;
    text-align: left;
    cursor: pointer;
}

/* ── G/L accounts ──────────────────────────────────────────────────────── */

.dpy .dpy-lands-grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(220px, 1fr));
    gap: var(--dpy-s4);
}

.dpy .dpy-land {
    display: flex;
    flex-direction: column;
    gap: 10px;
    padding: 14px 16px;
    border: 1px solid var(--dpy-rule);
    border-radius: var(--dpy-r-lg);
    background: var(--dpy-surface);
}

.dpy .dpy-land-head {
    display: flex;
    flex-direction: column;
    gap: 1px;
}

.dpy .dpy-land-code {
    font-family: var(--dpy-mono);
    font-size: 16px;
    font-weight: 600;
    color: var(--dpy-ink);
}

.dpy .dpy-land-name {
    font-size: 12px;
    color: var(--dpy-mute-2);
}

.dpy .dpy-land-group {
    display: flex;
    flex-direction: column;
    gap: 6px;
}

.dpy .dpy-land-kind {
    display: flex;
    align-items: center;
    font-size: 11px;
    font-weight: 500;
    color: var(--dpy-mute-3);
}

.dpy .dpy-land-codes {
    display: flex;
    flex-wrap: wrap;
    gap: 4px;
}

.dpy .dpy-land-chip {
    padding: 2px 6px;
    border-radius: 5px;
    font-family: var(--dpy-mono);
    font-size: 11px;
}

.dpy .dpy-land-chip.is-cash {
    color: var(--dpy-accent-text);
    background: rgba(var(--dpy-accent-rgb), .12);
}

.dpy .dpy-land-chip.is-el {
    color: var(--dpy-fam-info);
    background: rgba(var(--dpy-fam-info-rgb), .14);
}

.dpy .dpy-land-note {
    padding-top: 8px;
    border-top: 1px solid var(--dpy-rule);
    font-size: 11.5px;
    color: var(--dpy-mute-3);
    text-wrap: pretty;
}

/* ── The drawer: one payment, or one partner's accounts ────────────────── */

.dpy-overlay.dpy-overlay-drawer {
    align-items: stretch;
    justify-content: flex-end;
    padding: 0;
    overflow: hidden;
}

.dpy-overlay .dpy-drawer {
    display: flex;
    flex-direction: column;
    width: min(540px, 100%);
    height: 100%;
    min-height: 0;
    background: var(--dpy-surface);
    box-shadow: var(--dpy-shadow-lg);
    animation: dpy-slide .2s ease;
}

.dpy-overlay .dpy-drawer-head {
    display: flex;
    flex-direction: column;
    gap: 8px;
    padding: 20px 26px 18px;
    border-bottom: 1px solid var(--dpy-rule);
}

.dpy-overlay .dpy-drawer-top {
    display: flex;
    align-items: center;
    gap: 10px;
}

.dpy-overlay .dpy-ref {
    padding: 3px 8px;
    border-radius: 6px;
    background: var(--dpy-field-bg);
    font-family: var(--dpy-mono);
    font-size: 12px;
    color: var(--dpy-mute-2);
}

.dpy-overlay .dpy-drawer-kicker {
    font-size: 12px;
    font-weight: 500;
    color: var(--dpy-mute-3);
}

.dpy-overlay .dpy-drawer-title {
    font-size: 21px;
    font-weight: 600;
    letter-spacing: -.01em;
    color: var(--dpy-ink);
}

.dpy-overlay .dpy-drawer-sub {
    font-size: 13px;
    color: var(--dpy-mute-2);
}

.dpy-overlay .dpy-drawer-status {
    display: flex;
    align-items: center;
    flex-wrap: wrap;
    gap: 10px;
    font-size: 13px;
    color: var(--dpy-mute-2);
}

.dpy-overlay .dpy-drawer-body {
    display: flex;
    flex-direction: column;
    gap: 22px;
    flex: 1;
    min-height: 0;
    overflow-y: auto;
    padding: 20px 26px 28px;
}

.dpy-overlay .dpy-drawer-section {
    display: flex;
    flex-direction: column;
    gap: 10px;
}

.dpy-overlay .dpy-drawer-section-head {
    display: flex;
    align-items: baseline;
    justify-content: space-between;
    gap: 12px;
}

.dpy-overlay .dpy-drawer-section-head h3 {
    font-size: 12px;
    font-weight: 600;
    letter-spacing: .06em;
    text-transform: uppercase;
    color: var(--dpy-mute);
}

.dpy-overlay .dpy-drawer-total {
    font-size: 21px;
    font-weight: 600;
    font-variant-numeric: tabular-nums;
    color: var(--dpy-ink);
}

.dpy-overlay .dpy-posts {
    display: grid;
    grid-template-columns: 16px minmax(0, 1fr) auto;
    gap: 12px 6px;
    align-items: start;
    font-size: 13.5px;
}

.dpy-overlay .dpy-posts .dpy-key {
    margin-top: 6px;
}

.dpy-overlay .dpy-posts-what {
    font-weight: 500;
    color: var(--dpy-ink);
}

.dpy-overlay .dpy-posts-what .dpy-sub {
    font-weight: 400;
}

.dpy-overlay .dpy-lines-wrap {
    border: 1px solid var(--dpy-rule);
    border-radius: var(--dpy-r-md);
    overflow: hidden;
}

.dpy-overlay .dpy-lines {
    width: 100%;
    border-collapse: collapse;
    table-layout: fixed;
    font-size: 13px;
}

.dpy-overlay .dpy-lines th {
    padding: 8px 10px;
    font-size: 11px;
    font-weight: 500;
    text-align: left;
    color: var(--dpy-mute-3);
    background: var(--dpy-hover);
}

.dpy-overlay .dpy-lines td {
    padding: 8px 10px;
    border-top: 1px solid var(--dpy-rule);
    color: var(--dpy-ink-2);
    white-space: nowrap;
}

.dpy-overlay .dpy-lines th:nth-child(1) {
    width: 84px;
}

.dpy-overlay .dpy-lines th:nth-child(2) {
    width: 84px;
}

.dpy-overlay .dpy-lines th:nth-child(4) {
    width: 96px;
}

.dpy-overlay .dpy-lines th:nth-child(5) {
    width: 88px;
}

.dpy-overlay .dpy-lines .dpy-num {
    text-align: right;
}

.dpy-overlay .dpy-line-sale {
    overflow: hidden;
    text-overflow: ellipsis;
    color: var(--dpy-mute-2);
}

.dpy-overlay .dpy-recipients {
    display: flex;
    flex-direction: column;
    gap: 6px;
    margin: 0;
    padding: 0;
    list-style: none;
    font-size: 13px;
    color: var(--dpy-ink-2);
}

.dpy-overlay .dpy-recipients li {
    display: flex;
    align-items: center;
    gap: 10px;
}

.dpy-overlay .dpy-link {
    padding: 0;
    border: 0;
    background: none;
    font: inherit;
    color: var(--dpy-accent-text);
    text-decoration: underline;
    cursor: pointer;
}

.dpy-overlay .dpy-problem {
    display: flex;
    flex-direction: column;
    align-items: flex-start;
    gap: 6px;
    padding: 12px 14px;
    border-radius: var(--dpy-r-md);
    border: 1px solid rgba(var(--dpy-tone-rgb), .38);
    background: rgba(var(--dpy-tone-rgb), .08);
    font-size: 13px;
    color: var(--dpy-mute);
    text-wrap: pretty;
    overflow-wrap: anywhere;
}

.dpy-overlay .dpy-problem strong {
    font-weight: 600;
    color: var(--dpy-tone);
}

.dpy-overlay .dpy-problem .dpy-btn {
    margin-top: 4px;
}

.dpy-overlay .dpy-drawer-foot {
    display: flex;
    align-items: center;
    gap: 10px;
    padding: 14px 26px;
    border-top: 1px solid var(--dpy-rule);
}

.dpy-overlay .dpy-drawer-foot .dpy-form-hint {
    flex: 1;
}

/* ── The account editor ────────────────────────────────────────────────── */

.dpy-overlay .dpy-form-label {
    font-size: 10.5px;
    letter-spacing: .1em;
    text-transform: uppercase;
    color: var(--dpy-mute-3);
}

.dpy-overlay .dpy-form-field > label {
    display: flex;
    align-items: center;
}

/* The "same as cash" box sits in the field beside its label, and must not
   take the field label's small capitals. */
.dpy-overlay .dpy-form-field > label.dpy-check {
    font-size: 13px;
    letter-spacing: 0;
    text-transform: none;
    color: var(--dpy-ink);
    min-height: 32px;
}

.dpy-overlay .dpy-run-pick {
    display: grid;
    grid-template-columns: repeat(2, minmax(0, 1fr));
    gap: 8px;
}

/* MudBlazor's global `button` centres its contents both ways; a card reads from the left. */
.dpy-overlay .dpy-run-card {
    display: flex;
    flex-direction: column;
    align-items: stretch;
    justify-content: flex-start;
    gap: 2px;
    padding: 11px 13px;
    border: 1px solid var(--dpy-field-edge);
    border-radius: var(--dpy-r-md);
    background: transparent;
    font: inherit;
    color: var(--dpy-ink);
    text-align: left;
    cursor: pointer;
}

.dpy-overlay .dpy-run-card:hover:not(.is-on) {
    background: var(--dpy-hover);
}

.dpy-overlay .dpy-run-card.is-on {
    border-color: var(--dpy-accent);
    background: rgba(var(--dpy-accent-rgb), .12);
}

.dpy-overlay .dpy-run-card-title {
    font-weight: 600;
}

.dpy-overlay .dpy-run-card-hint {
    font-size: 12px;
    color: var(--dpy-mute-2);
}

.dpy-overlay .dpy-emails {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 6px;
    min-height: 42px;
    padding: 5px 6px;
    border: 1px solid var(--dpy-field-edge);
    border-radius: var(--dpy-r-md);
    background: var(--dpy-field-bg);
}

.dpy-overlay .dpy-emails:focus-within {
    border-color: var(--dpy-field-edge-hi);
}

.dpy-overlay .dpy-email-chip {
    display: inline-flex;
    align-items: center;
    gap: 2px;
    padding: 3px 3px 3px 9px;
    border-radius: 6px;
    font-size: 13px;
    color: var(--dpy-accent-text);
    background: rgba(var(--dpy-accent-rgb), .14);
}

.dpy-overlay .dpy-email-chip button {
    display: grid;
    place-items: center;
    width: 22px;
    height: 22px;
    padding: 0;
    border: 0;
    border-radius: 4px;
    background: transparent;
    color: inherit;
    cursor: pointer;
}

.dpy-overlay .dpy-email-chip button:hover {
    background: rgba(var(--dpy-accent-rgb), .2);
}

/* The box around it is the field, so the input itself is bare. Written at this
   specificity to beat app.css's dark `.dark-theme input` fill. */
.dpy-overlay .dpy-emails .dpy-email-input {
    flex: 1;
    min-width: 160px;
    height: 30px;
    padding: 0 6px;
    border: 0;
    outline: none;
    background: transparent;
    font: inherit;
    font-size: 13.5px;
    color: var(--dpy-ink);
}

.dpy-overlay .dpy-check-card {
    align-items: flex-start;
    padding: 13px 14px;
    border: 1px solid var(--dpy-rule);
    border-radius: var(--dpy-r-md);
}

/* app.css strips `.dark-theme input` to appearance:none, which leaves a native
   checkbox 0px wide in dark: it vanishes. Written at a higher specificity to
   put the platform box back in both themes. */
.dpy-overlay .dpy-check input[type=checkbox] {
    flex: none;
    width: 16px;
    height: 16px;
    margin: 0;
    -webkit-appearance: auto;
    appearance: auto;
    accent-color: var(--dpy-box);
}

.dpy-overlay .dpy-check-card input[type=checkbox] {
    margin-top: 3px;
}

@keyframes dpy-slide {
    from {
        transform: translateX(24px);
        opacity: 0;
    }
}

@media (prefers-reduced-motion: reduce) {
    .dpy-overlay .dpy-drawer,
    .dpy .dpy-day-caret {
        animation: none;
        transition: none;
    }
}

@media (max-width: 1100px) {
    .dpy .dpy-kpis {
        grid-template-columns: repeat(2, minmax(0, 1fr));
    }

    .dpy .dpy-kpi-lead {
        grid-column: 1 / -1;
    }
}

@media (max-width: 800px) {
    .dpy .dpy-runs {
        grid-template-columns: minmax(0, 1fr);
    }

    .dpy .dpy-run + .dpy-run {
        border-left: 0;
        border-top: 1px solid var(--dpy-rule);
    }

    .dpy .dpy-paused,
    .dpy .dpy-held {
        flex-wrap: wrap;
    }

    .dpy .dpy-attn-grid {
        grid-template-columns: minmax(0, 1fr);
    }

    .dpy-overlay .dpy-drawer-head,
    .dpy-overlay .dpy-drawer-body,
    .dpy-overlay .dpy-drawer-foot {
        padding-inline: 18px;
    }
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
