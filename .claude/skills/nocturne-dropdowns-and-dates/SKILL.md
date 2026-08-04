---
name: nocturne-dropdowns-and-dates
description: >-
  How to build dropdowns, select/filter controls, and date pickers in the
  ShopInventory.Web Nocturne UI so they survive dark mode and match the rest of
  the app. Use this whenever you are adding, restyling, or fixing any picker,
  dropdown, combo box, filter bar control, `<select>`, `<option>`, status/role
  chooser, month or date input, calendar popup, or `<input type="date">` in a
  Razor page or a page stylesheet under `ShopInventory.Web` — and also when the
  report is a symptom rather than a control name: "the dropdown options are
  invisible", "I can't read the list", "white on white", "the calendar looks
  like the browser's", "this control looks generic / looks nothing like the
  rest of the page". Consult it before writing the markup, not after, because
  the choice between a native element and a custom listbox is the whole
  decision and it is expensive to reverse.
---

# Nocturne dropdowns and date pickers

Two controls in this app cannot be built the obvious way. A native `<select>`
popup and a native `<input type="date">` calendar are **platform windows**: the
browser draws them outside the page, so no page stylesheet reaches inside them.
In a dark-themed app that is not a cosmetic problem — it is how controls end up
unreadable and how a page ends up with one widget that looks like Windows sitting
next to twenty that look like Nocturne.

This skill is the decision rule and the two recipes.

## Pick the control first

| What you're building | Use |
|---|---|
| Any date or month input | `<NocturneDateField>` — never `<input type="date">` |
| A dropdown that is a visible page control (filter bar, toolbar, table header) | `<NocturneSelect>` |
| A dropdown whose items carry meaning beyond their text — status, role, a colour, an icon, a count | `<NocturneSelect>` |
| A plain dropdown buried in a modal form, few options, text only | Native `<select>` is fine — **but style its `option`s** |

The native `<select>` row is not a grudging exception. 58 of them are still in
use in modal forms, and rewriting those would be churn for its own sake. The
line is whether the control is something the user *looks at* while scanning the
page, or something they only meet while filling in a form.

Every page control on the right side of that line has been converted — the sweep
of 2026-08-04 replaced 82 of them across 39 pages — so a native `<select>` in a
filter bar or a toolbar today is a regression, not a leftover.

## The bug that drives all of this

A native `<select>` popup takes its background from the field's own
`background-color`. Nocturne fields are a translucent wash —
`--noct-field-bg` is `rgba(243, 245, 254, .05)` in dark theme. The browser
composites that 5%-white onto **white**, not onto the page, and having done so
it ignores `color-scheme: dark` for the popup. So you get a white sheet, while
the `<option>`s inherit the field's light `--noct-ink`. Near-white ink on a
near-white sheet: the list is there, focusable, and invisible.

`color-scheme` on the page root does not save you, and neither does the page
being visibly dark around it — that is what makes this one hard to spot by
reading the stylesheet. It also means **it never reproduces in light theme**, so
a screenshot in light mode proves nothing.

A bare `<select>` with no page-sheet styling is **not** affected: `app.css:3120`
gives it an opaque `#1e293b` in dark theme. The bug needs a page sheet that
repaints the field with the wash — which is what every Nocturne sheet does.
Two lines fix it:

```css
.<page> .<page>-select option,
.<page> .<page>-select optgroup {
    color: var(--<page>-ink);
    background-color: var(--<page>-surface);
}
```

`--surface` rather than `--field-bg` on purpose: the popup needs an **opaque**
fill, and surface is the token that is opaque in both themes.

`scripts/check_selects.py` finds pages that still need it. It resolves each
`<select>`'s actual fill through the token aliases rather than grepping for the
word `option`, so its hits are worth acting on rather than triaging. Run it from
the repo root:

```bash
python .claude/skills/nocturne-dropdowns-and-dates/scripts/check_selects.py
```

Every page it knew about has been fixed, so a clean run is the expected result —
a new hit means a sheet was added or a field was repainted. It also flags any
page that has gone back to `<input type="date">`.

Fixing the page you're working on is always in scope. If the script turns up
others, say so rather than silently widening the change.

## Building the listbox

Don't. Use `<NocturneSelect>` — `ShopInventory.Web/Components/NocturneSelect.razor`,
styled in `wwwroot/css/nocturne-select.css` under the `nsel-` prefix. It is the
same shape `references/dropdown.md` describes, written once instead of per page,
and it is what all 82 converted controls use.

```razor
<NocturneSelect Class="nsel-block" TValue="string"
                Options="StatusFilterOptions" @bind-Value="statusFilter"
                OnChanged="ApplyFilters" Caption="Filter by status"
                AriaLabel="Filter by status" Placeholder="All statuses" />
```

```csharp
private static readonly NocturneSelectOption<string>[] StatusFilterOptions =
[
    new(string.Empty, "All statuses", "neutral") { RuleAfter = true, IsUnset = true },
    new("1", "Pending",  "warn"),
    new("2", "Approved", "good"),
    new("7", "Rejected", "bad")
];
```

The five things to get right at the call site:

- **`Family`** is one of `neutral | accent | info | good | warn | bad`. Feed it
  from the page's *existing* status→tone helper so the swatch in the menu and the
  dot in the results row cannot drift apart. Leave it null where the items carry
  no meaning beyond their text — a warehouse, a currency, a page size.
- **`IsUnset`** on the "All …" row wherever the page's no-filter sentinel is a
  word rather than an empty string — `"all"`, `"All"`, `0`. Without it the
  trigger sits in its accent "a filter is set" state from first paint. Empty
  string and null are handled for you, and are treated as the same value.
- **`OnChanged`** is the reload/paging-reset hook — the `@bind:after` equivalent.
  Use it rather than doing the work inside a `ValueChanged` lambda.
- **`Class`**: `nsel-block` to fill a filter cell, `nsel-sm` for a rows-per-page
  control, `nsel-end` to right-anchor the menu on a trailing control, `nsel-auto`
  to shrink to content, `nsel-bs` on the pages still built out of Bootstrap rows.
- **Passing a method group to `ValueChanged` does not compile.** TValue inference
  fails and the error names the non-generic `EventCallback`, which points nowhere
  near the cause. State `TValue` explicitly and wrap the handler in a lambda:
  `TValue="int" ValueChanged="@(days => OnRangeChanged(days))"`.

Fitting it to a page is a `--nsel-*` block on the page root, next to the sheet's
existing `--ndf-*` one; the sheets on audit-trail's palette need no block at all.
See the header of `nocturne-select.css`.

`references/dropdown.md` is now background — the anatomy, and why the component
is built the way it is. Read it if you are changing `NocturneSelect` itself, not
to write a new control.

The four principles below are why the component looks the way it does. They are
now settled inside it — you get all four for free — so read them to understand a
change to `NocturneSelect`, not to reimplement them.

**Close it with a veil, not a document listener.** A `position: fixed; inset: 0`
transparent div rendered behind the menu catches the outside click. It costs one
element and keeps the whole control inside Blazor — no JS interop to register per
render and unregister on dispose. `NocturneDateField` uses `.ndf-veil` for the
same reason; the pattern is already established here.

**Let the items carry the page's own semantics.** This is the payoff for leaving
the native control behind, and skipping it wastes the whole exercise. The six
families are named once in `nocturne-select.css` and every swatch, tick and
chosen-row wash reads `var(--nsel-fam)`, so the only way they drift is if a call
site invents its own mapping instead of feeding `Family` from the helper the
results table already uses.

**Give "open" and "chosen" separate looks.** Open is transient (an accent ring);
chosen persists after the menu shuts (accent ink in the trigger). A user
scanning the filter bar needs to see which filters are set without opening any
of them.

**Carry the infrastructure with the control.** The popup animation, the
`::-webkit-scrollbar` rules, the narrow-screen anchoring and the
`prefers-reduced-motion` block all live in `nocturne-select.css`, so a page that
adopts the component cannot forget one of them — which is what used to leave a
new menu animating after everything else on the page had agreed not to.

## Date fields

`<NocturneDateField>` already exists and is used 76 times. Full parameter list
and the per-page tuning block are in `references/datefield.md`.

The short version:

```razor
<NocturneDateField @bind-Date="fromDate" AriaLabel="From date" />
```

It exists because the two things this app needs from a date control can't come
from one native element: `<input type="date">` draws an unstyleable calendar and
refuses free text, while a plain text input takes the shorthand people actually
type (`030826`) but has no calendar. So the input is text, the parsing is a
format list, and the calendar is drawn in the component.

If the field looks a couple of pixels off next to the control beside it, that is
expected and there is a supported fix — set the `--ndf-*` fallbacks from your
page root (`.sox { --ndf-h: 32px; --ndf-fs: 14px; }`). Don't restyle `.ndf-*`
from a page sheet: the closed field is meant to fit the page, the calendar is
meant to be identical everywhere, and page-level overrides erase that line.

## Before you call it done

You cannot screenshot the running app — it sends `frame-ancestors 'self'`, so
both browser tools are refused. Build a static harness and shoot it with headless
Chrome; `references/verifying.md` has the recipe and the flags that stop it
lying to you.

Check, in this order:

1. **Dark theme, menu open.** This is the only state the original bug appears in.
2. **Light theme, menu open.** The tokens flip; translucent fills that read fine
   on a dark ground can vanish on a white one.
3. **A long option.** `MerchandiserPurchaseOrderViewer` is a real role in this
   app and a good stress test for `max-width` and ellipsis.
4. **The narrow breakpoint.** A left-anchored menu on the last control in a
   filter bar runs off the right edge; anchor it right below ~700px.

A `dotnet build ShopInventory.Web/ShopInventory.Web.csproj` catches the Razor
mistakes. It will not catch any of the four above.
