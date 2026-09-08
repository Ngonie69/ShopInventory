"""Checks for report_negative_stock.py. Run it: python test_report_negative_stock.py

Three things are worth pinning here, and only one of them is ordinary unit testing.

The first is that this script's SQLQueries codes are minted by the same rule as the
application's. That is not cosmetic: a code derived differently would create a
second SAP-side object for a statement the application already has, and a
SQLQueries object cannot practically be deleted. The check runs the Python
implementation over the exact statement ShopInventory.Tests pins as production's,
and compares against the code that test pins.

The second is that the statements carry no interpolation. Every varying value is
bound. If a future edit slips a format placeholder in, every distinct value it
takes mints another permanent OUQR row -- which is the leak that left roughly
4,400 of them behind already.

The third is find_crossing, which is the only real arithmetic in the script.

No network. The end-to-end check drives the whole run against a fake Service Layer.
"""

from __future__ import annotations

import json
import sys
import urllib.parse
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
import report_negative_stock as r  # noqa: E402

FAILURES: list[str] = []


def check(name: str, got, want) -> None:
    if got == want:
        print(f"PASS  {name}")
        return
    print(f"FAIL  {name}\n        got  {got!r}\n        want {want!r}")
    FAILURES.append(name)


def movement(trans_num: int, in_qty: float = 0, out_qty: float = 0, trans_type: int = 13, doc_entry: int = 900) -> dict:
    return {
        "TransNum": trans_num,
        "TransType": trans_type,
        "CreatedBy": doc_entry,
        "BASE_REF": 5000 + trans_num,
        "DocDate": "2026-09-01",
        "InQty": in_qty,
        "OutQty": out_qty,
    }


# ---------------------------------------------------------------------------
# 1. Query identity matches the application's
# ---------------------------------------------------------------------------


def test_fingerprint_matches_the_application() -> None:
    """The code this mints for a statement must equal the code the C# client mints.

    ShopInventory.Tests/SapSqlQueryEquivalenceTests.cs pins the price-list-35
    statement's real production code. Read that statement out of the test file the
    way the C# compiler would -- strip the closing delimiter's indentation from
    each line of the raw string literal -- and put it through this script.
    """
    test_file = Path(__file__).resolve().parents[2] / "ShopInventory.Tests" / "SapSqlQueryEquivalenceTests.cs"
    if not test_file.exists():
        print(f"SKIP  fingerprint golden vector ({test_file.name} not found)")
        return

    lines = test_file.read_text(encoding="utf-8").splitlines()
    start = next(i for i, line in enumerate(lines) if 'PriceList35Statement = """' in line)
    end = next(i for i, line in enumerate(lines) if i > start and line.strip() == '""";')
    indent = len(lines[end]) - len(lines[end].lstrip())
    statement = "\n".join(line[indent:] if line.strip() else "" for line in lines[start + 1 : end])

    check(
        "content-addressed code matches the C# client",
        r.content_addressed_code("SH_PL35", statement),
        "SH_PL35_1239DBF4EC45",
    )


def test_normalizer_matches_the_application() -> None:
    check("trailing semicolon is trimmed", r.normalize_sql_text("SELECT 1 FROM OJDT;"), r.normalize_sql_text("SELECT 1 FROM OJDT"))
    check("CRLF and bare CR both fold to LF", r.normalize_sql_text("SELECT 1\r\nFROM OJDT"), r.normalize_sql_text("SELECT 1\rFROM OJDT"))
    check("the name carries the fingerprint too", r.query_name("Negative warehouse stock", "SELECT 1").endswith(r.sql_fingerprint("SELECT 1")), True)


# ---------------------------------------------------------------------------
# 2. The statements stay constant, so OUQR stays bounded
# ---------------------------------------------------------------------------


def test_statements_carry_no_interpolation() -> None:
    for name, sql in (
        ("warehouse negatives", r.SQL_WAREHOUSE_NEGATIVES),
        ("batch negatives", r.SQL_BATCH_NEGATIVES),
        ("item movements", r.SQL_ITEM_MOVEMENTS),
    ):
        check(f"{name}: no format placeholder", "{" in sql or "%s" in sql, False)
        check(f"{name}: no trailing semicolon", sql.rstrip().endswith(";"), False)

    # The movement query is the only one that varies per call, and it varies by
    # binding rather than by text.
    check("item movements binds its varying values", ":itemCode" in r.SQL_ITEM_MOVEMENTS and ":fromDate" in r.SQL_ITEM_MOVEMENTS, True)

    # SQLQueries rejects a BETWEEN carrying two bound parameters.
    check("no BETWEEN in any statement", any("BETWEEN" in s.upper() for s in (r.SQL_WAREHOUSE_NEGATIVES, r.SQL_BATCH_NEGATIVES, r.SQL_ITEM_MOVEMENTS)), False)


# ---------------------------------------------------------------------------
# 3. find_crossing
# ---------------------------------------------------------------------------


def test_find_crossing() -> None:
    # 100 in, then 40, 50 and 15 out: the last one takes it to -5.
    crossing = r.find_crossing(
        [movement(1, in_qty=100), movement(2, out_qty=40), movement(3, out_qty=50), movement(4, out_qty=15)],
        on_hand=-5,
    )
    check("names the transaction that went under", (crossing["transNum"], crossing["balanceBefore"], crossing["balanceAfter"]), (4, 10.0, -5.0))

    # Already negative when the window opened: there is nothing here to name, and
    # saying so is better than blaming the oldest movement in view.
    check("no crossing inside the window", r.find_crossing([movement(1, out_qty=3), movement(2, out_qty=2)], on_hand=-20), None)

    # Under, replenished, under again. The recent one is the one to investigate.
    crossing = r.find_crossing([movement(1, out_qty=15), movement(2, in_qty=25), movement(3, out_qty=22)], on_hand=-2)
    check("reports the most recent crossing", (crossing["transNum"], crossing["balanceBefore"]), (3, 20.0))

    check("zero is not negative", r.find_crossing([movement(1, out_qty=10)], on_hand=0), None)
    check("empty history names nothing", r.find_crossing([], on_hand=-4), None)

    crossing = r.find_crossing([movement(1, in_qty=5), movement(2, out_qty=8, trans_type=67)], on_hand=-3)
    check("a non-invoice crossing is typed", r.TRANS_TYPES[crossing["transType"]], "Inventory Transfer")


# ---------------------------------------------------------------------------
# 4. Parameter binding
# ---------------------------------------------------------------------------


class RecordingClient(r.ServiceLayer):
    """A ServiceLayer that answers from a script instead of a socket."""

    def __init__(self, responses: dict[str, tuple[int, str]] | None = None):
        self.requests: list[str] = []
        self.responses = responses or {}
        self.written: list[dict] = []

    def get(self, path, page_size=None):
        self.requests.append(path)
        for prefix, response in self.responses.items():
            if path.startswith(prefix):
                return response
        return 200, '{"value": []}'

    def post(self, path, body=None):
        self.requests.append(f"POST {path}")
        self.written.append({"path": path, "body": body})
        for prefix, response in self.responses.items():
            if path.startswith(prefix):
                return response
        return 201, "{}"

    def patch(self, path, body):
        self.requests.append(f"PATCH {path}")
        return 204, ""


def test_parameters_are_quoted_and_escaped() -> None:
    client = RecordingClient()
    r.ServiceLayer.run_query(client, "Q", {"itemCode": "CHE011", "fromDate": "2026-08-09"})
    check(
        "parameters are bound, quoted and ordered",
        client.requests[0],
        "SQLQueries('Q')/List?itemCode=%27CHE011%27&fromDate=%272026-08-09%27&$skip=0",
    )

    client = RecordingClient()
    r.ServiceLayer.run_query(client, "Q", {"itemCode": "O'BRIEN"})
    bound = urllib.parse.unquote(client.requests[0].split("itemCode=")[1].split("&")[0])
    check("an apostrophe is doubled, not injected", bound, "'O''BRIEN'")


def test_ensure_query_creates_once_and_never_deletes() -> None:
    # Missing: create it.
    client = RecordingClient({"SQLQueries('NEW')": (404, "not found")})
    r.ServiceLayer.ensure_query(client, "NEW", "Name", "SELECT 1")
    check("a missing query is created", client.written[0]["body"]["SqlCode"], "NEW")

    # Present and identical: leave it alone. A PATCH here is the slow write the
    # content-addressing exists to avoid.
    client = RecordingClient({"SQLQueries('SAME')": (200, json.dumps({"SqlText": "SELECT 1"}))})
    r.ServiceLayer.ensure_query(client, "SAME", "Name", "SELECT 1;")
    check("an unchanged query is left alone", [req for req in client.requests if req.startswith(("POST", "PATCH"))], [])

    # Present but drifted: repair in place.
    client = RecordingClient({"SQLQueries('OLD')": (200, json.dumps({"SqlText": "SELECT 2"}))})
    r.ServiceLayer.ensure_query(client, "OLD", "Name", "SELECT 1")
    check("a drifted query is patched", any(req.startswith("PATCH") for req in client.requests), True)

    # A concurrent run won the race. -2035 is the outcome we wanted, not an error.
    client = RecordingClient({"SQLQueries('RACE')": (404, ""), "SQLQueries": (400, '{"error":{"code":-2035}}')})
    r.ServiceLayer.ensure_query(client, "RACE", "Name", "SELECT 1")
    check("a lost create race is not an error", True, True)

    # Never a DELETE. A SQLQueries DELETE takes over three minutes and does not
    # complete, so the only safe operations are create-once and patch-in-place.
    # Read the methods the script can actually issue rather than grepping for the
    # word, which appears in the comments explaining exactly this.
    import ast

    methods = {
        node.args[0].value
        for node in ast.walk(ast.parse(Path(r.__file__).read_text(encoding="utf-8")))
        if isinstance(node, ast.Call)
        and isinstance(node.func, ast.Attribute)
        and node.func.attr == "_request"
        and node.args
        and isinstance(node.args[0], ast.Constant)
    }
    check("the script issues only GET, POST and PATCH", methods, {"GET", "POST", "PATCH"})


# ---------------------------------------------------------------------------
# 5. Attribution
# ---------------------------------------------------------------------------


def test_classify_invoice_names_the_posting_path() -> None:
    def client_for(reference: str | None, doc_entry: int = 42):
        payload = json.dumps({"DocNum": 7001, "NumAtCard": "X", "U_Van_saleorder": reference})
        return RecordingClient({f"Invoices({doc_entry})": (200, payload)})

    check(
        "no U_Van_saleorder means the web path",
        r.classify_invoice(client_for(None), 42, "KEFSHOP")["path"],
        "web/API invoice (CreateInvoiceHandler)",
    )
    check(
        "a CONSOL- reference means the 18:00 consolidation",
        r.classify_invoice(client_for("CONSOL-20260908-ABC001"), 42, "KEFSHOP")["path"],
        "18:00 consolidation (ConsolidateDailySalesHandler)",
    )
    check(
        "a van warehouse means the van route",
        r.classify_invoice(client_for("DS-0001"), 42, "VAN004")["path"],
        "van end-of-day (VanSalesEndOfDayPostingService)",
    )
    check(
        "a DS- reference from a shop means the till",
        r.classify_invoice(client_for("DS-0001"), 42, "KEFSHOP")["path"],
        "till sale (DesktopSalePostingService)",
    )
    check(
        "an unreadable invoice is unknown, not guessed",
        r.classify_invoice(RecordingClient({"Invoices(42)": (404, "")}), 42, "KEFSHOP")["path"],
        "unknown",
    )


def test_attribution_degrades_when_oinm_is_refused() -> None:
    """OINM is the one table here not confirmed against working application code.

    If SAP refuses the statement the census must still stand, and the operator must
    be told which columns to look at rather than shown a stack trace.
    """
    client = RecordingClient({"SQLQueries('NEGSTK_MOVES": (404, ""), "SQLQueries": (400, "Invalid column name 'BASE_REF'")})
    result = r.attribute(client, [{"itemCode": "CHE011", "warehouseCode": "KEFSHOP", "onHand": -5.0}], 30, 50)
    check("a refused OINM query does not throw", result["available"], False)
    check("the failure names what to check", "OINM column names" in result["hint"], True)


# ---------------------------------------------------------------------------
# 6. End to end
# ---------------------------------------------------------------------------


def test_full_run_against_a_fake_service_layer() -> None:
    warehouse_rows = {
        "value": [
            {"ItemCode": "CHE011", "ItemName": "Cheddar 1kg", "WhsCode": "KEFSHOP", "OnHand": -12.5, "IsCommited": 3, "OnOrder": 0},
            {"ItemCode": "BON001", "ItemName": "Bonnita 500g", "WhsCode": "VAN004", "OnHand": -2, "IsCommited": 0, "OnOrder": 0},
        ]
    }
    batch_rows = {"value": [{"ItemCode": "CHE011", "ItemName": "Cheddar 1kg", "DistNumber": "B2609", "WhsCode": "KEFSHOP", "Quantity": -12.5}]}

    warehouse_code = r.content_addressed_code("NEGSTK_WHS", r.SQL_WAREHOUSE_NEGATIVES)
    batch_code = r.content_addressed_code("NEGSTK_BATCH", r.SQL_BATCH_NEGATIVES)

    client = RecordingClient(
        {
            f"SQLQueries('{warehouse_code}')/List": (200, json.dumps(warehouse_rows)),
            f"SQLQueries('{batch_code}')/List": (200, json.dumps(batch_rows)),
            "SQLQueries(": (404, ""),
            "CompanyService_GetAdminInfo": (200, json.dumps({"BlockStockNegativeQuantity": "tNO"})),
        }
    )

    setting = r.read_block_negative_setting(client)
    check("the block-negative setting is read", (setting["available"], setting["blocking"]), (True, False))

    result = {"readAt": "2026-09-08T09:00:00", "companyDb": "KEFALOS_USD_NEW2", "blockNegativeInventory": setting}
    result.update(r.census(client))
    result["attribution"] = None

    check("both negative warehouse rows are read", len(result["warehouseNegatives"]), 2)
    check("the negative batch row is read", result["batchNegatives"][0]["batchNumber"], "B2609")
    check("quantities survive as numbers", result["warehouseNegatives"][0]["onHand"], -12.5)

    # The report must render without raising -- it is the only output most runs produce.
    import io
    import contextlib

    buffer = io.StringIO()
    with contextlib.redirect_stdout(buffer):
        r.report(result)
    rendered = buffer.getvalue()

    check("the report names the warehouses", "KEFSHOP" in rendered and "VAN004" in rendered, True)
    check("the report calls out an unblocked company", "OFF" in rendered, True)
    check("the report says attribution was not run", "Pass --attribute" in rendered, True)

    # And with nothing negative at all.
    buffer = io.StringIO()
    with contextlib.redirect_stdout(buffer):
        r.report({**result, "warehouseNegatives": [], "batchNegatives": []})
    check("a clean company renders too", "None." in buffer.getvalue(), True)


def main() -> int:
    test_fingerprint_matches_the_application()
    test_normalizer_matches_the_application()
    test_statements_carry_no_interpolation()
    test_find_crossing()
    test_parameters_are_quoted_and_escaped()
    test_ensure_query_creates_once_and_never_deletes()
    test_classify_invoice_names_the_posting_path()
    test_attribution_degrades_when_oinm_is_refused()
    test_full_run_against_a_fake_service_layer()

    print()
    if FAILURES:
        print(f"{len(FAILURES)} failed: {', '.join(FAILURES)}")
        return 1
    print("all checks passed")
    return 0


if __name__ == "__main__":
    sys.exit(main())
