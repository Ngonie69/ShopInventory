"""Recovers the fiscal receipt number and fiscal day of desktop sales recorded before the REVMax fix.

Until the fix, DesktopSaleFiscaliser stored FiscalReceiptNumber blank - TransactM's body carries no
receipt number - and FiscalDayNo copied from the REVMax response envelope, which is not the receipt's
day: on 2026-09-11 every envelope read 524 while FDMS held those receipts on day 525.

READ-ONLY. It calls GET /api/RevmaxAPI/GetInvoice/{reference} and nothing else, so nothing is filed or
refiscalised. It writes nothing to the database either: it prints guarded UPDATE statements, inside an
open transaction, for a person to review and commit.

The receipt number is the one the device holds, cross-checked against the global number in the
receipt's own QR code. The day is never read from REVMax. It is carried from an ANCHOR receipt whose day
was read off FDMS ("Review invoice"), and only to a receipt provably on the same fiscal day: the receipt
counter restarts every fiscal day and the global number never does, so two receipts are on one day
exactly when both numbers moved by the same amount between them. A receipt that fails that is
reported, not guessed.

    python scripts/correct_till_fiscal_fields.py --anchor 216877:985:525 GRC-FAC-20260910-C67710AEF9C5

Anchor 216877:985:525 is GRC-FAC-20260911-286EEC7389FD as FDMS shows it: "Invoice No: 985/216877,
Fiscal day No: 525". The sales to pass in:

    SELECT "ExternalReferenceId" FROM "DesktopSales" WHERE "FiscalQRCode" IS NOT NULL ORDER BY "Id";
"""

import argparse
import json
import sys
import urllib.parse
import urllib.request


def global_no_from_qr(qr_code):
    """The global number in a ZIMRA QR code: device (10), date (8), global number (10), signature (16 hex)."""
    code = (qr_code or "").strip().rstrip("/").rsplit("/", 1)[-1]

    if len(code) != 44 or not code[:28].isdigit():
        return None

    try:
        int(code[28:], 16)
    except ValueError:
        return None

    return int(code[18:28]) or None


def get_invoice(base_url, reference):
    url = f"{base_url.rstrip('/')}/api/RevmaxAPI/GetInvoice/{urllib.parse.quote(reference, safe='')}"

    with urllib.request.urlopen(url, timeout=30) as response:
        return json.load(response)


def sql_text(value):
    return "'" + str(value).replace("'", "''") + "'"


def check(reference, answer, device_id, anchor):
    """(update, finding) for one sale. update is None when the sale cannot be corrected safely."""
    anchor_global, anchor_counter, anchor_day = anchor
    data = answer.get("Data") or {}

    if answer.get("Code") != "1" or not data:
        return None, f"REVMax holds no receipt: {answer.get('Message')}"

    if answer.get("DeviceID") != device_id:
        return None, f"the receipt belongs to device {answer.get('DeviceID')}, not {device_id}"

    global_no = data.get("receiptGlobalNo")
    counter = data.get("receiptCounter")
    qr_code = answer.get("QRcode")

    if not global_no or not counter or global_no_from_qr(qr_code) != global_no:
        return None, f"receipt {counter}/{global_no} does not match its QR code {qr_code}"

    if global_no - anchor_global != counter - anchor_counter:
        return None, (
            f"{counter}/{global_no} is not on the anchor's fiscal day; read its day off FDMS at {qr_code}")

    update = (
        'UPDATE "DesktopSales"\n'
        f'   SET "FiscalReceiptNumber" = {sql_text(global_no)}, "FiscalDayNo" = {sql_text(anchor_day)}\n'
        f' WHERE "ExternalReferenceId" = {sql_text(reference)}\n'
        f'   AND "FiscalQRCode" = {sql_text(qr_code)};'
    )

    return update, f"{counter}/{global_no}, fiscal day {anchor_day}"


def main():
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("references", nargs="+", help='"DesktopSales"."ExternalReferenceId" values')
    parser.add_argument(
        "--anchor", required=True, help="GLOBALNO:COUNTER:DAY of a receipt whose day was read off FDMS")
    parser.add_argument("--base-url", default="http://172.16.16.201:8001")
    parser.add_argument("--device-id", default="22862")
    args = parser.parse_args()

    anchor = tuple(int(part) for part in args.anchor.split(":"))
    updates = []
    skipped = 0

    for reference in args.references:
        try:
            update, finding = check(reference, get_invoice(args.base_url, reference), args.device_id, anchor)
        except Exception as error:  # reported per sale; one unreachable lookup must not hide the rest
            update, finding = None, f"REVMax could not be asked: {error}"

        print(f"{'OK  ' if update else 'SKIP'} {reference}: {finding}", file=sys.stderr)

        if update:
            updates.append(update)
        else:
            skipped += 1

    if updates:
        print("BEGIN;\n")
        print("\n\n".join(updates))
        print("\n-- Each UPDATE must report exactly 1 row. Then COMMIT; otherwise ROLLBACK;")

    return 1 if skipped else 0


if __name__ == "__main__":
    sys.exit(main())
