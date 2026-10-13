"""Turn the VANSALES DAILY REPORT workbook into the data file Check-VanDailyReport.sql reads.

The production database cannot be reached from a laptop and the workbook is not on the server, so
the sheet's rows travel as a file. This writes them as one psql statement that parks the rows, as
JSON, in the psql variable `sheet_json`:

    python -I scripts/VanDailyReport/export_sheet.py "VANSALES DAILY REPORT OCTOBER.xlsx"

which writes scripts/van-daily-report-sheet.sql. Copy that file, Check-VanDailyReport.sql and
Check-VanDailyReport.ps1 to the server and run the .ps1.

Standard library only. Reads the 'Raw Data' sheet, finds its columns by their headings, and takes
each formula cell's cached value, so the workbook must have been saved by Excel.

The sheet names a truck by territory and the system by van account. TRUCKS pairs them. The pairing
was read off the account names, not out of any table, so the SQL prints it beside the reps on each
account. Override one with --truck "UPC 2=VAN015".
"""
import argparse
import datetime
import json
import pathlib
import re
import sys
import xml.etree.ElementTree as ET
import zipfile

MAIN = "http://schemas.openxmlformats.org/spreadsheetml/2006/main"
REL = "http://schemas.openxmlformats.org/officeDocument/2006/relationships"
PKG = "http://schemas.openxmlformats.org/package/2006/relationships"
NS = {"m": MAIN, "pr": PKG}

SHEET = "Raw Data"
DOLLAR_TAG = "$sheet$"

# Sheet territory -> the van account its reps sign in under (Users.AssignedBusinessPartnerCode).
TRUCKS = {
    "CBD/CHITUNGWIZA": "VAN010",
    "EAST 1 & 2": "VAN013",
    "WEST 1": "VAN009",
    "WEST 2": "VAN008",
    "UPC 1": "VAN014",
    "UPC 2": "VAN015",
    "UPC 3": "VAN016",
    "UPC 4": "VAN017",
    "BULAWAYO LOCAL": "VAN018",
    "BULAWAYO UPC": "VAN019",
}

# Output field -> its column heading, or how the heading starts, lower-cased.
COLUMNS = {
    "date": "date",
    "territory": "territory",
    "route": "route",
    "customers": "number of customers",
    "dep": "departure time",
    "ccr": "ccr",
    "pcr": "pcr",
    "rti": "rti",
    "aos": "aos",
    "sales": "actual day sales",
    "new_customers": "new customers",
    "back": "turn round time",
    "ecocash": "ecocash",
    "account": "account customers",
    "odo_open": "opening odometer",
    "odo_close": "closing odometer",
    "km": "total kms travelled",
    "fuel": "fuel allocated",
}
NUMBERS = ("customers", "ccr", "pcr", "rti", "aos", "sales", "new_customers", "ecocash", "account",
           "odo_open", "odo_close", "km", "fuel")


def normalise(text):
    return " ".join(str(text).split()).upper()


def read_sheet(path, sheet_name):
    """The sheet as {row number: {column letters: cached value}}."""
    with zipfile.ZipFile(path) as package:
        strings = []
        if "xl/sharedStrings.xml" in package.namelist():
            for item in ET.fromstring(package.read("xl/sharedStrings.xml")).findall("m:si", NS):
                strings.append("".join(node.text or "" for node in item.iter("{%s}t" % MAIN)))

        workbook = ET.fromstring(package.read("xl/workbook.xml"))
        rels = ET.fromstring(package.read("xl/_rels/workbook.xml.rels"))
        targets = {rel.get("Id"): rel.get("Target") for rel in rels.findall("pr:Relationship", NS)}
        part = None
        for sheet in workbook.find("m:sheets", NS):
            if sheet.get("name") == sheet_name:
                target = targets[sheet.get("{%s}id" % REL)]
                part = target[1:] if target.startswith("/") else "xl/" + target
        if part is None:
            raise SystemExit("%s has no sheet called '%s'." % (path, sheet_name))

        rows = {}
        for cell in ET.fromstring(package.read(part)).iter("{%s}c" % MAIN):
            column, row = re.match(r"([A-Z]+)(\d+)", cell.get("r")).groups()
            value = cell.find("m:v", NS)
            if cell.get("t") == "s" and value is not None:
                text = strings[int(value.text)]
            elif cell.get("t") == "inlineStr":
                text = "".join(node.text or "" for node in cell.iter("{%s}t" % MAIN))
            else:
                text = value.text if value is not None else None
            if text is not None and str(text).strip() != "":
                rows.setdefault(int(row), {})[column] = text
        return rows


def number(value):
    try:
        return float(value)
    except (TypeError, ValueError):
        return None


def clock(value, warnings, where):
    """The sheet types a time as a number: 748 is 07:48, 2116 is 21:16."""
    typed = number(value)
    if typed is None:
        return None
    hours, minutes = divmod(int(round(typed)), 100)
    if typed != int(typed) or not (0 <= hours < 24 and 0 <= minutes < 60):
        warnings.append("%s: '%s' is not a time, left blank" % (where, value))
        return None
    return "%02d:%02d" % (hours, minutes)


def build(path, trucks):
    rows = read_sheet(path, SHEET)
    headings = {column: str(text).strip().lower() for column, text in rows.get(1, {}).items()}
    letters = {}
    for field, start in COLUMNS.items():
        # The exact heading wins: 'Date' is also how 'Date-territory lookup key' starts.
        matches = ([column for column, heading in headings.items() if heading == start]
                   or [column for column, heading in headings.items() if heading.startswith(start)])
        if len(matches) != 1:
            raise SystemExit("'%s' needs exactly one column starting '%s'; found %d."
                             % (SHEET, start, len(matches)))
        letters[field] = matches[0]

    records, warnings, unknown = [], [], set()
    for row_number in sorted(rows):
        row = rows[row_number]
        serial = number(row.get(letters["date"]))
        territory = row.get(letters["territory"])
        if row_number == 1 or serial is None or territory is None:
            continue

        truck = trucks.get(normalise(territory))
        if truck is None:
            unknown.add(territory)
            continue

        where = "row %d" % row_number
        day = datetime.date(1899, 12, 30) + datetime.timedelta(days=int(serial))
        route = row.get(letters["route"])
        record = {
            "d": day.isoformat(),
            "territory": " ".join(str(territory).split()),
            "truck": truck,
            "route": str(route).strip() if route is not None else None,
            # A row with a date and a truck and no route is a day the truck did not report.
            "submitted": route is not None,
            "dep": clock(row.get(letters["dep"]), warnings, where + " departure"),
            "back": clock(row.get(letters["back"]), warnings, where + " turn round"),
        }
        for field in NUMBERS:
            value = number(row.get(letters[field]))
            if value is not None:
                # 45 customers, not 45.0: a whole number goes out as one so psql prints it as one.
                value = int(value) if value == int(value) else round(value, 4)
            record[field] = value
        records.append(record)

    if unknown:
        raise SystemExit("No van account for: %s. Pass --truck \"<territory>=<account>\"."
                         % ", ".join(sorted(unknown)))
    if not records:
        raise SystemExit("'%s' has no dated rows." % SHEET)
    return records, warnings


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("workbook")
    parser.add_argument("--out", default=str(pathlib.Path(__file__).resolve().parent.parent
                                             / "van-daily-report-sheet.sql"))
    parser.add_argument("--truck", action="append", default=[], metavar="TERRITORY=ACCOUNT")
    args = parser.parse_args()

    trucks = dict(TRUCKS)
    for pair in args.truck:
        territory, _, account = pair.partition("=")
        if not account.strip():
            raise SystemExit("--truck wants TERRITORY=ACCOUNT, got '%s'." % pair)
        trucks[normalise(territory)] = account.strip().upper()

    records, warnings = build(args.workbook, trucks)
    payload = json.dumps(records, ensure_ascii=True, separators=(",", ":"))
    if DOLLAR_TAG in payload:
        raise SystemExit("The sheet contains the text %s, which the data file quotes with." % DOLLAR_TAG)

    days = sorted(record["d"] for record in records)
    lines = [
        "-- Rows of '%s' from %s, %s to %s." % (SHEET, pathlib.Path(args.workbook).name, days[0], days[-1]),
        "-- Written by scripts/VanDailyReport/export_sheet.py. Do not edit; run it again.",
        "-- Read by Check-VanDailyReport.sql, which Check-VanDailyReport.ps1 runs straight after this file.",
        "SELECT %s%s%s AS sheet_json \\gset" % (DOLLAR_TAG, payload, DOLLAR_TAG),
        "",
    ]
    pathlib.Path(args.out).write_text("\n".join(lines), encoding="ascii", newline="\n")

    def total(field, only=None):
        return sum(record[field] or 0 for record in records if only is None or record[only])

    submitted = [record for record in records if record["submitted"]]
    print("%s" % args.out)
    print("  %d rows, %d submitted, %s to %s, %d trucks"
          % (len(records), len(submitted), days[0], days[-1], len({r["truck"] for r in records})))
    print("  sales %.2f  ecocash %.2f  account %.2f  km %.2f  fuel %.2f"
          % (total("sales"), total("ecocash"), total("account"), total("km"), total("fuel")))
    for warning in warnings:
        print("  warning: " + warning, file=sys.stderr)


if __name__ == "__main__":
    main()
