# CrawlForge fixture site

The acceptance target for CrawlForge (architecture `07-quality-and-verification/03-fixture-site.md`, change record CR-015). A small, dependency-free Node site with known **traps**. "Done" means a full CrawlForge audit, started from the UI or MCP, against this site produces the results in `expected-results.json`.

## Run

```
node server.mjs                 # http://127.0.0.1:47311
node server.mjs --port 48000    # another port
node server.mjs --tls-cert cert.pem --tls-key key.pem   # HTTPS, needed only for the mixed-content trap
```

Node 18 or later. Loopback only by default.

## Files

| File | Purpose |
|---|---|
| `server.mjs` | Routes, redirects, traps, websocket live feed, request recorder |
| `pages.mjs` | Every page, generated from one module |
| `expected-results.json` | Per trap: URLs, capabilities, acceptance criteria (#49 to #58), expected outcome, required capture mode |

## Recorder

- `GET /__fixture/requests` returns every non-GET request and every analytics tag hit since the last reset.
- `GET /__fixture/reset` clears it.
- **Safety assertion:** after any audit, `nonGet` must be empty. A POST to `/api/bet`, `/api/deposit` or `/api/logout` means the crawler pressed a control it must never press (click-discovery safety, architecture §5.6.4).

## Traps

31 traps: redirect chain and loop, noindex, robots-blocked, header-vs-HTML canonical conflict, broken internal and external links, sitemap-only orphan, JavaScript-only link, script-injected content, late meta change, soft 404 (server page and SPA route), hash routing, client-side redirect, ASP.NET postback links, cookieless session URL, large ViewState, faceted-filter and calendar crawler traps, cookie-dependent redirect, consent banner with a tag firing before consent, service worker, structured-data errors, missing alt text, mixed content (HTTPS only), hreflang without return tag, duplicate titles, thin page, and the betting-style SPA traps: a continuous websocket odds feed, click-only navigation, and "Place bet" / "Deposit" / "Logout" controls that must never be triggered.

## Known limits

- Mixed content is only testable when served over HTTPS (`--tls-cert`, `--tls-key`); over HTTP it is skipped.
- The broken external link uses `broken.example.invalid`, which never resolves by design.
- Expectations are written in capability terms; the acceptance harness maps them to CrawlForge rule keys.
