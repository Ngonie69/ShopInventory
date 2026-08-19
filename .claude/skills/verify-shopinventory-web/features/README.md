# Feature map: ShopInventory.Web

What a user can actually do in this app, one file per feature. Each file says
how to reach the feature as a user, how to drive it with `scripts/cdp.py`, and
what observable end state proves it works.

This is the maintained source of truth for verification scope. **A proof that
drives one convenient route is incomplete when this map lists others touched by
the same change.** If you change transfer approval, the transfers file tells you
approval and the request flow are both in scope.

| Feature | Route(s) | File |
|---|---|---|
| Sign in | `/login` | [sign-in.md](sign-in.md) |
| Inventory transfers | `/inventory-transfers`, `/inventory-transfer/create`, `/transfer-request/create` | [inventory-transfers.md](inventory-transfers.md) |
| Credit notes | `/credit-notes`, `/credit-notes/create` | [credit-notes.md](credit-notes.md) |
| Reports and Excel export | `/reports`, `/reports/*` | [reports-and-export.md](reports-and-export.md) |
| Customer portal | `/customer-portal/*` | [customer-portal.md](customer-portal.md) |

Seeded from the five surfaces with the heaviest change traffic. The app has 112
routes; add a file when you verify a feature this map does not cover yet.
