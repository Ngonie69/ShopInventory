#!/usr/bin/env python3
"""Prove two builds sync the same special prices from SAP, and time them.

GetAllSpecialPricesAsync decides which customers get a negotiated price. A read that drops a row
does not fail -- the customer is just charged list price (order 2209, 2026-07-27) -- so a change to
it is proven by running the old and new builds against the same SAP company and comparing what each
one stored.

For each run this builds the ref in a temporary worktree, starts its API on localhost:5106
(Development, SAP company forced to KEFALOS_TEST_3), POSTs api/price/special-prices/sync, and dumps
BusinessPartnerSpecialPrices. The sync replaces that table with exactly the set the read returned,
and every row must carry this run's sync time or the run is rejected. Runs alternate so SAP's
latency drift falls on both builds.

Never point it at production: it refuses any company but KEFALOS_TEST_3. It REWRITES the local
BusinessPartnerSpecialPrices table, and needs the API's user secrets (SAP login, local Postgres,
Security:ApiKeys:0:Key) and psql on PATH or in the default PostgreSQL 17 location.

Usage:
    python scripts/compare_special_price_sync.py                          # origin/main vs HEAD
    python scripts/compare_special_price_sync.py --old <ref> --new <ref> --rounds 3

Exits 1 when any run's (CardCode, ItemCode, Price, ValidFrom, ValidTo) set differs from the first.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
USER_SECRETS_ID = "2fee8ae2-b581-4db3-8bc4-de6892c0b9d8"
TEST_COMPANY = "KEFALOS_TEST_3"
BASE = "http://localhost:5106"
SAP_UNREACHABLE = "connection attempt failed"


def load_secrets() -> dict:
    path = Path(os.environ["APPDATA"]) / "Microsoft" / "UserSecrets" / USER_SECRETS_ID / "secrets.json"
    return json.loads(path.read_text(encoding="utf-8-sig"))


def psql_path() -> str:
    found = shutil.which("psql")
    if found:
        return found
    default = Path(r"C:\Program Files\PostgreSQL\17\bin\psql.exe")
    if default.exists():
        return str(default)
    sys.exit("psql not found")


def git(*args: str) -> str:
    return subprocess.run(["git", "-C", str(REPO), *args], capture_output=True, text=True, check=True).stdout.strip()


def port_pids() -> set[str]:
    out = subprocess.run(["netstat", "-ano"], capture_output=True, text=True).stdout
    return {line.split()[-1] for line in out.splitlines() if re.search(r":5106\s.*LISTENING", line)}


def stop(proc: subprocess.Popen) -> None:
    subprocess.run(["taskkill", "/PID", str(proc.pid), "/T", "/F"], capture_output=True)
    for pid in port_pids():
        subprocess.run(["taskkill", "/PID", pid, "/T", "/F"], capture_output=True)
    deadline = time.time() + 30
    while port_pids() and time.time() < deadline:
        time.sleep(1)


def wait_up(proc: subprocess.Popen) -> None:
    deadline = time.time() + 240
    while time.time() < deadline:
        if proc.poll() is not None:
            raise RuntimeError(f"API exited with {proc.returncode}")
        try:
            urllib.request.urlopen(BASE + "/health/live", timeout=5)
            return
        except urllib.error.HTTPError:
            return
        except Exception:
            time.sleep(2)
    raise RuntimeError("API did not come up")


class Comparison:
    def __init__(self, secrets: dict, workdir: Path):
        self.api_key = secrets["Security:ApiKeys:0:Key"]
        parts = dict(p.split("=", 1) for p in secrets["ConnectionStrings:DefaultConnection"].split(";") if "=" in p)
        self.db = {k.strip().lower(): v for k, v in parts.items()}
        self.psql = psql_path()
        self.workdir = workdir

    def dump(self) -> list[list[str]]:
        sql = ('SELECT "CardCode","ItemCode","Price","ValidFrom","ValidTo","LastSyncedAt" '
               'FROM "BusinessPartnerSpecialPrices" ORDER BY 1,2')
        out = subprocess.run(
            [self.psql, "-w", "-h", self.db.get("host", "localhost"), "-p", self.db.get("port", "5432"),
             "-U", self.db.get("username", "postgres"), "-d", self.db["database"],
             "-A", "-t", "-F", "|", "-c", sql],
            capture_output=True, text=True, check=True,
            env=dict(os.environ, PGPASSWORD=self.db["password"])).stdout
        return [line.split("|") for line in out.splitlines() if line]

    def run(self, label: str, checkout: Path) -> dict:
        if port_pids():
            raise RuntimeError("port 5106 is already in use; stop the local API first")
        log_path = self.workdir / f"api-{label}-{int(time.time())}.log"
        env = dict(os.environ, ASPNETCORE_ENVIRONMENT="Development", SAP__CompanyDB=TEST_COMPANY)
        with open(log_path, "w") as log:
            proc = subprocess.Popen(
                ["dotnet", "run", "--no-build", "--no-launch-profile", "--urls", BASE],
                cwd=checkout / "ShopInventory", env=env, stdout=log, stderr=subprocess.STDOUT)
            try:
                wait_up(proc)
                time.sleep(10)  # let startup work settle before timing
                request = urllib.request.Request(
                    BASE + "/api/price/special-prices/sync", data=b"", method="POST",
                    headers={"X-API-Key": self.api_key, "Content-Type": "application/json"})
                started = time.time()
                try:
                    body = json.load(urllib.request.urlopen(request, timeout=1200))
                except urllib.error.HTTPError as error:
                    raise RuntimeError(f"sync failed {error.code}: {error.read()[:600]!r}")
                seconds = time.time() - started
                rows = self.dump()
            finally:
                stop(proc)

        stamps = {row[5] for row in rows}
        if len(stamps) > 1:
            raise RuntimeError(f"{label}: rows from more than one sync in the table: {sorted(stamps)}")
        return {"label": label, "seconds": round(seconds, 1), "response": body,
                "rows": [row[:5] for row in rows], "log": str(log_path)}

    def run_with_retry(self, label: str, checkout: Path) -> dict:
        for attempt in range(1, 4):
            try:
                return self.run(label, checkout)
            except RuntimeError as error:
                # SAP's connect timeouts (a flat 21 s) are the server, not the read under test.
                if SAP_UNREACHABLE not in str(error) or attempt == 3:
                    raise
                print(f"    attempt {attempt} could not reach SAP; retrying", flush=True)
                time.sleep(30)
        raise AssertionError("unreachable")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--old", default="origin/main", help="ref with the read to compare against")
    parser.add_argument("--new", default="HEAD", help="ref with the read under test")
    parser.add_argument("--rounds", type=int, default=2, help="new/old pairs to run (default 2)")
    args = parser.parse_args()

    secrets = load_secrets()
    if secrets.get("SAP:CompanyDB") != TEST_COMPANY:
        sys.exit(f"user secrets name {secrets.get('SAP:CompanyDB')!r}; this only runs against {TEST_COMPANY}")

    workdir = Path(tempfile.mkdtemp(prefix="special-price-compare-"))
    checkouts = {}
    try:
        for label, ref in (("old", args.old), ("new", args.new)):
            sha = git("rev-parse", "--short", ref)
            path = workdir / label
            git("worktree", "add", "--detach", str(path), ref)
            print(f"building {label} = {ref} ({sha})", flush=True)
            subprocess.run(["dotnet", "build", str(path / "ShopInventory" / "ShopInventory.csproj"),
                            "-nologo", "-v", "q"], check=True, capture_output=True)
            checkouts[label] = path

        comparison = Comparison(secrets, workdir)
        results = []
        for _ in range(args.rounds):
            for label in ("new", "old"):
                print(f"--- {label}", flush=True)
                result = comparison.run_with_retry(label, checkouts[label])
                results.append(result)
                print(f"    {result['seconds']} s, {len(result['rows'])} rows, response {json.dumps(result['response'])}",
                      flush=True)
    finally:
        for path in checkouts.values():
            subprocess.run(["git", "-C", str(REPO), "worktree", "remove", "--force", str(path)], capture_output=True)

    reference = set(map(tuple, results[0]["rows"]))
    print("\n(CardCode, ItemCode, Price, ValidFrom, ValidTo) of the first run:")
    for row in sorted(reference):
        print("   ", row)

    identical = all(set(map(tuple, r["rows"])) == reference for r in results)
    for result in results:
        rows = set(map(tuple, result["rows"]))
        if rows != reference:
            print(f"\n{result['label']} differs: missing {sorted(reference - rows)}, extra {sorted(rows - reference)}")

    print(f"\nIDENTICAL across every run: {identical} ({len(reference)} rows)")
    for label in ("new", "old"):
        print(f"{label}: {[r['seconds'] for r in results if r['label'] == label]} s")
    print(f"API logs: {workdir}")
    return 0 if identical else 1


if __name__ == "__main__":
    sys.exit(main())
