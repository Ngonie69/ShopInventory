# Maintenance mode

The switch that freezes transactions while the system is being worked on — a
database restore, a schema migration, an SAP outage being cleared. It lives in
the Web at **Settings → General → Maintenance Mode**, but it is enforced by the
API, so proving it means driving both.

## Sub-features

- Three audiences: mobile apps, web portal, tills and integrations. They
  partition the traffic, so ticking all three stops everything.
- Two scopes: `Transactions` (reads keep working) and `All`.
- An app picker that narrows the mobile audience only, greyed out when the
  phones are not ticked.
- An automatic lift after a chosen window, or on until somebody turns it off.
- An operator's own message, shown to whoever is refused.
- An Admin exemption on the web portal, so the people running the maintenance
  keep working.
- A banner on every page while a lockout covering the portal is running, in two
  variants: amber for somebody who is frozen, slate for an exempt Admin.

## How to get to it (user POV)

Sign in as an Admin, open `/settings`, choose **General** in the left column.
Maintenance Mode is the first card. Turn the switch on, tick who is frozen,
write a message, press **Turn maintenance mode on**.

## Driving it

Two scripts, because the operator can only ever see the exempt half:

```bash
python .claude/skills/verify-shopinventory-web/scripts/drive_maintenance_freeze.py
python .claude/skills/verify-shopinventory-web/scripts/drive_maintenance_frozen_user.py
```

The first drives the settings screen as Admin and then asks the API what a
frozen portal request gets. The second signs in as a non-admin and establishes
the half that matters — that they are actually stopped, and told why.

Both need `VERIFY_API_KEY` set to a key from the API's user secrets
(`Security:ApiKeys:0:Key`), because the settings endpoints are `AdminOnly` and
the Web reaches them with the key **and** a user token together.

**Proof it worked:** `result.json` with zero failures, plus
`03-banner.*.png` / `01-frozen-banner.*.png` showing the right banner variant.
Both scripts turn the lockout off in a `finally`, so a failed run does not leave
the system frozen — check `Maintenance.Enabled` in `SystemConfigs` if one is
interrupted.

## The non-admin account

`drive_maintenance_frozen_user.py` needs one, and the local database ships only
`admin`. Copy the admin's password hash rather than trying to compute one:

```sql
-- verification scaffolding, local dev DB only
INSERT INTO "Users" ("Id","Username","Email","PasswordHash","Role","FirstName","LastName",
                     "IsActive","EmailVerified","FailedLoginAttempts","CreatedAt",
                     "PhoneNumberVerified","TwoFactorEnabled")
SELECT gen_random_uuid(),'verifyclerk','verifyclerk@example.invalid',"PasswordHash",'Cashier',
       'Verify','Clerk',true,true,0,now(),false,false
FROM "Users" WHERE "Username"='admin';

-- and afterwards
DELETE FROM "Users" WHERE "Username" = 'verifyclerk';
```

## Gotchas

- **The Admin exemption is the thing most likely to be silently broken**, and a
  screenshot will not show it. The Web sends its `X-API-Key` and the user's token
  on the same request, and the key's identity carries `Admin` — so an exemption
  read off the merged principal exempts *everybody* and the freeze stops nobody.
  Prove it the way the script does: the same request, same key, same header, once
  with a cashier's token and once with an Admin's.

- **Aim writes at a path that answers 405.** `POST /api/product` is not a real
  route, so getting past the gate produces a 405 and never writes anything. The
  405 is the proof — it means routing was reached.

- **Do not use `/api/product` for the read check.** It goes out to SAP and a slow
  Service Layer will hang the run. `GET /api/notification` is answered from the
  API's own database.

- **A handset status call needs `X-App-Version`.** The version gate runs first
  and answers `400 Auth.InvalidAppVersionMetadata` to an Android caller that names
  no version, which looks like the maintenance endpoint failing and is not.

- **The settings endpoints are `AdminOnly`.** An API key alone gets 401; the call
  needs a user token beside it.

- The banner polls every 20 seconds, so give it a moment after switching the
  lockout rather than reading the page immediately.
