# Sign in

The gate for every other feature. Staff sign in at `/login` against the API on
5106; the Web app holds no password of its own.

## Sub-features

- Username or email plus password
- Password reveal toggle (`.nsi-eye`)
- Two-factor challenge (TOTP) when the account has it enabled
- Passkey button (`.nsi-passkey`), a separate path not covered here

## How to get to it (user POV)

Open the app at `http://localhost:5051`. Anything requiring auth redirects to
`/login`. Enter credentials, press Sign in, land on `/dashboard`.

## Driving it with cdp.py

```python
from cdp import Chrome
with Chrome(headless=True) as c:
    c.goto("http://localhost:5051/login")
    c.wait_for("#username")
    c.type_into("#username", "admin")
    c.type_into("#password", "admin123")
    c.click("button[type=submit].nsi-submit")
```

`verify.py` wraps this and polls `location.pathname` until it stops ending in
`/login`. **Proof it worked:** the path changed and `00-after-login.png` shows
the shell with navigation, not the login card.

## Gotchas

- `type_into` must dispatch `input` and `change`. Assigning `.value` leaves the
  Blazor model empty and submit does nothing, so the page just sits there.
- The local database has exactly one user, `admin`, with `TwoFactorEnabled` on.
  See SKILL.md for the local-only toggle and its restore.
- A `Login hit the two-factor step` error means the password was **correct**.
  The 2FA branch runs only after the password check passes
  (`Services/AuthService.cs:195`).
- The API must be up first. With it down, login fails on a connection error that
  looks like bad credentials.
