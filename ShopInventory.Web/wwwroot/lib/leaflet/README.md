# Leaflet 1.9.4 — vendored

Served from here rather than a CDN because `SecurityMiddleware` sets
`script-src 'self' 'unsafe-inline' 'unsafe-eval'` with no CDN host in it. Loaded
from unpkg the script is blocked outright and `/van-sales/activity` renders an
empty grey panel where the round map should be — the same failure Phosphor hits
in `style-src`, and the reason App.razor pins that to jsDelivr.

Files are the untouched `leaflet@1.9.4/dist` contents. Both text files were
checked against the subresource-integrity hashes published for that release:

    leaflet.js   sha384-cxOPjt7s7Iz04uaHJceBmS+qpjv2JkIHNVcuOrM+YHwZOmJGBXI00mdUXEq65HTH
    leaflet.css  sha384-sHL9NAb7lN7rfvG5lfHpm643Xkcjzp4jFvuavGOndn6pjVqS6ny56CAt3nsEVT4H

To re-verify, or to check a replacement before committing it:

```bash
printf 'sha384-%s\n' "$(openssl dgst -sha384 -binary leaflet.js | openssl base64 -A)"
```

`images/` is needed even though the map draws its pins as `divIcon`s: leaflet.css
references `layers.png` and `marker-icon.png` by relative path, and without them
the zoom control and any future default marker 404.

Tiles come from `tile.openstreetmap.org` over `img-src ... https:`, so they need
no CSP change — but they are fetched by each viewer's browser, which tells
OpenStreetMap that someone is looking at that part of Harare. The pins
themselves are drawn locally and never leave the page.

Licence: BSD-2-Clause, retained in the header of each file.
