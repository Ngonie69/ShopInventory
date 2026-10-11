# SAP outage banner

The bar at the top of every staff page (`Components/Layout/SapOutageBanner.razor`). It has three
kinds: **down** (an outage is open, or SAP is switched off), **slow** (SAP answers a ping but SL
Health finds its logins or queries failing) and **back** (an outage ended in the last 30 minutes).
With SL Health being read, the down and slow bars carry a "why" sentence. The rules are in
`docs/operations/sap-outages.md`.

## How a user reaches it

Sign in. It is on every page under `MainLayout`, and shows nothing at all while SAP is well.

## Driving it

The bar is fed by the API's outage record, so it cannot be produced by seeding a page. It needs a
SAP that can be taken down, which is SL Health's simulator (the SLHealth repo):

| Piece | Where | Notes |
|---|---|---|
| Simulator | `tools/SLSimulator`, launch entry `simulator-50102-https` | `POST /sim/down?seconds=N`, `POST /sim/servertools?down=true\|false` |
| SL Health | launch entry `slhealth-5083-certificate` | carries the dev token for ShopInventory; add `--Security:DevAutoLogin=admin` to drive its dashboard |
| API | this repo, own port | `SAP__Enabled=true`, `SAP__ServiceLayerUrl=http://localhost:50102/b1s/v1/`, any non-empty `SAP__CompanyDB` / `SAP__Username` / `SAP__Password`, `SlHealth__Url=http://localhost:5083`, `SlHealth__Token=<the dev token>` |
| Web | this repo, own port | `ApiSettings__BaseUrl` at that API |

Run **both** ShopInventory apps on throwaway databases (`ConnectionStrings__DefaultConnection` with
another `Database=`). The API writes real `SapOutages` rows, which any other instance on the dev
database would obey, and the simulator answers every entity read with a made-up row, which the Web's
cache syncs would write into the dev cache. A fresh database in Development seeds `admin` /
`admin123` with two-factor off.

```bash
python .claude/skills/verify-shopinventory-web/scripts/read_sap_banner.py sim-down --wait-kind down --shots
```

`read_sap_banner.py` signs in, reads the bar's kind, headline, why and detail, and with `--wait-kind`
reloads until the bar is of that kind and says how long it took.

## Proof it worked

- **Down, with SL Health:** the API log has `the outage is declared now rather than after 3
  failures`, and the newest `SapOutages` row has one or two `FailedProbes` at declaration. Without SL
  Health the log has `3 probes failed in a row` and the bar has no why sentence.
- **Why:** a maintenance window made on SL Health's dashboard gives "Planned maintenance until HH:mm
  (reason)."; a Service Layer restart from the dashboard gives "SAP is being restarted."
- **Slow:** two minutes after `servertools?down=true` the bar reads "SAP is answering slowly since
  HH:mm" / "New logins will fail: Server Tools not answering.", `SapOutages` has no new row, and the
  `sap` check in `GET /api/health` is still Healthy.
- **SL Health stopped:** the API logs `SL Health is not being read`, and within 60 seconds the `sap`
  check's data has no `slHealth…` keys.

## Gotchas

- **The probe fires every 30 seconds.** A `/sim/down` posted just before one is seen by the probe
  before SL Health has reported it, and the outage is then declared on the second failed probe, not
  the first. To see the first, post it just after a probe: `SELECT next_fire_time FROM qrtz_triggers
  WHERE job_name = 'sap-availability-probe'` (UTC ticks).
- **The simulator's own restart lasts 12 seconds**, shorter than the probe interval. Post
  `/sim/down?seconds=75` straight after starting a restart from the dashboard to keep it away.
- **A failed probe may be this node's open circuit, not SAP.** Other jobs' failed requests open the
  breaker within seconds of SAP going, and a probe it refuses counts as failed, as it always has.
- **SL Health's "End now" on a maintenance window is a native `confirm()`**, which the built-in
  browser dismisses. `POST /api/maintenance/{id}/cancel` with the header `X-SLHealth: 1` does the same.
- **SL Health's database path must be short.** Run from a deep folder its SQLite `-wal` file passes
  260 characters and it dies with "unable to open database file"; pass `--Storage:DatabasePath`.
- The page opens in whichever theme the profile last used. Set the theme before shooting (the driver
  does) and trust the computed colours over the PNG.
