# Google Integrations (Search Console, GA4, PageSpeed Insights)

> **CrawlForge architecture v2.0 — area of concern: `08-integrations`.**
> New domain added by change record CR-013 (decisions D-005, D-012, D-009). Not derived from the baseline documents.

| Source | Version | Sections |
|---|---|---|
| CR-013 (`changes/CR-013.md`) | 2026-11-17 | complete file |

**Notation.** A bare `§n` refers to sections of the architecture hierarchy inherited from FS-CRAWL-001; network rules are in `01-foundation-and-governance/03-network-boundary-and-security.md` §2.6 (amended by CR-003).

---

### 1. Connections
> Capabilities: CAP-H-181, CAP-H-182, CAP-H-183 · Decisions: D-005, D-012, D-009 · Change records: CR-013

- Integrations are **off by default**. Each integration is enabled per project by an explicit toggle.
- The user connects Google Search Console and Google Analytics 4 through an OAuth flow using **their own OAuth client** (client ID and secret from their own Google Cloud project), and PageSpeed Insights with **their own API key**.
- The OAuth client sits behind a provider abstraction so a shipped, Google-verified client can be added later without changing integration contracts.
- Credentials and tokens are stored encrypted at rest (§9.1) and are sent only to the respective Google API.

### 2. Property binding
> Capabilities: CAP-H-181, CAP-H-182 · Decisions: D-005 · Change records: CR-013

- Each project binds at most one Search Console property and one GA4 property. Binding is explicit; nothing is inferred from crawled sites.

### 3. Data retrieved and toggles
> Capabilities: CAP-H-181, CAP-H-182, CAP-H-183, CAP-C-081, CAP-C-083 · Decisions: D-005, D-012 · Change records: CR-013

Every data type below has its own per-project toggle; disabled types are never requested.

| Integration | Data type | Default when the integration is enabled |
|---|---|---|
| Search Console | Page performance (clicks, impressions, CTR, position by page) | On |
| Search Console | Query performance by page | On |
| Search Console | URL Inspection, **only for URLs the user selects** (Google daily quota per property) | On, user-selected URLs only |
| GA4 | Sessions, engaged sessions and key events by landing page | On |
| GA4 | Comparison with the previous period | On |
| PageSpeed Insights | Lighthouse lab results and real-user field data (including INP) for **user-selected URLs plus a sample of one URL per URL pattern** | On |

- Default date range: **last 28 days**, compared with the previous 28 days; configurable per project.

### 4. Sync
> Capabilities: CAP-H-181, CAP-H-182, CAP-H-183 · Decisions: D-005, D-012 · Change records: CR-013

- Data is retrieved only when the user starts a sync or a schedule the user created triggers one (§2.6).
- Synced data is stored locally per project and date range, so audits and reports work offline. Synced data is included in project archive export.
- Sync failures are reported locally and never fail an audit.

### 5. Uses in audits
> Capabilities: CAP-H-181, CAP-H-182, CAP-H-183 · Decisions: D-005, D-012 · Change records: CR-013

- Search Console and GA4 page lists join crawl results by normalised URL to identify: pages with impressions or sessions that the crawl did not find (orphan candidates); indexed-but-not-crawlable and crawlable-but-not-indexed pages; and traffic-weighted prioritisation of findings.
- PageSpeed Insights results appear alongside the local lab probe (§7.11), labelled by source.

### 6. Offline fallback
> Capabilities: CAP-A-X08 · Decisions: D-005 · Change records: CR-013

- Without a connection, the user may import Search Console and GA4 page exports as CSV, with the same join behaviour as synced data.

### 7. Contracts
> Capabilities: CAP-H-181, CAP-H-182, CAP-H-183 · Decisions: D-005, D-012 · Change records: CR-013

- REST, hub and MCP additions for connection status, toggles, sync start, sync progress and synced datasets follow the conventions of §10 and §11.
