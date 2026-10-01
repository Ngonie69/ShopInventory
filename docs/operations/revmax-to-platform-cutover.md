# REVMax to fiscalisation platform cut-over

ZIMRA issued the in-house platform (<https://fiscal.kefaloscheese.com/>) three live **Online**
devices in September 2026: 46668, 46669 and 46670, all "Virtual Server v1". This page is the order of
work for moving ShopInventory's filing from REVMax (device 22862) to the platform. The design is in
`architecture.md` under *Fiscalisation*.

The switch is `Fiscalisation:Provider=Platform`, and appsettings.json sets it. **Merging the change is
the go-live.** `deploy-production.yml` deploys `main` at 19:30 CAT, so do not merge until every item
under *Before merging* is done.

## What changes, and what does not

| | Before | After |
|---|---|---|
| New invoices, till, vending and van sales | REVMax | Platform |
| A SAP document dated on or before `Revmax:LastFilingDate` | REVMax | Checked on REVMax first. Adopted if REVMax holds it, otherwise filed on the platform |
| Credit note against a REVMax invoice | REVMax | REVMax |
| Credit note against a platform invoice | n/a | Platform |
| Till credit note (desktop credit) | REVMax | Where the sale was filed: the platform for its own sales, REVMax for sales REVMax filed |
| Invoice status read-back | REVMax | Platform, then REVMax |
| Handset tax table (van lease) | REVMax ids | Platform ids. No handset signs, because the devices are Online |

REVMax stays switched on after the cut-over and files nothing new. Its licence runs to **21 Nov 2026**.

## Before merging

1. **All three devices registered in Production.** Register them on the platform's Device Registration
   page. The activation keys expire **2026-10-01 15:09**. Check that the platform's device list for this
   taxpayer holds only the live devices. ShopInventory leaves `DefaultDeviceId` at 0, and 0 walks every
   device the platform has configured.
2. **Live tax ids.** Read `GET /api/fiscal-config?deviceId=46668` with an API key that has `device.read`.
   ShopInventory now declares **515** for the 15.5% groups (O01, O1, O8) and **2** for zero-rated (O0).
   Those are the ids this taxpayer's REVMax receipts declare on live FDMS. 517 was the ZIMRA *test*
   service's id. If the live config differs, fix `Fiscalisation:TaxIdMappings` and `DefaultTaxId` in
   appsettings.json before merging. Also set the platform's own `SapServiceLayer:TaxMappings` in its
   deployed config. The repo ships them empty with `DefaultTaxId: 1`, and that path files every SAP
   invoice the platform's bridge prints.
3. **API key.** On the platform's API Keys page, issue a key with `receipt.submit`, `sap.fiscalise` and
   `device.read`, and **no device allowlist**. Enter it on ShopInventory's Settings → Fiscalisation,
   which writes `Fiscalisation__ApiKey` into web.config, then press *Test connection*.
4. **Fiscal day.** ShopInventory never opens or closes a day for the Online devices. Confirm that the
   platform's `FiscalDayAutomationJob` is running in Production and opens and closes each device's day.
5. **Drain REVMax's queue.** Under REVMax, run the SQL below. Every pending or failed till and vending
   sale should be fiscalised or explained before the switch. After the switch they can still be
   retried, because a retry asks the platform and then REVMax. But a sale REVMax cannot answer for is
   held back until it can.

   ```sql
   -- Till and vending sales not yet fiscalised (0 = Pending, 2 = Failed)
   SELECT "SourceSystem", "FiscalizationStatus", count(*), min("CreatedAt"), max("CreatedAt")
   FROM "DesktopSales"
   WHERE "FiscalizationStatus" IN (0, 2)
   GROUP BY 1, 2;

   -- Van rows the signed-receipt ingest would start submitting under the platform. Expect none.
   SELECT "ReceiptIngestStatus", count(*), min("CreatedAt"), max("CreatedAt")
   FROM "DesktopSales"
   WHERE "SourceSystem" IN ('KefalosVanSales', 'KefalosVanSalesOnline')
     AND "ReceiptIngestStatus" IN (1, 3, 4, 5)
   GROUP BY 1;
   ```

   Any rows in the second query were signed on handsets during August's platform trial against the
   test service. Mark them or leave them, but know that the ingest will try them.

## On the day

1. **Set `Revmax__LastFilingDate`** in web.config to the cut-over date (`yyyy-MM-dd`), in both slots.
   Documents dated on or before it are checked against REVMax. Documents dated after it are not, so a
   REVMax outage cannot hold them up. Left blank, every document is checked.
2. **Switch off the REVMax vendor's SAP B1 add-on on every SAP workstation.** It files invoices straight
   from the SAP client and writes nothing to ShopInventory. If it keeps running after the cut-over date,
   invoices it files are invisible to the date check and can be filed a second time on the platform.
   Where users fiscalise from SAP, install the platform's SAP bridge in its place.
3. **Merge**, and let the 19:30 deploy take it.
4. **Check the startup log** for the line
   `Fiscalisation provider: the platform at https://fiscal.kefaloscheese.com/. REVMax at … files nothing new`.
   It should also give the date from step 1. `/api/health` has no fiscal-provider check, so this has to
   be read on the server. `scripts/Check-PlatformCutover.ps1` does it read-only on the API box: it prints
   each slot's `Fiscalisation__`/`Revmax__` overrides (secrets as their length only), this log line and
   any fiscal warnings, and what has been filed since the cut-over by device. 22862 is REVMax; 46668–46670
   are the platform.
5. **Smoke test.** Fiscalise one till sale and one SAP invoice. Check that each appears on the
   platform's receipt list, and that the invoice PDF prints the QR code. Open one REVMax-era invoice and
   check that it still reads *Fiscalised* with its REVMax receipt number.

## Rollback

Set `Fiscalisation__Provider=Revmax` in web.config and recycle the pool. This is a last resort. REVMax
has never seen the receipts the platform filed in between, so the same double-filing risk now runs the
other way. Do not re-fiscalise anything dated after the cut-over while rolled back. If the platform has
filed nothing yet (section 3 of `scripts/Check-PlatformCutover.ps1` is empty), that risk does not apply.

`scripts/Set-FiscalProviderOverride.ps1 -Provider Revmax` makes the edit. Run it on the API box as an
administrator, with `-DryRun` first. It edits both API slots, because a deploy seeds the new slot from
the live slot's web.config. Each file is backed up next to itself. It then recycles the live slot's
pool and waits for the startup line `Fiscalisation provider: REVMax at …`. `-Provider Platform`
removes the override again once `Fiscalisation__ApiKey` is set.

## Retiring REVMax

Set `Revmax__Enabled=false` once no REVMax receipt is left to credit and nothing from before the switch is
pending. The plain platform service takes over and REVMax is never asked again. Do this **before
21 Nov 2026**, when the licence ends. After that, a credit note against a REVMax invoice is refused
(RCPT032), because the platform only credits originals in its own archive. Till credit notes on sales
the platform filed are unaffected; those on REVMax-filed sales are refused with "raise the credit note
in SAP". Lifting that needs a change
to the platform that lets it reference a receipt another device filed.
