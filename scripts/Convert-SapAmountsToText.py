r'''Select every decimal column of the API's stored SAP queries as text, and read it back as text or a number.

Why: a Service Layer worker writing a decimal into a SQLQueries answer can stall for good (an 8 GB
buffer of "0.000...", refilled for ever). Thirteen of them held 76.8 GB on the SAP server on 23 Nov 2026
and HANA's index server was killed six times. Text never goes that way. See
ShopInventory/Common/Sap/SapSqlAmount.cs.

What it rewrites, in the C# string literals that hold a SELECT ... FROM:

    T1."OnHand" as "InStock"     ->  {SapSqlAmount.Column("T1", "OnHand", "InStock")}
    T0."Quantity"                ->  {SapSqlAmount.Column("T0", "Quantity")}

in the select list only (the text between SELECT and the next FROM), for the columns in DECIMALS. A
column inside a function call -- SUM(T1."OnHand") -- is left alone and reported: whether SAP takes
LEFT over an aggregate has not been tried. The WHERE, GROUP BY and ORDER BY are never touched. The
literal is made interpolated (`"""` -> `$"""`, `@"` -> `$@"`, `"` -> `$"`), and a `const string`
holding it becomes `static readonly string`.

And in the row readers of ShopInventory/Services/SAPServiceLayerClient*.cs:

    row.TryGetProperty("InStock", out var v) ? v.GetDecimal() : 0   ->  SapSqlAmount.Read(row, "InStock")
    row.TryGetValue("Quantity", out var q) ? RowDecimal(q) : 0      ->  SapSqlAmount.Read(row, "Quantity")
    GetSapSqlDecimal(row, "Quantity")                               ->  SapSqlAmount.Read(row, "Quantity")

    python scripts/Convert-SapAmountsToText.py            # report only
    python scripts/Convert-SapAmountsToText.py --apply    # rewrite the files
    python scripts/Convert-SapAmountsToText.py --root DIR # work on a copy of the tree under DIR

Idempotent: a converted select item and a converted reader no longer match.
'''

import glob
import io
import os
import re
import sys

# Every decimal column the API's stored queries select. A column missing from here is caught by
# SapStoredQueryAmountTests, which refuses any selected column it cannot classify.
DECIMALS = [
    "Quantity", "OnHand", "IsCommited", "OnOrder",
    "Price", "Factor", "Rate",
    "LineTotal", "TotalFrgn", "GrssProfit", "GrssProfFC", "DocTotal", "VatSum", "PaidToDate",
    "Debit", "Credit", "FCDebit", "FCCredit", "BalDueDeb", "BalDueCred",
]

# The names the typed row parsers read an amount under.
AMOUNT_ALIASES = [
    "InStock", "Quantity", "Committed", "Ordered", "Available", "Price", "Rate",
    "TotalQuantitySold", "TotalSalesValue",
]

SQL_GLOBS = [
    "ShopInventory/Services/SAPServiceLayerClient*.cs",
    "ShopInventory/Services/ReportService.cs",
    "ShopInventory/Features/**/*.cs",
    "ShopInventory/Common/**/*.cs",
]
READER_GLOBS = ["ShopInventory/Services/SAPServiceLayerClient*.cs"]

# A C# lexer just good enough to find string literals without being fooled by comments, and to walk
# past the holes of an interpolated string that hold quotes of their own.
TOKEN = re.compile(r'''
    (?P<lc>//[^\n]*)
  | (?P<bc>/\*.*?\*/)
  | (?P<raw>(?P<rawp>\$?)"""(?P<rawb>.*?)""")
  | (?P<iverb>(?:\$@|@\$)"(?P<iverbb>(?:[^"{]|""|\{\{|\{[^{}]*\})*)")
  | (?P<verb>@"(?P<verbb>(?:[^"]|"")*)")
  | (?P<ireg>\$"(?P<iregb>(?:[^"\\\n{]|\\.|\{\{|\{[^{}]*\})*)")
  | (?P<reg>"(?P<regb>(?:[^"\\\n]|\\.)*)")
  | (?P<ch>'(?:[^'\\\n]|\\.)')
''', re.S | re.X)

SELECT_LIST = re.compile(r'\bSELECT\b(?P<list>.*?)\bFROM\b', re.S)
CONST_DECL = re.compile(r'\bconst string(\s+\w+\s*=\s*)$')


def item_pattern(quote):
    q = re.escape(quote)
    columns = "|".join(DECIMALS)
    return re.compile(
        r'(?<![\w(.])(?P<t>[A-Za-z_]\w*)\.' + q + r'(?P<c>' + columns + r')' + q +
        r'(?:\s+(?:as|AS)\s+' + q + r'(?P<a>\w+)' + q + r')?')


def aggregate_pattern(quote):
    q = re.escape(quote)
    return re.compile(r'\b(?:SUM|AVG)\(\w+\.' + q + r'(\w+)' + q + r'\)', re.I)


def convert_select_lists(body, quote):
    """The literal's body with its select lists rewritten, the columns converted, and the aggregates left."""
    converted, aggregates = [], []
    item = item_pattern(quote)
    aggregate = aggregate_pattern(quote)

    def replace_item(match):
        table, column, alias = match.group("t"), match.group("c"), match.group("a")
        converted.append(column if alias in (None, column) else f"{column} as {alias}")
        if alias in (None, column):
            return f'{{SapSqlAmount.Column("{table}", "{column}")}}'
        return f'{{SapSqlAmount.Column("{table}", "{column}", "{alias}")}}'

    def replace_list(match):
        select_list = match.group("list")
        aggregates.extend(aggregate.findall(select_list))
        return "SELECT" + item.sub(replace_item, select_list) + "FROM"

    return SELECT_LIST.sub(replace_list, body), converted, aggregates


def convert_sql(text, report, path):
    out, position = [], 0
    for token in TOKEN.finditer(text):
        kind = token.lastgroup if token.lastgroup in ("lc", "bc", "ch") else next(
            name for name in ("raw", "iverb", "verb", "ireg", "reg") if token.group(name) is not None)
        if kind in ("lc", "bc", "ch"):
            continue

        body = token.group(kind + "b")
        quote = {"raw": '"', "iverb": '""', "verb": '""', "ireg": '\\"', "reg": '\\"'}[kind]
        line = text.count("\n", 0, token.start()) + 1
        if "SELECT" in body and "FROM" not in body and item_pattern(quote).search(body):
            # A statement built from several literals joined with +: its select list ends in another one.
            report.append(f"  {path}:{line}: NOT CONVERTED, the statement is split across literals; make it one literal")
        if "SELECT" not in body or "FROM" not in body:
            continue

        new_body, converted, aggregates = convert_select_lists(body, quote)
        if aggregates:
            report.append(f"  {path}:{line}: left as a decimal aggregate: {', '.join(aggregates)}")
        if not converted:
            continue

        report.append(f"  {path}:{line}: {', '.join(converted)}")
        interpolated = kind in ("iverb", "ireg") or (kind == "raw" and token.group("rawp") == "$")
        if not interpolated and ("{" in body or "}" in body):
            raise SystemExit(f"{path}:{line}: the literal holds a brace and is not interpolated; convert it by hand")

        # A const cannot hold an interpolated string that calls a method.
        before = CONST_DECL.sub(lambda m: "static readonly string" + m.group(1), text[position:token.start()])
        if kind == "raw":
            literal = '$"""' + new_body + '"""'
        elif kind in ("iverb", "verb"):
            literal = '$@"' + new_body + '"'
        else:
            literal = '$"' + new_body + '"'

        out.append(before)
        out.append(literal)
        position = token.end()

    out.append(text[position:])
    return "".join(out)


ALIASES = "|".join(AMOUNT_ALIASES)
READERS = [
    # row.TryGetProperty("InStock", out var v) ? v.GetDecimal() : 0
    (re.compile(r'(?P<row>\w+)\.TryGetProperty\("(?P<n>' + ALIASES + r')", out var (?P<v>\w+)\) \? (?P=v)\.GetDecimal\(\) : 0\b'),
     r'SapSqlAmount.Read(\g<row>, "\g<n>")'),
    # ... the first leg of a chain whose last leg the line above has just converted
    (re.compile(r'(?P<row>\w+)\.TryGetProperty\("(?P<n>' + ALIASES + r')", out var (?P<v>\w+)\) \? (?P=v)\.GetDecimal\(\)(?P<ws>\s*): (?=SapSqlAmount\.Read\()'),
     r'\g<row>.TryGetProperty("\g<n>", out _) ? SapSqlAmount.Read(\g<row>, "\g<n>")\g<ws>: '),
    # row.TryGetValue("Quantity", out var q) ? RowDecimal(q) : 0
    (re.compile(r'(?P<row>\w+)\.TryGetValue\("(?P<n>' + ALIASES + r')", out var (?P<v>\w+)\) \? RowDecimal\((?P=v)\) : 0\b'),
     r'SapSqlAmount.Read(\g<row>, "\g<n>")'),
    (re.compile(r'(?P<row>\w+)\.TryGetValue\("(?P<n>' + ALIASES + r')", out var (?P<v>\w+)\) \? RowDecimal\((?P=v)\)(?P<ws>\s*): (?=SapSqlAmount\.Read\()'),
     r'\g<row>.TryGetValue("\g<n>", out _) ? SapSqlAmount.Read(\g<row>, "\g<n>")\g<ws>: '),
    # GetSapSqlDecimal(row, "Quantity")
    (re.compile(r'(?<![\w.])GetSapSqlDecimal\((?=\w+, ")'), 'SapSqlAmount.Read('),
    # SqlDecimal(row, "LineTotal"), but not SqlDecimal(row, "DocEntry"): that one is a whole number
    (re.compile(r'(?<![\w.])SqlDecimal\((?=\w+, "(?:' + "|".join(DECIMALS) + r')"\))'), 'SapSqlAmount.Read('),
]


def convert_readers(text, report, path):
    for pattern, replacement in READERS:
        for match in pattern.finditer(text):
            line = text.count("\n", 0, match.start()) + 1
            report.append(f"  {path}:{line}: reader: {match.group(0).splitlines()[0][:90]}")
        text = pattern.sub(replacement, text)
    return text


USING = "using ShopInventory.Common.Sap;"


def add_using(text):
    if "SapSqlAmount." not in text or USING in text or "namespace ShopInventory.Common.Sap" in text:
        return text
    usings = list(re.finditer(r'^using [^\n;]*;', text, re.M))
    if not usings:
        raise SystemExit("no using directive to follow")
    newline = "\r\n" if "\r\n" in text else "\n"
    # In order among the file's own ShopInventory usings; after the last using otherwise.
    for using in usings:
        if using.group(0).startswith("using ShopInventory.") and using.group(0).rstrip(";") > USING.rstrip(";"):
            return text[:using.start()] + USING + newline + text[using.start():]
    last = usings[-1].end()
    return text[:last] + newline + USING + text[last:]


def files(patterns, root):
    found = set()
    for pattern in patterns:
        for path in glob.glob(os.path.join(root, pattern), recursive=True):
            normalised = path.replace("\\", "/")
            if "/bin/" in normalised or "/obj/" in normalised or "/Migrations/" in normalised:
                continue
            found.add(path)
    return sorted(found)


def main():
    apply = "--apply" in sys.argv
    root = sys.argv[sys.argv.index("--root") + 1] if "--root" in sys.argv else "."
    readers = set(files(READER_GLOBS, root))
    changed = 0
    for path in files(SQL_GLOBS, root):
        raw = io.open(path, "rb").read()
        bom = raw.startswith(b"\xef\xbb\xbf")
        text = raw.decode("utf-8-sig")
        report = []
        shown = os.path.relpath(path, root).replace("\\", "/")
        new = convert_sql(text, report, shown)
        if path in readers:
            new = convert_readers(new, report, shown)
        new = add_using(new)
        if report:
            print("\n".join(report))
        if new != text:
            changed += 1
            if apply:
                io.open(path, "wb").write((b"\xef\xbb\xbf" if bom else b"") + new.encode("utf-8"))
    print(f"{changed} file(s) {'rewritten' if apply else 'would change'}")


if __name__ == "__main__":
    main()
