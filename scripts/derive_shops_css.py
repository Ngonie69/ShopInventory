"""Derive shops.css from the sheets that already dress its parts.

/shops is an Administration page built out of three things this codebase has
already solved somewhere else, so none of them is written again by eye:

  * user-management.css — the page shell, the buttons, the filter bar and search
    box, the empty state, and the modal with the two-column form inside it. It is
    the sibling admin page, one row up the same sidebar group, and it is fully
    aliased onto nocturne-tokens.css. Almost all of this sheet comes from here.
  * van-sales.css — the table and the status chips. User Management draws a list
    of cards rather than a table, so the one thing it cannot lend is the one thing
    /shops is mostly made of.
  * credit-note-approvals.css — the segmented status filter. Same object, same
    job: three answers to one question with a count on each.

Every class is renamed to `shop-` so the sheets stay independent, which each
source sheet's own header asks for. A rule is copied only when *every* class in
its selector is one this page uses, so an unused rule cannot ride along, and two
sources may not produce the same class — that is checked below and raises.

Run from the repo root. Deterministic: re-running reproduces the file exactly.
"""

import re
import pathlib
from dataclasses import dataclass, field

CSS = pathlib.Path("ShopInventory.Web/wwwroot/css")
DST = CSS / "shops.css"


@dataclass
class Source:
    """One sheet to lift rules from, and the rules for lifting them."""

    path: pathlib.Path

    #: Class names (source-prefixed) the page uses. A rule survives when every
    #: class in its selector is in here, in `scopes`, or in `keep`.
    wanted: set[str]

    #: Classes allowed to *appear* in a selector without being lifted on their
    #: own. `.vs` is van-sales' page root: `.vs .vs-table` is wanted, the bare
    #: `.vs { … }` block is not — that is this page's own root, and it comes
    #: from user-management.css instead.
    scopes: set[str] = field(default_factory=set)

    #: Classes that pass through unrenamed. State classes (`is-on`) and Blazor's
    #: own (`required`), which mean the same thing on every page.
    keep: set[str] = field(default_factory=set)

    #: Class renames that are not the default prefix swap.
    rename: dict[str, str] = field(default_factory=dict)

    #: Custom-property renames that are not the default prefix swap. Used where
    #: two sheets spell the same system value differently.
    tokens: dict[str, str] = field(default_factory=dict)

    #: Prose printed above this source's rules in the generated sheet.
    note: str = ""


SOURCES = [
    Source(
        path=CSS / "user-management.css",
        note="""/* ── From user-management.css ─────────────────────────────────────────────
   The shell, the controls, the empty state and the modal. `.um-page` becomes
   `.shop` and `.um-overlay` becomes `.shop-overlay`; the token block is declared
   on both because the modal is a *sibling* of the page element, not a child, and
   would otherwise inherit neither the palette nor the font context. Everything
   that resolves a token here is two classes deep so it out-specifies app.css's
   global `.dark-theme input/select/button` block regardless of load order.
   ────────────────────────────────────────────────────────────────────────── */""",
        wanted={
            # Scopes, both liftable in their own right.
            "um-page", "um-overlay",
            # Header.
            # No `um-header-icon`: it exists in the source only to switch off a
            # gradient tile that page used to carry, and this one never had one.
            "um-header", "um-header-left", "um-title",
            "um-subtitle", "um-header-actions",
            # Buttons. Nocturne outlines its primary action rather than filling
            # it, which is what the design's own .btn-primary does — so these
            # replace van-sales' filled .vs-btn-accent rather than joining it.
            "um-btn", "um-btn-primary", "um-btn-ghost", "um-btn-danger",
            "um-action-btn",
            # Filter row.
            "um-filter-bar", "um-search-box",
            # Text fields, on the page and in the modal.
            "um-input",
            # States.
            "um-empty", "um-loading",
            # The modal.
            "um-modal", "um-modal-header", "um-modal-body", "um-modal-footer",
            "um-modal-close",
            "um-form-grid", "um-form-full", "um-form-field", "um-form-hint",
            # The dialog that says what it is before anything is read. Closing a
            # shop is not deleting a user, but it is the same warning at the same
            # weight, so it takes the same dress under a name that fits — and the
            # band alone (`.shop-alert`) is this page's error notice too, since
            # both say the same kind of thing in the same hue.
            "um-delete-header", "um-delete-warning",
        },
        keep={"required"},
        rename={
            "um-page": "shop",
            "um-delete-header": "shop-alert-head",
            "um-delete-warning": "shop-alert",
        },
    ),
    Source(
        path=CSS / "van-sales.css",
        note="""/* ── From van-sales.css ───────────────────────────────────────────────────
   The table and the status chips, which is all this sheet lends: User
   Management draws a card list, and /shops is a table. The chips resolve
   against the family tokens the sheet above declares, so `--vs-good` is
   remapped to `--shop-fam-good` rather than left to dangle.
   ────────────────────────────────────────────────────────────────────────── */""",
        wanted={
            "vs-table-wrap", "vs-table", "vs-num", "vs-primary",
            "vs-chip", "vs-chip-good", "vs-chip-neutral", "vs-chip-bad",
        },
        scopes={"vs"},
        tokens={
            "--vs-good": "--shop-fam-good",
            "--vs-good-rgb": "--shop-fam-good-rgb",
            "--vs-bad": "--shop-fam-bad",
            "--vs-bad-rgb": "--shop-fam-bad-rgb",
            "--vs-neutral": "--shop-fam-neutral",
            "--vs-neutral-rgb": "--shop-fam-neutral-rgb",
        },
    ),
    Source(
        path=CSS / "credit-note-approvals.css",
        note="""/* ── From credit-note-approvals.css ───────────────────────────────────────
   The segmented status filter. Three choices, all of them worth seeing at
   once, and the count of each is part of the answer — the same reason that
   page gives for not using a dropdown. `.is-on` passes through unrenamed:
   it is a state, not a name.
   ────────────────────────────────────────────────────────────────────────── */""",
        wanted={"cna-seg", "cna-seg-btn"},
        scopes={"cna"},
        keep={"is-on"},
    ),
]


def prefix_of(source: Source) -> str:
    """The source sheet's own class prefix, taken from the names it declares."""
    return sorted(source.wanted | source.scopes, key=len)[0].split("-")[0]


def lift(source: Source) -> tuple[str, set[str]]:
    """Return this source's renamed rules, and the class names it defines."""
    text = source.path.read_text(encoding="utf-8")

    # Strip comments so a selector quoted inside prose cannot match.
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)

    # The animation names this sheet declares, collected before the at-rules are
    # dropped. A lifted rule saying `animation: um-fade .18s` would otherwise
    # keep naming a keyframes block that is not in the output — which is not an
    # error anywhere, just an animation that silently never runs. They are
    # renamed with everything else and redeclared in the footer, and `main`
    # refuses to write a sheet where a name is used and not declared.
    keyframes = set(re.findall(r"@keyframes\s+([A-Za-z0-9_-]+)", text))

    # Drop @media and @keyframes blocks whole. The flat matcher below cannot see
    # the wrapper, so a rule lifted out of a media query stops being conditional
    # — an early run of this script emitted the narrow-screen `.vs { padding }`
    # unconditionally and it overrode the real padding. What this page needs of
    # both is stated in the footer instead, where it can be read.
    text = re.sub(
        r"@(?:media|keyframes|supports)[^{]*\{(?:[^{}]*\{[^{}]*\})*[^{}]*\}",
        "", text, flags=re.S)

    rule_pattern = re.compile(r"(?P<sel>[^{}@]+?)\{(?P<body>[^{}]*)\}", re.S)
    allowed = source.wanted | source.scopes | source.keep

    kept: list[str] = []
    defines: set[str] = set()

    for match in rule_pattern.finditer(text):
        selector = match.group("sel").strip()
        if not selector or selector.startswith("@"):
            continue

        classes = set(re.findall(r"\.([A-Za-z0-9_-]+)", selector))

        # Every class must be one we want, not merely one of them. Nearly every
        # rule in these sheets is scoped under the page root, so testing for
        # *any* match keeps the whole file.
        if not classes or not classes <= allowed:
            continue
        # A selector made only of scopes is the source page's own root rule.
        if classes <= source.scopes:
            continue

        defines |= classes - source.scopes - source.keep
        body = match.group("body").strip()
        kept.append(f"{selector} {{\n    " + "\n    ".join(
            line.strip() for line in body.splitlines() if line.strip()
        ) + "\n}")

    chunk = "\n\n".join(kept)
    prefix = prefix_of(source)

    # Longest first, so `vs-chip-good` is not half-renamed by the `vs-chip` rule.
    for name in sorted(allowed, key=len, reverse=True):
        if name in source.keep:
            continue
        target = source.rename.get(name, name.replace(prefix, "shop", 1))
        chunk = re.sub(rf"\.{re.escape(name)}\b", f".{target}", chunk)

    for name in sorted(keyframes, key=len, reverse=True):
        if name.startswith(f"{prefix}-"):
            chunk = re.sub(rf"\b{re.escape(name)}\b",
                           name.replace(prefix, "shop", 1), chunk)

    for token, target in sorted(source.tokens.items(), key=lambda kv: -len(kv[0])):
        chunk = re.sub(rf"{re.escape(token)}\b", target, chunk)
    chunk = re.sub(rf"--{prefix}-", "--shop-", chunk)

    renamed_defines = {
        source.rename.get(name, name.replace(prefix, "shop", 1)) for name in defines
    }
    return chunk, renamed_defines


HEADER = """/* ==========================================================================
   Shops — Nocturne

   /shops: the retail shop master. A shop names the business partner its sales
   are invoiced to, the warehouse its stock leaves and the cost centre its
   takings book against, and every till operator assigned to it inherits all
   three. `shop-` is not shared with any other sheet.

   GENERATED FILE. Do not hand-edit: regenerate with

       python scripts/derive_shops_css.py

   run from the repo root. A rule this page needs that no source sheet has goes
   in that script's footer, and a rule it should stop inheriting comes out of the
   relevant source's WANTED set — either way the change survives the next
   regeneration, which a hand-edit here would not. (It has not before: the
   `--npick-*` tuning block below and the removal of two dead `<select>` repairs
   were both hand-edits that the generator would have reverted. Both are the
   generator's now.)

   Nothing here is new. It is three sheets' existing vocabulary under this page's
   prefix — see the section notes below for what comes from where and why.

   Palette and spacing come from nocturne-tokens.css. The block below aliases
   them rather than restating them, so a colour is corrected in one place for the
   whole system, and there is no `.dark-theme .shop` block at all: these tokens
   flip at the root and every alias resolves against whichever scheme is in force.

   There are no native `<select>`s on this page and no rules for one. The three
   fields that name an SAP record are NocturnePickers, which the page draws
   itself — several thousand business partners cannot be read by scrolling, and
   the shorter warehouse and cost-centre lists are alpha-coded (KEFGRS, CMTRANS,
   Centr_z4) so they cannot either. Two repairs used to stand at the foot of this
   sheet for the `<select>`s that used to be here — an opaque option fill and an
   ellipsis — and neither has anything left to repair. Do not reintroduce a
   `<select>` without them: see .claude/skills/nocturne-dropdowns-and-dates.
   ========================================================================== */

"""

FOOTER = """

/* ══════════════════════════════════════════════════════════════════════════
   This page's own — rules no source sheet has
   ══════════════════════════════════════════════════════════════════════════ */

/* Tokens the sources above do not declare, added rather than overridden: a
   later block of the same specificity wins for custom properties, so these
   land on the same `.shop` / `.shop-overlay` root the alias block opened.

   The `--npick-*` set tunes the three NocturnePickers' closed triggers to the
   modal's own field metrics, so a picker does not sit two pixels off the text
   input beside it. Only the trigger is tunable — the menu is the same object on
   every page, deliberately. See nocturne-picker.css. */
.shop,
.shop-overlay {
    --shop-shadow-sm: 0 0 0 1px var(--noct-rule);

    --npick-h: 34px;
    --npick-pad-y: 8px;
    --npick-pad-x: 12px;
    --npick-fs: 13px;
    --npick-radius: var(--noct-r-md);
    --npick-fill: transparent;
    --npick-edge: transparent;
    --npick-edge-hi: var(--noct-field-edge);
}

/* The picker's trigger takes the field wash the text inputs carry. Set here
   rather than through `--npick-fill` because that fallback paints the trigger
   only; this also gives a focused one the same accent edge `.shop-input` gets. */
.shop .npick .npick-btn,
.shop-overlay .npick .npick-btn {
    background: var(--shop-field-bg);
}

.shop .npick .npick-btn:focus-visible,
.shop-overlay .npick .npick-btn:focus-visible {
    border-color: rgba(var(--shop-accent-rgb), .55);
}

/* The shop code is not editable once set — it is what sales history groups on —
   so the field says so by looking unavailable rather than only refusing to take
   a keystroke. */
.shop .shop-input:disabled,
.shop-overlay .shop-input:disabled {
    opacity: .6;
    cursor: not-allowed;
}

/* The breadcrumb. Administration is where this page lives and the design says
   so above the title; it is not a link, because there is no Administration
   index to link to. */
.shop-crumb {
    display: flex;
    align-items: center;
    gap: 6px;
    margin-bottom: 7px;
    font-size: 10.5px;
    letter-spacing: .1em;
    text-transform: uppercase;
    color: var(--shop-mute-3);
}

.shop-crumb-here {
    color: var(--shop-accent-text);
}

/* The count on a segment. The segmented control the filter is lifted from
   carries no count, and the count is half of what this filter is for. */
.shop .shop-seg-count {
    margin-left: 6px;
    font-variant-numeric: tabular-nums;
    opacity: .65;
}

/* How many rows the filters left standing, said once at the end of the row
   rather than per segment. */
.shop-result {
    margin-left: auto;
    font-size: 12px;
    color: var(--shop-mute-3);
    font-variant-numeric: tabular-nums;
}

/* The code, as a chip rather than as text. It is the shop's identity — what
   sales history and reporting group on — and the accent family is what this
   system tints an identifier with. */
.shop .shop-code {
    display: inline-flex;
    align-items: center;
    padding: 3px 9px;
    border-radius: var(--shop-r-sm);
    background: rgba(var(--shop-fam-accent-rgb), .13);
    color: var(--shop-fam-accent);
    font-size: 11.5px;
    font-weight: 600;
    letter-spacing: .06em;
    white-space: nowrap;
}

/* The shop cell: the storefront or padlock glyph, then the name. The glyph
   says trading or closed a second time, in the row's first readable column,
   for the same reason the chip says it at the other end. */
.shop .shop-cell-shop {
    display: flex;
    align-items: center;
    gap: 10px;
    min-width: 0;
}

.shop .shop-cell-shop > i {
    flex: none;
    font-size: 15px;
    color: var(--shop-mute-3);
}

/* A cell that pairs a glyph with its figure — the operator count. */
.shop .shop-with-icon {
    display: inline-flex;
    align-items: center;
    gap: 6px;
}

.shop .shop-with-icon > i {
    font-size: 13px;
    color: var(--shop-faint);
}

/* Row actions: quiet until the row is under the pointer, which is the design's
   own treatment. Quiet, not hidden — a control that appears out of nothing is
   one nobody knows is there. Keyboard focus reveals them too, and a device with
   no hover never dims them at all, or they would be unreachable. */
.shop .shop-row-act {
    display: flex;
    align-items: center;
    justify-content: flex-end;
    gap: 2px;
    opacity: .4;
    transition: opacity .12s ease;
}

.shop tbody tr:hover .shop-row-act,
.shop tbody tr:focus-within .shop-row-act {
    opacity: 1;
}

@media (hover: none) {
    .shop .shop-row-act {
        opacity: 1;
    }
}

/* The empty state sits inside the table card, so it drops the border it
   carries when it stands alone — two rings around one box reads as a mistake. */
.shop .shop-table-wrap .shop-empty {
    border: 0;
}

/* Nothing here about the dialog's scrolling or clipping any more, and that is
   the point: it is user-management.css's problem and user-management.css solves
   it. Its overlay is the scrollport, its dialog and body no longer clip, and all
   of that is derived above — which is what lets the business-partner picker's
   menu, 378px of it, hang past the dialog's lower edge instead of being cut off
   at it. See that sheet's Modals section for the reasoning and for what the
   change costs. This block used to restate the whole fix; the restatement went
   away when the source sheet grew the real one.

   One consequence worth naming here rather than there: the overlay scrolls, so a
   picker menu opened near the bottom of the dialog extends the overlay's scroll
   area rather than being clipped, and the overlay grows a scrollbar for as long
   as the menu is open. That is the intended behaviour, not a glitch. */

/* The overlay's scrollbar, which cannot be lifted: in the source it is grouped
   with three lists this page does not have, and the generator keeps a rule only
   when *every* class in its selector is one this page uses. Same declarations,
   on the one selector that applies here. */
.shop-overlay::-webkit-scrollbar {
    width: 8px;
}

.shop-overlay::-webkit-scrollbar-thumb {
    background: var(--shop-thumb);
    border-radius: 8px;
}

.shop-overlay::-webkit-scrollbar-track {
    background: transparent;
}

/* Lifted out of their source sheets by hand because the generator drops every
   at-rule whole — see its note on why a rule taken out of a media query stops
   being conditional. */
@keyframes shop-fade {
    from {
        opacity: 0;
    }
}

@keyframes shop-rise {
    from {
        transform: translateY(8px);
        opacity: 0;
    }
}

@media (prefers-reduced-motion: reduce) {

    .shop-overlay,
    .shop-modal {
        animation: none;
    }
}

/* Narrow screens. Stated here rather than derived, for the same reason. */
@media (max-width: 900px) {
    .shop-form-grid {
        grid-template-columns: minmax(0, 1fr);
    }
}

@media (max-width: 700px) {
    .shop {
        padding: var(--shop-s8) 18px 56px;
    }

    .shop-title {
        font-size: 24px;
    }

    .shop-header-actions {
        width: 100%;
    }

    .shop-header-actions .shop-btn {
        flex: 1;
    }

    .shop-search-box {
        max-width: none;
    }

    .shop-modal-header,
    .shop-modal-body,
    .shop-modal-footer {
        padding-inline: var(--shop-s6);
    }

    /* 16px is the smallest size iOS will render a focused field at without
       zooming the page. */
    .shop .shop-input,
    .shop .shop-search-box input,
    .shop-overlay .shop-input {
        font-size: 16px;
    }
}
"""


def main() -> None:
    chunks: list[str] = []
    owner: dict[str, str] = {}

    for source in SOURCES:
        chunk, defines = lift(source)
        for name in sorted(defines):
            if name in owner:
                raise SystemExit(
                    f"{name} is produced by both {owner[name]} and "
                    f"{source.path.name} — one of them has to give it up, or "
                    f"rename it, or the later rule silently redresses the earlier.")
            owner[name] = source.path.name
        chunks.append(f"{source.note}\n\n{chunk}")

    sheet = HEADER + "\n\n".join(chunks) + FOOTER

    # An animation naming a keyframes block that is not here does not fail, it
    # just never runs — so nothing on screen says the dialog stopped rising.
    # That is exactly what happened until the rename above was added.
    declared = set(re.findall(r"@keyframes\s+([A-Za-z0-9_-]+)", sheet))
    used = {name for value in re.findall(r"animation:\s*([^;]+);", sheet)
            for name in value.split()
            if re.fullmatch(r"[A-Za-z_-][A-Za-z0-9_-]*", name)
            and name not in {"ease", "linear", "both", "forwards", "backwards",
                             "infinite", "alternate", "none", "normal",
                             "ease-in", "ease-out", "ease-in-out"}}
    missing = sorted(used - declared)
    if missing:
        raise SystemExit(
            f"animation names used but never declared: {', '.join(missing)} — "
            f"either rename them into this sheet or add the @keyframes to the footer.")

    # Nothing may resolve a token this sheet does not carry.
    body = re.sub(r"/\*.*?\*/", "", sheet, flags=re.S)
    have = set(re.findall(r"(--[a-z0-9-]+)\s*:", body))
    want = set(re.findall(r"var\((--shop-[a-z0-9-]+)", body))
    want |= set(re.findall(r"rgba\(var\((--shop-[a-z0-9-]+)", body))
    dangling = sorted(want - have)
    if dangling:
        raise SystemExit(f"tokens used but never declared: {', '.join(dangling)}")

    DST.write_text(sheet, encoding="utf-8")
    lines = len(DST.read_text(encoding="utf-8").splitlines())
    print(f"wrote {DST} - {len(owner)} classes from "
          f"{len(SOURCES)} sheets, {lines} lines")


if __name__ == "__main__":
    main()
