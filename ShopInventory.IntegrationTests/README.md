# SAP integration tests

Read-only checks that a real SAP Service Layer accepts every document query this codebase builds.

## Why these exist

`ShopInventory.Tests` checks that each `$select` names only fields present in the committed
`$metadata`, and that the URL literals actually interpolate. Neither proves SAP accepts the finished
URL. Three URLs shipped broken because nothing exercised them against a live instance — the
compiler was happy, the unit tests were green, and the braces went to SAP as text.

## Running them

They **skip** unless a Service Layer is configured and reachable, and say why:

```
SAP Service Layer at 10.10.10.6:50000 did not answer within 2000ms. Run these on the SAP network.
```

Skipped rather than passed on purpose — a test that quietly returns early looks identical to one
that ran, which is the failure this project exists to close.

On the SAP network, credentials come from the same user-secrets store as the app, so there is
usually nothing to set up:

```bash
dotnet test ShopInventory.IntegrationTests
```

To point at a different instance without touching secrets:

```bash
SAP__ServiceLayerUrl=https://host:50000/b1s/v1/ SAP__CompanyDB=DB SAP__Username=user SAP__Password=pass dotnet test ShopInventory.IntegrationTests
```

## Pointing them at a new company

A company must define the UDFs this application reads, or the tests will fail on queries that are
perfectly correct. `KEFALOS_TEST_3` was missing `U_OrderNumber` and `U_Van_saleorder` and reported
them as `Property 'U_OrderNumber' of 'Document' is invalid`, which reads exactly like a bad
`$select` and is not one.

Check before blaming the code:

```bash
curl -sk -H "Cookie: B1SESSION=$SID" -H "Prefer: odata.maxpagesize=500" "https://host:50000/b1s/v1/UserFieldsMD?\$filter=Name%20eq%20'OrderNumber'&\$select=TableName&\$top=500"
```

The `Prefer` header is not optional. `$top` bounds the result, `odata.maxpagesize` governs the page,
and without the header SAP answers 20 rows with a nextLink — which has produced a wrong conclusion
about these very fields more than once.

## What they will and will not tell you

A failure carries SAP's own error text, which names the offending field or filter directly:

```
SAP rejected the query: Failed to get credit notes: BadRequest -
{"error":{"code":-1,"message":{"value":"Invalid field name 'Bogus' in $select"}}}
```

Most assert only that SAP accepted the request. An empty result is not a failure — a test company
need not hold any given document type — so nothing asserts on row counts. The exceptions are the
item lookup, which checks `$select` did not drop a bound field, and the batched order-number probe,
which checks unmatched numbers are absent rather than null.

## Rules for adding to these

- **Read-only.** Nothing here creates, patches or cancels a document.
- **No SQL-backed methods.** Those provision `SQLQueries` objects, which this SAP instance cannot
  practically delete, so running them repeatedly leaves litter behind. The queries these tests
  cover are all OData.
