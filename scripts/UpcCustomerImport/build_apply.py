"""Step 2: match the UPC workbook outlets to the shops already on each van and write the apply SQL.

    python -I scripts/UpcCustomerImport/build_apply.py <folder holding upc-customers.csv>

Reads out/outlets.csv (from read_upc_workbooks.py) and the server export (Export-UpcRouteCustomers.ps1),
and writes, all in out/ (git-ignored):

    plan.csv       one row per shop: what will happen to it, and why
    review.csv     shops left out of the load because the match is a judgement call
    upc-apply.sql  run on the server by Apply-UpcRouteCustomers.ps1 (dry run unless -Apply)

An optional decisions.csv in the export folder settles review rows: columns `key` (as in review.csv)
and `decision` (`new`, `skip`, or the Id of an existing customer).

How outlets become shops
  The workbook lists a shop once per day it is called on, and some names belong to several branches.
  Rows of one van with the same name are one shop when their phones match, or when they are in the
  same town and their phones do not disagree. Otherwise they are separate shops, and each is named
  with its town ("Masamvu (Nyanga)") so the rep can tell them apart on the handset. Blocked (🚫)
  outlets are left out.

How shops are matched to the van's customers
  The same keys as the Calling schedule page (RouteScheduleMatcher): exact is letters and digits,
  close is that with trade words ("shop", "groceries", ...) removed. One hit is a match; two are a
  review. No hit, but a name at least 75% alike, is a review too: creating it would likely make a
  duplicate. Nothing alike is created.

What the apply does, in one transaction
  - matched shops: phone and address filled where blank (never overwritten), reactivated if removed
  - new shops: created on the van account with the code the app would give them
  - calling days: every calling day on the four vans is replaced by the workbook's
"""
import collections
import csv
import pathlib
import sys

HERE = pathlib.Path(__file__).resolve().parent
OUT = HERE / "out"
DAY_NUMBERS = {"Sunday": 0, "Monday": 1, "Tuesday": 2, "Wednesday": 3, "Thursday": 4, "Friday": 5,
               "Saturday": 6}
# RouteScheduleMatcher.TradeWords
TRADE_WORDS = {
    "the", "shop", "shops", "tuckshop", "grocery", "groceries", "grocer", "investment", "investments",
    "trading", "tradings", "general", "dealer", "dealers", "supermarket", "store", "stores",
    "wholesale", "wholesales", "wholesaler", "wholesalers", "retail", "enterprise", "enterprises",
    "pvt", "ltd", "private", "limited",
}
SUGGESTION_THRESHOLD = 0.75


def exact_key(name):
    return "".join(c.lower() for c in name.replace("&", "and") if c.isalnum())


def words(name):
    word, out = [], []
    for c in name.replace("&", " and "):
        if c.isalnum():
            word.append(c.lower())
        elif word:
            out.append("".join(word))
            word = []
    if word:
        out.append("".join(word))
    return out


def core_key(name):
    kept = [w for w in words(name) if w not in TRADE_WORDS]
    return "".join(kept)


def distance(a, b):
    previous = list(range(len(b) + 1))
    for i in range(1, len(a) + 1):
        current = [i] + [0] * len(b)
        for j in range(1, len(b) + 1):
            current[j] = min(current[j - 1] + 1, previous[j] + 1, previous[j - 1] + (a[i - 1] != b[j - 1]))
        previous = current
    return previous[len(b)]


def similarity(a, b):
    if not a or not b:
        return 0.0
    edit = 1.0 - distance(a, b) / max(len(a), len(b))
    contained = 0.8 if min(len(a), len(b)) >= 4 and (a in b or b in a) else 0.0
    return max(edit, contained)


def digits(name):
    return "".join(c for c in name if c.isdigit())


def phone_digits(phone):
    """The last nine digits, so 0772..., +263 772... and 263772... compare equal."""
    only = digits(phone or "")
    return only[-9:] if len(only) >= 9 else None


def code_of(name):
    """CreateRouteCustomerHandler.NormalizeCode."""
    out = []
    for c in name.strip().upper():
        if c.isalnum():
            out.append(c)
        elif c in "-_":
            out.append("-")
    return "".join(out).strip("-")[:50]


def town_label(town):
    town = " ".join(town.replace(",", ", ").split())
    town = {"Bindura Locations/ Rank": "Bindura Rank"}.get(town, town)
    return town.title() if town.isupper() else town


def read_csv(path):
    with open(path, newline="", encoding="utf-8-sig") as handle:
        return list(csv.DictReader(handle))


def shops_of(outlets):
    """Outlet rows -> shops, per the merge rule in the module docstring."""
    by_name = collections.defaultdict(list)
    for row in outlets:
        if row["status"] != "blocked":
            by_name[(row["account"], row["code"])].append(row)

    shops = []
    for (account, _), rows in by_name.items():
        clusters = []
        for row in rows:
            home = None
            for cluster in clusters:
                phones = {r["phone"] for r in cluster if r["phone"]}
                towns = {r["town"] for r in cluster}
                if row["phone"] and row["phone"] in phones:
                    home = cluster
                elif row["town"] in towns and (not row["phone"] or not phones or row["phone"] in phones):
                    home = cluster
                if home:
                    break
            if home:
                home.append(row)
            else:
                clusters.append([row])

        for cluster in clusters:
            first = cluster[0]
            name = first["name"].strip()
            if len(clusters) > 1:
                name = "%s (%s)" % (name, town_label(first["town"]))
            phone = next((r["phone"] for r in cluster if r["phone"]), "")
            contact = next((r for r in cluster if r["contact"]), None)
            address = town_label(first["town"])
            if contact:
                address += "; contact %s%s" % (contact["contact"], " (%s)" % contact["title"] if contact["title"] else "")
            days = sorted({DAY_NUMBERS[d] for r in cluster for d in r["days"].split("+")})
            shops.append(dict(
                key="%s|%s" % (account, code_of(name)), account=account, route=first["route"],
                name=name[:200], base_name=first["name"].strip(), code=code_of(name), phone=phone,
                address=address[:500], days=days, refs=" ".join(r["ref"] for r in cluster),
                split=len(clusters) > 1, flags="; ".join(sorted({r["flags"] for r in cluster if r["flags"]}))))

    # Two branches of one name in one town (two "Mudemba" in Chimhanda, different phones) still share
    # a code. Number the later ones, as CreateRouteCustomerHandler.GenerateCode would.
    seen = collections.Counter()
    for shop in shops:
        seen[(shop["account"], shop["code"])] += 1
        number = seen[(shop["account"], shop["code"])]
        if number > 1:
            shop["name"] = "%s %d" % (shop["name"], number)
            shop["code"] = code_of(shop["name"])
            shop["key"] = "%s|%s" % (shop["account"], shop["code"])
    return shops


def match(shops, customers, decisions):
    """Decide each shop: match an existing customer, create it, or send it to review."""
    by_account = collections.defaultdict(list)
    for c in customers:
        by_account[c["Account"]].append(c)

    claimed = collections.defaultdict(list)
    for shop in shops:
        mine = by_account.get(shop["account"], [])
        decision = decisions.get(shop["key"], "").strip().lower()
        shop["candidates"] = []
        if decision:
            if decision == "new":
                shop["action"], shop["why"] = "create", "decided: new"
            elif decision == "skip":
                shop["action"], shop["why"] = "skip", "decided: skip"
            else:
                target = next((c for c in mine if c["Id"] == decision), None)
                if not target:
                    raise SystemExit("decisions.csv: %s names Id %s, which is not on %s."
                                     % (shop["key"], decision, shop["account"]))
                shop["action"], shop["why"], shop["existing"] = "match", "decided", target
            continue

        names = [shop["name"]] + ([shop["base_name"]] if shop["split"] else [])
        found = None
        for name in names:
            for kind, key_of in (("exact", exact_key), ("close", core_key)):
                key = key_of(name)
                hits = [c for c in mine if key and (key_of(c["Name"]) == key or exact_key(c["Code"]) == key)]
                if hits:
                    found = (kind, hits, name)
                    break
            if found:
                break

        if found:
            kind, hits, name = found
            if len(hits) == 1 and shop["split"] and name == shop["base_name"]:
                # One customer for a name the workbook gives several branches: only a phone tells which.
                if shop["phone"] and phone_digits(hits[0]["Phone"]) == phone_digits(shop["phone"]):
                    shop["action"], shop["why"], shop["existing"] = "match", "%s by name, branch by phone" % kind, hits[0]
                else:
                    shop["action"], shop["why"] = "review", "one customer '%s', several branches in the workbook" % hits[0]["Name"]
                    shop["candidates"] = hits
            elif len(hits) == 1:
                shop["action"], shop["why"], shop["existing"] = "match", kind, hits[0]
            else:
                shop["action"], shop["why"] = "review", "%d customers share this name" % len(hits)
                shop["candidates"] = hits
        else:
            mine_key = core_key(shop["base_name"]) or exact_key(shop["base_name"])
            alike = sorted(
                ((similarity(mine_key, core_key(c["Name"]) or exact_key(c["Name"])), c) for c in mine
                 # "Maregere 2" is not a misspelling of "Maregere 1": numbered branches differ by number.
                 if digits(c["Name"]) == digits(shop["base_name"]) or not digits(c["Name"]) or not digits(shop["base_name"])),
                key=lambda pair: -pair[0])
            alike = [c for score, c in alike if score >= SUGGESTION_THRESHOLD][:3]
            same_phone = [c for c in alike if shop["phone"] and phone_digits(c["Phone"]) == phone_digits(shop["phone"])]
            if len(same_phone) == 1:
                shop["action"], shop["why"], shop["existing"] = "match", "alike name, same phone", same_phone[0]
            elif alike:
                shop["action"], shop["why"], shop["candidates"] = "review", "a similar name is on the van", alike
            else:
                shop["action"], shop["why"] = "create", "no similar name on the van"

        if shop["action"] == "match":
            claimed[shop["existing"]["Id"]].append(shop)

    # Two workbook shops on one customer: neither is safe to load.
    for customer_id, claimants in claimed.items():
        if len(claimants) > 1:
            for shop in claimants:
                shop["candidates"] = [shop.pop("existing")]
                shop["action"], shop["why"] = "review", "%d workbook shops matched the same customer" % len(claimants)

    # A created code that an existing (unmatched) customer already holds would collide on the unique index.
    for shop in shops:
        if shop["action"] == "create":
            taken = {c["Code"].upper() for c in by_account.get(shop["account"], [])}
            if shop["code"] in taken:
                shop["action"], shop["why"] = "review", "code %s is already used on the van" % shop["code"]
                shop["candidates"] = [c for c in by_account[shop["account"]] if c["Code"].upper() == shop["code"]]
    return shops


def sql_text(value):
    return "NULL" if value in (None, "") else "'" + str(value).replace("'", "''") + "'"


def write_sql(shops, accounts, path):
    loaded = [s for s in shops if s["action"] in ("match", "create")]
    codes = collections.Counter((s["account"], (s["existing"]["Code"] if s.get("existing") else s["code"]).upper())
                                for s in loaded)
    clashes = [key for key, count in codes.items() if count > 1]
    if clashes:
        raise SystemExit("Two shops would share a code on one van: %s" % clashes)

    rows = []
    for s in loaded:
        existing = s.get("existing")
        rows.append("  (%s, %s, %s, %s, %s, %s, ARRAY[%s]::int[])" % (
            sql_text(s["account"]), sql_text(existing["Code"] if existing else s["code"]),
            existing["Id"] if existing else "NULL", sql_text(s["name"]), sql_text(s["phone"]),
            sql_text(s["address"]), ",".join(str(d) for d in s["days"])))

    account_list = ", ".join(sql_text(a) for a in sorted(accounts))
    sql = """-- Generated by scripts/UpcCustomerImport/build_apply.py. Run with Apply-UpcRouteCustomers.ps1.
-- Dry run unless psql is given -v apply=true; either way it prints what it did.
\\set ON_ERROR_STOP on
SET client_encoding = 'UTF8';
BEGIN;

CREATE TEMP TABLE upc_plan (
  account text NOT NULL, code text NOT NULL, existing_id int, name text NOT NULL,
  phone text, address text, days int[] NOT NULL, new_id int) ON COMMIT DROP;

INSERT INTO upc_plan (account, code, existing_id, name, phone, address, days) VALUES
%s;

\\echo '== Guard: every matched customer is still on its van'
SELECT count(*) = 0 AS guard_ok
FROM upc_plan p
WHERE p.existing_id IS NOT NULL
  AND NOT EXISTS (SELECT 1 FROM "RouteCustomers" rc
                  WHERE rc."Id" = p.existing_id AND upper(btrim(rc."AssignedBusinessPartnerCode")) = p.account) \\gset
\\if :guard_ok
\\else
  \\echo 'A matched customer has moved or been deleted since the export. Nothing was changed; export again.'
  ROLLBACK;
  \\quit
\\endif

\\echo '== Guard: no shop to be created is on its van already (this file was applied, or a rep added it since the export)'
SELECT p.account, p.code, p.name, rc."Id" AS "ExistingId", rc."Name" AS "ExistingName"
FROM upc_plan p
JOIN "RouteCustomers" rc ON upper(btrim(rc."AssignedBusinessPartnerCode")) = p.account AND upper(rc."Code") = p.code
WHERE p.existing_id IS NULL
ORDER BY 1, 2;
SELECT count(*) = 0 AS fresh_ok
FROM upc_plan p
JOIN "RouteCustomers" rc ON upper(btrim(rc."AssignedBusinessPartnerCode")) = p.account AND upper(rc."Code") = p.code
WHERE p.existing_id IS NULL \\gset
\\if :fresh_ok
\\else
  \\echo 'The shops listed above are already on their van. Nothing was changed; export again and rebuild.'
  ROLLBACK;
  \\quit
\\endif

\\echo '== Matched customers: phone and address filled where blank, reactivated if removed'
UPDATE "RouteCustomers" rc
SET "Phone" = CASE WHEN coalesce(btrim(rc."Phone"), '') = '' THEN p.phone ELSE rc."Phone" END,
    "Address" = CASE WHEN coalesce(btrim(rc."Address"), '') = '' THEN p.address ELSE rc."Address" END,
    "IsActive" = true,
    "UpdatedAt" = now()
FROM upc_plan p
WHERE rc."Id" = p.existing_id
  AND ((coalesce(btrim(rc."Phone"), '') = '' AND p.phone IS NOT NULL)
       OR (coalesce(btrim(rc."Address"), '') = '' AND p.address IS NOT NULL)
       OR NOT rc."IsActive");

\\echo '== New customers'
WITH created AS (
  INSERT INTO "RouteCustomers" ("AssignedBusinessPartnerCode", "Code", "Name", "Phone", "Address", "IsActive", "CreatedAt")
  SELECT p.account, p.code, p.name, p.phone, p.address, true, now()
  FROM upc_plan p
  WHERE p.existing_id IS NULL
  RETURNING "Id", "AssignedBusinessPartnerCode", "Code")
UPDATE upc_plan p SET new_id = c."Id"
FROM created c
WHERE c."AssignedBusinessPartnerCode" = p.account AND c."Code" = p.code;

\\echo '== Calling days on the four vans replaced by the workbook'
DELETE FROM "RouteCustomerVisitDays" v
USING "RouteCustomers" rc
WHERE v."RouteCustomerId" = rc."Id" AND upper(btrim(rc."AssignedBusinessPartnerCode")) IN (%s);

INSERT INTO "RouteCustomerVisitDays" ("RouteCustomerId", "DayOfWeek", "CreatedAt")
SELECT DISTINCT coalesce(p.existing_id, p.new_id), d, now()
FROM upc_plan p CROSS JOIN LATERAL unnest(p.days) AS d
WHERE coalesce(p.existing_id, p.new_id) IS NOT NULL;

\\echo '== Result per van'
SELECT upper(btrim(rc."AssignedBusinessPartnerCode")) AS "Account",
       count(DISTINCT rc."Id") FILTER (WHERE rc."IsActive") AS "ActiveCustomers",
       count(DISTINCT rc."Id") FILTER (WHERE rc."Id" IN (SELECT new_id FROM upc_plan)) AS "Created",
       count(DISTINCT rc."Id") FILTER (WHERE rc."Id" IN (SELECT existing_id FROM upc_plan)) AS "Matched",
       count(DISTINCT v."RouteCustomerId") AS "WithCallingDays",
       count(v."Id") AS "CallingDays"
FROM "RouteCustomers" rc
LEFT JOIN "RouteCustomerVisitDays" v ON v."RouteCustomerId" = rc."Id"
WHERE upper(btrim(rc."AssignedBusinessPartnerCode")) IN (%s)
GROUP BY 1 ORDER BY 1;

\\if :apply
  COMMIT;
  \\echo 'APPLIED.'
\\else
  ROLLBACK;
  \\echo 'DRY RUN: rolled back, nothing was changed. Run again with -Apply to keep it.'
\\endif
""" % (",\n".join(rows), account_list, account_list)
    path.write_text(sql, encoding="utf-8", newline="\n")
    return len(rows)


def main(export_folder):
    export_folder = pathlib.Path(export_folder)
    outlets = read_csv(OUT / "outlets.csv")
    customers = read_csv(export_folder / "upc-customers.csv")
    for c in customers:
        c["Account"] = c["Account"].strip().upper()
        c["Phone"] = c.get("Phone") or ""
    decisions_path = export_folder / "decisions.csv"
    decisions = {r["key"]: r["decision"] for r in read_csv(decisions_path)} if decisions_path.exists() else {}

    accounts = sorted({o["account"] for o in outlets})
    shops = match(shops_of(outlets), customers, decisions)

    fields = ["key", "account", "route", "action", "why", "name", "code", "existing_id", "existing_name",
              "phone", "existing_phone", "address", "days", "refs", "flags"]
    names = {v: k for k, v in DAY_NUMBERS.items()}
    with open(OUT / "plan.csv", "w", newline="", encoding="utf-8-sig") as handle:
        writer = csv.DictWriter(handle, fieldnames=fields, extrasaction="ignore")
        writer.writeheader()
        for s in sorted(shops, key=lambda s: (s["account"], s["action"], s["name"].lower())):
            e = s.get("existing") or {}
            writer.writerow(dict(s, existing_id=e.get("Id", ""), existing_name=e.get("Name", ""),
                                 existing_phone=e.get("Phone", ""),
                                 days="+".join(names[d][:3] for d in s["days"])))
    with open(OUT / "review.csv", "w", newline="", encoding="utf-8-sig") as handle:
        writer = csv.writer(handle)
        writer.writerow(["key", "why", "workbook name", "town/contact", "phone", "candidates (Id: name, phone)", "decision"])
        for s in shops:
            if s["action"] == "review":
                writer.writerow([s["key"], s["why"], s["name"], s["address"], s["phone"],
                                 "; ".join("%s: %s, %s" % (c["Id"], c["Name"], c["Phone"] or "-") for c in s["candidates"]), ""])

    loaded = write_sql(shops, accounts, OUT / "upc-apply.sql")
    counts = collections.Counter((s["account"], s["action"]) for s in shops)
    for account in accounts:
        print("%s: %s" % (account, ", ".join("%d %s" % (counts[(account, a)], a)
                                             for a in ("match", "create", "review", "skip"))))
    print("%d shops in upc-apply.sql; review.csv holds %d" % (loaded, sum(s["action"] == "review" for s in shops)))


if __name__ == "__main__":
    if len(sys.argv) != 2:
        raise SystemExit(__doc__)
    main(sys.argv[1])
