# Shop & Vending sales: what was credited back

Every Shop & Vending sales surface states what was sold, what the office credited back and the net,
so a credited sale reads as a correction rather than a smaller sale. Sold figures (takings,
quantities, rankings, shares, comparisons, margin) are never reduced.

## Sub-features

- `/desktop-sales` — the day card's "Credited back … · Net of credits" line, a credited row's mark
  under its Total ("Credited − …" or "Fully credited"), and the drawer's "N credited" on a line
- `/reports/desktop-sales` — the Takings headline, tender rows, "Who took it" rows, best sellers
  (absent on `/van-sales/reports/sales-breakdown`, which shares the page and reads no credits)
- `/reports/management-sales` — the Takings figure, each day, breakdown, item and item-group row,
  and the item drill-down
- `/reports/desktop-sales/review` — the headline, shop and best-seller rows, large sales, and the
  `credits` finding; opens on **last week**, so pick "This week" for a seed dated today
- `/vending/sales` — a credited settlement's mark, and the month card's note
- Each page's Excel export, the review PDF and email, and the nightly end-of-day email (covered by
  unit tests that reopen the bytes, not by this drive)

## How to get to it (user POV)

Sign in, open Shop & Vending Sales, its Analysis or Review, Management Sales, or Vending → Sales.

## Driving it

`scripts/drive_sales_credits.py` logs in, visits all five pages in light and dark, opens the drawer
of a partly credited sale, and checks the credited item alone is marked.

It needs today's sales with fiscalised credits against them. Seed a scratch API database (never the
dev one): four sales dated today in CAT — two till sales partly and fully credited, one untouched, one
vending settlement partly credited — and one `DesktopCreditNotes` row per credit with
`Status = 'Fiscalised'`. A credit's `PlanJson` names **receipt lines**, which are the sale's lines
ordered by `Id` (`DesktopSaleLineOrder.Receipt`), not by `LineNum`: a multi-row `INSERT … SELECT`
may store them in another order, so read the ids back before writing the plan.

**Proof it worked:** `result.json` with no failures, the drawer check naming exactly the credited
line, and the screenshots showing the sold figure with the credit beneath it.

## Gotchas

- A fresh `goto` takes the app's stored theme; set the theme again after re-navigating.
- Headless shots render Phosphor icons as empty boxes until the font arrives — not a missing icon.
