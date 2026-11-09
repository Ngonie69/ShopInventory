# SAP SQLQueries: census and cleanup

Every SQL statement the API runs in SAP is stored there as a `SQLQueries` object. Until July 2026
the API made a new random code per request and tried to delete it afterwards. On this Service Layer
`DELETE SQLQueries('code')` never finishes (HTTP 502 at 300 s, the row survives), so about 4,100
objects leaked. Writes to the entity set slow down as it grows: creating one took 30-43 s at ~4,300
rows (2026-07-27), while reads stayed fast.

The leak itself is fixed. Codes are now content-addressed, `family_<first 12 hex of SHA-256 over the
statement>` (`BuildContentAddressedQueryCode` in `SAPServiceLayerClient.cs`, commit `8235dcb`), and
per-request values are bound as `:name` parameters. A code review on 2026-11-09 found no call site
that still puts per-request values into the statement text. What remains are families that are
bounded but not tiny: `STK_ITEMS`, `WHS_BATCHES_<warehouse>`, `WHS_ITEM_CODES_<warehouse>`,
`PKG_STK` and `SH_PL<list>` make one object per warehouse × item-code family (`SqlItemCodePrefixCover`),
so they grow towards a ceiling of warehouses × families rather than without bound.

## Census

`scripts/Get-SapSqlQueryCensus.ps1`, run on a production API server in Windows PowerShell 5.1:

```powershell
.\scripts\Get-SapSqlQueryCensus.ps1
```

It reads the `SAP__*` settings from the live API slot's `web.config`, pins the certificate to
`SAP__AllowedServerCertificateThumbprints` exactly as the API does, logs in as the API's user, reads
`SQLQueries` page by page, and logs out. It runs no SQL and changes nothing in SAP. It writes a CSV
of every code beside itself (`scripts\sap-sqlqueries-census\`, git-ignored) and compares with the
previous CSV there, so **run it twice, a week apart**, to get a growth rate.

Each code is put in one bucket:

| Bucket | Meaning |
|---|---|
| `legacy random` | A random suffix: 6 digits, a tick count, or a GUID fragment. No current code builds one, so nothing will ask for these again. These are the cleanup candidates. |
| `content-addressed` | The stored text hashes to the code, so the API made it. Either in use, or an older version of a statement that has since changed (dead too, but not provably). |
| `unverified hex` | A `_<12 hex>` code made on or after 2026-08-01 whose text does not hash to it. SAP trims trailing spaces from each stored line, which can do this to a real one. The census lists them; leave them. |
| `fixed` | A constant code such as `ITEM_BATCHES_V1`. In use. |

Old builds also made `prefix_<12 uppercase hex>` codes from GUIDs, which look exactly like
content-addressed ones. Only hashing the stored text separates them, so the census reads `SqlText`
too (a few MB). `-SkipTextCheck` skips that and reports those codes as `content-addressed?`.

What to read first:

- **`created recently`**: objects created in the last 1, 7 and 30 days, by family. A family that
  keeps gaining objects week after week is a leak, and a code fix, not a cleanup.
- **`by kind`**: how many `legacy random` objects exist. That is the most a cleanup could remove.
- **`Paged read`**: read times, a baseline to compare before and after any cleanup.

## Cleanup: decide after two census runs

Do **not** build cleanup on `DELETE SQLQueries('code')`. It was measured not to work: the gateway
kills it at 300 s and nothing commits. Each attempt also ties up one of the API's six SAP slots.

The options, cheapest first:

1. **Leave them.** If the second census shows no family still growing, the leaked rows cost time
   only when the API creates or rewrites a query object. That now happens once per new statement
   (typically after a deploy that changes SQL), not per request. The cost is real but small and
   bounded.
2. **SAP-supported removal.** Ask the SAP partner how to remove `SQLQueries` objects in bulk on this
   Service Layer build, and whether `DELETE` hangs for a known reason (a patch level, or a lock
   held by another session). Give them the census CSV's `legacy random` codes.
3. **Direct database delete.** Deleting the rows in the company database bypasses the Service
   Layer. SAP does not support direct writes to its tables, so this needs the partner's sign-off
   and a HANA backup taken just before. Never do it without both.

Whatever is done, rerun the census afterwards and compare `Paged read` and the counts.
