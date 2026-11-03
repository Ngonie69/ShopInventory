"""Builds the month-on-month Shop & Vending workbook from Export-CounterSalesMonths.ps1's CSVs.

Four reports, each the previous month against the current one:
  1. Complimentary lines by shop     (SAP item group 150 "Complimentary Products")
  2. Cash vs plastic by shop         (the sale's tender; plastic = Swipe)
  3. Shamiso ice cream by shop       (Ice Cream group items named Shamiso; the flavours added
                                      from May 2026 are flagged "New")
  4. Product lines by shop, and for vending (SAP item group)

Reports 1, 3, 4 and the summary come from SAP invoices less credit notes on each shop's and vending
point's customer account, so both months are complete. Report 2 comes from the till export, the
only record of how each sale was paid; tills went live in mid-September 2026, so that report
compares shares and per-day averages. Item values are before VAT; tender values include VAT.

Reads SAP (read-only GETs) for item groups and warehouse names with the API's user-secrets,
company KEFALOS_USD_NEW2. With --sap-cache, SAP answers are saved there and reused on the next run.

  python Build-CounterSalesMonthsWorkbook.py <export folder or .zip> --prev 2026-09 --cur 2026-10 -o out.xlsx
"""
import argparse
import csv
import io
import json
import os
import sys
import zipfile
from collections import defaultdict
from datetime import datetime

from openpyxl import Workbook
from openpyxl.styles import Alignment, Border, Font, PatternFill, Side
from openpyxl.utils import get_column_letter

COMPLIMENTARY_GROUP = 150
ICE_CREAM_GROUP = 103
NEW_SHAMISO_FROM = "2026-05-01"
VENDING = "KefalosVending"
ZIG_SPELLINGS = {"ZWG", "ZIG", "ZWL"}
SHOP_TILL = "KefalosShopTill"
# SAP customer account per vending point (warehouse). Shops come from the Shops table's BusinessPartnerCode.
VENDING_CUSTOMERS = {"COR008": "KEFBYC", "MAC006": "CORMACH", "COR006": "KEFGRC"}

TENDERS = {
    "cash": "Cash",
    "swipe": "Swipe", "card": "Swipe", "swipemachine": "Swipe", "creditcard": "Swipe", "debitcard": "Swipe",
    "ecocash": "Ecocash",
    "innbucks": "Innbucks", "inbucks": "Innbucks",
    "transfer": "Transfer",
    "paynow": "PayNow",
}


def tender(raw):
    key = (raw or "").strip().replace("_", "").replace("-", "").replace(" ", "").lower()
    if not key:
        return "Not recorded"
    return TENDERS.get(key, raw.strip())


# ---------------------------------------------------------------- inputs

def read_export(path):
    tables = {}
    if path.lower().endswith(".zip"):
        with zipfile.ZipFile(path) as z:
            for n in z.namelist():
                if n.endswith(".csv"):
                    tables[os.path.basename(n)[:-4]] = list(csv.DictReader(io.TextIOWrapper(z.open(n), "utf-8-sig")))
    else:
        for n in os.listdir(path):
            if n.endswith(".csv"):
                with open(os.path.join(path, n), encoding="utf-8-sig", newline="") as f:
                    tables[n[:-4]] = list(csv.DictReader(f))
    missing = {"shops", "sales_by_tender", "lines_by_item", "credits", "trading_days"} - tables.keys()
    if missing:
        sys.exit(f"Export is missing {sorted(missing)}")
    return tables


def sap_session():
    import requests
    import urllib3
    urllib3.disable_warnings()
    secrets_path = os.path.join(os.environ["APPDATA"], "Microsoft", "UserSecrets",
                                "2fee8ae2-b581-4db3-8bc4-de6892c0b9d8", "secrets.json")
    with open(secrets_path, encoding="utf-8-sig") as f:
        sec = json.load(f)
    base = sec["SAP:ServiceLayerUrl"].rstrip("/") + "/"
    s = requests.Session()
    s.verify = False
    s.post(base + "Login", json={"CompanyDB": "KEFALOS_USD_NEW2", "UserName": sec["SAP:Username"],
                                 "Password": sec["SAP:Password"]}, timeout=60).raise_for_status()

    def get(path, page=1000):
        url, out = base + path, []
        while url:
            r = s.get(url, headers={"Prefer": f"odata.maxpagesize={page}"}, timeout=600)
            r.raise_for_status()
            j = r.json()
            out += j["value"]
            nl = j.get("odata.nextLink")
            url = base + nl if nl else None
        return out
    return get


def read_sap_lines(cache, customers, prev, cur):
    """Invoices and credit notes with their lines for each customer account, both months, half a month per call."""
    path = os.path.join(cache, f"sap_lines_{prev}_{cur}.json") if cache else None
    if path and os.path.exists(path):
        with open(path) as f:
            return json.load(f)
    from concurrent.futures import ThreadPoolExecutor
    import threading
    local = threading.local()
    windows = []
    for mo in (prev, cur):
        y, mm = map(int, mo.split("-"))
        last = (datetime(y + (mm == 12), mm % 12 + 1, 1) - datetime(y, mm, 1)).days
        windows += [(f"{mo}-01", f"{mo}-15"), (f"{mo}-16", f"{mo}-{last}")]

    chunk_dir = os.path.join(cache, "sap_chunks") if cache else None
    if chunk_dir:
        os.makedirs(chunk_dir, exist_ok=True)

    def fetch(job):
        ent, bp, a, b = job
        chunk = os.path.join(chunk_dir, f"{ent}_{bp}_{a}_{b}.json") if chunk_dir else None
        if chunk and os.path.exists(chunk):
            with open(chunk) as f:
                return ent, json.load(f)
        if not hasattr(local, "get"):
            local.get = sap_session()  # one SAP session per worker
        for attempt in range(3):
            try:
                docs = local.get(f"{ent}?$select=DocEntry,DocNum,DocDate,CardCode,DocTotal,VatSum,Cancelled,CancelStatus,DocumentLines"
                                 f"&$filter=CardCode eq '{bp}' and DocDate ge '{a}' and DocDate le '{b}'", page=200)
                break
            except Exception as e:  # the Service Layer's slow tail; a fresh session usually answers
                if attempt == 2:
                    raise
                print(f"  retry {ent} {bp} {a}: {e}", flush=True)
                local.get = sap_session()
        # A cancellation document comes back without Cancelled; its CancelStatus says what it is.
        odd = [d for d in docs if "DocumentLines" not in d]
        if odd:
            print(f"  {ent} {bp} {a}: {len(odd)} documents without lines", flush=True)
        print(f"  {ent} {bp} {a}..{b}: {len(docs)}", flush=True)
        rows = [{"DocEntry": d["DocEntry"], "DocNum": d["DocNum"], "DocDate": d["DocDate"][:10],
                 "CardCode": d["CardCode"], "DocTotal": d["DocTotal"], "VatSum": d["VatSum"],
                 "Cancelled": d.get("Cancelled"), "CancelStatus": d.get("CancelStatus"),
                 "Lines": [{"ItemCode": l["ItemCode"], "Desc": l["ItemDescription"], "Qty": l["Quantity"],
                            "LineTotal": l["LineTotal"], "Whs": l["WarehouseCode"]} for l in d.get("DocumentLines", [])]}
                for d in docs]
        if chunk:
            with open(chunk, "w") as f:
                json.dump(rows, f)
        return ent, rows

    jobs = [(ent, bp, a, b) for ent in ("Invoices", "CreditNotes") for bp in sorted(customers) for a, b in windows]
    out = {"Invoices": [], "CreditNotes": []}
    with ThreadPoolExecutor(max_workers=4) as pool:
        for ent, rows in pool.map(fetch, jobs):
            out[ent] += rows
    if path:
        with open(path, "w") as f:
            json.dump(out, f)
    return out


def read_sap(cache):
    if cache and os.path.exists(os.path.join(cache, "items.json")):
        with open(os.path.join(cache, "items.json")) as f:
            items = json.load(f)
        with open(os.path.join(cache, "warehouses.json")) as f:
            warehouses = json.load(f)
        return items, warehouses

    get = sap_session()
    groups = {str(g["Number"]): g["GroupName"] for g in get("ItemGroups?$select=Number,GroupName")}
    items = {"groups": groups,
             "items": get("Items?$select=ItemCode,ItemName,ItemsGroupCode,SalesItem,CreateDate")}
    warehouses = get("Warehouses?$select=WarehouseCode,WarehouseName,Inactive")
    if cache:
        os.makedirs(cache, exist_ok=True)
        with open(os.path.join(cache, "items.json"), "w") as f:
            json.dump(items, f)
        with open(os.path.join(cache, "warehouses.json"), "w") as f:
            json.dump(warehouses, f)
    return items, warehouses


# ---------------------------------------------------------------- model

class Model:
    def __init__(self, t, sap_items, sap_warehouses, prev, cur, zig_rate):
        self.prev, self.cur, self.zig_rate = prev, cur, zig_rate
        self.months = (prev, cur)
        self.groups = {int(k): v for k, v in sap_items["groups"].items()}
        self.items = {i["ItemCode"]: i for i in sap_items["items"]}
        self.currencies = defaultdict(lambda: defaultdict(float))  # month -> currency -> gross native
        self.unknown_currencies = set()

        names = {}
        for row in t["shops"]:  # ordered active first per warehouse
            names.setdefault(row["WarehouseCode"], row["Name"])
        self.shop_names = names
        self.wh_names = {w["WarehouseCode"]: w["WarehouseName"] for w in sap_warehouses}

        self.sales = [r for r in t["sales_by_tender"] if r["Month"] in self.months]
        self.lines = [r for r in t["lines_by_item"] if r["Month"] in self.months]
        self.credits = [r for r in t["credits"] if r["Month"] in self.months]
        self.days = {(r["Month"], r["SourceSystem"], r["WarehouseCode"]): r for r in t["trading_days"]}
        for r in self.sales:
            self.currencies[r["Month"]][r["Currency"]] += float(r["Gross"])
        self.till_start = {(r["SourceSystem"], r["WarehouseCode"]): r["FirstDay"]
                           for r in sorted(t["trading_days"], key=lambda r: r["FirstDay"], reverse=True)}
        self.sap_docs = None

    def use_sap(self, sap_lines, customers):
        """Item lines and month totals from SAP invoices less credit notes, for complete months.

        customers maps CardCode -> (SourceSystem, WarehouseCode), so a document lands on its shop or
        vending point whichever warehouse its lines were issued from. Line totals are USD (SAP's local
        currency) and before VAT. Cancelled documents and the credit notes that cancel them both drop out.
        """
        self.lines = []
        self.sap_docs = defaultdict(lambda: {mo: {"inv": 0.0, "cred": 0.0, "n": 0} for mo in self.months})
        self.sap_skipped = defaultdict(int)
        for ent, sign in (("Invoices", 1), ("CreditNotes", -1)):
            for d in sap_lines[ent]:
                mo = d["DocDate"][:7]
                if mo not in self.months or d["CardCode"] not in customers:
                    continue
                if d["Cancelled"] == "tYES" or d.get("CancelStatus") == "csCancellation":
                    self.sap_skipped[ent] += 1
                    continue
                src, wh = customers[d["CardCode"]]
                cell = self.sap_docs[(self.business(src), src, wh)][mo]
                if sign > 0:
                    cell["inv"] += d["DocTotal"]
                    cell["n"] += 1
                else:
                    cell["cred"] += d["DocTotal"]
                for ln in d["Lines"]:
                    self.lines.append({"Month": mo, "SourceSystem": src, "WarehouseCode": wh,
                                       "ItemCode": ln["ItemCode"], "ItemDescription": ln["Desc"], "Currency": "USD",
                                       "Quantity": sign * ln["Qty"], "NetValue": sign * ln["LineTotal"]})

    def usd(self, amount, currency):
        c = (currency or "").strip().upper()
        if c == "USD":
            return float(amount)
        if c in ZIG_SPELLINGS:
            return float(amount) / self.zig_rate
        self.unknown_currencies.add(currency)
        return float(amount)

    def place(self, source, warehouse):
        if source == VENDING:
            return self.wh_names.get(warehouse, warehouse)
        return self.shop_names.get(warehouse) or self.wh_names.get(warehouse, warehouse)

    def business(self, source):
        return "Vending" if source == VENDING else "Shops"

    def item(self, code):
        return self.items.get(code, {})

    def group_name(self, code):
        g = self.item(code).get("ItemsGroupCode")
        return self.groups.get(g, "Not in SAP") if g is not None else "Not in SAP"

    def is_complimentary(self, code):
        return self.item(code).get("ItemsGroupCode") == COMPLIMENTARY_GROUP

    def is_shamiso(self, code):
        i = self.item(code)
        return i.get("ItemsGroupCode") == ICE_CREAM_GROUP and "shamiso" in (i.get("ItemName") or "").lower()

    def is_new_shamiso(self, code):
        return self.is_shamiso(code) and (self.item(code).get("CreateDate") or "") >= NEW_SHAMISO_FROM

    def item_name(self, code, fallback):
        return self.item(code).get("ItemName") or fallback or code

    # (business, place) -> month -> [qty, value usd]
    def lines_by(self, keyfn, business=None, where=lambda code: True):
        out = defaultdict(lambda: {m: [0.0, 0.0] for m in self.months})
        for r in self.lines:
            if business and self.business(r["SourceSystem"]) != business:
                continue
            if not where(r["ItemCode"]):
                continue
            k = keyfn(r)
            cell = out[k][r["Month"]]
            cell[0] += float(r["Quantity"])
            cell[1] += self.usd(r["NetValue"], r["Currency"])
        return out


# ---------------------------------------------------------------- styling

NAVY = "1F3A5F"
HEAD = PatternFill("solid", fgColor=NAVY)
SUB = PatternFill("solid", fgColor="DCE6F1")
TOTAL = PatternFill("solid", fgColor="F2F2F2")
THIN = Side(style="thin", color="BFBFBF")
MONEY = '#,##0.00;[Red]-#,##0.00;"-"'
QTY = '#,##0;[Red]-#,##0;"-"'
PCT = '0.0%;[Red]-0.0%;"-"'


def month_label(m):
    return datetime.strptime(m, "%Y-%m").strftime("%b %Y")


def title(ws, text, subtitle):
    ws["A1"] = text
    ws["A1"].font = Font(bold=True, size=14, color=NAVY)
    ws["A2"] = subtitle
    ws["A2"].font = Font(italic=True, color="595959")
    return 4


def header(ws, row, labels):
    for c, label in enumerate(labels, 1):
        cell = ws.cell(row=row, column=c, value=label)
        cell.font = Font(bold=True, color="FFFFFF")
        cell.fill = HEAD
        cell.alignment = Alignment(horizontal="center", vertical="center", wrap_text=True)
        cell.border = Border(bottom=THIN)
    ws.row_dimensions[row].height = 32


def change_cells(ws, row, prev_col, cur_col, change_col):
    p, c = get_column_letter(prev_col), get_column_letter(cur_col)
    ws.cell(row=row, column=change_col, value=f"={c}{row}-{p}{row}").number_format = MONEY
    pct = ws.cell(row=row, column=change_col + 1,
                  value=f'=IF({p}{row}=0,IF({c}{row}=0,"","new"),IF({p}{row}<0,"",{c}{row}/{p}{row}-1))')
    pct.number_format = PCT
    pct.alignment = Alignment(horizontal="right")


def sum_row(ws, row, first, last, cols, label="Total", label_col=1, fill=TOTAL):
    ws.cell(row=row, column=label_col, value=label).font = Font(bold=True)
    for c in range(1, ws.max_column + 1):
        ws.cell(row=row, column=c).fill = fill
        ws.cell(row=row, column=c).border = Border(top=THIN)
    for col, fmt in cols:
        L = get_column_letter(col)
        cell = ws.cell(row=row, column=col, value=f"=SUBTOTAL(109,{L}{first}:{L}{last})")
        cell.number_format = fmt
        cell.font = Font(bold=True)


def widths(ws, spec):
    for col, w in spec.items():
        ws.column_dimensions[col].width = w


# ---------------------------------------------------------------- sheets

def readme(wb, m, src_name):
    ws = wb.active
    ws.title = "Read me"
    r = title(ws, f"Shop & Vending: {month_label(m.cur)} vs {month_label(m.prev)}",
              f"Built {datetime.now():%d %b %Y %H:%M}. Till export {src_name}; SAP company KEFALOS_USD_NEW2.")
    starts = ", ".join(f"{m.place(src, wh)} {day[8:10]}/{day[5:7]}"
                       for (src, wh), day in sorted(m.till_start.items(), key=lambda kv: kv[1])
                       if day.startswith(m.prev))
    notes = [
        ("Scope", "The four shops and the three vending points. Van sales are not included."),
        ("Period", f"{month_label(m.prev)} and {month_label(m.cur)}, by document date. Both months are complete in every report except report 2."),
        ("Reports 1, 3, 4 and Summary", "SAP A/R invoices less A/R credit notes for each shop's and vending point's customer account. "
                                        "Cancelled documents are left out, and so are cancellations: in September 2026 these were 68 "
                                        "reversals of February-April credit notes ($4,492), a clean-up rather than trading. "
                                        "Item values are before VAT; the Summary is incl. VAT."),
        ("SAP vs till", f"For {month_label(m.cur)} SAP and the tills agree to the cent for every place except Graniteside Shop "
                        "(SAP $83.37 higher: an invoice raised in SAP, not on the till). For the months, see the Summary."),
        ("Report 2 (cash vs plastic)", "The tills record how each sale was paid; SAP does not (its daily POS receipts are keyed by hand and "
                                       "book Machipisa and Bulawayo entirely as card). The tills went live part-way through "
                                       f"{month_label(m.prev)} ({starts}), so {month_label(m.prev)} covers only the days after go-live. "
                                       "Compare the shares and the per-day averages, not the month totals."),
        ("Tender values", "Sale totals incl. VAT (the money taken). Plastic = Swipe (card). Mobile = Ecocash + Innbucks."),
        ("Currency", "All amounts USD. Every till sale in both months was in USD; SAP line totals are in USD, its local currency."),
        ("Complimentary lines", f"SAP item group {COMPLIMENTARY_GROUP} \"Complimentary Products\" (soft drinks, juices, snacks, bread and similar)."),
        ("Shamiso range", f"Items in SAP item group {ICE_CREAM_GROUP} \"Ice Cream\" whose name contains Shamiso. "
                          f"\"New\" marks the flavours created in SAP from {NEW_SHAMISO_FROM[:7]} (5 Litre Vanilla, Blueberry, Bubblegum, Banana)."),
        ("Product line", "The item's SAP item group."),
        ("Change %", "\"new\" means nothing was sold in the earlier month."),
    ]
    for k, v in notes:
        ws.cell(row=r, column=1, value=k).font = Font(bold=True)
        ws.cell(row=r, column=1).alignment = Alignment(vertical="top")
        ws.cell(row=r, column=2, value=v).alignment = Alignment(wrap_text=True, vertical="top")
        r += 1
    if m.unknown_currencies:
        r += 1
        ws.cell(row=r, column=1, value=f"Not converted (unknown currency): {sorted(m.unknown_currencies)}").font = Font(color="C00000", bold=True)
    widths(ws, {"A": 26, "B": 110})


def summary(wb, m):
    ws = wb.create_sheet("Summary")
    r = title(ws, "Sales by shop and vending point", "USD incl. VAT. SAP invoices less credit notes; complete months.")
    P, C = month_label(m.prev), month_label(m.cur)
    header(ws, r, ["Business", "Shop / vending point", "Till live from", f"Invoices {P}", f"Invoices {C}",
                   f"Sales {P}", f"Sales {C}", f"Credits {P}", f"Credits {C}", f"Net {P}", f"Net {C}", "Change", "Change %"])
    first = r + 1
    for biz in ("Shops", "Vending"):
        keys = sorted([k for k in m.sap_docs if k[0] == biz], key=lambda k: -m.sap_docs[k][m.cur]["inv"])
        for k in keys:
            r += 1
            d = m.sap_docs[k]
            ws.cell(row=r, column=1, value=biz)
            ws.cell(row=r, column=2, value=m.place(k[1], k[2]))
            start = m.till_start.get((k[1], k[2]))
            if start:
                ws.cell(row=r, column=3, value=datetime.strptime(start, "%Y-%m-%d")).number_format = "dd mmm yyyy"
            for i, mo in enumerate(m.months):
                ws.cell(row=r, column=4 + i, value=d[mo]["n"]).number_format = QTY
                ws.cell(row=r, column=6 + i, value=round(d[mo]["inv"], 2)).number_format = MONEY
                ws.cell(row=r, column=8 + i, value=round(d[mo]["cred"], 2)).number_format = MONEY
                ws.cell(row=r, column=10 + i, value=f"={get_column_letter(6 + i)}{r}-{get_column_letter(8 + i)}{r}").number_format = MONEY
            change_cells(ws, r, 10, 11, 12)
    last = r
    r += 1
    sum_row(ws, r, first, last, [(c, QTY if c < 6 else MONEY) for c in range(4, 13)])
    change_cells(ws, r, 10, 11, 12)
    ws.auto_filter.ref = f"A{first - 1}:M{last}"
    ws.freeze_panes = ws.cell(row=first, column=3)
    widths(ws, {"A": 10, "B": 30, "C": 13, "D": 10, "E": 10, **{get_column_letter(c): 14 for c in range(6, 13)}, "M": 10})


def complimentary(wb, m):
    ws = wb.create_sheet("1 Complimentary")
    r = title(ws, "1. Complimentary lines by shop",
              f"USD before VAT. SAP item group {COMPLIMENTARY_GROUP} Complimentary Products. Share = of the shop's item sales.")
    P, C = month_label(m.prev), month_label(m.cur)
    header(ws, r, ["Shop", f"Qty {P}", f"Qty {C}", f"Value {P}", f"Value {C}", "Change", "Change %",
                   f"Share {P}", f"Share {C}"])
    comp = m.lines_by(lambda x: (x["SourceSystem"], x["WarehouseCode"]), "Shops", m.is_complimentary)
    allv = m.lines_by(lambda x: (x["SourceSystem"], x["WarehouseCode"]), "Shops")
    first = r + 1
    for k in sorted(allv, key=lambda k: -comp[k][m.cur][1] if k in comp else 0):
        r += 1
        ws.cell(row=r, column=1, value=m.place(*k))
        for i, mo in enumerate(m.months):
            q, v = comp[k][mo] if k in comp else (0.0, 0.0)
            ws.cell(row=r, column=2 + i, value=q).number_format = QTY
            ws.cell(row=r, column=4 + i, value=round(v, 2)).number_format = MONEY
            tot = allv[k][mo][1]
            ws.cell(row=r, column=8 + i, value=(v / tot) if tot else 0).number_format = PCT
        change_cells(ws, r, 4, 5, 6)
    last = r
    r += 1
    sum_row(ws, r, first, last, [(2, QTY), (3, QTY), (4, MONEY), (5, MONEY), (6, MONEY)])
    change_cells(ws, r, 4, 5, 6)
    ws.freeze_panes = ws.cell(row=first, column=2)
    widths(ws, {"A": 30, "B": 11, "C": 11, "D": 14, "E": 14, "F": 13, "G": 10, "H": 10, "I": 10})

    # Item detail
    r += 3
    ws.cell(row=r, column=1, value="By item and shop").font = Font(bold=True, size=12, color=NAVY)
    r += 1
    header(ws, r, ["Shop", "Item", "Item code", f"Qty {P}", f"Qty {C}", f"Value {P}", f"Value {C}", "Change", "Change %"])
    items = m.lines_by(lambda x: (x["SourceSystem"], x["WarehouseCode"], x["ItemCode"], x["ItemDescription"]),
                       "Shops", m.is_complimentary)
    first2 = r + 1
    for k in sorted(items, key=lambda k: (m.place(k[0], k[1]), -items[k][m.cur][1])):
        r += 1
        ws.cell(row=r, column=1, value=m.place(k[0], k[1]))
        ws.cell(row=r, column=2, value=m.item_name(k[2], k[3]))
        ws.cell(row=r, column=3, value=k[2])
        for i, mo in enumerate(m.months):
            ws.cell(row=r, column=4 + i, value=items[k][mo][0]).number_format = QTY
            ws.cell(row=r, column=6 + i, value=round(items[k][mo][1], 2)).number_format = MONEY
        change_cells(ws, r, 6, 7, 8)
    ws.auto_filter.ref = f"A{first2 - 1}:I{r}"
    ws.column_dimensions["B"].width = 38 if ws.column_dimensions["B"].width < 38 else ws.column_dimensions["B"].width


def tenders(wb, m):
    ws = wb.create_sheet("2 Cash vs Plastic")
    P, C = month_label(m.prev), month_label(m.cur)
    r = title(ws, "2. Cash vs plastic money by shop",
              f"USD incl. VAT, from the tills. {P} starts at each till's go-live, so compare shares and per-day "
              "averages, not totals. Plastic = Swipe (card). Mobile = Ecocash + Innbucks.")
    buckets = ["Cash", "Plastic", "Mobile", "Other"]

    def bucket(t):
        return {"Cash": "Cash", "Swipe": "Plastic", "Ecocash": "Mobile", "Innbucks": "Mobile"}.get(t, "Other")

    val = defaultdict(lambda: {mo: defaultdict(float) for mo in m.months})
    cnt = defaultdict(lambda: {mo: defaultdict(int) for mo in m.months})
    cred = defaultdict(lambda: {mo: 0.0 for mo in m.months})
    other_kinds = set()
    for s in m.sales:
        k = (m.business(s["SourceSystem"]), s["SourceSystem"], s["WarehouseCode"])
        t = tender(s["PaymentMethod"])
        b = bucket(t)
        if b == "Other":
            other_kinds.add(t)
        val[k][s["Month"]][b] += m.usd(s["Gross"], s["Currency"])
        cnt[k][s["Month"]][b] += int(s["Sales"])
    for c in m.credits:
        k = (m.business(c["SourceSystem"]), c["SourceSystem"], c["WarehouseCode"])
        cred[k][c["Month"]] += m.usd(c["Amount"], c["Currency"])

    # B,C days | D..K the four buckets | L..O shares | P..S per day | T,U credits
    labels = ["Shop", f"Till days {P}", f"Till days {C}"]
    for b in buckets:
        labels += [f"{b} {P}", f"{b} {C}"]
    labels += [f"Cash share {P}", f"Cash share {C}", f"Plastic share {P}", f"Plastic share {C}",
               f"Cash per day {P}", f"Cash per day {C}", f"Plastic per day {P}", f"Plastic per day {C}",
               f"Till credits {P}", f"Till credits {C}"]
    header(ws, r, labels)
    first = r + 1

    def derived(row):
        for i, (col_cash, col_plastic) in enumerate((("D", "F"), ("E", "G"))):
            tot = "+".join(f"{x}{row}" for x in (["D", "F", "H", "J"] if i == 0 else ["E", "G", "I", "K"]))
            ws.cell(row=row, column=12 + i, value=f'=IF(({tot})=0,"",{col_cash}{row}/({tot}))').number_format = PCT
            ws.cell(row=row, column=14 + i, value=f'=IF(({tot})=0,"",{col_plastic}{row}/({tot}))').number_format = PCT
            days = "B" if i == 0 else "C"
            ws.cell(row=row, column=16 + i, value=f'=IF({days}{row}=0,"",{col_cash}{row}/{days}{row})').number_format = MONEY
            ws.cell(row=row, column=18 + i, value=f'=IF({days}{row}=0,"",{col_plastic}{row}/{days}{row})').number_format = MONEY

    def write_rows(keys):
        nonlocal r
        for k in keys:
            r += 1
            ws.cell(row=r, column=1, value=m.place(k[1], k[2]) + (" (vending)" if k[0] == "Vending" else ""))
            for i, mo in enumerate(m.months):
                d = m.days.get((mo, k[1], k[2]))
                ws.cell(row=r, column=2 + i, value=int(d["TradingDays"]) if d else 0)
                for bi, b in enumerate(buckets):
                    ws.cell(row=r, column=4 + bi * 2 + i, value=round(val[k][mo][b], 2)).number_format = MONEY
                ws.cell(row=r, column=20 + i, value=round(cred[k][mo], 2)).number_format = MONEY
            derived(r)

    money_cols = [(c, MONEY) for c in range(4, 12)] + [(20, MONEY), (21, MONEY)]
    shops = sorted([k for k in val if k[0] == "Shops"], key=lambda k: -sum(val[k][m.cur].values()))
    write_rows(shops)
    last = r
    r += 1
    sum_row(ws, r, first, last, money_cols, label="All shops")
    derived(r)
    for col in range(16, 20):  # a per-day average doesn't add across shops
        ws.cell(row=r, column=col, value=None)
    vend = [k for k in val if k[0] == "Vending"]
    if vend:
        r += 2
        ws.cell(row=r, column=1, value="Vending sells for cash only; shown for completeness.").font = Font(italic=True, color="595959")
        vfirst = r + 1
        write_rows(sorted(vend, key=lambda k: -sum(val[k][m.cur].values())))
        r += 1
        sum_row(ws, r, vfirst, r - 1, money_cols, label="All vending")
        derived(r)
        for col in range(16, 20):
            ws.cell(row=r, column=col, value=None)

    r += 3
    ws.cell(row=r, column=1, value="Number of sales").font = Font(bold=True, size=12, color=NAVY)
    r += 1
    header(ws, r, ["Shop", f"Till days {P}", f"Till days {C}"] + [f"{b} {mo}" for b in buckets for mo in (P, C)]
           + [f"Card sales share {P}", f"Card sales share {C}"])
    tfirst = r + 1
    for k in shops:
        r += 1
        ws.cell(row=r, column=1, value=m.place(k[1], k[2]))
        for i, mo in enumerate(m.months):
            d = m.days.get((mo, k[1], k[2]))
            ws.cell(row=r, column=2 + i, value=int(d["TradingDays"]) if d else 0)
            for bi, b in enumerate(buckets):
                ws.cell(row=r, column=4 + bi * 2 + i, value=cnt[k][mo][b]).number_format = QTY
        for i in range(2):
            tot = "+".join(f"{x}{r}" for x in (["D", "F", "H", "J"] if i == 0 else ["E", "G", "I", "K"]))
            ws.cell(row=r, column=12 + i, value=f'=IF(({tot})=0,"",{"F" if i == 0 else "G"}{r}/({tot}))').number_format = PCT
    r += 1
    sum_row(ws, r, tfirst, r - 1, [(c, QTY) for c in range(4, 12)], label="All shops")
    for i in range(2):
        tot = "+".join(f"{x}{r}" for x in (["D", "F", "H", "J"] if i == 0 else ["E", "G", "I", "K"]))
        ws.cell(row=r, column=12 + i, value=f'=IF(({tot})=0,"",{"F" if i == 0 else "G"}{r}/({tot}))').number_format = PCT
    if other_kinds:
        r += 2
        ws.cell(row=r, column=1, value="Other = " + ", ".join(sorted(other_kinds))).font = Font(italic=True, color="595959")
    ws.freeze_panes = ws.cell(row=first, column=2)
    widths(ws, {"A": 32, "B": 9, "C": 9, **{get_column_letter(c): 13 for c in range(4, 22)}})


def shamiso(wb, m):
    ws = wb.create_sheet("3 Shamiso")
    r = title(ws, "3. Shamiso ice cream by shop",
              f"USD before VAT. New = flavours created in SAP from {NEW_SHAMISO_FROM[:7]}.")
    P, C = month_label(m.prev), month_label(m.cur)
    header(ws, r, ["Shop", f"New range {P}", f"New range {C}", "Change", "Change %",
                   f"All Shamiso {P}", f"All Shamiso {C}", "Change", "Change %"])
    new = m.lines_by(lambda x: (x["SourceSystem"], x["WarehouseCode"]), "Shops", m.is_new_shamiso)
    allsh = m.lines_by(lambda x: (x["SourceSystem"], x["WarehouseCode"]), "Shops", m.is_shamiso)
    first = r + 1
    for k in sorted(allsh, key=lambda k: -allsh[k][m.cur][1]):
        r += 1
        ws.cell(row=r, column=1, value=m.place(*k))
        for i, mo in enumerate(m.months):
            ws.cell(row=r, column=2 + i, value=round(new[k][mo][1], 2) if k in new else 0).number_format = MONEY
            ws.cell(row=r, column=6 + i, value=round(allsh[k][mo][1], 2)).number_format = MONEY
        change_cells(ws, r, 2, 3, 4)
        change_cells(ws, r, 6, 7, 8)
    last = r
    r += 1
    sum_row(ws, r, first, last, [(2, MONEY), (3, MONEY), (4, MONEY), (6, MONEY), (7, MONEY), (8, MONEY)])
    change_cells(ws, r, 2, 3, 4)
    change_cells(ws, r, 6, 7, 8)

    r += 3
    ws.cell(row=r, column=1, value="By item and shop").font = Font(bold=True, size=12, color=NAVY)
    r += 1
    header(ws, r, ["Shop", "Item", "Item code", "New", f"Qty {P}", f"Qty {C}", f"Value {P}", f"Value {C}", "Change", "Change %"])
    items = m.lines_by(lambda x: (x["SourceSystem"], x["WarehouseCode"], x["ItemCode"], x["ItemDescription"]), "Shops", m.is_shamiso)
    first2 = r + 1
    for k in sorted(items, key=lambda k: (m.place(k[0], k[1]), not m.is_new_shamiso(k[2]), -items[k][m.cur][1])):
        r += 1
        ws.cell(row=r, column=1, value=m.place(k[0], k[1]))
        ws.cell(row=r, column=2, value=m.item_name(k[2], k[3]))
        ws.cell(row=r, column=3, value=k[2])
        ws.cell(row=r, column=4, value="New" if m.is_new_shamiso(k[2]) else "")
        for i, mo in enumerate(m.months):
            ws.cell(row=r, column=5 + i, value=items[k][mo][0]).number_format = QTY
            ws.cell(row=r, column=7 + i, value=round(items[k][mo][1], 2)).number_format = MONEY
        change_cells(ws, r, 7, 8, 9)
    if r >= first2:
        ws.auto_filter.ref = f"A{first2 - 1}:J{r}"
    ws.freeze_panes = ws.cell(row=first, column=2)
    widths(ws, {"A": 30, "B": 34, "C": 11, "D": 13, "E": 11, "F": 11, "G": 13, "H": 13, "I": 13, "J": 10})


def product_lines(wb, m, business, sheet, heading):
    ws = wb.create_sheet(sheet)
    place_word = "Shop" if business == "Shops" else "Vending point"
    r = title(ws, heading, "USD before VAT, by SAP item group. Share = of the whole " + place_word.lower() + "'s item sales that month.")
    P, C = month_label(m.prev), month_label(m.cur)

    # Totals by product line
    header(ws, r, ["Product line", f"Qty {P}", f"Qty {C}", f"Value {P}", f"Value {C}", "Change", "Change %", f"Share {P}", f"Share {C}"])
    lines = m.lines_by(lambda x: m.group_name(x["ItemCode"]), business)
    grand = {mo: sum(v[mo][1] for v in lines.values()) for mo in m.months}
    first = r + 1
    for g in sorted(lines, key=lambda g: -lines[g][m.cur][1]):
        r += 1
        ws.cell(row=r, column=1, value=g)
        for i, mo in enumerate(m.months):
            ws.cell(row=r, column=2 + i, value=lines[g][mo][0]).number_format = QTY
            ws.cell(row=r, column=4 + i, value=round(lines[g][mo][1], 2)).number_format = MONEY
            ws.cell(row=r, column=8 + i, value=(lines[g][mo][1] / grand[mo]) if grand[mo] else 0).number_format = PCT
        change_cells(ws, r, 4, 5, 6)
    last = r
    r += 1
    sum_row(ws, r, first, last, [(4, MONEY), (5, MONEY), (6, MONEY)], label=f"All {business.lower()}")
    change_cells(ws, r, 4, 5, 6)

    # Per place
    r += 3
    ws.cell(row=r, column=1, value=f"By {place_word.lower()}").font = Font(bold=True, size=12, color=NAVY)
    r += 1
    header(ws, r, [place_word, "Product line", f"Qty {P}", f"Qty {C}", f"Value {P}", f"Value {C}", "Change", "Change %", f"Share {P}", f"Share {C}"])
    per = m.lines_by(lambda x: (x["SourceSystem"], x["WarehouseCode"], m.group_name(x["ItemCode"])), business)
    places = defaultdict(list)
    for k in per:
        places[(k[0], k[1])].append(k)
    place_tot = {p: {mo: sum(per[k][mo][1] for k in ks) for mo in m.months} for p, ks in places.items()}
    for p in sorted(places, key=lambda p: -place_tot[p][m.cur]):
        pfirst = r + 1
        for k in sorted(places[p], key=lambda k: -per[k][m.cur][1]):
            r += 1
            ws.cell(row=r, column=1, value=m.place(p[0], p[1]))
            ws.cell(row=r, column=2, value=k[2])
            for i, mo in enumerate(m.months):
                ws.cell(row=r, column=3 + i, value=per[k][mo][0]).number_format = QTY
                ws.cell(row=r, column=5 + i, value=round(per[k][mo][1], 2)).number_format = MONEY
                t = place_tot[p][mo]
                ws.cell(row=r, column=9 + i, value=(per[k][mo][1] / t) if t else 0).number_format = PCT
            change_cells(ws, r, 5, 6, 7)
        r += 1
        sum_row(ws, r, pfirst, r - 1, [(5, MONEY), (6, MONEY), (7, MONEY)], label=m.place(p[0], p[1]) + " total", fill=SUB)
        change_cells(ws, r, 5, 6, 7)
    ws.freeze_panes = ws.cell(row=5, column=2)
    widths(ws, {"A": 30, "B": 24, "C": 11, "D": 11, "E": 14, "F": 14, "G": 13, "H": 10, "I": 10, "J": 10})


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("export")
    ap.add_argument("--prev", required=True)
    ap.add_argument("--cur", required=True)
    ap.add_argument("--zig-rate", type=float, default=30.0)
    ap.add_argument("--sap-cache")
    ap.add_argument("-o", "--out", required=True)
    a = ap.parse_args()

    t = read_export(a.export)
    items, warehouses = read_sap(a.sap_cache)
    m = Model(t, items, warehouses, a.prev, a.cur, a.zig_rate)
    customers = {r["BusinessPartnerCode"]: (SHOP_TILL, r["WarehouseCode"]) for r in t["shops"] if r["IsActive"] in ("t", "true", "True")}
    customers.update({bp: (VENDING, wh) for bp, wh in VENDING_CUSTOMERS.items()})
    m.use_sap(read_sap_lines(a.sap_cache, customers, a.prev, a.cur), customers)

    wb = Workbook()
    readme(wb, m, os.path.basename(a.export))
    summary(wb, m)
    complimentary(wb, m)
    tenders(wb, m)
    shamiso(wb, m)
    product_lines(wb, m, "Shops", "4 Product lines - Shops", "4. Product lines by shop")
    product_lines(wb, m, "Vending", "4b Product lines - Vending", "4b. Product lines, vending")
    for ws in wb.worksheets:
        ws.sheet_view.showGridLines = False
        ws.page_setup.orientation = "landscape"
        ws.page_setup.fitToWidth = 1
        ws.page_setup.fitToHeight = 0
        ws.sheet_properties.pageSetUpPr.fitToPage = True
    wb.save(a.out)

    # Printed so the sources can be checked against each other without opening Excel.
    print("SAP documents left out (cancelled or cancellations):", dict(m.sap_skipped))
    till = defaultdict(float)
    for x in m.sales:
        till[(x["Month"], x["SourceSystem"], x["WarehouseCode"])] += m.usd(x["Gross"], x["Currency"])
    print(f"{'month':8} {'place':28} {'SAP invoices':>13} {'till sales':>12} {'diff':>10}  till from")
    for k, d in sorted(m.sap_docs.items(), key=lambda kv: (kv[0][0], kv[0][2])):
        for mo in m.months:
            tv = till.get((mo, k[1], k[2]), 0.0)
            print(f"{mo:8} {m.place(k[1], k[2])[:28]:28} {d[mo]['inv']:13,.2f} {tv:12,.2f} {d[mo]['inv'] - tv:10,.2f}  "
                  f"{m.till_start.get((k[1], k[2]), '')}")
    lines = {mo: sum(x["NetValue"] for x in m.lines if x["Month"] == mo) for mo in m.months}
    print("SAP line totals (net of credits, before VAT):", {k: round(v, 2) for k, v in lines.items()})
    unmapped = {x["ItemCode"] for x in m.lines if x["ItemCode"] not in m.items}
    if unmapped:
        print(f"{len(unmapped)} item codes not in SAP item master:", sorted(unmapped)[:20])
    print("Wrote", a.out)


if __name__ == "__main__":
    main()
