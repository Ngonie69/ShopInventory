"""Turn the office's four UPC route workbooks into one table of outlets.

    python -I scripts/UpcCustomerImport/read_upc_workbooks.py <folder holding the four .xlsx>

writes scripts/UpcCustomerImport/out/outlets.csv and headings.csv (git-ignored: customer names and
phone numbers stay out of this public repo).

Each workbook is a pair of sheets, one per cycle week, and every sheet is laid out differently: some
give title, contact and phone beside the outlet, some only a ✅/🚫 mark. So the columns are declared
per sheet below rather than guessed. A row is an outlet when it has a mark, a title, a contact, a
phone or a sequence number; a name with none of them is a town or area sub-heading ("Shamva",
"Bindura Locations/ Rank") and becomes the town of the outlets under it. Every heading is written to
headings.csv so a shop mistaken for one can be spotted.

Standard library only; reuses the xlsx reader in scripts/VanDailyReport/export_sheet.py.
"""
import csv
import pathlib
import re
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parent.parent / "VanDailyReport"))
from export_sheet import read_sheet  # noqa: E402

MON, TUE, WED, THU, FRI = "Monday", "Tuesday", "Wednesday", "Thursday", "Friday"

# Route -> the van account its reps sell on. Same pairing as scripts/VanDailyReport/export_sheet.py;
# the server export prints the reps on each account so it can be confirmed before anything is written.
ACCOUNTS = {"UPC1": "VAN014", "UPC2": "VAN015", "UPC3": "VAN016", "UPC4": "VAN017"}


def block(days, name, status=None, title=None, contact=None, phone=None, seq=None, rows=(1, 9999),
          town=""):
    return dict(days=days, name=name, status=status, title=title, contact=contact, phone=phone,
                seq=seq, rows=rows, town=town)


# (file, sheet, route, cycle week, blocks). Week numbers follow VanSalesRouteSeedData's week1/week2.
SHEETS = [
    ("UPC 1.xlsx", "Masvingo Chiredzi", "UPC1", 1, [
        block([MON], "C", title="D", contact="E", phone="F", seq="G", rows=(3, 9999)),
        block([TUE], "K", title="L", contact="M", phone="N", seq="P", rows=(3, 9999)),
        block([WED], "T", title="U", contact="V", phone="W", seq="X", rows=(3, 9999)),
        block([THU], "AA", title="AB", contact="AC", phone="AD", seq="AE", rows=(3, 9999)),
        block([FRI], "AH", title="AI", contact="AJ", phone="AK", seq="AL", rows=(2, 9999)),
    ]),
    ("UPC 1.xlsx", "Mvurwi Guruve Centenary", "UPC1", 2, [
        block([MON], "B", title="C", contact="D", phone="E", seq="F", rows=(4, 9999)),
        block([TUE], "I", title="J", contact="K", phone="L", seq="M", rows=(4, 9999)),
        block([WED], "P", title="Q", contact="R", phone="S", seq="T", rows=(4, 9999)),
        block([THU], "W", title="X", contact="Y", phone="Z", seq="AA", rows=(4, 9999)),
        block([FRI], "AD", title="AE", contact="AF", phone="AG", seq="AH", rows=(4, 9999)),
    ]),
    ("UPC 2 Customers.xlsx", "CHIVHU,ZVISHAVANE", "UPC2", 1, [
        block([MON], "B", status="A", rows=(2, 9999)),
        block([TUE], "E", status="D", rows=(2, 9999)),
        block([WED], "H", status="G", rows=(2, 9999)),
    ]),
    ("UPC 2 Customers.xlsx", "RUSAPE,MURAMBINDA", "UPC2", 2, [
        block([MON], "B", status="A", rows=(2, 9999)),
        block([TUE, WED], "F", status="E", rows=(2, 9999)),
        block([THU], "J", status="I", rows=(2, 9999)),
        block([FRI], "N", status="M", rows=(2, 9999)),
    ]),
    ("UPC 3 Database.xlsx", "CHEGUTU KWEKWE", "UPC3", 1, [
        block([MON], "B", status="A", rows=(2, 9999)),
        block([TUE], "E", status="D", rows=(2, 9999)),
        block([WED], "H", status="G", rows=(2, 9999)),
        block([THU], "K", status="J", rows=(2, 9999)),
        block([FRI], "N", status="M", rows=(2, 9999)),
    ]),
    ("UPC 3 Database.xlsx", "BINDURA MUTOKO", "UPC3", 2, [
        # Monday has no town row: the Bindura shops come first, then "Bindura Locations/ Rank" and "Shamva".
        block([MON], "A", title="B", contact="C", phone="D", rows=(2, 9999), town="Bindura"),
        # Tuesday is two lists: Murewa with contacts in F:I, then from row 27 a Mutawatawa list of
        # names in H with marks in G.
        block([TUE], "F", title="G", contact="H", phone="I", rows=(2, 26)),
        block([TUE], "H", status="G", rows=(27, 9999)),
        block([WED], "L", status="K", title="M", contact="N", phone="O", rows=(2, 9999)),
        block([THU], "Q", title="R", contact="S", phone="T", rows=(2, 9999)),
        block([FRI], "V", title="W", contact="X", phone="Y", rows=(2, 9999)),
    ]),
    ("UPC 4 Customers.xlsx", "CHINHOYI,KARIBA", "UPC4", 1, [
        block([MON], "B", status="A", rows=(2, 9999)),
        block([TUE], "E", status="D", rows=(2, 9999)),
        block([WED], "H", status="G", rows=(2, 9999)),
        block([THU], "K", status="J", rows=(2, 9999)),
        block([FRI], "N", status="M", rows=(2, 9999)),
    ]),
    ("UPC 4 Customers.xlsx", "MACHEKE,MUTARE", "UPC4", 2, [
        block([MON], "B", status="A", rows=(2, 9999)),
        block([TUE], "E", status="D", rows=(2, 9999)),
        block([WED], "H", status="G", rows=(2, 9999)),
        block([THU, FRI], "K", status="J", rows=(2, 9999)),
    ]),
]

# Column labels, not names.
LABELS = {"outlet name", "outlet id", "title", "tittle", "contact name", "contact number"}

# Cells that sit on the town row and carry a mark, but are the day's town, not a shop.
FORCED_HEADINGS = {("CHINHOYI,KARIBA", "K2")}

# Heading text -> the town it stands for.
TOWN_NAMES = {"MASVINGO LOCATIONS": "Masvingo"}


def text(row, column):
    if column is None:
        return ""
    return " ".join(str(row.get(column, "")).split())


def code_of(name):
    """The code CreateRouteCustomerHandler would give this name: letters and digits, upper-cased."""
    out = []
    for character in name.strip().upper():
        if character.isalnum():
            out.append(character)
        elif character in "-_":
            out.append("-")
    return "".join(out).strip("-")[:50]


def phone_of(raw):
    """0 + nine digits starting 7, or None (with the raw value kept for the report)."""
    digits = re.sub(r"\D", "", raw.split(".")[0] if re.fullmatch(r"\d+\.0+", raw) else raw)
    if digits.startswith("263"):
        digits = digits[3:]
    digits = digits.lstrip("0")
    return "0" + digits if re.fullmatch(r"7\d{8}", digits) else None


def main(folder):
    folder = pathlib.Path(folder)
    out = pathlib.Path(__file__).resolve().parent / "out"
    out.mkdir(exist_ok=True)
    outlets, headings = [], []

    for file_name, sheet, route, week, blocks in SHEETS:
        rows = read_sheet(folder / file_name, sheet)
        for spec in blocks:
            town = spec["town"]
            first, last = spec["rows"]
            for row_number in sorted(r for r in rows if first <= r <= last):
                row = rows[row_number]
                name = text(row, spec["name"])
                ref = "%s!%s%d" % (sheet, spec["name"], row_number)
                if not name or not any(c.isalpha() for c in name) or name.lower() in LABELS:
                    continue
                if name.upper() in {d.upper() for d in (MON, TUE, WED, THU, FRI)}:
                    continue

                mark = text(row, spec["status"])
                title, contact = text(row, spec["title"]), text(row, spec["contact"])
                phone_raw, seq = text(row, spec["phone"]), text(row, spec["seq"])
                status = "blocked" if "🚫" in mark else "active" if "✅" in mark else ""
                is_shop = bool(status or title or contact or phone_raw or seq)

                if not is_shop or (sheet, "%s%d" % (spec["name"], row_number)) in FORCED_HEADINGS:
                    town = TOWN_NAMES.get(name.upper(), name)
                    headings.append(dict(route=route, week=week, ref=ref, days="+".join(spec["days"]),
                                         heading=name))
                    continue

                if name.islower():
                    # Some lists are typed all lower case ("fair price cc"); the handset shows this name.
                    name = " ".join(word[:1].upper() + word[1:] for word in name.split())
                phone = phone_of(phone_raw) if phone_raw else None
                flags = []
                if phone_raw and not phone:
                    flags.append("bad phone '%s'" % phone_raw)
                if not (title or contact or phone_raw) and spec["phone"]:
                    flags.append("no contact details")
                outlets.append(dict(
                    route=route, account=ACCOUNTS[route], week=week, ref=ref,
                    days="+".join(spec["days"]), town=town, name=name, code=code_of(name),
                    title=title.capitalize(), contact=contact, phone=phone or "",
                    phone_raw=phone_raw, status=status or "active", seq=seq, flags="; ".join(flags)))

    with open(out / "outlets.csv", "w", newline="", encoding="utf-8-sig") as handle:
        writer = csv.DictWriter(handle, fieldnames=list(outlets[0]))
        writer.writeheader()
        writer.writerows(outlets)
    with open(out / "headings.csv", "w", newline="", encoding="utf-8-sig") as handle:
        writer = csv.DictWriter(handle, fieldnames=list(headings[0]))
        writer.writeheader()
        writer.writerows(headings)

    for route in sorted(ACCOUNTS):
        mine = [o for o in outlets if o["route"] == route]
        print("%s (%s): %d outlet rows, %d blocked, %d with a phone, %d distinct codes"
              % (route, ACCOUNTS[route], len(mine), sum(o["status"] == "blocked" for o in mine),
                 sum(bool(o["phone"]) for o in mine), len({o["code"] for o in mine})))
    print("%d headings, %d flagged rows -> %s" % (len(headings), sum(bool(o["flags"]) for o in outlets), out))


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit(__doc__)
    main(sys.argv[1])
