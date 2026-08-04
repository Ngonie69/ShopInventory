# NocturneDateField

`ShopInventory.Web/Components/NocturneDateField.razor`, styles in
`ShopInventory.Web/wwwroot/css/nocturne-date-field.css` under the `ndf-` prefix.
Already used 76 times across the pages. Use it; don't build a second one and
don't reach for `<input type="date">`.

## Why it exists

The two things this app needs from a date control can't come from one native
element:

- `<input type="date">` draws its own calendar, which no stylesheet can reach —
  it arrives in the browser's colours, not the design's — and it refuses free
  text, so the shorthand these users type (`030826`) is rejected.
- A plain text input takes the shorthand but has no calendar at all.

So the input is text, the parsing is an explicit format list, and the calendar is
drawn in the component.

## Usage

```razor
<NocturneDateField @bind-Date="fromDate" AriaLabel="From date" />
```

| Parameter | Type | Notes |
|---|---|---|
| `Date` | `DateTime?` | Two-way; use `@bind-Date` |
| `Placeholder` | `string` | Defaults to `dd.MM.yyyy` |
| `Disabled` | `bool` | |
| `Id` | `string?` | For a `<label for>` |
| `AriaLabel` | `string?` | Worth setting — a filter-bar date has no visible label |
| `Class` | `string?` | Extra wrapper classes; see below |

Accepted input formats include `ddMMyy`, `ddMMyyyy`, and `d/M/yy`-style variants
with `.`, `/` and `-` separators, plus `yyyy-MM-dd`. That list came from the
control this replaced, deliberately unchanged — the point is that what people
already type keeps working. Add to the list rather than narrowing it.

## Fitting it to a page

The calendar is meant to be the same object everywhere. The **closed field** is
not: each page sheet sets its own height, type size and edge for the controls in
a filter row, and a date field two pixels taller than the select beside it reads
as a mistake rather than a design.

So the eight values that actually differ are read through `var()` fallbacks, and
a page tunes them from its own root:

```css
.sox {
    --ndf-h: 32px;
    --ndf-fs: 14px;
    --ndf-pad-y: 6px;
    --ndf-pad-x: 10px;
    --ndf-radius: var(--sox-r-md);
    --ndf-fill: var(--sox-field-bg);
    --ndf-edge: var(--sox-field-edge);
    --ndf-edge-hi: var(--sox-field-edge-hi);
}
```

They are fallbacks rather than declarations on `.ndf` on purpose: a value set on
`.ndf` itself would beat one inherited from the page root, and the page could
never win.

The defaults are `audit-trail.css`'s own `.aud-input`, which is what the field
was drawn against — pages sharing that palette need no block at all.

**Nothing below the field is reachable this way, and that is deliberate.** The
popup is the design being standardised. If a page needs the calendar to look
different, that is a conversation about the design, not a page-sheet override.

### Bootstrap pages

Several pages are still Bootstrap rows where the neighbour is a `.form-select`.
Pass `Class="ndf-bs"` — the variant is already in the sheet, carrying the values
from `app.css`'s own `.form-control` rule (**not** stock Bootstrap, which that
rule overrides on padding, type size, radius and edge).

```razor
<NocturneDateField @bind-Date="model.DueDate" Class="ndf-bs" />
```

Measure rather than read if you're fitting a new page: render a real
`.form-select` next to the field in a harness and print both computed boxes. The
stylesheet and the rendered result disagree often enough that this settles it in
one shot.

## Things that bite

**Don't restyle `.ndf-*` from a page sheet.** Use the tuning block. A page sheet
reaching into `.ndf-pop` or `.ndf-day` is the failure this design prevents.

**The field can't see your page tokens directly.** It's a component with its own
`ndf-` prefix, so it reads `--noct-*` and its own fallbacks. Feeding it page
colours means assigning them to `--ndf-*` in the tuning block.

**`z-index: 40` while open.** `.ndf-open` raises the field so the calendar clears
the rows beneath it. If a calendar is appearing behind a sticky footer or a
toolbar, that is the number to reconcile — and check for an `overflow: hidden`
ancestor before changing it.

**Resync guard.** `OnParametersSet` only reassigns from `Date` when the
parameter actually moved, so a re-render mid-edit doesn't overwrite what's being
typed. Preserve that if you touch the component.
