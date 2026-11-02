"""Read SAP marketing-document amounts in the document's own currency.

SAP's DocTotal, VatSum, TotalDiscount and line LineTotal are in the company's local currency (USD)
even on a ZiG document. The document-currency figures are DocTotalFc, VatSumFc, TotalDiscountFC and
RowTotalFC. Common/Sales/SapDocumentAmounts picks the right set for any model that implements
ISapDocumentAmounts / ISapDocumentLineAmounts.

This script:
  1. makes each SAP document model implement those interfaces, adding the missing properties;
  2. rewrites each SAP -> DTO mapper to read its amounts through SapDocumentAmounts;
  3. adds DocTotalFc (and only DocTotalFc: an unknown $select name 400s the whole query) to the
     purchase invoice, goods receipt PO and purchase quotation $selects;
  4. reads the purchase-order report's sums through SapDocumentAmounts.

It is idempotent: a second run changes nothing. Run from the repo root:
    python scripts/Convert-SapForeignCurrencyAmounts.py
"""

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent / "ShopInventory"
USING = "using ShopInventory.Common.Sales;\n"

# (model file, header class, line class)
MODELS = [
    ("Models/SAPSalesOrder.cs", "SAPSalesOrder", "SAPSalesOrderLine"),
    ("Models/SAPQuotation.cs", "SAPQuotation", "SAPQuotationLine"),
    ("Models/SAPPurchaseOrder.cs", "SAPPurchaseOrder", "SAPPurchaseOrderLine"),
    ("Models/SAPPurchaseInvoice.cs", "SAPPurchaseInvoice", "SAPPurchaseInvoiceLine"),
    ("Models/SAPGoodsReceiptPurchaseOrder.cs", "SAPGoodsReceiptPurchaseOrder", "SAPGoodsReceiptPurchaseOrderLine"),
    ("Models/SAPPurchaseQuotation.cs", "SAPPurchaseQuotation", "SAPPurchaseQuotationLine"),
]

# property -> (the property it follows, its summary)
HEADER_PROPERTIES = {
    "DocTotalFc": ("DocTotal", "The total in the document's own currency; zero on a local-currency (USD) document."),
    "VatSumFc": ("VatSum", "The tax in the document's own currency. VatSum is in local currency (USD)."),
    "DocRate": ("VatSumFc", "The rate SAP converted the document at: document currency per unit of local currency."),
    "TotalDiscountFC": ("TotalDiscount", "The header discount in the document's own currency."),
}
LINE_PROPERTIES = {
    "RowTotalFC": ("LineTotal", "The line total in the document's own currency; zero on a local-currency document."),
}

MAPPERS = [
    "Features/Quotations/Queries/GetQuotationFromSAPByDocEntry/GetQuotationFromSAPByDocEntryHandler.cs",
    "Features/Quotations/Queries/GetQuotationsFromSAP/GetQuotationsFromSAPHandler.cs",
    "Features/PurchaseOrders/Queries/GetPurchaseOrderFromSAPByDocEntry/GetPurchaseOrderFromSAPByDocEntryHandler.cs",
    "Features/PurchaseOrders/Queries/GetPurchaseOrdersFromSAP/GetPurchaseOrdersFromSAPHandler.cs",
    "Features/PurchaseInvoices/PurchaseInvoiceMappings.cs",
    "Features/GoodsReceiptPurchaseOrders/GoodsReceiptPurchaseOrderMappings.cs",
    "Features/PurchaseQuotations/PurchaseQuotationMappings.cs",
]

SELECTS = ["PurchaseQuotationSelect", "GoodsReceiptPurchaseOrderSelect", "PurchaseInvoiceSelect"]

changed: list[str] = []


def fail(message: str) -> None:
    sys.exit(f"FAILED: {message}")


def read(path: Path) -> str:
    """The file's text with LF line endings; write() restores the file's own."""
    return path.read_bytes().decode("utf-8").replace("\r\n", "\n")


def write(path: Path, before: str, after: str) -> None:
    if after == before:
        return
    if b"\r\n" in path.read_bytes():
        after = after.replace("\n", "\r\n")
    path.write_bytes(after.encode("utf-8"))
    changed.append(str(path.relative_to(ROOT.parent)))


def add_using(text: str) -> str:
    if USING in text:
        return text
    anchor = "using ShopInventory.DTOs;\n"
    if anchor not in text:
        fail("no `using ShopInventory.DTOs;` to place the Common.Sales using before")
    return text.replace(anchor, USING + anchor, 1)


def class_span(text: str, name: str) -> tuple[int, int]:
    match = re.search(rf"^public class {name}\b[^\n]*\n", text, re.M)
    if not match:
        fail(f"class {name} not found")
    following = re.search(r"^(///[^\n]*\n)*public class ", text[match.end():], re.M)
    end = match.end() + following.start() if following else len(text)
    return match.start(), end


def implement(text: str, name: str, interface: str, properties: dict) -> str:
    start, end = class_span(text, name)
    body = text[start:end]

    declaration = re.match(rf"public class {name}\b([^\n]*)\n", body)
    if interface not in declaration.group(1):
        if declaration.group(1).strip():
            fail(f"{name} already declares `{declaration.group(1).strip()}`; extend the script for it")
        body = body.replace(declaration.group(0), f"public class {name} : {interface}\n", 1)

    for prop, (after, summary) in properties.items():
        if re.search(rf"public decimal\? {prop} \{{", body):
            continue
        anchor = re.search(rf"^    public decimal\? {after} \{{ get; set; \}}\n", body, re.M)
        if not anchor:
            fail(f"{name}.{after} not found to place {prop} after")
        block = (
            f"\n    /// <summary>{summary}</summary>\n"
            f"    [JsonPropertyName(\"{prop}\")]\n"
            f"    public decimal? {prop} {{ get; set; }}\n"
        )
        body = body[:anchor.end()] + block + body[anchor.end():]

    return text[:start] + body + text[end:]


def convert_models() -> None:
    for relative, header, line in MODELS:
        path = ROOT / relative
        before = read(path)
        after = implement(before, header, "ISapDocumentAmounts", HEADER_PROPERTIES)
        after = implement(after, line, "ISapDocumentLineAmounts", LINE_PROPERTIES)
        write(path, before, after)


def convert_mappers() -> None:
    for relative in MAPPERS:
        path = ROOT / relative
        before = read(path)
        signature = re.search(r"\bMap\w*\((SAP\w+) (\w+)\)", before)
        if not signature:
            fail(f"{relative}: no Map...(SAPxxx doc) signature")
        doc = signature.group(2)

        after = before
        for field in ("DocTotal", "VatSum", "TotalDiscount"):
            after, count = re.subn(rf"\b{doc}\.{field} \?\? 0\b", f"SapDocumentAmounts.{field}({doc}) ?? 0", after)
            if count == 0 and f"SapDocumentAmounts.{field}({doc})" not in after:
                fail(f"{relative}: no {doc}.{field} read")

        after, count = re.subn(r"\b(\w+)\.LineTotal \?\? 0\b", rf"SapDocumentAmounts.LineTotal({doc}, \1) ?? 0", after)
        if count == 0 and "SapDocumentAmounts.LineTotal(" not in after:
            fail(f"{relative}: no line LineTotal read")

        leftovers = re.findall(r"\b\w+\.(?:DocTotal|VatSum|TotalDiscount|LineTotal) \?\? 0", after)
        leftovers = [hit for hit in leftovers if not hit.startswith("SapDocumentAmounts.")]
        if leftovers:
            fail(f"{relative}: unconverted reads {leftovers}")

        write(path, before, add_using(after))


def convert_selects() -> None:
    path = ROOT / "Services/SAPServiceLayerClient.cs"
    before = read(path)
    after = before
    for name in SELECTS:
        pattern = rf'(private const string {name} = "\$select=[^"]*\bDocTotal,)(?!DocTotalFc,)'
        after, count = re.subn(pattern, r"\1DocTotalFc,", after)
        if count == 0 and not re.search(rf'{name} = "[^"]*\bDocTotal,DocTotalFc,', after):
            fail(f"{name} not found or has no DocTotal")
    write(path, before, after)


def convert_report() -> None:
    path = ROOT / "Services/ReportService.cs"
    before = read(path)
    after = before

    # Every `po.` in this file is a SAPPurchaseOrder (GetPurchaseOrderSummaryAsync and the profit
    # overview's purchase cost); the line numbers are printed so a reviewer can confirm.
    for number, text in enumerate(before.splitlines(), 1):
        if "po.DocTotal ?? 0" in text:
            print(f"  ReportService.cs:{number}: po.DocTotal")
    after = after.replace("po.DocTotal ?? 0", "SapDocumentAmounts.DocTotal(po) ?? 0")

    line_projection = ".Select(l => new { Line = l, po.DocCurrency }))"
    if line_projection in after:
        after = after.replace(
            line_projection,
            ".Select(l => new { Line = l, po.DocCurrency, LineTotal = SapDocumentAmounts.LineTotal(po, l) }))",
        )
        count = after.count("x.Line.LineTotal ?? 0")
        if count != 2:
            fail(f"expected the two top-product line sums, found {count}")
        after = after.replace("x.Line.LineTotal ?? 0", "x.LineTotal ?? 0")

    if "po.DocTotal" in after.replace("SapDocumentAmounts.DocTotal(po)", ""):
        fail("ReportService still reads po.DocTotal directly")

    write(path, before, add_using(after) if after != before else after)


def main() -> None:
    convert_models()
    convert_mappers()
    convert_selects()
    convert_report()
    print("changed:" if changed else "nothing to change")
    for name in changed:
        print(f"  {name}")


if __name__ == "__main__":
    main()
