# Creating Projects and Scopes

A project is CrawlForge's container for everything you audit about one website: its crawl configurations, its audit history, its suppressions, and its exports. This guide walks through creating a project, defining what the crawler is allowed to touch (the crawl scope), tuning how URLs are normalized and filtered, and validating your start URLs before any work begins.

Everything described here runs entirely on your machine. CrawlForge is local-first: the backend listens on the loopback interface only, there are no accounts, licenses, or entitlements, and outbound traffic is limited to the crawl itself, destinations and database servers you explicitly configure, and the optional user-triggered DNS bot verification in the log analyzer (off by default, DNS queries only, explicit per-import consent). Defining a scope never contacts a vendor service.

> **Note:** Two different things are called "scope" in CrawlForge. The **crawl scope** (this guide) decides which URLs are fetched. The **content scope** (Include/Exclude Content Area selectors) decides which parts of a fetched page count toward text metrics. They are configured separately and do not affect each other — see [Content area scoping](#content-area-scoping-text-metrics-only) below.

## Projects

A project stores a name, a description, your tags/labels, created/updated timestamps, and a scope summary. All crawl data is project-scoped, and projects (with their audits) support soft delete with transactional pruning — deleting a project removes its data cleanly rather than leaving orphans behind.

### Create a project

1. Open the projects surface and choose **Create project**.
2. Enter a **name** (required), an optional **description**, and optional **tags** to group client work.
3. Save. The project is created locally and immediately available for configurations.

The same operations are available on the loopback REST API:

| Operation | Method & path | Input fields |
|---|---|---|
| List projects | `GET /api/projects` | Pagination, search text |
| Create project | `POST /api/projects` | Name, description, tags, scope summary |
| Read / update / delete | `GET`, `PUT`, `DELETE /api/projects/{id}` | Project fields on update |

> **Tip:** Use one project per website (or per client property). Suppressions and content-scope profiles are project-scoped and carry across re-audits of the same project, so a clean project boundary keeps report history comparable.

### Configurations: reusable input sets

A **configuration** is a reusable set of crawl inputs attached to a project — seeds, scope rules, depth, limit overrides, rendering mode, politeness options, proxy routing, custom extraction rules, and optional headers/cookies. You can keep several configurations per project (for example, "full site" and "blog only") and start audits from any of them.

| Operation | Method & path |
|---|---|
| List / create configurations | `GET`, `POST /api/projects/{id}/configurations` |
| Read / update / delete configuration | `GET`, `PUT`, `DELETE /api/configurations/{id}` |

Sensitive optional values in a configuration (such as site credentials in custom headers/cookies) are encrypted at rest. See [Proxies and authenticated crawls](proxies-and-authenticated-crawls.md) for the credential handling rules.

## Seed URLs

Seed URLs are the starting points of a crawl. They are the one required input besides the scope definition:

- Supply **one or more** starting URLs.
- The crawl scope is **derived from the seeds** unless you override it explicitly.
- Seeds are fetched first: the priority frontier orders work seeds-first, then a breadth-leaning order with depth as tiebreaker.

You can also supply **sitemap seeds** — additional sitemap URLs to expand at start. Sitemap entries are treated as *discovered* URLs and scheduled like any other discovery; sitemap indexes are expanded recursively.

> **Note:** A URL is fetched at most once per audit. A persistent seen-set keyed by canonical URL deduplicates seeds, discovered links, and sitemap entries alike.

### The complete audit-input reference

Every configuration is built from the same input set. Only seeds and the scope definition are required; everything else is optional:

| Input | Required | What it controls |
|---|---|---|
| Seed URL(s) | Yes | Starting URLs; the scope derives from them unless overridden |
| Scope definition | Yes | Host-only, include-subdomains, or whole-domain; optional path-prefix rules |
| Depth ceiling | No | Overrides the limits default for this audit |
| Inclusion patterns | No | URL patterns that must match for a URL to be crawled |
| Exclusion patterns | No | URL patterns that disqualify a URL (evaluated after inclusion) |
| Query-parameter policy | No | Keep all, keep a whitelist, or strip all query parameters during canonicalization |
| Rendering mode | No | Plain HTTP fetch, or JavaScript-rendered fetch |
| Limit overrides | No | Any subset of the crawl-limit parameters |
| Custom request headers / cookies | No | Optional credentials for the target site, stored locally |
| Sitemap seeds | No | Additional sitemap URLs to expand at start |
| Politeness options | No | Robots compliance toggle, per-host throttle, user-agent string |
| Proxy routing | No | Upstream proxy and/or the built-in local intercepting proxy |
| Custom extraction rules | No | User-defined content extraction rules applied during the crawl |
| Custom search items | No | User-defined content search items applied during the crawl |
| JavaScript snippets | No | User-defined in-page scripts for rendering mode |
| Sample-crawl mode | No | A depth/page-bounded sampling run for a quick preview |
| Virtual robots override | No | A user-supplied robots.txt body used in place of the live one for this audit |
| Signed-request access | No | Signature material applied to in-scope requests, with expiry tracking |
| Pre-crawl validation | No | Resolve and probe the start URL(s) before crawling |

## Crawl scope definitions

The scope definition decides which discovered URLs are in bounds. CrawlForge offers three scope modes plus optional path-prefix rules:

| Mode | What is in scope |
|---|---|
| Host-only | Exactly the seed host(s); other hosts are external |
| Include-subdomains | The seed host(s) plus their subdomains |
| Whole-domain | The whole registered domain |

- **Path-prefix rules** (optional) narrow the crawl to sections of a site, for example only URLs under a given path prefix.
- The per-domain page ceiling can count per **registered domain** or per **host** — your choice — independently of the scope mode.

Discovery classification is deterministic:

| Classification | What happens |
|---|---|
| In-scope | Enqueued for fetching |
| External | Recorded as an external link only — never fetched |
| Excluded | Recorded as `skipped` with the reason |

### Inclusion and exclusion patterns

Two optional pattern lists refine the scope:

| Field | Default | Bounds | Notes |
|---|---|---|---|
| Inclusion patterns | None | URL patterns | A URL must match to be crawled |
| Exclusion patterns | None | URL patterns | Disqualifies a URL; evaluated **after** inclusion |

Order matters: inclusion is evaluated first, then exclusion. A URL that matches an exclusion pattern is recorded as skipped with its reason — it is not silently dropped, so you can always see why a URL was left out.

> **Tip:** Start broad (host-only or include-subdomains) and add exclusion patterns for sections you never want crawled (search results, faceted navigation, admin paths). Run a [sample crawl](#sample-crawl-mode) to preview what the scope captures before committing to a full run.

## URL canonicalization and the query-parameter policy

Before a URL enters the frontier it is canonicalized. This is what makes deduplication and scope matching reliable:

- Scheme and host are lowercased; default ports are removed; fragments are discarded.
- A consistent trailing-slash policy is applied; percent-encoding is normalized.
- Query parameters are sorted, filtered by your configured policy, and known session-tracking parameters are stripped.
- Relative references are resolved against the document base, honoring any declared base override.

The **query-parameter policy** controls how query strings survive canonicalization:

| Policy | Effect |
|---|---|
| Keep all | Every query parameter is retained (after sorting and session-parameter stripping) |
| Keep a whitelist | Only the parameters you list are retained |
| Strip all | All query parameters are removed |

> **Warning:** "Keep all" on a site with tracking or session parameters can explode the URL count with near-duplicate pages. Prefer a whitelist of the parameters that genuinely change content (for example, a product or pagination parameter), or strip all for static sites.

On-the-fly **rewrite rules** (ordered regex find/replace applied during canonicalization) and **host mapping** are migration-oriented extensions of this stage — they are covered in [List Mode and the migration matrix](list-mode-and-migration-matrix.md).

## Link discovery and fetch behavior

Understanding what the crawler does with each fetched page helps you predict scope outcomes:

- From HTML: anchor and image-map references, document-level link relations (canonical, alternates, stylesheets, next/previous), and optionally embedded frame sources and meta-refresh targets.
- Relative references resolve against the document base, honoring any declared base override.
- Each discovered target is canonicalized, then classified against the scope as in-scope (enqueued), external (recorded only), or excluded (recorded as skipped with the reason).

Fetching itself is conservative and fully recorded:

- A configurable HTTP client honoring the configured proxy routing; a configurable user-agent; per-request timeout, retry budget, redirect-hop ceiling, and response-size ceiling.
- Redirects are followed within the hop ceiling; the full hop chain and final URL are recorded, including loop detection.
- Transient failures (connection reset, 429, 5xx) are retried within budget using exponential backoff.
- Permanent failures are classified and stored with the URL record: name resolution, TLS, timeout, HTTP status, oversize, robots-blocked, proxy failure.
- Response bodies stream into the compressed content store without full in-memory buffering.

> **Tip:** Because external links are recorded but never fetched, your link report still shows outbound links to other domains even though the crawl stayed in scope. Excluded URLs appear as `skipped` with their reason — use that record to tune patterns instead of guessing.

## Pre-crawl start-URL validation

Pre-crawl validation resolves and probes your start URL(s) **before** the crawl begins, so blockers surface while you can still fix the configuration cheaply. It is an optional audit input; when enabled, the probe checks:

| Check | What a failure looks like |
|---|---|
| Protocol | The start URL does not resolve to a usable HTTP(S) endpoint |
| Redirect | The start URL redirects somewhere unexpected (the probe follows and records the chain) |
| TLS | Certificate or handshake problems on the start URL |
| Login-gate detection | The start URL lands on an authentication gate instead of content |
| WAF challenge | The seed probe runs the WAF & Challenge Fingerprinting Pass; a challenge page on a seed URL surfaces as a blocker before the crawl starts |

Blockers are surfaced before work begins — you see them at configuration time, not after thousands of wasted fetches.

> **Note:** In List Mode, pre-crawl validation probes a **bounded sample** of the list entries rather than every row. WAF verdicts apply to the sample exactly as they do to seeds.

If a seed is blocked by a WAF challenge, the remediation is to verify crawler access permissions with the site owner or adjust the scope or supplied credentials — CrawlForge detects and reports challenges; it never bypasses them. See [Proxies and authenticated crawls](proxies-and-authenticated-crawls.md).

### Verify a single URL without a project

For a quick check outside any project, the standalone tools run the same machinery:

| Tool | Method & path | What you get |
|---|---|---|
| URL check | `POST /api/tools/url-check` | Status, redirect chain, headers, indexability (plus the WAF verdict) |
| Robots test | `POST /api/tools/robots-test` | Allow/disallow verdict and matched rule per test URL, for a robots body or URL and user-agent |
| Single-page analysis | `POST /api/analysis/single-page` | Full findings and element inventory for one page — no project or crawl required |

## The project settings workflow

Settings resolve through a single local merge, in priority order:

> **Per-audit override → project default → application default → built-in unlimited profile.**

The built-in profile is the maximum configuration: **every ceiling defaults to Unlimited**. There is no license, entitlement, tier, or account input anywhere in this resolution — nothing remote is consulted, ever.

### Recommended workflow

1. **Create the project** and give it a meaningful name and tags.
2. **Set project defaults** — the limits and toggles you want every audit of this site to inherit (application-wide defaults live in settings, `GET`/`PUT /api/settings`).
3. **Create a configuration**: add seed URLs, choose the scope mode, add path-prefix rules and inclusion/exclusion patterns, and pick the query-parameter policy.
4. **Choose politeness options**: robots compliance toggle, per-host throttle, and the user-agent string. Robots rules are fetched, cached per host, and obeyed; a robots `crawl-delay` directive is an inviolable floor on request spacing.
5. **Optionally enable pre-crawl validation** and run it. Fix any protocol, redirect, TLS, login-gate, or WAF blocker before continuing.
6. **Optionally run a sample crawl** (see below) to preview scope and block rate.
7. **Start the audit** (`POST /api/audits` with the configuration reference and any per-audit limit overrides). The call returns immediately with an audit identifier; progress flows over the real-time hub.
8. **Review and refine**: adjust patterns or overrides and start a new audit. A running audit keeps the limit snapshot it started with — changes apply to subsequent audits, never mid-run.

### Limit overrides

Any subset of the crawl limits can be overridden per audit:

| Parameter | Built-in default | Bounds |
|---|---|---|
| Total page ceiling | Unlimited | Positive integer, or unlimited |
| Per-domain page ceiling | Unlimited | Positive integer, or unlimited (per registered domain or per host, selectable) |
| URL throughput ceiling | Unlimited (politeness still applies) | Positive number when set |
| Worker concurrency | Scales with the machine's logical processor count | Positive integer within a configurable safety cap |
| Crawl depth ceiling | Unlimited | Non-negative integer |
| Request timeout | A generous fixed default (order of tens of seconds) | Positive duration |
| Retry budget | Small fixed default | Non-negative integer |
| Redirect hop ceiling | Small fixed default | Positive integer |
| Response size ceiling | Generous fixed default | Positive size |
| Audit duration ceiling | Unlimited | Positive duration |

*Unlimited* is a distinguished sentinel (absence of a value), never a magic large number. Effective values are validated before an audit starts; invalid overrides are rejected with a descriptive local error.

> **Note:** Depth ceiling counts link hops from any seed URL. List Mode's `list-only` discovery mode forces depth to 0 regardless of this setting — see [List Mode and the migration matrix](list-mode-and-migration-matrix.md).

### Sample-crawl mode

Sample-crawl mode is a depth/page-bounded sampling run for a quick preview. It uses the **same pipeline and the same limits resolution** as a full audit — it is not a separate, lighter engine. The WAF fingerprinting pass is active in the sample: a high block rate in the sample warns you before you commit to a full run.

### Virtual robots override

You can supply a **virtual robots override**: a user-supplied robots.txt body used in place of the live one for this audit. This is useful for testing how a proposed robots change would affect crawling, or for auditing a site whose live robots file blocks everything. The override applies to the audit only; it never changes the live site.

### Audit states you will see

An audit moves through a fixed state machine:

> `configuring → running → (paused ⇄ running) → completed | cancelled | failed | interrupted`

- The crawl and analysis phases are visible in progress events.
- Cancellation retains already-fetched data.
- Failures carry diagnostic error records with remediation hints.
- An interrupted audit (for example after a crash) is marked as such on restart and can resume from its checkpoint.

### Troubleshooting scope and start-URL problems

| Symptom | What it means | What to do |
|---|---|---|
| Start-URL blocker: protocol | The seed does not resolve to a usable HTTP(S) endpoint | Fix the seed URL scheme/host before crawling |
| Start-URL blocker: redirect | The seed redirects somewhere unexpected | Inspect the recorded chain; point the seed at the intended entry |
| Start-URL blocker: TLS | Certificate or handshake problems | Resolve the certificate issue, or route through your configured proxy if egress requires it |
| Start-URL blocker: login gate | The seed lands on authentication instead of content | Supply site credentials or signed access (see [Proxies and authenticated crawls](proxies-and-authenticated-crawls.md)) |
| Start-URL blocker: WAF challenge | The seed probe fingerprinted a challenge page | Verify crawler access with the site owner; adjust scope or credentials — never bypass |
| URLs recorded as `skipped` | An exclusion pattern applied (in `list-bounded` mode, off-list discoveries are skipped with reason `out-of-list`) | Read the recorded reason; tune inclusion/exclusion patterns. Out-of-scope URLs are not skipped — they are recorded as external links only |
| Far fewer URLs than expected | Query policy or deduplication collapsed variants | Review the query-parameter policy and canonicalization effects |
| Invalid limit override rejected | A per-audit override failed validation | Read the descriptive local error; correct the value (positive integer/duration, or unlimited) |

## Content area scoping (text metrics only)

The **content scope** is a separate, text-metrics-only configuration: an ordered Include list and an ordered Exclude list of selectors, each entry declared as CSS or XPath (both may be mixed in one scope). It produces the Scoped Visible Text Tree used by word counts, readability, reading time, text-to-HTML ratio, spelling, near-duplicate detection, custom content search (visible-text target), and n-gram body/headings zones.

Key semantics:

1. Include list empty → the whole document body is the candidate region.
2. Include list non-empty → the union of matched subtrees, in document order.
3. Exclude selectors apply **after** inclusion; **exclude wins** on any conflict.
4. Nested matches are deduplicated — text is never counted twice.
5. The result preserves document order.

Scoping never changes document-head facts (title, meta, canonical), the heading outline, link discovery, anchors, images, structured data, resources, or tables. Nothing is stripped automatically: applying the bundled boilerplate preset (common selectors such as `nav`, `header`, `footer`, `aside`, `.cookie-banner`, `[role=navigation]`) is an explicit user action.

Configuration lives at two levels, merged through the same priority order: an audit-level scope overrides the project-level default; no configured scope anywhere means full visible text (baseline behavior). Reusable project-level scopes are stored as named profiles.

Preview before you crawl with `POST /api/tools/scope-preview`: supply a target URL (or a stored artifact reference) and the include/exclude lists; you get a matched-region summary, word counts before/after, sample scoped text, and per-selector match counts. Selector syntax is validated at configuration time; invalid selectors are rejected with a descriptive local error.

What scoping deliberately does **not** touch:

- Document-head facts (title, meta, canonical), the heading outline, link discovery, anchors, images, structured data, resources, tables.
- SERP pixel measurement and the accessibility pass consume their own inputs unchanged.
- Shingle near-duplicate detection operates on the scoped text when a scope is configured.

A scoped **re-audit** recomputes text findings from stored bodies under the new scope without re-fetching (refresh remains explicit). Text-based findings note the scoped basis in evidence, so judgments are reproducible — you can always tell which basis produced a number.

> **Tip:** The audit header shows the active scope state (for example, "Scoped: 2 include / 7 exclude") so reports are never silently misread. Text-based finding evidence notes the scoped basis.

## Related guides

- [List Mode and the migration matrix](list-mode-and-migration-matrix.md)
- [Proxies and authenticated crawls](proxies-and-authenticated-crawls.md)
- [Crawl limits and AIMD politeness](../03-running-and-scheduling-audits/crawl-limits-and-aimd-politeness.md)
- [Lifecycle and checkpointing](../03-running-and-scheduling-audits/lifecycle-and-checkpointing.md)
- [Content scoping and boilerplate stripping](../05-advanced-seo-and-intelligence-modules/content-scoping-and-boilerplate-stripping.md)

[← Documentation portal](../README.md)
