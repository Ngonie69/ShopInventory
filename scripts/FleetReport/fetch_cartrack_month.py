"""Pulls one month of Cartrack data for the fleet report. Read-only: every call is a GET.

    python -I scripts/FleetReport/fetch_cartrack_month.py --month 2026-10 --out <dir>

About 4 + 3 per vehicle requests: vehicles, trips (paged at 500, 31-day window), then per
vehicle fuel fills, fuel level and odometer for the month.
Credentials come from the API project's user secrets (Cartrack:Username / Cartrack:Password) or
CARTRACK_USERNAME / CARTRACK_PASSWORD.
"""
import argparse
import base64
import datetime as dt
import json
import os
import time
import urllib.error
import urllib.parse
import urllib.request

BASE = 'https://fleetapi-zw.cartrack.com/rest/'
SECRETS_ID = '2fee8ae2-b581-4db3-8bc4-de6892c0b9d8'


def credentials():
    user, pwd = os.environ.get('CARTRACK_USERNAME'), os.environ.get('CARTRACK_PASSWORD')
    if not (user and pwd):
        path = os.path.join(os.environ['APPDATA'], 'Microsoft', 'UserSecrets', SECRETS_ID, 'secrets.json')
        sec = json.load(open(path, encoding='utf-8-sig'))
        user, pwd = sec['Cartrack:Username'], sec['Cartrack:Password']
    return base64.b64encode(f'{user.strip()}:{pwd}'.encode()).decode()


def get(token, path, params):
    url = BASE + path + '?' + urllib.parse.urlencode(params)
    req = urllib.request.Request(url, headers={'Authorization': 'Basic ' + token, 'Accept': 'application/json'})
    for attempt in range(4):
        try:
            with urllib.request.urlopen(req, timeout=120) as r:
                return json.loads(r.read())
        except urllib.error.HTTPError as e:
            if e.code == 429 and attempt < 3:
                time.sleep(int(e.headers.get('Retry-After') or 30))
                continue
            body = e.read().decode(errors='replace')[:300]
            raise SystemExit(f'{e.code} on {path}: {body}')
        except (urllib.error.URLError, TimeoutError):
            if attempt == 3:
                raise
            time.sleep(5 * (attempt + 1))


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--month', required=True, help='yyyy-MM')
    p.add_argument('--out', required=True)
    a = p.parse_args()
    first = dt.date.fromisoformat(a.month + '-01')
    last = (first.replace(day=28) + dt.timedelta(days=4)).replace(day=1) - dt.timedelta(days=1)
    window = {'start_timestamp': f'{first} 00:00:00', 'end_timestamp': f'{last} 23:59:59'}
    os.makedirs(a.out, exist_ok=True)
    token = credentials()
    calls = 0

    def save(name, data):
        json.dump(data, open(os.path.join(a.out, name), 'w', encoding='utf-8'))

    vehicles = get(token, 'vehicles', {'limit': 500}); calls += 1
    save('vehicles.json', vehicles)

    trips, page = [], 1
    while True:
        res = get(token, 'trips', {**window, 'limit': 500, 'page': page}); calls += 1
        trips += res.get('data') or []
        if page >= (res.get('meta') or {}).get('last_page', 1):
            break
        page += 1
    # The window returns any trip overlapping it; keep only those that started inside the month.
    trips = [t for t in trips if str(first) <= t['start_timestamp'][:10] <= str(last)]
    save('trips.json', trips)

    for v in vehicles['data']:
        reg = v['registration']
        save(f'fills_{reg}.json', get(token, f'fuel/fills/{urllib.parse.quote(reg)}', {**window, 'limit': 500}))
        save(f'level_{reg}.json', get(token, f'fuel/level/{urllib.parse.quote(reg)}', window))
        save(f'odo_{reg}.json', get(token, f'vehicles/{urllib.parse.quote(reg)}/odometer', window))
        calls += 3

    print(f'{len(vehicles["data"])} vehicles, {len(trips)} trips, {calls} requests -> {a.out}')


if __name__ == '__main__':
    main()
