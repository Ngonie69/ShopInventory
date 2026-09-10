"""Finds items whose SAP item master disagrees with the VAT group they are actually sold under.

Why this matters. The REVMax invoice feed is the fiscal device vendor's own SAP B1 add-on, and it
reads the ITEM MASTER's SalesVATGroup. The invoice LINE carries what the customer was actually
charged. Where the two differ, the receipt filed with ZIMRA declares a different amount of VAT from
the invoice in the customer's hand, and neither document can be amended afterwards.

ShopInventory itself is not affected: RevmaxFiscalizationService reads the line's VatGroup, and since
2026-09-09 also reads each filed receipt back and raises TaxDeclarationMismatch if the device taxed a
line differently from the way it was declared.

Two filters keep the answer honest, and without either one the number is wildly overstated:

  * Only invoices REVMax actually holds are counted. An invoice that was never fiscalised has no
    receipt, so nothing was misdeclared. Without this, a single zero-rated export (770074, EXP047,
    ZAR 25,188 CIF Lusaka) inflated the total from under 3 to over 3,600.
  * EXP* business partners are skipped outright. Export sales are fiscalised by a different route.

Read-only. Writes nothing to SAP and nothing to the device.

    python vat_master_audit.py <B1SESSION> [since=YYYY-MM-DD] [--json out.json]

Get a session from POST https://10.10.10.6:50000/b1s/v1/Login.
"""
import json
import ssl
import sys
import urllib.parse
import urllib.request
from collections import defaultdict
from concurrent.futures import ThreadPoolExecutor

SAP = 'https://10.10.10.6:50000/b1s/v1'
REVMAX = 'http://172.16.16.201:8001/api/RevmaxAPI'
CTX = ssl._create_unverified_context()

# The rate each SAP VAT group charges, so groups are compared by rate and never by code -- the item
# master and the document line do not spell them the same way. Note O0 is "Exempt from Output VAT";
# the separate "Zero Rated Output VAT" group (O3) is not in use.
RATES = {'O0': 0.0, 'O3': 0.0, 'O01': 0.155, 'O1': 0.155, 'O7': 0.155,
         'O8': 0.155, 'O9': 0.155, 'O2': 0.155, 'O010': 0.15, 'O011': 0.15}


def sap(path, session, page_size=None):
    headers = {'Cookie': f'B1SESSION={session}'}

    # Without this the Service Layer answers 20 rows and says nothing about the rest, which reads
    # exactly like "that is all of them".
    if page_size:
        headers['Prefer'] = f'odata.maxpagesize={page_size}'

    req = urllib.request.Request(f'{SAP}/{path}', headers=headers)
    with urllib.request.urlopen(req, timeout=180, context=CTX) as r:
        return json.load(r)


def sap_all(path, session, page_size, label):
    rows, skip = [], 0
    while True:
        page = sap(f'{path}&$skip={skip}', session, page_size)['value']
        rows.extend(page)
        print(f'  {label}: {len(rows)}', flush=True)
        if len(page) < page_size:
            return rows
        skip += len(page)


def is_filed(doc_num):
    """Whether REVMax holds a receipt for this invoice."""
    try:
        with urllib.request.urlopen(f'{REVMAX}/GetInvoice/{doc_num}', timeout=30) as r:
            return doc_num, json.load(r).get('Code') == '1'
    except Exception:
        return doc_num, None


def main():
    args = [a for a in sys.argv[1:] if not a.startswith('--')]

    if not args:
        print(__doc__)
        return 64

    session = args[0]
    since = args[1] if len(args) > 1 else '2026-09-01'
    out = sys.argv[sys.argv.index('--json') + 1] if '--json' in sys.argv else None

    print('Reading the item master ...', flush=True)
    master = {
        i['ItemCode']: i
        for i in sap_all('Items?' + urllib.parse.urlencode(
            {'$select': 'ItemCode,ItemName,SalesVATGroup,VatLiable'}), session, 500, 'items')
    }

    # Lines come back inline on the collection, which turns ~1,200 requests into a dozen.
    print(f'\nReading invoice lines since {since} ...', flush=True)
    invoices = sap_all('Invoices?' + urllib.parse.urlencode({
        '$filter': f"DocDate ge '{since}'",
        '$select': 'DocNum,CardCode,DocumentLines',
        '$orderby': 'DocNum desc'}), session, 100, 'invoices')

    candidates, exports = [], 0

    for doc in invoices:
        if (doc.get('CardCode') or '').upper().startswith('EXP'):
            exports += 1
            continue

        for line in doc.get('DocumentLines') or []:
            code, line_group = line.get('ItemCode'), line.get('VatGroup')
            item = master.get(code)

            if not code or not line_group or item is None:
                continue

            master_rate = RATES.get((item.get('SalesVATGroup') or '').strip())
            line_rate = RATES.get(line_group.strip())

            if master_rate is None or line_rate is None or master_rate == line_rate:
                continue

            gross = abs(line.get('GrossTotal') or 0)
            candidates.append({
                'docNum': doc['DocNum'],
                'itemCode': code,
                'itemName': item.get('ItemName'),
                'masterGroup': item.get('SalesVATGroup'),
                'vatLiable': item.get('VatLiable'),
                'lineGroup': line_group,
                'gross': round(gross, 2),
                # What the feed declares (from the master) less what was charged (from the line).
                'vatDifference': round(
                    gross * master_rate / (1 + master_rate) - gross * line_rate / (1 + line_rate), 2),
            })

    print(f'\n{exports} export invoices skipped (fiscalised by another route)')
    print(f'{len(candidates)} candidate lines; checking which invoices REVMax actually holds ...',
          flush=True)

    with ThreadPoolExecutor(max_workers=8) as pool:
        filed = dict(pool.map(is_filed, sorted({c['docNum'] for c in candidates})))

    real = [c for c in candidates if filed.get(c['docNum']) is True]

    by_item = defaultdict(lambda: {'lines': 0, 'gross': 0.0, 'vat': 0.0, 'docs': set()})
    for c in real:
        e = by_item[c['itemCode']]
        e['lines'] += 1
        e['gross'] += c['gross']
        e['vat'] += c['vatDifference']
        e['docs'].add(c['docNum'])
        e.update(name=c['itemName'], master=c['masterGroup'],
                 liable=c['vatLiable'], sold=c['lineGroup'])

    print(f'\n{len(real)} of {len(candidates)} candidate lines are on invoices REVMax holds, '
          f'across {len({c["docNum"] for c in real})} receipts\n')
    print(f"{'item':<9} {'master':<6} {'liable':<7} {'sold':<5} {'lines':>5} {'gross':>9} "
          f"{'VAT diff':>9}  name")

    for code, e in sorted(by_item.items(), key=lambda kv: -abs(kv[1]['vat'])):
        print(f"{code:<9} {e['master'] or '-':<6} {e['liable'] or '-':<7} {e['sold']:<5} "
              f"{e['lines']:>5} {e['gross']:>9.2f} {e['vat']:>9.2f}  {(e['name'] or '')[:32]}")

    over = sum(e['vat'] for e in by_item.values() if e['vat'] > 0)
    under = sum(e['vat'] for e in by_item.values() if e['vat'] < 0)
    print(f"\nVAT declared but not charged : {over:>10.2f}")
    print(f"VAT charged but not declared : {under:>10.2f}")
    print(f"Net                          : {over + under:>10.2f}")

    if out:
        json.dump({'since': since, 'lines': real,
                   'byItem': {k: {**v, 'docs': sorted(v['docs'])} for k, v in by_item.items()}},
                  open(out, 'w'), indent=1, default=str)
        print(f'\nwrote {out}')

    return 0


if __name__ == '__main__':
    sys.exit(main())
