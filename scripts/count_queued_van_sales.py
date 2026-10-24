"""Point every van sales report read at VanSaleReservations instead of confirmed-only.

Two shapes, rewritten mechanically:
  A. db.StockReservations ... reservation.Status == ReservationStatus.Confirmed
     -> VanSaleReservations.Sold(db) with the status clause removed.
  B. .Where(line => line.Reservation.Status == ReservationStatus.Confirmed)
     -> .Where(line => VanSaleReservations.SoldIds(db).Contains(line.ReservationId))

Usage: python count_queued_van_sales.py <repo root>. Idempotent: a second run changes nothing.
"""
import re
import sys
from pathlib import Path

ROOT = Path(sys.argv[1])
FILES = [
    "ShopInventory/Features/VanSalesReports/Queries/VanSalesFactReader.cs",
    "ShopInventory/Features/VanSalesReports/Queries/VanDayPlans.cs",
    "ShopInventory/Features/RouteCustomers/Queries/GetRouteCustomerSales/GetRouteCustomerSalesHandler.cs",
    "ShopInventory/Features/RouteCustomers/Queries/GetRouteCustomerSalesSummary/GetRouteCustomerSalesSummaryHandler.cs",
    "ShopInventory/Features/RouteCustomers/Queries/GetRouteCustomerProductMix/GetRouteCustomerProductMixHandler.cs",
    "ShopInventory/Features/RouteCustomers/Queries/GetRouteCustomerProductSales/GetRouteCustomerProductSalesHandler.cs",
]
USING = "using ShopInventory.Features.VanSalesReports.Queries;"
NAMESPACE_OF_HELPER = "namespace ShopInventory.Features.VanSalesReports.Queries;"

# A: a reservation query whose Where carries the confirmed clause, within the next few lines.
SOURCE_A = re.compile(
    r"db\.StockReservations(\s*\.AsNoTracking\(\)(?:\s*\.Include\([^)]*\))*\s*\.Where\(reservation =>)"
)
CLAUSE_INLINE = re.compile(r"\n\s*&& reservation\.Status == ReservationStatus\.Confirmed")
CLAUSE_OWN_WHERE = re.compile(r"\n\s*\.Where\(reservation => reservation\.Status == ReservationStatus\.Confirmed\)")
LEAD_CLAUSE = re.compile(r"\.Where\(reservation => reservation\.Status == ReservationStatus\.Confirmed\)")
LINES_B = re.compile(r"\.Where\(line => line\.Reservation\.Status == ReservationStatus\.Confirmed\)")


def rewrite(text: str) -> tuple[str, int]:
    changes = 0

    text, n = LINES_B.subn(
        ".Where(line => VanSaleReservations.SoldIds(db).Contains(line.ReservationId))", text)
    changes += n

    out = []
    pos = 0
    for match in SOURCE_A.finditer(text):
        # Only a query whose own filter (up to the statement's end) names the confirmed status.
        end = text.find(";", match.end())
        statement = text[match.start():end]
        if "ReservationStatus.Confirmed" not in statement:
            continue
        rewritten = "VanSaleReservations.Sold(db)" + statement[len("db.StockReservations"):]
        rewritten = CLAUSE_INLINE.sub("", rewritten, count=1)
        rewritten = CLAUSE_OWN_WHERE.sub("", rewritten, count=1)
        out.append(text[pos:match.start()])
        out.append(rewritten)
        pos = end
        changes += 1
    out.append(text[pos:])
    text = "".join(out)

    # A query whose only Where is the status clause, on a separate line after the source.
    text, n = re.subn(
        r"db\.StockReservations(\s*\.AsNoTracking\(\))((?:\s*\.Where\(reservation => reservation\.RouteCustomerCode != null\))?)"
        r"\s*\.Where\(reservation => reservation\.Status == ReservationStatus\.Confirmed\)",
        r"VanSaleReservations.Sold(db)\1\2", text)
    changes += n
    return text, changes


def main() -> None:
    total = 0
    for rel in FILES:
        path = ROOT / rel
        raw = path.read_bytes().decode("utf-8-sig")
        newline = "\r\n" if "\r\n" in raw else "\n"
        text = raw.replace("\r\n", "\n")
        text, changes = rewrite(text)
        if changes and NAMESPACE_OF_HELPER not in text and USING not in text:
            # After the last using line at the top of the file.
            lines = text.split("\n")
            last_using = max(i for i, line in enumerate(lines) if line.startswith("using "))
            lines.insert(last_using + 1, USING)
            text = "\n".join(lines)
        path.write_bytes(text.replace("\n", newline).encode("utf-8"))
        left = len(re.findall(r"Status == ReservationStatus\.Confirmed", text))
        print(f"{rel}: {changes} rewritten, {left} confirmed-only clause(s) left")
        total += changes
    print(f"total rewritten: {total}")


if __name__ == "__main__":
    main()
