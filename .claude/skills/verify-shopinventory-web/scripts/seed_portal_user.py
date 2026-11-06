"""Verification scaffolding, LOCAL dev Web DB only: add or remove one customer-portal account.

usage: seed_portal_user.py add <cardCode> <passwordFile> | remove <cardCode>
The password is generated here, hashed with the app's own BCrypt.Net-Next, and written to passwordFile.
"""
import json
import os
import secrets
import subprocess
import sys

action, card = sys.argv[1], sys.argv[2]

secrets_path = os.path.expanduser(
    "~/AppData/Roaming/Microsoft/UserSecrets/e57cecec-6502-42fd-9f52-59181c014695/secrets.json")
data = json.load(open(secrets_path, encoding="utf-8-sig"))


def strings(o):
    if isinstance(o, dict):
        for v in o.values():
            yield from strings(v)
    elif isinstance(o, str):
        yield o


cs = next(v for v in strings(data) if "Host=" in v and "ShopInventoryWeb" in v)
kv = dict(part.split("=", 1) for part in cs.split(";") if "=" in part)
assert kv.get("Host", "localhost") in ("localhost", "127.0.0.1"), "refusing: not a local database"
env = dict(os.environ, PGPASSWORD=kv["Password"])


def psql(sql):
    r = subprocess.run(["psql", "-w", "-v", "ON_ERROR_STOP=1", "-h", kv.get("Host", "localhost"),
                        "-U", kv.get("Username", "postgres"), "-d", "ShopInventoryWeb", "-c", sql],
                       env=env, capture_output=True, text=True)
    if r.returncode:
        raise SystemExit(r.stderr)
    return r.stdout


if action == "remove":
    # Whatever else the sign-in wrote under this account goes first; then the account.
    tables = psql("""SELECT string_agg(table_name, ',') FROM information_schema.columns
        WHERE column_name = 'CardCode' AND table_schema = 'public' AND table_name LIKE 'Customer%'
          AND table_name <> 'CustomerPortalUsers';""")
    names = [t for t in tables.splitlines()[2].strip().split(",") if t]
    for table in names:
        print(table, psql(f"""DELETE FROM "{table}" WHERE "CardCode" = '{card}';""").strip())
    print(psql(f"""DELETE FROM "CustomerPortalUsers" WHERE "CardCode" = '{card}';"""))
    raise SystemExit(0)

password = "Verify-" + secrets.token_urlsafe(12)
dll = os.path.expanduser("~/.nuget/packages/bcrypt.net-next/4.0.3/lib/net6.0/BCrypt.Net-Next.dll")
ps = (f"$ErrorActionPreference='Stop'; Add-Type -Path '{dll}'; "
      f"[BCrypt.Net.BCrypt]::HashPassword($env:SEED_PW, [BCrypt.Net.BCrypt]::GenerateSalt(12))")
hashed = subprocess.run(["pwsh", "-NoProfile", "-Command", ps], env=dict(os.environ, SEED_PW=password),
                        capture_output=True, text=True, check=True).stdout.strip()
assert hashed.startswith("$2") and len(hashed) == 60, f"bad hash: {hashed!r}"

print(psql(f"""INSERT INTO "CustomerPortalUsers"
  ("Id","CardCode","CardName","PasswordHash","IsActive","TwoFactorEnabled","EmailVerified",
   "FailedLoginAttempts","CreatedAt","MustChangePassword","Status","ReceiveStatements","AccountStructure")
SELECT COALESCE(MAX("Id"),0)+1, '{card}', 'Greenfield Supermarket', '{hashed}', true, false, true,
   0, now(), false, 'Active', true, 'Single' FROM "CustomerPortalUsers";"""))
open(sys.argv[3], "w").write(password)
print("seeded", card)
