# Moving stock out of a warehouse the tills sell from

The rule for depot controllers and stock controllers. It covers anything that takes stock out of a
warehouse in SAP: inventory transfers (including van loads), goods issues, write-offs and invoices keyed
by hand.

## The warehouses

The ones the tills and vending sell from, which are `DailyStock:ReconcileWarehouses` in the API's
settings:

**KEFSHOP, CORMACH, CORMACH2, KEFGRS, KEFGRC, KEFBYC, KEFBYS**

KEFGRC and KEFBYC need the most care. They are vending depots *and* the depots the vans load from, so
van loads and vending sales draw on the same SAP stock every day.

## Why

A till or vending sale is checked against SAP when it is rung up, printed on a ZIMRA receipt, and
reaches SAP as an invoice about a minute later. If SAP refuses it, it reaches SAP only once someone
fixes it. **Until then, SAP still shows the units that sale sold.** Anything that reads SAP in that gap
sees them as free.

If they are moved, the sale cannot be invoiced: SAP refuses it for stock that has already left, and the
fiscal receipt cannot be taken back. On 2026-09-28 vending sale KEF-FAC-20260928-C35E53C1E13E sold 8
YOG145 at KEFGRC at 13:28. At 13:31 transfer 89450 loaded VAN002 with all 685 that SAP showed,
including those 8. The sale was then refused seven times, and fixing it took a transfer back from the
van.

## The rule

**1. Take stock out of these warehouses through ShopInventory, not the SAP client.**

- Van loads: raise the stock request wherever you like, but **convert it on `/inventory-transfers`**.
  ShopInventory leaves behind whatever the tills have sold and SAP has not invoiced yet. If a request
  would take those units, it is refused and says how many; nothing moves.
- Other transfers and write-offs: use the ShopInventory pages, which run the same check.
- When a conversion is refused for sold units, wait a minute and try again. Sales normally reach SAP
  within the minute. If it is still refused, reduce the line to the figure the message gives.

The SAP client cannot see sales that have not reached SAP, so it cannot do this check. ShopInventory
can.

**2. If you have to use the SAP client** (a document ShopInventory does not offer, or ShopInventory is
down), leave the sold units behind yourself:

1. Open `/desktop-sales`. Set the period to **Last 30 days**, **Warehouse** to the warehouse you are
   moving stock out of, and **Consolidation** to **Awaiting close** and **Failed**.
2. Every sale listed has been sold and is not yet in SAP. Open the sales that contain the items you are
   moving, and add up their quantities per item.
3. For each item, move at most: *what SAP shows* − *that total*.

**3. Never load "everything SAP shows" from these warehouses.** That figure includes units already sold.

**4. A sale showing "SAP has not accepted this sale" still holds its units.** Leave them in the
warehouse until the sale has been fixed and posted, even if that takes days. They are sold.

## If it happens anyway

A sale on `/desktop-sales` reads "SAP has not accepted this sale" with *Insufficient remaining batch
stock*:

1. Run `scripts/diagnose-unpostable-sale.sql` on the API box with the sale's reference. It is read-only.
   Section 10 lists every movement of the sale's items in that warehouse that day, and names the transfer
   that took the stock.
2. In SAP, transfer the short quantity back from where that document sent it into the till's warehouse,
   with a batch. First check the destination still holds that much.
3. Press **Post to SAP** on the sale, and check that it gets an invoice number.

Do not credit or delete the sale. It has a fiscal receipt, so the invoice has to reach SAP.

## What enforces what

| Path | Checked by | Since |
|---|---|---|
| Sale at the till | `CounterSapStockCheck` | 2026-09-16 |
| Transfer request converted in ShopInventory (van loads) | `ConvertTransferRequestHandler` → `IUnpostedTillClaims` | #617 |
| Approved transfer, desktop transfer, transfer queue, write-off, breakage | `StockValidationService.ValidateInventoryTransferStockAsync` | #617 |
| Anything keyed in the SAP client | **This rule only** | — |
