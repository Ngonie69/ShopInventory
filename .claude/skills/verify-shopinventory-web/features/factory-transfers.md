# Factory transfers

Production releasing stock to dispatch, and dispatch returning it, through the holding warehouse FSRW.
The sender's post moves the stock into FSRW at once; somebody holding the destination warehouse counts
it and accepts, part-accepts or rejects it, which moves it out again. Every action is limited to the
warehouses on the account.

## Sub-features

- Queue under three tabs: Waiting for me (`view=awaiting`), Open, All; search by number, sender,
  warehouse or item
- Send drawer: From (`NocturneSelect`, the account's production or dispatch warehouses), To (fixed when
  there is one, a select for a dispatch return to a two-warehouse department), reason, products from a
  `NocturnePicker` over the source warehouse's stock
- Open drawer: lines with the batches SAP moved, a count stepper per line, Accept / Accept *n* of *m*
  (a short count needs a note), Reject (needs a note), Recall (sender only), Retry, Cancel send

- Depot trucks: a DistributionOfficer picks "Load a truck for a depot" in From, a depot in To, then
  truck registration and driver (both required). An open depot order fills the lines from the dispatch
  warehouse holding most of each item. The DepotController for that depot accepts it; short lines go
  back to each line's own dispatch warehouse. A depot can also return stock from its centre or shop.

## How to get to it (user POV)

Sign in as a `ProductionOfficer` or `DispatchOfficer` and you land on `/factory-transfers`; Admin finds
it under Inventory. Both factory roles need warehouses on the account (Users → edit → Assigned
Warehouses). The dispatch manager holds `factory.transfers.accept_any` on the account. A
DistributionOfficer lands here too; a DepotController keeps its dashboard and finds the link under Inventory.

## Driving it with cdp.py

The real API posts both legs to SAP, so drive the Web against a stub API (see the
`stub-api-drives-web-without-login` memory). The stub needs `api/auth/login`, `api/auth/me`, the eight
`api/factory-transfers` routes and `api/stock/warehouse/{wh}` for the picker. Use one browser per user:
there is no `/logout` route.

```python
c.click("button.fxt-btn-primary")             # New transfer
c.eval("document.querySelector('.fxt-form .nsel-btn').click()")   # From
# pick ".nsel-item" by text, then ".npick-btn" and ".npick-item" by item name
c.type_into("input[aria-label='Quantity of YOG500']", "20")
c.type_into("#fxt-reason", "End of shift 2 release")
c.click(".ops-drawer-foot .fxt-dconfirm")
```

**Proof it worked:** the snackbar names the new `FT-` number, the row reads "waiting for DISPYOG" for
the sender and "yours to count" for the receiver, and the stub's `/__requests` log shows the send body
(source, destination, reason, a `clientRequestId`) and then the decision body with the counted
quantities. After a short accept the drawer reads Part-accepted.

## Gotchas

- `Value="x"` on a string component parameter is the literal text "x" — bind with `Value="@x"`. The
  first build of this page had both pickers bound to their own field names.
- The API's half (scope, routes, two legs, lost-reply adoption) is proven by `FactoryTransferTests`,
  not by this page; a stub cannot show it.
- Depot flow: `scratchpad/verify/drive_depot.py` in the session that built it. The stub also needs
  `api/factory-transfers/depot-orders` and stock for DISPYOG and DISPICE. Its stock sync writes into
  `CachedWarehouseStocks`, so delete the stub rows afterwards.
