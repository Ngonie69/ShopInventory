# Password policy

The company policy, and how much of it the system enforces.

## The policy

1. Passwords must contain a minimum of 8 characters.
2. Passwords must not contain the user's name, username, or easily guessed personal information.
3. Common passwords such as `password` and `12345678` are prohibited.
4. Each account must use a unique password.
5. Passwords must remain confidential and must never be shared.
6. Passwords must be changed immediately if they are suspected to have been compromised.
7. Passwords must be renewed every 3 months.

## What enforces each rule

| Rule | Enforced by | Notes |
|------|-------------|-------|
| 1. Eight characters | `PasswordPolicy.Check` | Every account type, every route. |
| 2. Nothing personal | `PasswordPolicy.Check` | Username, first and last name, the mailbox part of the email address and the phone number, whole and in their parts (`t.moyo` is looked for as `tmoyo` and as `moyo`), with look-alike letters undone (`T3nd@i`). Pieces shorter than three letters are ignored. For a portal customer: the account code and name. For a shop on the ordering app: its number, code and name. |
| 3. Not common | `PasswordPolicy.Check` | The list in `CommonPasswords.cs`, plus the same words with digits or punctuation hung on either end (`Welcome2026`, `2026summer`) and with look-alike letters (`P@ssw0rd`). Also a repeated character or block (`aaaaaaaa`, `12121212`) and a straight run (`12345678`, `abcdefgh`). The company's own names are on the list. |
| 4. Unique | Partly | A staff account cannot reuse any of its last five passwords (`Security:PasswordPolicy:RememberedPasswords`), so a renewal cannot bring an old one back. Whether two *different* accounts share a password is deliberately not checked — see below. |
| 5. Confidential | Partly | A password typed by an administrator — a new account, or a reset — is known to two people, so its holder is made to replace it at the next sign-in (`RequireChangeAfterAdminSet`). The rest is conduct. |
| 6. Changed when compromised | **Require Password Change** on User Management | Signs the account out on every device at once and holds it to choosing a new password at the next sign-in. Any user can also change their own at any time, which signs out every other session. An administrator's reset now signs the account out too; it did not before. |
| 7. Renewed every 3 months | `UserPasswords.StandingOf` | Staff accounts. A password is good through the day three calendar months after it was set and lapses at midnight, Harare time, so it is met at the start of a day and never in the middle of a shift. |

Rules 1–3 are one source file, `ShopInventory/Common/Security/PasswordPolicy.cs`, compiled into the
API and into the Web (portal customer accounts never pass through the API). Everything that sets a
staff password goes through `ShopInventory/Features/Password/UserPasswords.cs`;
`StaffPasswordRoutesTests.No_other_code_puts_a_password_on_a_staff_account` fails the build if
anything else does.

### Why two accounts are not compared

Passwords are stored as salted BCrypt hashes, so the only way to learn whether a new password is
already in use elsewhere is to test it against every other account. Answering "that password is
taken" would tell the person typing it that it opens somebody else's account. The rule is met by
the three that are enforced — a password that is not common, not derived from its owner, and not
recycled is not one two people arrive at independently.

## Which accounts

| Accounts | Rules 1–3 | No reuse | Renewal |
|----------|-----------|----------|---------|
| Staff (`Users`): portal, tills, vans, merchandisers, POD | Yes | Last 5 | Every 3 months |
| Service accounts (role `ApiUser`, or named in `ExpiryExemptUsernames`) | Yes | Last 5 | Never — nothing can show a service a "choose a new one" screen |
| Portal customers | Yes, in addition to the portal's own character rules | Already kept by the portal | Already 90 days, by the portal |
| Shops on the ordering app | Yes | No | No — an operator sets these for the shopkeeper |
| SAP user accounts | No — SAP applies its own policy | | |

Self-service change and the emailed reset link also ask for an upper-case letter, a lower-case
letter, a digit and a symbol. They did before the policy and still do; the policy does not ask for
it, and the other routes do not.

## What a user sees

**Web portal.** From 14 days before the date, a bar across every page says how many days are left,
with a link to Security. Once a change is owed, sign-in lands on Security with the password form
open, and every other page leads back there until the password is changed. Security shows when the
password was last changed and the last day it is good for.

**Tills, the van app, the merchandiser app and the POD app.** Each sign-in reply carries
`passwordChangeRequired`, `passwordChangeReason`, `passwordExpiresAt`, `passwordExpiresInDays` and
`passwordExpiringSoon` (see API.md, `POST /api/Auth/login`). The van app signs in through
`/api/vansales/auth/login` and `/refresh`, whose replies carry the same five in the handset's
snake_case: `password_change_required`, `password_change_reason`, `password_expires_at`,
`password_expires_in_days` and `password_expiring_soon`.

### Enforcing it on the tills and handsets

`Security:PasswordPolicy:EnforceOnApiClients` (off by default) makes the API itself hold an account
that owes a change to the routes that change a password. Sign-in still succeeds — on those apps the
change-password screen is behind it — and every other request is answered **403**
`Password.ChangeRequired` with a sentence saying why.

It is off until every app in the field can meet that refusal. These are the first builds that
read `passwordChangeRequired` at sign-in and open their own change-password screen; an older build
shows the refusal's sentence but cannot always get the user out of it (the till's change never
worked before 1.1.5.21, and the POD app's wiped its queue of saved PODs before 1.1.5):

| App | Package | First build that handles it | Source |
|-----|---------|-----------------------------|--------|
| Till (KefShop) | MSIX, `.appinstaller` | 1.1.5.21 | Ngonie69/KefShop #112 |
| Van app | `com.kefalos.vansales` | 2.1.1 (42) | DBSDEVS/KefalosVanSales #161, released as #165 |
| Merchandiser app | `com.kefalocheese.so` | 1.0.10 (17) | Ngonie69/ShopInventory.Mobile #8, released as #9 |
| POD app | `com.cheeseman.app` | 1.1.5 (33) | DBSDEVS/Cheeseman #13, released as #15 |

The order:

1. Publish the three Android builds on Google Play and install 1.1.5.21 or later on every till.
2. In Web → **Settings**, raise each Android app's **Minimum supported version** to the build
   above. The three apps send `X-App-Id` / `X-App-Version`, so an older build is answered **426**
   and told to update; nobody is left on a build that cannot change its password.
3. On a test account in each app, check the refusal itself (below).
4. Turn `EnforceOnApiClients` on.

To check the refusal on a test account in each app: flag the account with **Require Password Change**, sign in, and confirm
that (a) the app shows the sentence rather than a blank screen or a generic error, (b) the app's own
change-password screen still opens and works, and (c) signing in again afterwards is normal. The
app-side fix, where one is needed, is to read `passwordChangeRequired` at sign-in and open the
change-password screen.

Nothing expires before **three months after the deploy**, because the migration stamps every
existing account with the deploy time. That is the date by which either the apps read the flag or
the setting is on; until then only accounts an administrator resets or flags are affected, and only
in the portal.

## Settings

`Security:PasswordPolicy` in the API's configuration:

| Setting | Default | Meaning |
|---------|---------|---------|
| `MaxAgeMonths` | 3 | Renewal period. 0 switches renewal off. |
| `ExpiryWarningDays` | 14 | How far ahead `passwordExpiringSoon` turns on. |
| `RememberedPasswords` | 5 | Passwords, the current one included, that cannot be chosen again. |
| `RequireChangeAfterAdminSet` | true | A password an administrator typed must be replaced by its holder. |
| `ExpiryExemptRoles` | `["ApiUser"]` | Roles that never expire and are never flagged. |
| `ExpiryExemptUsernames` | `[]` | Individual accounts likewise. |
| `EnforceOnApiClients` | false | Hold accounts that owe a change to the password routes, on every client. |

What a password must look like (rules 1–3) is not configurable.
