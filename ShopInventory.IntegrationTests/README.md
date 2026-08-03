# SAP integration tests

Checks that a real SAP Service Layer accepts every document query this codebase builds.

> **These run against a live instance, and they are off by default.** Nothing here creates, patches
> or cancels a *document* — but the SQL-backed tests do provision `SQLQueries` objects, and this SAP
> cannot practically delete one. Running them changes the target company permanently. Two opt-ins
> gate that, both unset by default:
>
> | Variable | Allows |
> | --- | --- |
> | `SHOPINVENTORY_SAP_TESTS=1` | contacting SAP at all |
> | `SHOPINVENTORY_SAP_SQL_TESTS=1` | *additionally*, the tests that leave query objects behind |
>
> With neither set, the whole assembly skips in milliseconds without opening a socket, so
> `dotnet test` at the solution root cannot wander in here by accident. The SQL tests need **both**.

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
usually nothing to set up beyond the opt-in:

```bash
SHOPINVENTORY_SAP_TESTS=1 dotnet test ShopInventory.IntegrationTests
```

To include the SQL-backed suites, which add query objects to the target company:

```bash
SHOPINVENTORY_SAP_TESTS=1 SHOPINVENTORY_SAP_SQL_TESTS=1 dotnet test ShopInventory.IntegrationTests
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

- **No document writes.** Nothing here creates, patches or cancels a document. "Read-only" is worth
  stating precisely, because it used to be stated loosely and that is how a probe ended up scanning
  an unfiltered table on a live host: it means read-only with respect to *business data*. A SELECT
  still writes a query object before it can run.
- **Use `[SapSqlFact]` / `[SapSqlTheory]` for anything that runs SQL**, including a test that only
  reads rows — the permanent part is the object the read has to provision first. `[SapFact]` and
  `[SapTheory]` are for document queries, which provision nothing. Putting a SQL test on the wrong
  attribute silently removes its gate.
- **No SQL under a code that varies.** SQL provisions a `SQLQueries` object, and this SAP instance
  cannot practically delete one, so anything content-addressed or generated per run leaves litter
  behind. `SapDocumentQueryTests` avoids SQL entirely for that reason.
- **Bound, not unbounded.** `ExecuteRawSqlQueryAsync` walks every page, so a statement without a
  selective `WHERE` pages an entire table across a cluster shared with live users. Filter to a
  handful of rows, and prefer a predicate that matches nothing when the question is only whether SAP
  accepts the statement — the validator rejects at create, before any data is touched.

  `SapStatementQueryTests` and `SapReportQueryTests` do run SQL, under the same fixed codes the
  application itself uses — a bounded set of objects, provisioned once and reused by every later
  run. Both exist because a statement SAP will not accept is invisible to everything else: the SQL
  is valid HANA, the unit tests pass, and only the create call ever sees the error. That is how five
  report statements reached main returning HTTP 400 to every caller.
