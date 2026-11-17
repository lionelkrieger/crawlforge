# Fixture Site and End-to-End Acceptance

> **CrawlForge architecture v2.0 — area of concern: `07-quality-and-verification`.**
> Added by change record CR-015 (decision D-007). Not derived from the baseline documents.

---

### 1. Fixture site
> Decisions: D-007, D-016 · Change records: CR-015

- A self-contained local website at `app/tests/fixture-site/`, served on loopback during tests and versioned with the product. Each page is a named **trap** with a documented expected outcome (findings, URL states, links).
- A machine-readable expected-results file in **JSON** lists, per trap, what a full audit must produce.
- Minimum trap set: redirect chain; redirect loop; noindexed page; robots-blocked URL; canonical conflict; broken internal and external links; orphan page (sitemap only); JavaScript-only link; content injected only by script; late title, canonical or robots change by script; soft 404 on a server page and on a client-side route; hash route; client-side redirect; postback link; cookieless-session URL; large ViewState; crawler trap (parameter combinations and calendar); cookie-dependent redirect; consent banner with a tag firing before consent; service worker; structured data with errors; missing alt text; mixed content; hreflang without return tag; duplicate titles; thin page.
- **SPA traps modelled on live betting sites:** a page with a continuous websocket data feed (live odds); navigation reachable only by clicking; and a "Place bet" style control backed by a POST endpoint that the crawler **must never trigger**. The fixture server records any non-GET request; any such request during click discovery fails acceptance.

### 2. Acceptance rule
> Decisions: D-007 · Change records: CR-015

- A criterion from #49 onward passes only when a full audit, started through the product's normal entry points (UI or MCP), against the fixture site, produces the expected results for the traps the criterion names. Unit tests alone never satisfy a criterion.
- Criteria #1 to #48 are re-verified against the fixture site in a **re-baselining pass**, applying the same rule.
