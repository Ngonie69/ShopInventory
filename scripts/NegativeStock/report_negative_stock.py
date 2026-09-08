"""Report where SAP stock has gone negative, and which posting path took it there.

Phase 0 of the negative-stock remediation. It answers the two questions that have
to be settled before any guard is changed, because either answer reorders the
work:

  1. Is SAP blocking negative inventory at all? Every fail-open catch in the API
     -- BatchInventoryValidationService.cs:1655 and :710, SAPServiceLayerClient.cs
     :10312 -- lets an invoice through on the stated assumption that "SAP will
     validate". Nothing in either codebase has ever read the setting that
     assumption rests on. This reads it.

  2. Which warehouses and which posting path are actually producing negatives?
     Five code paths post an A/R invoice, gated by three stock ledgers that never
     reconcile. Guessing which one is leaking wastes the fix.

The census (stages 1 and 2) is the part to trust: every table and column it
touches is lifted from SQL this application already runs in production. The
attribution stage walks OINM, which the application never queries, so its column
names are the one thing here not confirmed against working code -- it is opt-in
for that reason, and it reports rather than throws when SAP refuses it.

Re-run it. That is the point: this is the instrument every later phase is measured
with, not a one-time query. Keep the JSON output as a baseline and diff it.

Usage:
    python report_negative_stock.py --session <B1SESSION id>
    python report_negative_stock.py --login            # SAP_USERNAME / SAP_PASSWORD / SAP_COMPANY_DB
    python report_negative_stock.py --session <id> --attribute --lookback-days 30
    python report_negative_stock.py --session <id> --json baseline-2026-09-08.json
    python report_negative_stock.py --print-sql        # no session needed, creates nothing

On SQLQueries objects: a SQLQueries row cannot practically be deleted -- a DELETE
takes over three minutes and does not complete -- and roughly 4,400 leaked ones are
already waiting to be cleaned up. So every statement below is a constant. Nothing
is interpolated into SQL; the varying parts of the attribution query are bound
parameters. Three runs or three thousand, this script adds at most three rows to
OUQR, and it derives their codes with the same content-addressing the application
uses, so a statement it shares with the application shares the object too.
"""

from __future__ import annotations

import argparse
import collections
import hashlib
import json
import os
import ssl
import sys
import urllib.error
import urllib.parse
import urllib.request
from datetime import date, timedelta

DEFAULT_SERVICE_LAYER = "https://10.10.10.6:50000/b1s/v1/"

# Rows per page. The Service Layer answers 20 without an explicit Prefer header,
# which turns a 4,000-row census into 200 round trips.
PAGE_SIZE = 500

# ---------------------------------------------------------------------------
# The statements. Constants, deliberately -- see the module docstring.
#
# Column names here are taken from SQL the application runs against live SAP:
# OITW."OnHand"/"IsCommited"/"OnOrder"/"WhsCode" from the stock-quantities query,
# and the OBTN |><| OBTQ join with "AbsEntry"/"MdAbsEntry"/"DistNumber"/"Quantity"
# from the warehouse-batches query. Neither uses a SQL function, column
# arithmetic, or an ORDER BY over an aggregate, all of which SQLQueries rejects.
# ---------------------------------------------------------------------------

# Character-for-character what SAPServiceLayerClient.NegativeStockSql sends, and both derive the
# query code the same way -- so the baseline taken by hand here and the figure the daily job records
# resolve to one SAP object and cannot drift into measuring slightly different things. Change one and
# NegativeStockQueryParityTests fails.
SQL_WAREHOUSE_NEGATIVES = """SELECT T1."ItemCode", T0."ItemName", T1."WhsCode" as "WarehouseCode", T1."OnHand" as "InStock", T1."IsCommited" as "Committed", T1."OnOrder" as "Ordered"
FROM OITW T1
INNER JOIN OITM T0 ON T0."ItemCode" = T1."ItemCode"
WHERE T1."OnHand" < 0
ORDER BY T1."WhsCode", T1."ItemCode\""""

SQL_BATCH_NEGATIVES = """SELECT T0."ItemCode", T2."ItemName", T0."DistNumber", T1."WhsCode", T1."Quantity"
FROM OBTN T0
INNER JOIN OBTQ T1 ON T0."AbsEntry" = T1."MdAbsEntry"
INNER JOIN OITM T2 ON T0."ItemCode" = T2."ItemCode"
WHERE T1."Quantity" < 0
ORDER BY T1."WhsCode", T0."ItemCode", T0."DistNumber\""""

# Bound, not interpolated: one object serves every item, warehouse and window.
# Three separate comparisons rather than a BETWEEN -- SQLQueries rejects a BETWEEN
# carrying two bound parameters.
SQL_ITEM_MOVEMENTS = """SELECT T0."TransNum", T0."TransType", T0."CreatedBy", T0."BASE_REF", T0."DocDate", T0."InQty", T0."OutQty"
FROM OINM T0
WHERE T0."ItemCode" = :itemCode AND T0."Warehouse" = :warehouseCode AND T0."DocDate" >= :fromDate
ORDER BY T0."TransNum\""""

# SAP object types, for the document that crossed zero.
TRANS_TYPES = {
    13: "A/R Invoice",
    14: "A/R Credit Memo",
    15: "Delivery",
    16: "Return",
    18: "A/P Invoice",
    20: "Goods Receipt PO",
    21: "Goods Return",
    59: "Goods Receipt",
    60: "Goods Issue",
    67: "Inventory Transfer",
    68: "Work Order",
    162: "Inventory Revaluation",
    10000071: "Inventory Transfer Request",
}

# The reserved U_Van_saleorder prefixes, from Common/Sales/SaleReferenceNamespace.cs.
# They are how an invoice names the path that posted it.
DESKTOP_SALE_PREFIX = "DS-"
CONSOLIDATION_PREFIX = "CONSOL-"

# Van warehouses are coded VAN001..VAN009 in DailyStock:MonitoredWarehouses. The
# van route posts one invoice per handset sale and consults no stock ledger at
# all, so separating it from the shop tills is most of the attribution.
VAN_WAREHOUSE_PREFIX = "VAN"


# ---------------------------------------------------------------------------
# SQLQueries identity, reproduced from SAPServiceLayerClient so a statement this
# script shares with the application resolves to the application's object rather
# than a second copy of it.
# ---------------------------------------------------------------------------


def normalize_sap_sql_text(sql_text: str) -> str:
    """Strip the trailing terminator SQLQueries refuses outright."""
    return (sql_text or "").rstrip(";  \t\r\n")


def normalize_sql_text(sql_text: str) -> str:
    """Canonicalise so the stored statement and the sent one compare equal.

    SAP rewrites the newlines it is given -- text posted with CRLF comes back with
    a bare CR -- and the terminator is stripped before the text is ever sent. Miss
    either and every run PATCHes a query that did not change.
    """
    return normalize_sap_sql_text(sql_text).replace("\r\n", "\n").replace("\r", "\n").strip()


def sql_fingerprint(sql_text: str) -> str:
    digest = hashlib.sha256(normalize_sql_text(sql_text).encode("utf-8")).hexdigest()
    return digest[:12].upper()


def content_addressed_code(prefix: str, sql_text: str) -> str:
    return f"{prefix[:24]}_{sql_fingerprint(sql_text)}"


def query_name(name: str, sql_text: str) -> str:
    """Stamp the fingerprint onto the name too.

    SAP has been seen rejecting a create with -2035 for a code a GET reports as
    404, which points at SqlName being constrained as well. Deriving both from one
    fingerprint means a collision on the name is always also a collision on the
    code -- where sharing the object is what we want.
    """
    return f"{name[:36]} {sql_fingerprint(sql_text)}"


# ---------------------------------------------------------------------------
# Service Layer client
# ---------------------------------------------------------------------------


class ServiceLayerError(RuntimeError):
    def __init__(self, message: str, status: int | None = None, body: str = ""):
        super().__init__(message)
        self.status = status
        self.body = body


class ServiceLayer:
    def __init__(self, base_url: str, session_id: str, timeout: int = 180, verbose: bool = False):
        self.base_url = base_url if base_url.endswith("/") else base_url + "/"
        self.session_id = session_id
        self.timeout = timeout
        self.verbose = verbose
        # The Service Layer sits on a private address behind a self-signed
        # certificate, which is also how the application reaches it.
        self.context = ssl.create_default_context()
        self.context.check_hostname = False
        self.context.verify_mode = ssl.CERT_NONE

    def _request(self, method: str, path: str, body: dict | None = None, headers: dict | None = None) -> tuple[int, str]:
        url = urllib.parse.urljoin(self.base_url, path)
        data = json.dumps(body).encode("utf-8") if body is not None else None
        request = urllib.request.Request(url, data=data, method=method)
        request.add_header("Cookie", f"B1SESSION={self.session_id}")
        request.add_header("Accept", "application/json")
        if data is not None:
            request.add_header("Content-Type", "application/json")
        for key, value in (headers or {}).items():
            request.add_header(key, value)

        if self.verbose:
            print(f"  -> {method} {url}", file=sys.stderr)

        try:
            with urllib.request.urlopen(request, context=self.context, timeout=self.timeout) as response:
                return response.status, response.read().decode("utf-8", "replace")
        except urllib.error.HTTPError as error:
            return error.code, error.read().decode("utf-8", "replace")
        except urllib.error.URLError as error:
            raise ServiceLayerError(f"Could not reach {url}: {error.reason}") from error

    def get(self, path: str, page_size: int | None = None) -> tuple[int, str]:
        headers = {"Prefer": f"odata.maxpagesize={page_size}"} if page_size else None
        return self._request("GET", path, headers=headers)

    def post(self, path: str, body: dict | None = None) -> tuple[int, str]:
        return self._request("POST", path, body=body if body is not None else {})

    def patch(self, path: str, body: dict) -> tuple[int, str]:
        return self._request("PATCH", path, body=body)

    # -- SQLQueries ---------------------------------------------------------

    def ensure_query(self, code: str, name: str, sql_text: str) -> None:
        """Create the query if it is missing, repair it if its text has drifted.

        Never deletes. A SQLQueries row is effectively permanent, so the only safe
        operations are create-once and patch-in-place.
        """
        status, body = self.get(f"SQLQueries('{urllib.parse.quote(code)}')?$select=SqlText")

        if status == 200:
            stored = json.loads(body).get("SqlText") or ""
            if normalize_sql_text(stored) == normalize_sql_text(sql_text):
                return
            status, body = self.patch(
                f"SQLQueries('{urllib.parse.quote(code)}')",
                {"SqlText": normalize_sap_sql_text(sql_text)},
            )
            if status >= 400:
                raise ServiceLayerError(f"Could not repair SQL query '{code}'", status, body)
            return

        if status != 404:
            raise ServiceLayerError(f"Could not look up SQL query '{code}'", status, body)

        status, body = self.post(
            "SQLQueries",
            {
                "SqlCode": code,
                "SqlName": name,
                "SqlText": normalize_sap_sql_text(sql_text),
            },
        )
        # -2035 is "entry already exists", which a concurrent run can produce
        # between the GET above and this POST. That is the outcome we wanted.
        if status >= 400 and "-2035" not in body and status != 409:
            raise ServiceLayerError(f"Could not create SQL query '{code}'", status, body)

    def run_query(self, code: str, parameters: dict[str, str] | None = None) -> list[dict]:
        """Page a SQLQueries result to exhaustion."""
        pairs = ""
        if parameters:
            pairs = "&".join(
                f"{urllib.parse.quote(key)}={urllib.parse.quote(chr(39) + str(value).replace(chr(39), chr(39) * 2) + chr(39))}"
                for key, value in parameters.items()
            ) + "&"

        rows: list[dict] = []
        skip = 0
        while True:
            path = f"SQLQueries('{urllib.parse.quote(code)}')/List?{pairs}$skip={skip}"
            status, body = self.get(path, page_size=PAGE_SIZE)
            if status == 404:
                return rows
            if status >= 400:
                raise ServiceLayerError(f"Query '{code}' failed", status, body)

            page = json.loads(body).get("value") or []
            rows.extend(page)
            if len(page) < PAGE_SIZE:
                return rows
            skip += len(page)


def login(base_url: str, company_db: str, username: str, password: str, verbose: bool) -> str:
    client = ServiceLayer(base_url, session_id="", verbose=verbose)
    status, body = client.post("Login", {"CompanyDB": company_db, "UserName": username, "Password": password})
    if status >= 400:
        # The body carries the SAP error code; -304 is a bad password, 312 is the
        # SLD login failure that trips the breaker on a 30-second sawtooth.
        raise ServiceLayerError(f"SAP login failed for company '{company_db}'", status, body)
    session_id = json.loads(body).get("SessionId")
    if not session_id:
        raise ServiceLayerError("SAP login returned no SessionId", status, body)
    return session_id


# ---------------------------------------------------------------------------
# Stage 1 -- the setting the fail-open catches assume
# ---------------------------------------------------------------------------


def read_block_negative_setting(client: ServiceLayer) -> dict:
    """Read AdminInfo.BlockStockNegativeQuantity.

    Confirmed against reference/sap-service-layer-metadata.xml: the property is on
    the AdminInfo complex type, typed BoYesNoEnum, and is returned by the
    CompanyService_GetAdminInfo action. There is no entity set for it.

    The Service Layer exposes no per-warehouse or per-item-group override, so a
    "tYES" here does not on its own prove every warehouse is covered -- B1's "Block
    Negative Inventory by" level is not in the OData surface. The census below is
    the empirical answer, and it is the one that settles the argument.
    """
    status, body = client.post("CompanyService_GetAdminInfo")
    if status >= 400:
        return {"available": False, "error": f"HTTP {status}: {body[:300]}"}

    info = json.loads(body)
    raw = info.get("BlockStockNegativeQuantity")
    return {
        "available": True,
        "raw": raw,
        "blocking": raw in ("tYES", "Y", True),
        "companyDb": info.get("CompanyDB") or info.get("CompanyName"),
    }


# ---------------------------------------------------------------------------
# Stage 2 -- the census
# ---------------------------------------------------------------------------


def to_decimal(value) -> float:
    if value is None:
        return 0.0
    try:
        return float(value)
    except (TypeError, ValueError):
        return 0.0


def census(client: ServiceLayer) -> dict:
    warehouse_code = content_addressed_code("NEGSTK_WHS", SQL_WAREHOUSE_NEGATIVES)
    client.ensure_query(warehouse_code, query_name("Negative warehouse stock", SQL_WAREHOUSE_NEGATIVES), SQL_WAREHOUSE_NEGATIVES)
    warehouse_rows = client.run_query(warehouse_code)

    batch_code = content_addressed_code("NEGSTK_BATCH", SQL_BATCH_NEGATIVES)
    client.ensure_query(batch_code, query_name("Negative batch quantities", SQL_BATCH_NEGATIVES), SQL_BATCH_NEGATIVES)
    batch_rows = client.run_query(batch_code)

    warehouses = [
        {
            "itemCode": (row.get("ItemCode") or "").strip(),
            "itemName": (row.get("ItemName") or "").strip(),
            "warehouseCode": (row.get("WarehouseCode") or "").strip(),
            "onHand": to_decimal(row.get("InStock")),
            "committed": to_decimal(row.get("Committed")),
            "ordered": to_decimal(row.get("Ordered")),
        }
        for row in warehouse_rows
    ]

    batches = [
        {
            "itemCode": (row.get("ItemCode") or "").strip(),
            "itemName": (row.get("ItemName") or "").strip(),
            "batchNumber": (row.get("DistNumber") or "").strip(),
            "warehouseCode": (row.get("WhsCode") or "").strip(),
            "quantity": to_decimal(row.get("Quantity")),
        }
        for row in batch_rows
    ]

    return {"warehouseNegatives": warehouses, "batchNegatives": batches}


# ---------------------------------------------------------------------------
# Stage 3 -- which document crossed zero, and which path posted it
# ---------------------------------------------------------------------------


def find_crossing(movements: list[dict], on_hand: float) -> dict | None:
    """Walk the movement history backwards to the transaction that went negative.

    The current on-hand is known, so no starting balance has to be reconstructed:
    subtract each movement in turn and the balance before it falls out. The
    crossing is the most recent transaction whose balance went from at-or-above
    zero to below it.

    Walking backwards is what keeps the window small. Cumulating forwards would
    need the item's whole history from the beginning of the company.
    """
    after = on_hand
    for row in reversed(movements):
        delta = to_decimal(row.get("InQty")) - to_decimal(row.get("OutQty"))
        before = after - delta
        if before >= 0 > after:
            return {
                "transNum": row.get("TransNum"),
                "transType": row.get("TransType"),
                "docEntry": row.get("CreatedBy"),
                "docNum": row.get("BASE_REF"),
                "docDate": row.get("DocDate"),
                "balanceBefore": round(before, 4),
                "balanceAfter": round(after, 4),
            }
        after = before
    return None


def classify_invoice(client: ServiceLayer, doc_entry, warehouse_code: str) -> dict:
    """Name the posting path from the invoice's own U_Van_saleorder.

    Every path but one writes that field, so an empty one is itself the signal.
    See Common/Sales/SaleReferenceNamespace.cs for the reserved prefixes.
    """
    if doc_entry in (None, "", 0):
        return {"path": "unknown", "reason": "no DocEntry on the movement row"}

    status, body = client.get(f"Invoices({int(doc_entry)})?$select=DocNum,NumAtCard,U_Van_saleorder")
    if status >= 400:
        return {"path": "unknown", "reason": f"invoice {doc_entry} unreadable (HTTP {status})"}

    invoice = json.loads(body)
    reference = (invoice.get("U_Van_saleorder") or "").strip()
    is_van = warehouse_code.upper().startswith(VAN_WAREHOUSE_PREFIX)

    if not reference:
        path = "web/API invoice (CreateInvoiceHandler)"
    elif reference.upper().startswith(CONSOLIDATION_PREFIX):
        path = "18:00 consolidation (ConsolidateDailySalesHandler)"
    elif is_van:
        path = "van end-of-day (VanSalesEndOfDayPostingService)"
    elif reference.upper().startswith(DESKTOP_SALE_PREFIX):
        path = "till sale (DesktopSalePostingService)"
    else:
        # A client-supplied reference: a till that generates its own, or a
        # reservation confirm. Both are one-invoice-per-sale from a shop warehouse.
        path = "till sale or reservation confirm (client-supplied reference)"

    return {
        "path": path,
        "docNum": invoice.get("DocNum"),
        "numAtCard": invoice.get("NumAtCard"),
        "vanSaleOrder": reference or None,
    }


def attribute(client: ServiceLayer, negatives: list[dict], lookback_days: int, limit: int) -> dict:
    """Attribute each negative to the document that caused it.

    Opt-in, and it degrades rather than throws. OINM is the only table this script
    touches that the application never queries, so its column names -- "TransNum",
    "TransType", "CreatedBy" as the source DocEntry, "BASE_REF" as its DocNum --
    are standard B1 but unconfirmed against working code here. If SAP refuses the
    statement, the census above still stands on its own.
    """
    movement_code = content_addressed_code("NEGSTK_MOVES", SQL_ITEM_MOVEMENTS)
    from_date = (date.today() - timedelta(days=lookback_days)).isoformat()

    try:
        client.ensure_query(movement_code, query_name("Item movements since date", SQL_ITEM_MOVEMENTS), SQL_ITEM_MOVEMENTS)
    except ServiceLayerError as error:
        return {
            "available": False,
            "error": f"{error} -- {error.body[:400]}",
            "hint": "Check the OINM column names in SQL_ITEM_MOVEMENTS against this company's schema.",
            "results": [],
        }

    results = []
    for negative in negatives[:limit]:
        try:
            movements = client.run_query(
                movement_code,
                {
                    "itemCode": negative["itemCode"],
                    "warehouseCode": negative["warehouseCode"],
                    "fromDate": from_date,
                },
            )
        except ServiceLayerError as error:
            return {
                "available": False,
                "error": f"{error} -- {error.body[:400]}",
                "hint": "Check the OINM column names in SQL_ITEM_MOVEMENTS against this company's schema.",
                "results": results,
            }

        crossing = find_crossing(movements, negative["onHand"])
        entry = {
            "itemCode": negative["itemCode"],
            "warehouseCode": negative["warehouseCode"],
            "onHand": negative["onHand"],
            "movementsInWindow": len(movements),
        }

        if crossing is None:
            entry["crossing"] = None
            entry["path"] = f"crossed zero before {from_date}"
        else:
            entry["crossing"] = crossing
            trans_type = crossing.get("transType")
            entry["document"] = TRANS_TYPES.get(trans_type, f"object type {trans_type}")
            if trans_type == 13:
                entry.update(classify_invoice(client, crossing.get("docEntry"), negative["warehouseCode"]))
            else:
                entry["path"] = f"not an invoice -- {entry['document']}"

        results.append(entry)

    return {"available": True, "fromDate": from_date, "results": results}


# ---------------------------------------------------------------------------
# Reporting
# ---------------------------------------------------------------------------


def rule(width: int = 78) -> str:
    return "-" * width


def report(result: dict) -> None:
    setting = result["blockNegativeInventory"]
    warehouses = result["warehouseNegatives"]
    batches = result["batchNegatives"]

    print()
    print("NEGATIVE STOCK IN SAP")
    print(f"Company: {result['companyDb']}    Read at: {result['readAt']}")
    print(rule())

    print()
    print("1. Block Negative Inventory")
    if not setting.get("available"):
        print(f"   Could not read the setting: {setting.get('error')}")
    elif setting["blocking"]:
        print(f"   ON  (BlockStockNegativeQuantity = {setting['raw']})")
        print("   Note: the Service Layer exposes no per-warehouse or per-item-group")
        print("   override, so this does not prove every warehouse is covered.")
    else:
        print(f"   OFF (BlockStockNegativeQuantity = {setting['raw']})")
        print("   Every 'SAP will validate' catch in the API is therefore a hole.")

    print()
    print("2. Negative warehouse stock (OITW)")
    if not warehouses:
        print("   None.")
    else:
        by_warehouse = collections.defaultdict(list)
        for row in warehouses:
            by_warehouse[row["warehouseCode"]].append(row)

        print(f"   {len(warehouses)} item/warehouse rows below zero across {len(by_warehouse)} warehouses.")
        print()
        print(f"   {'WAREHOUSE':<12} {'ROWS':>6} {'TOTAL UNITS':>14}")
        for warehouse in sorted(by_warehouse, key=lambda w: sum(r["onHand"] for r in by_warehouse[w])):
            rows = by_warehouse[warehouse]
            print(f"   {warehouse:<12} {len(rows):>6} {sum(r['onHand'] for r in rows):>14,.4f}")

        print()
        print(f"   {'ITEM':<12} {'WAREHOUSE':<12} {'ON HAND':>13} {'COMMITTED':>12}  NAME")
        for row in sorted(warehouses, key=lambda r: r["onHand"])[:40]:
            print(
                f"   {row['itemCode']:<12} {row['warehouseCode']:<12} "
                f"{row['onHand']:>13,.4f} {row['committed']:>12,.4f}  {row['itemName'][:28]}"
            )
        if len(warehouses) > 40:
            print(f"   ... and {len(warehouses) - 40} more (use --json for the full set)")

    print()
    print("3. Negative batch quantities (OBTN |><| OBTQ)")
    if not batches:
        print("   None.")
    else:
        print(f"   {len(batches)} batch rows below zero.")
        print()
        print(f"   {'ITEM':<12} {'BATCH':<18} {'WAREHOUSE':<12} {'QUANTITY':>13}")
        for row in sorted(batches, key=lambda r: r["quantity"])[:40]:
            print(f"   {row['itemCode']:<12} {row['batchNumber']:<18} {row['warehouseCode']:<12} {row['quantity']:>13,.4f}")
        if len(batches) > 40:
            print(f"   ... and {len(batches) - 40} more (use --json for the full set)")

    attribution = result.get("attribution")
    if attribution is None:
        print()
        print("4. Attribution")
        print("   Not run. Pass --attribute to walk OINM and name the posting path.")
    elif not attribution.get("available"):
        print()
        print("4. Attribution")
        print(f"   Unavailable: {attribution.get('error')}")
        print(f"   {attribution.get('hint', '')}")
    else:
        print()
        print(f"4. Attribution (movements since {attribution['fromDate']})")
        counts = collections.Counter(entry.get("path", "unknown") for entry in attribution["results"])
        if not counts:
            print("   Nothing to attribute.")
        else:
            print()
            print(f"   {'COUNT':>6}  POSTING PATH")
            for path, count in counts.most_common():
                print(f"   {count:>6}  {path}")
            print()
            print(f"   {'ITEM':<12} {'WAREHOUSE':<12} {'DOC':>9}  PATH")
            for entry in attribution["results"]:
                crossing = entry.get("crossing") or {}
                print(
                    f"   {entry['itemCode']:<12} {entry['warehouseCode']:<12} "
                    f"{str(crossing.get('docNum') or '-'):>9}  {entry.get('path', 'unknown')}"
                )

    print()
    print(rule())
    print("Keep this run's --json output. The count above is the number every phase")
    print("of the remediation is measured against; a fix nobody measured is a claim.")
    print()


# ---------------------------------------------------------------------------


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--service-layer", default=os.environ.get("SAP_SERVICE_LAYER", DEFAULT_SERVICE_LAYER))
    parser.add_argument("--session", help="An existing B1SESSION id.")
    parser.add_argument("--login", action="store_true", help="Log in using SAP_USERNAME / SAP_PASSWORD / SAP_COMPANY_DB.")
    parser.add_argument("--company-db", default=os.environ.get("SAP_COMPANY_DB", "KEFALOS_USD_NEW2"))
    parser.add_argument("--attribute", action="store_true", help="Walk OINM to name the document that crossed zero.")
    parser.add_argument("--lookback-days", type=int, default=30, help="Movement window for --attribute (default 30).")
    parser.add_argument("--limit", type=int, default=50, help="Most-negative rows to attribute (default 50).")
    parser.add_argument("--json", dest="json_path", help="Write the full result as JSON, for use as a baseline.")
    parser.add_argument("--print-sql", action="store_true", help="Print the statements and exit. Touches nothing.")
    parser.add_argument("--verbose", action="store_true")
    args = parser.parse_args(argv)

    if args.print_sql:
        # The real prefixes, not display-derived ones: printing a code the script does not use is
        # worse than printing none, because the whole point of showing it is to look it up in SAP.
        for label, prefix, sql in (
            ("Negative warehouse stock", "NEGSTK_WHS", SQL_WAREHOUSE_NEGATIVES),
            ("Negative batch quantities", "NEGSTK_BATCH", SQL_BATCH_NEGATIVES),
            ("Item movements since date", "NEGSTK_MOVES", SQL_ITEM_MOVEMENTS),
        ):
            code = content_addressed_code(prefix, sql)
            print(f"-- {label}   SqlCode {code}")
            print(sql)
            print()
        return 0

    try:
        if args.login:
            username = os.environ.get("SAP_USERNAME")
            password = os.environ.get("SAP_PASSWORD")
            if not username or not password:
                parser.error("--login needs SAP_USERNAME and SAP_PASSWORD in the environment.")
            session_id = login(args.service_layer, args.company_db, username, password, args.verbose)
        elif args.session:
            session_id = args.session
        else:
            parser.error("Pass --session <B1SESSION id> or --login.")

        client = ServiceLayer(args.service_layer, session_id, verbose=args.verbose)

        result = {
            "readAt": __import__("datetime").datetime.now().isoformat(timespec="seconds"),
            "companyDb": args.company_db,
            "serviceLayer": args.service_layer,
            "blockNegativeInventory": read_block_negative_setting(client),
        }
        result.update(census(client))

        if args.attribute:
            ordered = sorted(result["warehouseNegatives"], key=lambda r: r["onHand"])
            result["attribution"] = attribute(client, ordered, args.lookback_days, args.limit)
        else:
            result["attribution"] = None

    except ServiceLayerError as error:
        print(f"error: {error}", file=sys.stderr)
        if error.body:
            print(error.body[:800], file=sys.stderr)
        return 2

    report(result)

    if args.json_path:
        with open(args.json_path, "w", encoding="utf-8") as handle:
            json.dump(result, handle, indent=2)
        print(f"Wrote {args.json_path}", file=sys.stderr)

    # Non-zero when anything is negative, so this can gate a scheduled check.
    return 1 if (result["warehouseNegatives"] or result["batchNegatives"]) else 0


if __name__ == "__main__":
    sys.exit(main())
