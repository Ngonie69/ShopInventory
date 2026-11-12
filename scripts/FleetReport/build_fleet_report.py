"""Builds the monthly fleet workbook from a Cartrack pull, the van export and SAP diesel issues.

    python -I scripts/FleetReport/fetch_cartrack_month.py --month 2026-10 --out <dir>
    python scripts/FleetReport/build_fleet_report.py --month 2026-10 --cartrack <dir> \
        [--vans <folder Export-FleetMonth.ps1 wrote>] [--diesel <SAP diesel issues, tab separated>] \
        --out Fleet-2026-10.xlsx

What each vehicle is measured by:
- Cartrack vehicles: mileage, places, speed and speeding come from Cartrack trips.
- Vans without a tracker: one row per truck (the plate the rep typed at Start Day) per day.
  Mileage is the odometer typed at Start Day and End Day, repaired from the truck's own
  reading chain when a figure was obviously mistyped. Places are the shops its reps checked
  into. A phone cannot see speed, so there is no average speed or speeding for these.
- Fuel: SAP diesel issues are booked to a route's cost centre, not to a truck. Each route's
  litres are exact. A truck's share is its route's litres split by the km it ran on that route.

The summary and route sheets are formulas over the data sheets.
"""
import argparse
import csv
import datetime as dt
import json
import math
import os
import statistics
from collections import Counter, defaultdict

from openpyxl import Workbook
from openpyxl.comments import Comment
from openpyxl.styles import Alignment, Border, Font, PatternFill, Side
from openpyxl.utils import get_column_letter

# The Van days sheet's columns. Other sheets reach into it by heading through VAN_DAY_COL, so a column
# added or removed here cannot leave a formula pointing at its neighbour.
VAN_DAY_COLUMNS = ['Plate', 'Route', 'Date', 'Van account', 'Area', 'Reps on the truck', 'Out', 'Back', 'Hours out',
                   'Odometer km', 'GPS fixes', 'GPS km (straight line)', 'Opening reading', 'Closing reading',
                   'Note', 'Typed as', 'Cost centre', 'Fuel share (L)']
VAN_DAY_COL = {name: get_column_letter(i) for i, name in enumerate(VAN_DAY_COLUMNS, 1)}

# Told to us directly (2026-11-03): AFQ9644 is the only van with a tracker, and it is UPC 2.
# Its reps type AHT2239 at Start Day; their odometer readings follow AFQ9644's.
KNOWN_VANS = {'AFQ9644': ('VAN015', 'UPC 2')}

# SAP cost centre (as named on the diesel issue) -> van accounts it fuels. Matched on the words
# after "Van Sales n-". The van daily report maps UPC 1-4 to VAN014-VAN017.
COST_CENTRES = [
    ('cbd', 'CBD', ['VAN010']),
    ('up country', 'Up country (UPC 1-4)', ['VAN014', 'VAN015', 'VAN016', 'VAN017']),
    ('east 1', 'East 1', ['VAN013']),
    ('east 2', 'East 2', []),
    ('west 1', 'West 1', ['VAN009']),
    ('west 2', 'West 2', ['VAN008']),
]

FONT = 'Arial'
HEAD_FILL = PatternFill('solid', start_color='1F3A5F')
SUB_FILL = PatternFill('solid', start_color='E8EEF5')
THIN = Side(style='thin', color='C9D3DF')
MAX_PLAUSIBLE_KMH = 150      # a phone fix implying more than this from the last one is a bad fix
MIN_DAY_LIMIT_KM = 300       # a day's odometer km is suspect above max(this, 3 x the truck's median)


def font(**kw):
    return Font(name=FONT, size=kw.pop('size', 10), **kw)


# ── inputs ────────────────────────────────────────────────────────────────────────────────────

def load_cartrack(folder):
    trips = json.load(open(os.path.join(folder, 'trips.json'), encoding='utf-8'))
    vehicles = json.load(open(os.path.join(folder, 'vehicles.json'), encoding='utf-8'))['data']
    fills, levels, odometers = {}, {}, {}
    for v in vehicles:
        reg = v['registration']
        fills[reg] = json.load(open(os.path.join(folder, f'fills_{reg}.json'), encoding='utf-8')).get('data') or []
        levels[reg] = json.load(open(os.path.join(folder, f'level_{reg}.json'), encoding='utf-8')).get('data') or {}
        odometers[reg] = json.load(open(os.path.join(folder, f'odo_{reg}.json'), encoding='utf-8')).get('data') or {}
    return trips, vehicles, fills, levels, odometers


def read_text(path):
    # Exports before PGCLIENTENCODING=UTF8 came out in the server's Windows code page.
    for encoding in ('utf-8-sig', 'cp1252'):
        try:
            return open(path, encoding=encoding, newline='').read()
        except UnicodeDecodeError:
            continue


def read_csv(folder, name):
    path = os.path.join(folder, name)
    if not os.path.exists(path):
        return []
    rows = list(csv.DictReader(read_text(path).splitlines()))
    # Only van accounts. Cart vendors (COR…, MAC…) also sign in under a business partner.
    if rows and 'van_account' in rows[0]:
        rows = [r for r in rows if (r['van_account'] or '').strip().upper().startswith('VAN')]
    return rows


def read_diesel(path):
    """SAP diesel issues as copied from the query result: tab separated, dd/mm/yy dates."""
    if not path:
        return []
    out = []
    for r in csv.DictReader(read_text(path).splitlines(), delimiter='\t'):
        r = {k.strip(): (v or '').strip() for k, v in r.items() if k}
        if not r.get('Posting Date'):
            continue
        name = r['Center Name']
        key = name.lower().split('-', 1)[-1].strip() if '-' in name else name.lower()
        centre = next((c for c in COST_CENTRES if c[0] in ' '.join(key.split())), None)
        out.append(dict(day=dt.datetime.strptime(r['Posting Date'], '%d/%m/%y').date(), centre_name=name,
                        centre=centre[1] if centre else name, accounts=centre[2] if centre else [],
                        item=r.get('Item No.', ''), litres=float(r['SUM(Quantity)']), price=float(r['Price']),
                        amount=float(r['(Price*SUM(Quantity))'])))
    return out


def num(value):
    try:
        return float(value) if value not in (None, '') else None
    except ValueError:
        return None


def ts(value):
    return dt.datetime.fromisoformat(value) if value else None


def haversine_km(a, b):
    lat1, lon1, lat2, lon2 = map(math.radians, (a[0], a[1], b[0], b[1]))
    h = math.sin((lat2 - lat1) / 2) ** 2 + math.cos(lat1) * math.cos(lat2) * math.sin((lon2 - lon1) / 2) ** 2
    return 6371.0088 * 2 * math.asin(math.sqrt(h))


def norm_plate(value):
    return ''.join(ch for ch in (value or '').upper() if ch.isalnum())


def edit_distance(a, b):
    prev = list(range(len(b) + 1))
    for i, ca in enumerate(a, 1):
        cur = [i]
        for j, cb in enumerate(b, 1):
            cur.append(min(prev[j] + 1, cur[j - 1] + 1, prev[j - 1] + (ca != cb)))
        prev = cur
    return prev[-1]


def plate_aliases(counts):
    """Typed plate -> probable plate. A plate is folded into one typed on at least 3 times as
    many truck-days (and 3 or more) when it is one keystroke away or a fragment of it ('5826')."""
    alias = {}
    for plate, n in counts.items():
        alias[plate] = plate
        for e, m in counts.most_common():
            if e != plate and m >= 3 and m >= 3 * n and (
                    edit_distance(plate, e) <= 1 or (len(plate) >= 4 and plate in e)):
                alias[plate] = e
                break
    return alias


def place_of(location):
    """Cartrack names a place 'street, suburb, town, province, Zimbabwe'. Drop the country."""
    parts = [p.strip() for p in (location or '').split(',') if p.strip()]
    if parts and parts[-1] == 'Zimbabwe':
        parts = parts[:-1]
    return ', '.join(parts) or '(unnamed)'


def gps_track_km(points):
    """Straight-line km through one person's fixes in time order, skipping impossible jumps."""
    pts = sorted({p for p in points if p[0]}, key=lambda p: p[0])
    km, used, last = 0.0, 0, None
    for p in pts:
        if last is not None:
            step = haversine_km(last[1:], p[1:])
            hours = (p[0] - last[0]).total_seconds() / 3600
            if step > 1 and (hours <= 0 or step / hours > MAX_PLAUSIBLE_KMH):
                continue  # a bad fix; keep the previous good one as the anchor
            km += step
            used += 1
        last = p
    return (km if used else None), len(pts)


# ── vans from the phone, one row per truck per day ────────────────────────────────────────────

def odometer_day(seq, i, limit):
    """km for day i of a truck's reading chain, and how it was got."""
    s, c = seq[i]['start'], seq[i]['close']
    prev_c = next((seq[j]['close'] for j in range(i - 1, -1, -1) if seq[j]['close'] is not None), None)
    next_s = seq[i + 1]['start'] if i + 1 < len(seq) else None

    def ok(a, b):
        return a is not None and b is not None and 0 < b - a <= limit

    if ok(s, c):
        return c - s, ''
    if c is not None and ok(prev_c, c):
        return c - prev_c, f'start typed {s:.0f}; previous close {prev_c:.0f} used'
    if ok(s, next_s):
        why = 'no End Day reading' if c is None else f'close typed {c:.0f}'
        return next_s - s, f"{why}; next day's start {next_s:.0f} used"
    if ok(prev_c, next_s):
        return next_s - prev_c, f'start {s} and close {c} typed; previous close and next start used'
    if s is not None and c is not None and c == s:
        return 0.0, 'no movement recorded'
    return None, '' if s is None and c is None else f'readings {s} to {c} not usable'


def build_vans(vans, tracked_accounts):
    reps = read_csv(vans, 'reps.csv')
    route_days = read_csv(vans, 'route_days.csv')
    checkins = read_csv(vans, 'checkins.csv')
    orders = read_csv(vans, 'sales_orders.csv')
    role = {r['username']: r['role'] for r in reps}
    uname = {r['user_id']: r['username'] for r in reps}

    alias = plate_aliases(Counter(norm_plate(r['truck_reg']) for r in route_days if norm_plate(r['truck_reg'])))

    def plate_of(r):
        if r['van_account'] in tracked_accounts:
            return tracked_accounts[r['van_account']]
        p = norm_plate(r['truck_reg'])
        return alias.get(p, p) if p else f"{r['van_account']} (no plate)"

    # Truck-days from Start Day / End Day.
    days = defaultdict(lambda: dict(rows=[], typed=Counter(), accounts=Counter(), routes=Counter(), area=Counter(),
                                    reps=set()))
    user_plate = {}  # (username, day) -> plate
    for r in route_days:
        key = (plate_of(r), r['trading_date'])
        d = days[key]
        d['rows'].append(r)
        d['typed'][norm_plate(r['truck_reg']) or '(blank)'] += 1
        d['accounts'][r['van_account']] += 1
        if r['route_name']:
            d['routes'][r['route_name']] += 1
        if r['area_name']:
            d['area'][r['area_name'].strip()] += 1
        d['reps'].add(r['username'])
        user_plate[(r['username'], r['trading_date'])] = key[0]

    # A rep who sold without a Start Day rode with a truck-mate: their account's only truck that day.
    acct_day_plates = defaultdict(set)
    for (plate, day), d in days.items():
        for a in d['accounts']:
            acct_day_plates[(a, day)].add(plate)

    def plate_for(username, acct, day):
        if role.get(username) == 'ADR':
            return None  # ADRs work apart from the truck
        if (username, day) in user_plate:
            return user_plate[(username, day)]
        plates = acct_day_plates.get((acct, day), set())
        return next(iter(plates)) if len(plates) == 1 else None

    points = defaultdict(list)  # (username, day) -> fixes
    for r in route_days:
        for when, lat, lon in ((r['departed_cat'], r['departed_lat'], r['departed_lon']),
                               (r['returned_cat'], r['returned_lat'], r['returned_lon'])):
            if when and num(lat) and num(lon):
                points[(r['username'], r['trading_date'])].append((ts(when), num(lat), num(lon)))
    visits = []  # (plate, day, shop)
    unplaced = Counter()
    for c in checkins:
        day = c['checkin_cat'][:10]
        plate = plate_for(c['username'], c['van_account'], day)
        if plate is None:
            unplaced['ADR' if role.get(c['username']) == 'ADR' else 'no truck that day'] += 1
            continue
        if (c['username'], day) not in user_plate:
            user_plate[(c['username'], day)] = plate
            days[(plate, day)]['reps'].add(c['username'])
        visits.append((plate, day, c['customer_name'] or c['customer_code'] or '(no name)'))
        for when, lat, lon in ((c['checkin_cat'], c['checkin_lat'], c['checkin_lon']),
                               (c['checkout_cat'], c['checkout_lat'], c['checkout_lon'])):
            if when and num(lat) and num(lon):
                points[(c['username'], day)].append((ts(when), num(lat), num(lon)))
    for o in orders:
        user = o['username'] or uname.get(o['user_id'], '')
        day = (o['created_cat'] or '')[:10]
        if day and num(o['lat']) and num(o['lon']) and plate_for(user, o['van_account'], day):
            points[(user, day)].append((ts(o['created_cat']), num(o['lat']), num(o['lon'])))

    # Odometer chain per truck.
    by_plate = defaultdict(list)
    for (plate, day), d in days.items():
        if not d['rows']:
            continue
        starts = [num(r['starting_mileage']) for r in d['rows'] if num(r['starting_mileage']) is not None]
        closes = [num(r['closing_mileage']) for r in d['rows'] if num(r['closing_mileage']) is not None]
        by_plate[plate].append(dict(day=day, start=min(starts) if starts else None, close=max(closes) if closes else None))
    odo = {}
    for plate, seq in by_plate.items():
        seq.sort(key=lambda x: x['day'])
        raw = [x['close'] - x['start'] for x in seq if x['start'] is not None and x['close'] is not None
               and 0 < x['close'] - x['start'] <= 1000]
        limit = max(MIN_DAY_LIMIT_KM, 3 * statistics.median(raw)) if raw else MIN_DAY_LIMIT_KM
        for i, x in enumerate(seq):
            odo[(plate, x['day'])] = odometer_day(seq, i, limit) + (x['start'], x['close'])

    out = []
    for (plate, day), d in sorted(days.items()):
        km, note, s, c = odo.get((plate, day), (None, '', None, None))
        tracks = [gps_track_km(points[(u, day)]) for u in d['reps'] if role.get(u) != 'ADR']
        gps_km = max((t[0] for t in tracks if t[0] is not None), default=None)
        fixes = max((t[1] for t in tracks), default=0)
        departed = [ts(r['departed_cat']) for r in d['rows'] if r['departed_cat']]
        returned = [ts(r['returned_cat']) for r in d['rows'] if r['returned_cat']]
        start_t, end_t = (min(departed) if departed else None), (max(returned) if returned else None)
        out.append(dict(
            plate=plate, day=day,
            account=d['accounts'].most_common(1)[0][0] if d['accounts'] else '',
            route=d['routes'].most_common(1)[0][0] if d['routes'] else '',
            area=d['area'].most_common(1)[0][0] if d['area'] else '',
            reps=', '.join(sorted(d['reps'])),
            typed=', '.join(sorted(d['typed'])),
            odo_start=s, odo_close=c,
            odo_km=km, odo_note=note, gps_points=fixes, gps_km=gps_km,
            start=start_t, end=end_t,
            hours_out=(end_t - start_t).total_seconds() / 3600 if start_t and end_t and end_t > start_t else None))
    return dict(days=out, visits=visits, unplaced=unplaced, alias=alias, reps=reps, route_days=route_days,
                plate_of=plate_of, role=role)


# ── workbook helpers ──────────────────────────────────────────────────────────────────────────

def header(ws, row, titles, widths=None):
    for i, t in enumerate(titles, 1):
        c = ws.cell(row=row, column=i, value=t)
        c.font = font(bold=True, color='FFFFFF')
        c.fill = HEAD_FILL
        c.alignment = Alignment(wrap_text=True, vertical='center')
        c.border = Border(bottom=THIN)
    ws.row_dimensions[row].height = 30
    if widths:
        for i, w in enumerate(widths, 1):
            ws.column_dimensions[get_column_letter(i)].width = w
    ws.freeze_panes = ws.cell(row=row + 1, column=1)


def title(ws, text, sub):
    ws['A1'] = text
    ws['A1'].font = font(bold=True, size=14, color='1F3A5F')
    ws['A2'] = sub
    ws['A2'].font = font(italic=True, color='5A6B7D')


def write_rows(ws, start_row, rows, formats):
    for r, row in enumerate(rows, start_row):
        for c, value in enumerate(row, 1):
            cell = ws.cell(row=r, column=c, value=value)
            cell.font = font()
            if formats and c - 1 < len(formats) and formats[c - 1]:
                cell.number_format = formats[c - 1]
    return start_row + len(rows) - 1


def formula(ws, row, col, text, fmt=None, bold=False):
    c = ws.cell(row=row, column=col, value=text)
    c.font = font(bold=bold)
    if fmt:
        c.number_format = fmt
    return c


def autofilter(ws, head_row, last_row, cols):
    ws.auto_filter.ref = f'A{head_row}:{get_column_letter(cols)}{max(last_row, head_row)}'


def rng(sheet, col, last):
    return f"'{sheet}'!${col}$5:${col}${max(last, 5)}"


# ── build ─────────────────────────────────────────────────────────────────────────────────────

def build(month, cartrack_dir, vans_dir, diesel_path, out_path):
    first = dt.date.fromisoformat(month + '-01')
    last = (first.replace(day=28) + dt.timedelta(days=4)).replace(day=1) - dt.timedelta(days=1)
    month_name = first.strftime('%B %Y')
    days = [first + dt.timedelta(days=i) for i in range((last - first).days + 1)]

    trips, vehicles, fills, levels, odometers = load_cartrack(cartrack_dir)
    regs = [v['registration'] for v in vehicles]
    vmeta = {v['registration']: v for v in vehicles}
    links = {}
    if vans_dir:
        for t in read_csv(vans_dir, 'telematics_vehicles.csv'):
            links[t['registration'].upper()] = t['van_account'].upper()
    for reg, (acct, _) in KNOWN_VANS.items():
        links.setdefault(reg, acct)
    tracked_accounts = {acct: reg for reg, acct in links.items()}

    vans = build_vans(vans_dir, tracked_accounts) if vans_dir else dict(days=[], visits=[], unplaced=Counter(), alias={}, reps=[], route_days=[], role={})
    van_days = vans['days']
    diesel = read_diesel(diesel_path)
    centre_of = {a: c[1] for c in COST_CENTRES for a in c[2]}

    route_name = {}
    for r in vans['reps']:
        if r['route_name']:
            route_name.setdefault(r['van_account'], r['route_name'])
    for reg, (acct, name) in KNOWN_VANS.items():
        route_name.setdefault(acct, name)

    def label(reg):
        acct = links.get(reg)
        if acct:
            return f'{reg} ({route_name.get(acct, acct)}, {acct})'
        m = vmeta.get(reg, {})
        return ' '.join(f"{reg} ({(m.get('manufacturer') or '').title()} {(m.get('model') or '').title()})".split())

    plate_routes = defaultdict(Counter)
    for d in van_days:
        plate_routes[d['plate']][d['account']] += 1
    phone_plates = sorted((p for p in plate_routes if p not in regs), key=lambda p: (-sum(plate_routes[p].values()), p))

    def plate_label(p):
        return f"{p} ({', '.join(route_name.get(a, a) for a, _ in plate_routes[p].most_common())})"

    wb = Workbook()
    summary = wb.active
    summary.title = 'Summary'

    # ── Trips (Cartrack) ──
    ws = wb.create_sheet('Trips')
    title(ws, f'Cartrack trips, {month_name}', 'One row per trip. Times are CAT. Trips that never moved (under 0.1 km) are kept and marked.')
    cols = ['Vehicle', 'Date', 'Start', 'End', 'From', 'To', 'Km', 'Duration (min)', 'Idle (min)',
            'Moving (min)', 'Max speed (km/h)', 'Road-speeding events', 'Speeding (min)', 'Moved?']
    header(ws, 4, cols, [12, 11, 8, 8, 40, 40, 8, 10, 9, 10, 10, 11, 10, 8])
    rows = []
    for t in sorted(trips, key=lambda t: (t['registration'], t['start_timestamp'])):
        s, e = ts(t['start_timestamp']), ts(t['end_timestamp'])
        dur = (t['trip_duration_seconds'] or 0) / 60
        idle = min((t['idle_time_seconds'] or 0) / 60, dur)
        km = (t['trip_distance'] or 0) / 1000
        rows.append([t['registration'], s.date(), s.time().replace(microsecond=0), e.time().replace(microsecond=0),
                     place_of(t['start_location']), place_of(t['end_location']), round(km, 2), round(dur, 1),
                     round(idle, 1), round(dur - idle, 1), t['max_speed'] or 0, t['road_speeding_events'] or 0,
                     round((t['road_speeding_duration_seconds'] or 0) / 60, 1), 'yes' if km >= 0.1 else 'no'])
    trips_last = write_rows(ws, 5, rows, [None, 'dd mmm', 'hh:mm', 'hh:mm', None, None, '#,##0.0', '0', '0', '0', '0', '0', '0.0', None])
    autofilter(ws, 4, trips_last, len(cols))
    T = lambda col: rng('Trips', col, trips_last)

    # ── Van days (phone) ──
    vd_last = 4
    if vans_dir:
        ws = wb.create_sheet('Van days')
        title(ws, f'Van trucks on the phone, {month_name}',
              'One row per truck (plate typed at Start Day) per day. Odometer km is closing minus opening, repaired from the truck\'s '
              'own readings when one was mistyped (see Note). GPS km is straight lines between the phone\'s fixes, for reference only.')
        cols = VAN_DAY_COLUMNS
        header(ws, 4, cols, [11, 14, 10, 10, 16, 30, 7, 7, 7, 10, 7, 10, 10, 10, 44, 14, 18, 10])
        rows = []
        for d in van_days:
            rows.append([d['plate'], d['route'] or route_name.get(d['account'], ''), dt.date.fromisoformat(d['day']),
                         d['account'], d['area'], d['reps'],
                         d['start'].time() if d['start'] else None, d['end'].time() if d['end'] else None,
                         round(d['hours_out'], 2) if d['hours_out'] else None,
                         round(d['odo_km'], 1) if d['odo_km'] is not None else None, d['gps_points'],
                         round(d['gps_km'], 1) if d['gps_km'] is not None else None,
                         d['odo_start'], d['odo_close'], d['odo_note'], d['typed'],
                         centre_of.get(d['account'], ''), None])
        vd_last = write_rows(ws, 5, rows, [None, None, 'dd mmm', None, None, None, 'hh:mm', 'hh:mm', '0.0', '#,##0.0', '0',
                                           '#,##0.0', '0', '0', None, None, None, '#,##0.0'])
        autofilter(ws, 4, vd_last, len(cols))
    V = lambda col: rng('Van days', col, vd_last)

    # ── Fuel by route (SAP diesel issues) ──
    fr_rows = {}
    if diesel:
        ws = wb.create_sheet('Diesel issued')
        title(ws, f'Diesel issued in SAP, {month_name}', 'As posted to each van route\'s cost centre (item DSL001). Pasted from SAP; prices in USD.')
        cols = ['Posting date', 'Cost centre (SAP)', 'Route', 'Item', 'Litres', 'Price', 'Amount']
        header(ws, 4, cols, [11, 26, 22, 9, 9, 9, 10])
        rows = [[x['day'], x['centre_name'], x['centre'], x['item'], x['litres'], x['price'], x['amount']]
                for x in sorted(diesel, key=lambda x: (x['centre'], x['day']))]
        di_last = write_rows(ws, 5, rows, ['dd mmm', None, None, None, '#,##0', '0.00', '#,##0.00'])
        autofilter(ws, 4, di_last, len(cols))
        D = lambda col: rng('Diesel issued', col, di_last)

        ws = wb.create_sheet('Fuel by route')
        title(ws, f'Fuel per route, {month_name}',
              'Litres are exact (SAP issues). Km is every van truck-day booked to the route\'s van accounts, by odometer.')
        cols = ['Route', 'Van accounts', 'Litres issued', 'Cost (USD)', 'Km driven (odometer)', 'Litres per km',
                'L per 100 km', 'Km per litre', 'Truck-days with no odometer km']
        header(ws, 4, cols, [22, 30, 10, 10, 12, 9, 9, 9, 12])
        r = 5
        for key, name, accts in COST_CENTRES:
            if not any(x['centre'] == name for x in diesel) and not accts:
                continue
            ws.cell(row=r, column=1, value=name).font = font(bold=True)
            ws.cell(row=r, column=2, value=', '.join(f'{a} ({route_name.get(a, "")})' for a in accts) or 'no van route matches this cost centre').font = font()
            formula(ws, r, 3, f'=SUMIFS({D("E")},{D("C")},$A{r})', '#,##0')
            formula(ws, r, 4, f'=SUMIFS({D("G")},{D("C")},$A{r})', '#,##0.00')
            formula(ws, r, 5, f'=SUMIFS({V(VAN_DAY_COL['Odometer km'])},{V(VAN_DAY_COL['Cost centre'])},$A{r})' if accts else '=0', '#,##0')
            formula(ws, r, 6, f'=IF(E{r}>0,C{r}/E{r},"")', '0.000')
            formula(ws, r, 7, f'=IF(E{r}>0,C{r}/E{r}*100,"")', '0.0')
            formula(ws, r, 8, f'=IF(AND(C{r}>0,E{r}>0),E{r}/C{r},"")', '0.00')
            formula(ws, r, 9, f'=COUNTIFS({V(VAN_DAY_COL['Cost centre'])},$A{r},{V(VAN_DAY_COL['Odometer km'])},"")' if accts else '=0', '0')
            fr_rows[name] = r
            r += 1
        tot = r
        ws.cell(row=tot, column=1, value='All van routes').font = font(bold=True)
        for col, fmt in ((3, '#,##0'), (4, '#,##0.00'), (5, '#,##0'), (9, '0')):
            L = get_column_letter(col)
            formula(ws, tot, col, f'=SUM({L}5:{L}{tot - 1})', fmt, bold=True).fill = SUB_FILL
        formula(ws, tot, 7, f'=IF(E{tot}>0,C{tot}/E{tot}*100,"")', '0.0', bold=True).fill = SUB_FILL
        formula(ws, tot, 8, f'=IF(E{tot}>0,E{tot}/C{tot},"")', '0.00', bold=True).fill = SUB_FILL
        ws.cell(row=tot + 2, column=1, value=(
            'Routes with diesel but no matching van account (e.g. East 2) count in the litres total but not in km, '
            'so the all-routes L/100 km is overstated by their litres. Bulawayo routes had no diesel under these cost centres.')).font = font(italic=True, color='5A6B7D')

        # Each truck-day's share of its route's diesel, by km.
        vws = wb['Van days']
        for i in range(5, vd_last + 1):
            cc, km = VAN_DAY_COL['Cost centre'], VAN_DAY_COL['Odometer km']
            formula(vws, i, VAN_DAY_COLUMNS.index('Fuel share (L)') + 1,
                    f"=IF(OR({cc}{i}=\"\",{km}{i}=\"\"),\"\",{km}{i}*IFERROR(INDEX('Fuel by route'!$F$5:$F${tot - 1},MATCH({cc}{i},'Fuel by route'!$A$5:$A${tot - 1},0)),0))", '#,##0.0')

    # ── Daily mileage ──
    ws = wb.create_sheet('Daily mileage')
    title(ws, f'Kilometres per vehicle per day, {month_name}',
          'Cartrack vehicles: trip km by the day the trip started. Van trucks: odometer km typed at Start and End Day.')
    names = [label(r) for r in regs] + [plate_label(p) for p in phone_plates]
    header(ws, 4, ['Date'] + names, [11] + [15] * len(names))
    for i, day in enumerate(days, 5):
        formula(ws, i, 1, day, 'ddd dd mmm')
        for j, reg in enumerate(regs, 2):
            formula(ws, i, j, f'=SUMIFS({T("G")},{T("A")},"{reg}",{T("B")},$A{i})', '#,##0;-#,##0;"-"')
        for j, p in enumerate(phone_plates, 2 + len(regs)):
            formula(ws, i, j, f'=SUMIFS({V(VAN_DAY_COL['Odometer km'])},{V(VAN_DAY_COL['Plate'])},"{p}",{V(VAN_DAY_COL['Date'])},$A{i})', '#,##0;-#,##0;"-"')
    dm_last = 4 + len(days)
    tot = dm_last + 1
    ws.cell(row=tot, column=1, value='Month').font = font(bold=True)
    for j in range(2, 2 + len(names)):
        L = get_column_letter(j)
        formula(ws, tot, j, f'=SUM({L}5:{L}{dm_last})', '#,##0', bold=True).fill = SUB_FILL
    dm_col = {n: get_column_letter(j) for j, n in enumerate(names, 2)}

    # ── Locations ──
    ws = wb.create_sheet('Locations')
    title(ws, f'Places visited and how often, {month_name}',
          'Cartrack: where each moving trip ended (Cartrack\'s place name). Van trucks: shops the truck\'s reps checked into (ADRs left out). '
          'Days = distinct days with at least one visit.')
    cols = ['Vehicle', 'Source', 'Place / shop', 'Visits', 'Days visited', 'First visit', 'Last visit']
    header(ws, 4, cols, [32, 10, 52, 8, 10, 12, 12])
    rows, cartrack_rows = [], 0
    for reg in regs:
        seen = defaultdict(list)
        for t in trips:
            if t['registration'] == reg and (t['trip_distance'] or 0) >= 100:
                seen[place_of(t['end_location'])].append(ts(t['end_timestamp']).date())
        for place, dates in sorted(seen.items(), key=lambda kv: (-len(kv[1]), kv[0])):
            rows.append([label(reg), 'Cartrack', place, None, len(set(dates)), min(dates), max(dates)])
    cartrack_rows = len(rows)
    # Shop check-ins for every truck, the tracked van included (its reps check in like any other).
    for p in [r for r in regs if r in links] + phone_plates:
        seen = defaultdict(list)
        for plate, day, shop in vans['visits']:
            if plate == p:
                seen[shop].append(dt.date.fromisoformat(day))
        for shop, dates in sorted(seen.items(), key=lambda kv: (-len(kv[1]), kv[0])):
            rows.append([label(p) if p in regs else plate_label(p), 'Phone', shop, len(dates), len(set(dates)), min(dates), max(dates)])
    loc_last = write_rows(ws, 5, rows, [None, None, None, '0', '0', 'dd mmm', 'dd mmm'])
    for i in range(5, 5 + cartrack_rows):
        reg = ws.cell(row=i, column=1).value.split(' ')[0]
        formula(ws, i, 4, f'=COUNTIFS({T("A")},"{reg}",{T("F")},$C{i},{T("N")},"yes")', '0')
    autofilter(ws, 4, loc_last, len(cols))

    # ── Speeding ──
    ws = wb.create_sheet('Speeding')
    title(ws, f'Over-speeding, {month_name} (Cartrack vehicles only)',
          'An incident is a trip on which Cartrack saw the vehicle above the posted road limit. Events are GPS samples over it; minutes are Cartrack\'s.')
    cols = ['Vehicle', 'Date', 'Start', 'From', 'To', 'Km', 'Max speed (km/h)', 'Road-speeding events', 'Speeding (min)']
    header(ws, 4, cols, [12, 11, 8, 40, 40, 8, 10, 11, 10])
    rows = []
    for t in trips:
        if t['road_speeding_events']:
            s = ts(t['start_timestamp'])
            rows.append([t['registration'], s.date(), s.time().replace(microsecond=0), place_of(t['start_location']),
                         place_of(t['end_location']), round((t['trip_distance'] or 0) / 1000, 2), t['max_speed'] or 0,
                         t['road_speeding_events'], round((t['road_speeding_duration_seconds'] or 0) / 60, 1)])
    rows.sort(key=lambda r: (r[0], -r[8], r[1]))
    sp_last = write_rows(ws, 5, rows, [None, 'dd mmm', 'hh:mm', None, None, '#,##0.0', '0', '0', '0.0'])
    autofilter(ws, 4, sp_last, len(cols))

    # ── Fuel sensor (Cartrack) ──
    if any(fills[r] for r in regs):
        ws = wb.create_sheet('Fuel sensor')
        title(ws, f'Tank sensor fills, {month_name}', 'Only AFQ9644 has a fuel sensor. Use as a cross-check on the diesel issued to its route.')
        cols = ['Vehicle', 'Date', 'Time', 'Litres', 'Where', 'Odometer (km)', 'Sensor says accurate']
        header(ws, 4, cols, [12, 11, 8, 9, 50, 12, 10])
        rows = []
        for reg in regs:
            for f in sorted(fills[reg], key=lambda f: f['fill_timestamp']):
                t = ts(f['fill_timestamp'])
                rows.append([reg, t.date(), t.time(), round(f['fill_amount_litres'], 1), place_of(f['fill_location']),
                             round((f['fill_odometer'] or 0) / 1000), 'yes' if f['accurate'] else 'no'])
        fs_last = write_rows(ws, 5, rows, [None, 'dd mmm', 'hh:mm', '#,##0.0', None, '#,##0', None])
        autofilter(ws, 4, fs_last, len(cols))

    # ── Drivers ──
    ws = wb.create_sheet('Drivers')
    title(ws, 'Who drives which vehicle',
          'Cartrack records no driver on any trip. For van trucks the system knows which reps opened the day on which plate, '
          'not who was at the wheel. ADRs sign in under a van account but work apart from the truck.')
    cols = ['Van account', 'Route', 'Rep', 'Username', 'Role', 'Active', 'Days opened (Start Day)', 'Shop check-ins',
            'Last login', 'Trucks on days opened (plate: days)']
    header(ws, 4, cols, [12, 16, 26, 22, 8, 7, 11, 10, 17, 44])
    opened = Counter((r['van_account'], r['username']) for r in vans['route_days'])
    rep_plates = defaultdict(Counter)
    for r in vans['route_days']:
        rep_plates[(r['van_account'], r['username'])][vans['plate_of'](r)] += 1
    calls = Counter((c['van_account'], c['username']) for c in (read_csv(vans_dir, 'checkins.csv') if vans_dir else []))
    rows = []
    for r in sorted(vans['reps'], key=lambda r: (r['van_account'], r['role'] != 'Sales', r['username'])):
        acct = r['van_account']
        name = f"{r['first_name'] or ''} {r['last_name'] or ''}".strip() or r['username']
        rows.append([acct, r['route_name'] or route_name.get(acct, ''), name, r['username'], r['role'],
                     'yes' if r['is_active'] in ('t', 'true', 'True') else 'no',
                     opened.get((acct, r['username']), 0), calls.get((acct, r['username']), 0), r['last_login_cat'] or None,
                     ', '.join(f'{p}: {n}' for p, n in rep_plates[(acct, r['username'])].most_common())])
    for reg in regs:
        if reg not in links:
            rows.append([label(reg), '', 'Not recorded in Cartrack', '', '', '', None, None, None, ''])
    dr_last = write_rows(ws, 5, rows, [None, None, None, None, None, None, '0', '0', None, None])
    autofilter(ws, 4, dr_last, len(cols))

    # ── Vehicles (by plate) ──
    if van_days:
        ws = wb.create_sheet('Vehicles')
        title(ws, f'Van trucks by plate, {month_name}',
              'Plate as typed at Start Day; a plate typed a few times one keystroke off a common one is folded into it. '
              'Trucks move between routes, so one plate can serve several van accounts.')
        cols = ['Plate', 'Typed as (truck-days)', 'Km on route days', 'Truck-days', 'Routes worked (days)',
                'Reps who opened the day (days)']
        header(ws, 4, cols, [11, 26, 11, 9, 40, 60])
        info = defaultdict(lambda: dict(typed=Counter(), reps=Counter()))
        for d in van_days:
            v = info[d['plate']]
            for t in d['typed'].split(', '):
                v['typed'][t] += 1
        for r in vans['route_days']:
            info[vans['plate_of'](r)]['reps'][r['username']] += 1
        rows = []
        for p in sorted(info, key=lambda p: (-sum(plate_routes[p].values()), p)):
            v = info[p]
            rows.append([p, ', '.join(f'{t} ({n})' for t, n in v['typed'].most_common()), None, sum(plate_routes[p].values()),
                         ', '.join(f'{route_name.get(a, a)} {a}: {n}' for a, n in plate_routes[p].most_common()),
                         ', '.join(f'{k} ({n})' for k, n in v['reps'].most_common())])
        pl_last = write_rows(ws, 5, rows, [None, None, '#,##0', '0', None, None])
        for i in range(5, pl_last + 1):
            formula(ws, i, 3, f'=SUMIFS({V(VAN_DAY_COL['Odometer km'])},{V(VAN_DAY_COL['Plate'])},$A{i})', '#,##0')
        autofilter(ws, 4, pl_last, len(cols))

    # ── Summary ──
    ws = summary
    title(ws, f'Fleet summary, {month_name}',
          f'Built {dt.date.today():%d %b %Y}. Cartrack vehicles from Cartrack; van trucks from the phones; diesel from SAP. See Notes.')
    cols = ['Vehicle', 'Source', 'Km in month', 'Days driven', 'Avg km per day driven', 'Trips',
            'Hours moving', 'Avg speed moving (km/h)', 'Max speed (km/h)', 'Speeding trips', 'Speeding (min)',
            'Fuel (L)', 'Fuel basis', 'L per 100 km', 'Km per litre', 'Cartrack month odometer (km)']
    header(ws, 4, cols, [34, 10, 11, 9, 10, 8, 9, 11, 9, 9, 9, 9, 24, 9, 9, 13])
    fmts = [None, None, '#,##0', '0', '#,##0', '0', '#,##0.0', '0.0', '0', '0', '#,##0', '#,##0', None, '0.0', '0.00', '#,##0']

    def put(row, vals):
        for c, v in enumerate(vals, 1):
            cell = ws.cell(row=row, column=c, value=v)
            cell.font = font(bold=(c == 1))
            if fmts[c - 1]:
                cell.number_format = fmts[c - 1]
            if c >= 3:
                cell.alignment = Alignment(horizontal='right')

    row = 5
    for reg in regs:
        col = dm_col[label(reg)]
        dm = f"'Daily mileage'!{col}5:{col}{dm_last}"
        if diesel and reg in links:
            fuel, basis = f'=SUMIFS({V(VAN_DAY_COL['Fuel share (L)'])},{V(VAN_DAY_COL['Plate'])},"{reg}")', 'Share of route diesel (by km)'
        elif fills[reg]:
            fuel, basis = f'={round(sum(f["fill_amount_litres"] for f in fills[reg]), 1)}', 'Tank sensor fills'
        else:
            fuel, basis = None, 'No fuel data'
        put(row, [label(reg), 'Cartrack',
                  f"='Daily mileage'!{col}{tot}",
                  f'=COUNTIF({dm},">=1")',  # a day counts from 1 km; a yard ignition cycle moves metres
                  f'=IFERROR(C{row}/D{row},0)',
                  f'=COUNTIFS({T("A")},"{reg}",{T("N")},"yes")',
                  f'=SUMIFS({T("J")},{T("A")},"{reg}")/60', f'=IFERROR(C{row}/G{row},0)',
                  f'=_xlfn.MAXIFS({T("K")},{T("A")},"{reg}")',
                  f'=COUNTIFS({T("A")},"{reg}",{T("L")},">0")', f'=SUMIFS({T("M")},{T("A")},"{reg}")',
                  fuel, basis,
                  f'=IF(N(L{row})>0,L{row}/C{row}*100,"")', f'=IF(N(L{row})>0,C{row}/L{row},"")',
                  round((odometers[reg].get('distance') or 0) / 1000)])
        if reg in KNOWN_VANS:
            lv = levels[reg]
            sensor = sum(f['fill_amount_litres'] for f in fills[reg])
            ws.cell(row=row, column=12).comment = Comment(
                f'Tank sensor: {sensor:.0f} L filled in {len(fills[reg])} fills'
                + (f", {lv['estimated_fuel_used']:.0f} L used by its estimate" if lv.get('estimated_fuel_used') else '')
                + '. The share here uses the km its reps typed, which can differ from Cartrack\'s.', 'Fleet report')
        row += 1
    for p in phone_plates:
        fuelled = diesel and any(a in centre_of for a in plate_routes[p])
        put(row, [plate_label(p), 'Phone',
                  f'=SUMIFS({V(VAN_DAY_COL['Odometer km'])},{V(VAN_DAY_COL['Plate'])},"{p}")', f'=COUNTIFS({V(VAN_DAY_COL['Plate'])},"{p}",{V(VAN_DAY_COL['Odometer km'])},">=1")',
                  f'=IFERROR(C{row}/D{row},0)', None, None, 'n/a', 'n/a', 'n/a', 'n/a',
                  f'=SUMIFS({V(VAN_DAY_COL['Fuel share (L)'])},{V(VAN_DAY_COL['Plate'])},"{p}")' if fuelled else None,
                  'Share of route diesel (by km)' if fuelled else
                  ('No diesel under a van cost centre' if diesel else 'No fuel data'),
                  f'=IF(AND(N(L{row})>0,C{row}>0),L{row}/C{row}*100,"")', f'=IF(AND(N(L{row})>0,C{row}>0),C{row}/L{row},"")', None])
        row += 1
    summary_last = row - 1
    ws.auto_filter.ref = f'A4:{get_column_letter(len(cols))}{summary_last}'

    # ── Notes ──
    ws = wb.create_sheet('Notes')
    ws.column_dimensions['A'].width = 24
    ws.column_dimensions['B'].width = 120
    unplaced = vans['unplaced']
    folded = {k: v for k, v in vans['alias'].items() if k != v}
    notes = [
        ('Period', f'{first:%d %b %Y} to {last:%d %b %Y}, CAT.'),
        ('Cartrack vehicles', ', '.join(label(r) for r in regs) + '. Only AFQ9644 is a van (UPC 2). Its reps type AHT2239 at Start Day, '
         'but their odometer readings follow AFQ9644\'s, so those days are booked to AFQ9644.'),
        ('Mileage (Cartrack)', 'Sum of trip distances by the day each trip started. Cross-check: the last Summary column is Cartrack\'s month odometer.'),
        ('Mileage (van trucks)', 'Closing minus opening odometer per truck per day. A van account can run two trucks on one day, so '
         'trucks are kept apart by plate. When a day\'s figure is impossible (negative, or more than three times that truck\'s '
         'usual day and over 300 km), the truck\'s own previous close or next opening is used and the Van days Note says so. '
         'Every reading is kilometres. When a missing End Day is filled from the next opening reading after a weekend, '
         'any weekend driving lands on the earlier day. Driving on a day nobody opened a route day is not counted.'),
        ('Plates', 'Folded as probable typos: ' + (', '.join(f'{k} as {v}' for k, v in sorted(folded.items())) or 'none') + '.'),
        ('Places', 'Cartrack: the place each moving trip ended. Van trucks: shops checked into by the reps on the truck that day. '
         f"Left out: {unplaced.get('ADR', 0)} ADR check-ins (ADRs work apart from the truck) and "
         f"{unplaced.get('no truck that day', 0)} check-ins by reps whose account had no single truck open that day."),
        ('Average speed', 'Cartrack: km divided by time moving (trip time less idling). Van trucks: not given. The phone takes a GPS fix '
         'only at Start Day, End Day, each check-in and each order, so it cannot separate driving from time parked at a shop.'),
        ('Over-speeding', 'Cartrack only: a trip counts as an incident when the vehicle went above the posted road limit, with '
         'Cartrack\'s count of samples and minutes over it. A phone does not record speed.'),
        ('Fuel', 'SAP diesel issues (item DSL001) per route cost centre, matched to van accounts by name: CBD VAN010, West 1 VAN009, '
         'West 2 VAN008, East 1 VAN013, Up country VAN014-VAN017. "East 2" matches no route in the system and is not shared out. '
         'Bulawayo had no diesel under these cost centres. A truck\'s fuel is its route\'s litres times its share of the route\'s km, '
         'so every truck on a route shows the route\'s L per 100 km; compare routes on the Fuel by route sheet. '
         'AFQ9644\'s tank sensor fills are on the Fuel sensor sheet as a cross-check. The other Cartrack vehicles have no fuel data.'),
        ('Drivers', 'Cartrack holds a default driver ID on each vehicle, but this login cannot read driver names and no trip has a '
         'driver tag. For van trucks the Drivers and Vehicles sheets list who opened each day on each plate.'),
    ]
    if not vans_dir:
        notes.append(('Not yet included', 'Van trucks (phone data). Run Export-FleetMonth.ps1 on the API server.'))
    if not diesel:
        notes.append(('Not yet included', 'SAP diesel issues (--diesel).'))
    title(ws, 'How each figure is worked out', '')
    for i, (k, v) in enumerate(notes, 4):
        ws.cell(row=i, column=1, value=k).font = font(bold=True)
        c = ws.cell(row=i, column=2, value=v)
        c.font = font()
        c.alignment = Alignment(wrap_text=True, vertical='top')
        ws.cell(row=i, column=1).alignment = Alignment(vertical='top')

    for sheet in wb.worksheets:
        sheet.sheet_view.showGridLines = sheet.title in ('Trips', 'Van days', 'Diesel issued')
    wb.save(out_path)
    return out_path


if __name__ == '__main__':
    p = argparse.ArgumentParser()
    p.add_argument('--month', required=True)
    p.add_argument('--cartrack', required=True)
    p.add_argument('--vans')
    p.add_argument('--diesel')
    p.add_argument('--out', required=True)
    a = p.parse_args()
    print(build(a.month, a.cartrack, a.vans, a.diesel, a.out))
