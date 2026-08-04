# The Nocturne listbox

A working implementation to adapt. Live version: the role and status filters in
`ShopInventory.Web/Components/Pages/UserManagement.razor`, styles under
`── Filter dropdowns ──` in `ShopInventory.Web/wwwroot/css/user-management.css`.

Substitute your page's prefix for `um` throughout. The classes below assume the
page sheet already aliases the Nocturne tokens onto `--um-*`, which every page
sheet does in its opening block.

## Contents

- [Anatomy](#anatomy) — what the elements are and why each exists
- [Markup](#markup)
- [Blazor state](#blazor-state)
- [CSS](#css)
- [Wiring into the sheet's existing blocks](#wiring-into-the-sheets-existing-blocks)
- [Things that bite](#things-that-bite)

## Anatomy

```
.um-dd                     position: relative — the menu anchors to this
├── .um-dd-btn             the trigger; a button, not a div
│   ├── .um-dd-icon        leading glyph, tints when a filter is set
│   ├── .um-dd-value       the current label, ellipsised
│   └── .um-dd-caret       rotates 180° while open
├── .um-dd-catch           fixed inset-0 veil; catches the outside click
└── .um-dd-menu            role="listbox"
    ├── .um-dd-caption     "Filter by role" — orients, and gives the menu a top edge
    ├── .um-dd-item        role="option"
    │   ├── .um-dd-dot     swatch, colour from the item's own family class
    │   ├── .um-dd-label
    │   └── .um-dd-tick    opacity 0 until .selected
    └── .um-dd-rule        optional separator, e.g. under "All"
```

The trigger is a `<button type="button">`. Inside an `EditForm` a bare `<button>`
defaults to `type="submit"` and will submit the form when someone picks an
option — a bug that only shows up in the modal that happens to wrap it.

## Markup

```razor
<div class="um-dd @(openFilterMenu == "role" ? "open" : "")" @onkeydown="OnFilterMenuKeyDown">
    <button type="button" class="um-dd-btn @(string.IsNullOrEmpty(roleFilter) ? "" : "set")"
            @onclick="@(() => ToggleFilterMenu("role"))"
            aria-haspopup="listbox" aria-expanded="@(openFilterMenu == "role")">
        <i class="bi bi-person-badge um-dd-icon"></i>
        <span class="um-dd-value">@RoleFilterLabel</span>
        <i class="bi bi-chevron-down um-dd-caret"></i>
    </button>
    @if (openFilterMenu == "role")
    {
        <div class="um-dd-catch" @onclick="CloseFilterMenu"></div>
        <div class="um-dd-menu" role="listbox" aria-label="Filter by role">
            <div class="um-dd-caption">Filter by role</div>
            <button type="button"
                    class="um-dd-item um-badge-user @(string.IsNullOrEmpty(roleFilter) ? "selected" : "")"
                    role="option" aria-selected="@string.IsNullOrEmpty(roleFilter)"
                    @onclick="@(() => SelectRoleFilter(string.Empty))">
                <span class="um-dd-dot"></span>
                <span class="um-dd-label">All Roles</span>
                <i class="bi bi-check2 um-dd-tick"></i>
            </button>
            <div class="um-dd-rule"></div>
            @foreach (var role in availableRoles)
            {
                var r = role;
                <button type="button"
                        class="um-dd-item @GetRoleBadgeClass(r) @(roleFilter == r ? "selected" : "")"
                        role="option" aria-selected="@(roleFilter == r)"
                        @onclick="@(() => SelectRoleFilter(r))">
                    <span class="um-dd-dot"></span>
                    <span class="um-dd-label">@r</span>
                    <i class="bi bi-check2 um-dd-tick"></i>
                </button>
            }
        </div>
    }
</div>
```

`var r = role;` is not decoration. Without the copy, every lambda in the loop
closes over the same iteration variable and each item selects the last role.

`@GetRoleBadgeClass(r)` is the page's existing badge-class helper — the same one
the results rows call. Reusing it is what keeps the swatch and the badge the same
colour without a second mapping to maintain.

## Blazor state

```csharp
// Custom listboxes rather than <select>s: a native popup takes its background
// from the field, and this page's fields are a 5%-white wash, which the browser
// composites onto white — so the dark-theme options rendered near-white on
// near-white.
private string? openFilterMenu = null;

private static readonly (string Value, string Label, string Family)[] StatusFilterOptions =
{
    (string.Empty, "All Status", "um-badge-user"),
    ("active",     "Active",     "um-badge-active"),
    ("inactive",   "Inactive",   "um-badge-inactive"),
    ("locked",     "Locked",     "um-badge-locked")
};

private string StatusFilterLabel =>
    StatusFilterOptions.FirstOrDefault(o => o.Value == statusFilter).Label ?? "All Status";

private void ToggleFilterMenu(string menu)
{
    openFilterMenu = openFilterMenu == menu ? null : menu;
    openMenuUserId = null;   // only one popup at a time
}

private void CloseFilterMenu() => openFilterMenu = null;

private void OnFilterMenuKeyDown(KeyboardEventArgs e)
{
    if (e.Key == "Escape")
    {
        openFilterMenu = null;
    }
}

private async Task SelectRoleFilter(string role)
{
    openFilterMenu = null;
    if (roleFilter == role)
    {
        return;
    }

    roleFilter = role;
    currentPage = 1;        // a new filter means a new result set
    await LoadUsers();
}
```

One `string?` holds which menu is open rather than a `bool` per dropdown. It
makes "only one open at a time" the default instead of something you have to
remember in every handler.

The options tuple keeps value, label and family together. Three parallel
switches over the same four statuses is where a label and a colour drift apart.

Escape is handled on the wrapper `<div>`, not the button. The div isn't
focusable, but keydown bubbles up from the trigger, which is what holds focus
while the menu is open.

## CSS

```css
.um-dd {
    position: relative;
    display: inline-flex;
}

.um-page .um-dd-btn {
    display: inline-flex;
    align-items: center;
    gap: var(--um-s2);
    width: 100%;
    min-width: 156px;
    min-height: 34px;
    padding: 6px 12px;
    font: inherit;
    font-size: 13px;
    line-height: 1.3;
    color: var(--um-mute);
    background: var(--um-field-bg);
    border: 1px solid transparent;
    border-radius: 999px;
    cursor: pointer;
    text-align: left;
    transition: border-color .15s, background-color .15s, color .15s;
}

.um-page .um-dd-btn:hover {
    border-color: var(--um-field-edge);
    color: var(--um-ink);
}

.um-page .um-dd-btn:focus-visible {
    border-color: rgba(var(--um-accent-rgb), .55);
    outline-offset: 0;
}

/* Open and chosen are different states and read differently: open is the accent
   ring, chosen is the accent ink that persists after the menu shuts. */
.um-dd.open .um-dd-btn {
    color: var(--um-ink);
    background: var(--um-hover);
    border-color: rgba(var(--um-accent-rgb), .55);
}

.um-page .um-dd-btn.set {
    color: var(--um-accent-text);
    background: rgba(var(--um-accent-rgb), .12);
}

.um-dd-icon {
    flex: none;
    font-size: 12px;
    color: var(--um-mute-3);
    transition: color .15s;
}

.um-page .um-dd-btn.set .um-dd-icon,
.um-dd.open .um-dd-icon {
    color: var(--um-accent);
}

.um-dd-value {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    white-space: nowrap;
    text-overflow: ellipsis;
}

.um-dd-caret {
    flex: none;
    font-size: 9px;
    color: var(--um-mute-3);
    transition: transform .18s ease, color .15s;
}

.um-dd.open .um-dd-caret {
    transform: rotate(180deg);
    color: var(--um-accent);
}

/* Outside-click target. A transparent sheet under the menu costs nothing and
   keeps the close entirely in Blazor — no document listener to register per
   render and unregister on dispose. */
.um-dd-catch {
    position: fixed;
    inset: 0;
    z-index: 60;
}

.um-dd-menu {
    position: absolute;
    top: calc(100% + 6px);
    left: 0;
    z-index: 61;               /* above the catch */
    min-width: 100%;           /* never narrower than its trigger */
    width: max-content;        /* but as wide as its longest option */
    max-width: 272px;
    max-height: 336px;
    overflow-y: auto;
    padding: var(--um-s2);
    display: flex;
    flex-direction: column;
    gap: 1px;
    border: 1px solid var(--um-divider);
    border-radius: var(--um-r-lg);
    background: var(--um-surface);
    box-shadow: var(--um-shadow-pop);
    animation: um-rise .14s ease-out;
}

.um-dd-caption {
    padding: 5px 10px 6px;
    font-size: 10px;
    font-weight: 600;
    letter-spacing: .07em;
    text-transform: uppercase;
    color: var(--um-faint);
}

.um-page .um-dd-item {
    display: flex;
    align-items: center;
    gap: var(--um-s3);
    width: 100%;
    padding: 7px 10px;
    font: inherit;
    font-size: 12.5px;
    color: var(--um-mute);
    background: transparent;
    border: 0;
    border-radius: var(--um-r-sm);
    cursor: pointer;
    text-align: left;
    white-space: nowrap;
    transition: background-color .12s, color .12s;
}

.um-page .um-dd-item:hover {
    background: var(--um-hover);
    color: var(--um-ink);
}

/* The family comes from the item's own `um-badge-*` class, so this is the one
   place the swatch is described and every role is covered by it. */
.um-dd-dot {
    flex: none;
    width: 7px;
    height: 7px;
    border-radius: 50%;
    background: var(--um-fam, var(--um-mute-3));
    box-shadow: 0 0 0 3px rgba(var(--um-fam-rgb, 94, 100, 131), .16);
}

.um-dd-label {
    flex: 1;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
}

.um-dd-tick {
    flex: none;
    font-size: 11px;
    opacity: 0;
    color: var(--um-fam, var(--um-accent));
}

.um-page .um-dd-item.selected {
    color: var(--um-ink);
    background: rgba(var(--um-fam-rgb, 94, 100, 131), .12);
}

.um-page .um-dd-item.selected .um-dd-tick {
    opacity: 1;
}

.um-dd-rule {
    height: 1px;
    margin: var(--um-s2) var(--um-s2) calc(var(--um-s2) - 1px);
    background: var(--um-rule);
}
```

The two-classes-deep selectors (`.um-page .um-dd-btn`, 0-2-0) are not stylistic.
`app.css` carries a global `.dark-theme input, .dark-theme select, .dark-theme
button` block at (0,1,1) that repaints controls `#1e293b`, and MudBlazor's sheet
loads last. Specificity is what wins here, not source order.

## Wiring into the sheet's existing blocks

Four one-line additions, each to a list that is already in the sheet. Skipping
them is how a new control ends up subtly out of step with everything else.

```css
/* reduced motion — the menu should stop animating when the modals do */
@media (prefers-reduced-motion: reduce) {
    .um-overlay,
    .um-modal,
    .um-dd-menu,          /* ← */
    .um-spinner { animation: none; }

    .um-dd.open .um-dd-caret { transition: none; }
}

/* scrollbars — a long list scrolls, and a default scrollbar is very visible */
.um-modal-body::-webkit-scrollbar,
.um-dd-menu::-webkit-scrollbar { width: 8px; }
/* …and the -thumb and -track rules alongside it */

/* narrow screens */
@media (max-width: 700px) {
    .um-filter-bar .um-dd { flex: 1; min-width: 0; }
    .um-page .um-dd-btn { min-width: 0; }

    /* Right-anchor the trailing filter so its menu can't run off the edge. */
    .um-filter-bar .um-dd:last-of-type .um-dd-menu { left: auto; right: 0; }

    /* 16px is the smallest size iOS renders a focused field at without zooming. */
    .um-page .um-dd-btn { font-size: 16px; }
}
```

## Things that bite

**The trigger submits the form.** `<button>` inside an `EditForm` is
`type="submit"` by default. Every button here needs `type="button"`.

**Every item picks the last option.** The `foreach` variable was captured by the
lambda instead of copied. `var r = role;`.

**The menu is behind something.** The catch veil is `z-index: 60` and the menu
`61`. Any other popup on the page — a row's overflow menu is usually `z-index:
40` — needs to be closed when this one opens, or it will sit under the veil and
stop responding to clicks while looking perfectly normal.

**The menu is clipped.** An ancestor with `overflow: hidden` crops an absolutely
positioned child. This app has a live instance of that failure mode: `hidden` on
`.mud-main-content` turned it into a scrollport and broke every page's sticky
header. If a menu is cut off at a container edge, look up the tree before
touching the menu.

**The filter doesn't reset the page.** Changing a filter while on page 3 leaves
you on page 3 of a different result set. Set `currentPage = 1` in the handler —
the search handler on these pages already does, and the selects were usually the
ones that forgot.

**The page's `min-height` leaks into the menu.** Page roots are
`min-height: 100vh`. Any wrapper that repeats the page root class to inherit its
tokens must add `min-height: 0`, or it hangs a viewport tall.
