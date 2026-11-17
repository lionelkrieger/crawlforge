# Functional Specification — Standalone Desktop Website Crawler & Auditor

| Document control | Value |
|---|---|
| Identifier | FS-CRAWL-001 |
| Version | 1.4 |
| Status | Issued for clean-room implementation — full capability-parity audit applied |
| Audience | Clean-room development team (no access to reference artifacts) |
| Classification | Abstract functional specification — contains no code and no proprietary expression |
| Derivation | Abstracted from dirty-room architectural analyses of a reference desktop application |

| Revision history | Change summary |
|---|---|
| 1.0 | Initial clean-room specification |
| 1.1 | Gap-analysis amendments: scheduled audits, content store, spelling checks, user-configured export destinations, local automation interface, persistence patterns, localization |
| 1.2 | QA round-2 amendments: configurable database providers, runtime performance profile, local metrics endpoint, local crash artifacts, local proxy routing, MCP mandate with agent transports, tool/resource definitions, and delegated control flows |
| 1.3 | Parity amendment: user-defined Custom Content Extraction — rules, typed values, mandatory bounds, dynamic-column explorer, export, and MCP resource |
| 1.4 | Capability-parity audit: accessibility auditing; rendered performance and Core Web Vitals with budgets and code coverage; readability and content metrics; soft-404 detection; technology detection; custom content search; custom JavaScript snippets; response-vs-rendered comparison; audit scoring; finding suppression; standalone diagnostic tools and sitemap generator; interactive query builder; signed-request crawler access; sample-crawl mode; virtual robots; console capture; infinite-scroll rendering; pre-crawl start-URL validation. Scope-sensitive items recorded as open decisions |

---

## 1. Purpose and Clean-Room Protocol

### 1.1 Purpose

This document defines the complete functional behavior of a standalone, non-commercial desktop application that crawls websites, audits their technical and content quality, and presents interactive reports locally. A separate implementation team shall be able to build the product from this document alone, without ever consulting the reference application.

### 1.2 Chinese Wall rules

- This document conveys **abstract behavior only**: business rules, workflows, data shapes, mathematical principles, and architectural constraints.
- It contains no source code, no identifier names taken from the reference application, no proprietary algorithms, and no literal strings, endpoints, ports, or configuration values copied from the reference artifacts. Standard, publicly documented platform and protocol names (runtime settings, open standards) are used as technology vocabulary only.
- All names, routes, schemas, taxonomies, and defaults in this document are **new design artifacts** created for the target product.
- The implementation team shall not read, decompile, or otherwise consult the reference application, its binaries, or the dirty-room analysis documents. Clarifications are routed through the architect as abstract behavioral questions only.
- The target product is an independent work. Its similarity to any existing product is limited to generic, industry-standard crawler and auditor functionality.

### 1.3 Product principles

- Personal desktop use; single user; no accounts, no sign-in, no subscription or entitlement concepts of any kind.
- Fully functional with no network connectivity, except for the act of crawling target websites itself, optional user-configured export destinations (section 8.5), and an optional user-configured external database server (section 2.4) — all off by default.
- Every feature is enabled by default; no remote configuration channel exists.
- All application data remains on the local machine by default. Data leaves the machine only toward services the user has explicitly configured themselves (export destinations, an external database server), via transfers the user initiates or pre-authorizes on a schedule.

---

## 2. Architectural Foundation

### 2.1 Process model

The product consists of two cooperating processes with a strict separation of concerns:

| Process | Technology class | Responsibility |
|---|---|---|
| Desktop shell | Chromium-embedded window host (Electron-class) | Window lifecycle, native OS integration, loading the local UI bundle, spawning and supervising the backend. Contains **no domain logic**. |
| Local backend | Self-contained local HTTP service (ASP.NET Core-class) | All domain logic: crawl engine, audit engine, persistence, reporting aggregation, real-time event hub, local MCP server, metrics. Ships with its own runtime; no external prerequisites. |

- The shell spawns the backend as a child process, monitors its health, and restarts it on failure.
- The UI is a local web application loaded from the packaged bundle; it never loads remote content.

### 2.2 Communication channels

- **HTTP request/response** (JSON) for all commands and queries, including the local metrics endpoint (section 10.5).
- **A persistent WebSocket channel** (SignalR-class hub protocol) for server-push events: crawl progress, stage transitions, completion, errors.
- **Local agent transports (optional):** the local automation interface (section 11) additionally speaks the standard agent transport mechanisms — **standard input/output (stdio)** for locally spawned clients, and **Server-Sent Events (SSE) / streamable HTTP over loopback** for resident local clients. Both are strictly local; no remote or cloud bridging of any kind exists.
- The backend binds to the **loopback interface only**; it is never reachable from other machines.
- **Port strategy:** a configured default loopback port is attempted first; if occupied, the backend falls back to an operating-system-assigned ephemeral port. The shell discovers the actual port through a local handshake (a port descriptor written to the per-user application data directory, or an equivalent spawn-time handshake). This specification mandates no literal port number.
- **Optional local hardening:** a per-launch random session token may be generated at spawn and shared between shell and backend. It is explicitly **not** derived from hardware, OS stores, or any persistent machine identity, and it never leaves the machine.

### 2.3 Startup, shutdown, and recovery

**Startup sequence:**

1. Shell launches and displays a splash window.
2. Shell spawns the backend process under the mandated runtime performance profile (section 13).
3. Backend applies schema migrations and first-run seeding (section 9.4) against the configured database provider, then opens the API and hub.
4. Backend signals readiness; shell confirms via a health probe.
5. Shell loads the main window; the UI connects to the hub and hydrates from the API.

**Shutdown sequence:** the shell requests a graceful stop; the backend checkpoints any running crawl, drains workers and background services, closes the database, and exits; the shell then exits.

**Crash recovery:** if the backend exits unexpectedly, a local native diagnostic minidump is captured (section 10.5) and the shell restarts the backend with bounded backoff. On restart, the backend marks any audit that was running as *interrupted* and offers resume from its checkpoint. After repeated failures, the shell shows a local recovery screen with diagnostic information, a pointer to the local crash artifacts, and a retry action.

### 2.4 Persistence foundation

- **Configurable database providers.** Persistence is abstracted behind a provider contract; the application is not restricted to a single storage engine. Two provider classes are supported:
  - **Default — local file-based embedded database (SQLite-class):** resides in the per-user application data directory; zero configuration, zero network, fully offline.
  - **Optional — external client-server relational database (MySQL-class):** for high-volume deployments. Addressed by a user-configured connection (host, port, database name, credentials). The server is the **user's own infrastructure**; credentials are encrypted at rest (section 9.1). No vendor-hosted database exists or is contacted.
- The **logical schema, read models, and migration ledger are provider-agnostic**; each provider supplies a dialect-specific migration path to the same logical schema version. Provider selection and connection settings live in application settings. Switching providers is an explicit, user-initiated operation using the archive export/import mechanism — never automatic.
- **Durability and integrity:** the file-based provider uses write-ahead logging; the external provider uses its native transactional durability. Foreign keys are enforced; all multi-row writes occur inside transactions on either provider.
- Schema evolution is handled by **versioned migrations** applied automatically at backend startup, before the API begins serving, on the configured provider.
- First-run initialization is deterministic and requires **zero network calls** on the default provider (section 9.4); an external provider is never contacted unless the user has configured it.
- Optional per-project archive export/import produces a self-contained local bundle and doubles as the provider-migration vehicle.
- **Command/query separation:** all crawl and audit ingestion flows through a transactional write path; every report, dashboard, and list read is served from precomputed, indexed read models. Read queries must never require full scans over raw crawl data at the target scale (section 13).
- **Required database capabilities** (implementation-agnostic; the configured provider must supply them natively or through loadable extensions): regular-expression predicates in queries; in-database delimited-text import/export; vector-similarity search over stored embeddings; extended mathematical and statistical functions. For the embedded provider, loading native extensions per platform is part of the self-contained packaging requirement (section 13).
- Response bodies and rendered artifacts persist in a dedicated **content store** (section 9.6) on the local filesystem beside the database, independent of the chosen provider, enabling re-audit without re-fetching and cross-audit content comparison.

### 2.5 Feature availability

- **No remote feature-flag resolution exists.** Every feature is enabled by default, locally.
- Optional local-only toggles (user preferences) may disable or hide functionality; their values are stored locally and are never fetched from any service.

### 2.6 Network boundary

Outbound traffic is limited to:

- HTTP(S) requests to crawl targets, including their robots files, sitemaps, and page resources — optionally routed through a user-configured proxy (section 5.9).
- File writes for user-initiated exports (local filesystem).
- Transfers to **export destinations explicitly configured by the user** (section 8.5), occurring only when the user initiates them or pre-authorizes them on a schedule. No destinations exist by default.
- Connections to a **user-configured external database server** (section 2.4), only when that provider is selected. None is configured by default.

There is no telemetry, no remote crash reporting, no update checking, no account service, and no vendor-operated cloud service of any kind. User-configured destinations and database servers are the user's own services, not vendor services. Diagnostic artifacts (logs, minidumps) and metrics never leave the machine (section 10.5).

```
┌──────────────────────────────────────────────────────────┐
│ Desktop shell (window host)                              │
│  splash · main hub · report · visualization · analyzer   │
│  settings · recovery                                     │
└───────────────┬──────────────────────────────────────────┘
                │  HTTP (JSON)  +  WebSocket hub (events)
                │  loopback only
┌───────────────▼──────────────────────────────────────────┐
│ Local backend service                                    │
│  crawl engine (proxy routing) · audit engine             │
│  reporting aggregation · scheduler · export delivery     │
│  content store · local MCP server · metrics · hub        │
└───────┬──────────────────────────────────┬───────────────┘
        │ transactions · migrations        │ stdio / loopback SSE
        │                                  │ (optional, off by default)
┌───────▼───────────────────────────┐  ┌───▼───────────────────────┐
│ Configured database               │  │ Local AI / automation     │
│ default: local file-based         │  │ clients (user-owned)      │
│ optional: external client-server  │  └───────────────────────────┘
│ (user-configured)                 │
└───────────────────────────────────┘
```

### 2.7 Background services

The backend hosts long-lived local background services alongside the API:

- The **audit scheduler** (section 6), retention pruning and integrity maintenance (section 9.5), and export-delivery retries (section 8.5).
- Services start only after migrations and seeding complete, and before the readiness signal is emitted.
- On graceful shutdown every service is drained before the database closes; on crash recovery each service re-derives its state from persisted data — no scheduling state exists only in memory.
- A failing background service is logged locally and restarted with bounded backoff; it never takes down the API or an in-flight audit.

---

## 3. Excluded Subsystems (Negative Requirements)

The following capabilities are **out of scope and shall not be implemented**. If any requirement elsewhere in this document appears to conflict with this section, this section prevails.

| # | Excluded capability | Prohibited behavior in the target product |
|---|---|---|
| 1 | Machine identity and OS fingerprinting | No reading of OS registration stores, management instrumentation services, platform I/O registries, hardware serials, or any OS-level machine identifier. No persistent hardware-derived identity of any kind is generated, stored, or transmitted. |
| 2 | Licensing client and state model | No license, subscription, trial, or seat concepts; no entitlement state machine; no license cache files; no validation, activation, deactivation, expiry, grace periods, or periodic recheck services; no identity-bearing authentication headers toward any vendor service (user-configured destinations use the user's own credentials, section 8.5). |
| 3 | Account system | No sign-in, registration, password reset, or user profile. |
| 4 | Remote control plane and relay | No installation enrollment, no cloud relay bridging local tool servers to remote clients, no heartbeat or liveness reporting, no remote credential issuance or rotation, no remote revocation channel. This exclusion targets **remote bridging only**: a loopback-only local automation interface implementing a local MCP server with locally generated credentials (section 11) is explicitly permitted. |
| 5 | Remote feature flags | No remote flag resolution or proxying. All features are enabled locally by default. |
| 6 | Telemetry and remote diagnostics | No usage analytics, no remote crash reporting, no identity-dimension log enrichment, and **no automatic transmission of any diagnostic data**. Explicitly permitted: local structured logs and locally generated **native diagnostic minidumps** for user troubleshooting. These artifacts are produced and stored only on the local filesystem, are never transmitted automatically, and are user-viewable and purgeable (sections 10.5, 12). |
| 7 | Server-gated functionality | No report-viewing authorization checks, no server-provided audit configuration, no vendor cloud audit logging or alerting, no account-bound update feeds, no marketing or newsletter hooks. These exclusions target vendor-operated services; destinations and notification endpoints the user configures themselves (sections 6.5, 8.5) are not vendor services and are permitted. |
| 8 | Entitlement-based limits | No crawl limit, feature, or report may vary with license type, account state, or any remote input. |

---

## 4. Crawler Limits Subsystem

### 4.1 Resolver contract

- A single **limits resolver** produces the effective crawl configuration for every audit.
- The resolver accepts **no license, entitlement, tier, or account input**; no such concept exists anywhere in the product, so there is no license-type check to perform or bypass.
- Resolution is a pure local merge, in priority order: **per-audit override → project default → application default → built-in unlimited profile**.
- The built-in profile is the maximum configuration: **every ceiling defaults to Unlimited**.

### 4.2 Configuration parameters

| Parameter | Semantics | Built-in default | Override rules |
|---|---|---|---|
| Total page ceiling | Maximum URLs fetched in one audit | Unlimited | Positive integer, or unlimited |
| Per-domain page ceiling | Maximum URLs fetched per registered domain (or per host, selectable) | Unlimited | Positive integer, or unlimited |
| URL throughput ceiling | Maximum URLs started per second, globally or per host | Unlimited (politeness rules still apply) | Positive number when set |
| Worker concurrency | Number of simultaneous fetch workers | Scales with the machine's logical processor count; exact factor is an implementation choice | Positive integer within a configurable safety cap |
| Crawl depth ceiling | Maximum link hops from any seed URL | Unlimited | Non-negative integer |
| Request timeout | Per-request deadline | A generous fixed default (order of tens of seconds) | Positive duration |
| Retry budget | Retries per URL on transient failure | Small fixed default | Non-negative integer |
| Redirect hop ceiling | Maximum redirects followed for one URL | Small fixed default | Positive integer |
| Response size ceiling | Maximum bytes accepted per response | Generous fixed default | Positive size |
| Audit duration ceiling | Optional wall-clock cap for an audit | Unlimited | Positive duration |

### 4.3 Validation and invariants

- *Unlimited* is represented by a distinguished sentinel (absence of a value), never by a magic large number that could overflow arithmetic.
- Effective values are validated before an audit starts; invalid overrides are rejected with a descriptive local error.
- A running audit keeps the limit snapshot it started with; changes apply to subsequent audits.
- No code path may consult any remote service or persisted entitlement to alter these values.

### 4.4 Politeness (independent of limits)

- The robots exclusion protocol is fetched, cached per host, and obeyed (allow/disallow rules; crawl-delay directives when present). Sitemaps declared there are discovered automatically.
- When a throughput ceiling is set, it is enforced by token-bucket-style request spacing; when unset, per-host concurrency is still bounded adaptively to avoid overloading a single origin.
- Rate-limit and server-error responses trigger exponential backoff; explicit retry-after hints are honored when present.
- Requests carry an identifiable user-agent (product name and version; optional contact detail), configurable by the user.

---

## 5. Crawl Engine

### 5.1 Audit inputs

| Input | Required | Description |
|---|---|---|
| Seed URL(s) | Yes | One or more starting URLs; the crawl scope is derived from them unless overridden |
| Scope definition | Yes | Host-only, include-subdomains, or whole-domain; optional path-prefix rules |
| Depth ceiling | No | Overrides the limits default for this audit |
| Inclusion patterns | No | URL patterns that must match for a URL to be crawled |
| Exclusion patterns | No | URL patterns that disqualify a URL (evaluated after inclusion) |
| Query-parameter policy | No | Keep all, keep a whitelist, or strip all query parameters during canonicalization |
| Rendering mode | No | Plain HTTP fetch, or JavaScript-rendered fetch (section 5.6) |
| Limit overrides | No | Any subset of the parameters in section 4.2 |
| Custom request headers / cookies | No | Optional credentials for the target site, stored locally |
| Sitemap seeds | No | Additional sitemap URLs to expand at start |
| Politeness options | No | Robots compliance toggle, per-host throttle, user-agent string |
| Proxy routing | No | Upstream proxy and/or the built-in local intercepting proxy (section 5.9) |
| Custom extraction rules | No | User-defined content extraction rules applied during the crawl (section 7.9) |
| Custom search items | No | User-defined content search items applied during the crawl (section 7.16) |
| JavaScript snippets | No | User-defined in-page scripts for rendering mode (section 7.17) |
| Sample-crawl mode | No | A depth/page-bounded sampling run for a quick preview, using the same pipeline and limits resolution |
| Virtual robots override | No | A user-supplied robots.txt body used in place of the live one for this audit |
| Signed-request access | No | Optional signature material applied to in-scope requests, with expiry tracking and automatic re-application (section 5.10) |
| Pre-crawl validation | No | Resolve and probe the start URL(s) before crawling (protocol, redirect, TLS, login-gate detection) and surface blockers before work begins |

### 5.2 URL canonicalization and deduplication

Before a URL enters the frontier it is canonicalized:

- Scheme and host are lowercased; default ports are removed; fragments are discarded.
- Trailing-slash policy is applied consistently; percent-encoding is normalized.
- Query parameters are sorted, filtered by the configured policy, and known session-tracking parameters are stripped.
- Relative references are resolved against the document base, honoring any declared base override.

A persistent **seen-set** keyed by canonical URL guarantees each URL is fetched at most once per audit.

### 5.3 Frontier and scheduling

- A **priority frontier** orders work: seeds first, then a breadth-leaning order with depth as tiebreaker.
- **Per-host sub-queues** enforce politeness and throughput ceilings independently of global concurrency.
- Sitemap entries are treated as *discovered* URLs and scheduled like any other discovery.
- Per-URL states: `queued → fetching → fetched | redirected | error | blocked | skipped`, each transition persisted.

### 5.4 Fetching

- Workers pull URLs under the concurrency and throughput ceilings and fetch them with a configurable HTTP client, honoring the configured proxy routing (section 5.9).
- Redirects are followed up to the hop ceiling; **every hop is recorded** so full chains and loops can be reconstructed.
- Recorded per response: final status, content type, size, timing, selected headers (caching, security, redirection), and TLS outcome.
- Content-type routing: HTML is parsed; XML sitemaps are expanded; robots files update host rules; stylesheets, scripts, images, and fonts are recorded as resource metadata; other types are recorded without parsing.
- Transient failures are retried within the retry budget using exponential backoff; permanent failures are classified (name resolution, TLS, timeout, HTTP status, oversize, robots-blocked, proxy failure) and stored with the URL record.

### 5.5 Link discovery

- From HTML: anchor and image-map references, document-level link relations (canonical, alternates, stylesheets, next/previous), and optionally embedded frame sources and meta-refresh targets.
- From sitemaps: URL entries with their declared metadata; sitemap indexes are expanded recursively.
- Classification: **in-scope** URLs are enqueued; **external** URLs are recorded as external links only; **excluded** URLs are recorded as skipped with the reason.

### 5.6 Rendering mode (optional)

- A headless-browser mode renders JavaScript-heavy pages and captures the post-render DOM, a network-waterfall summary, and console messages (errors and warnings).
- Optional rendering behaviors: **infinite-scroll** expansion (scroll until content stabilizes or a bounded limit) and a **stealth** profile that reduces automation fingerprints. Device, viewport, and locale emulation are configurable per audit.
- Custom request headers are split so that credential-bearing headers are sent only to in-scope origins while non-credential headers (e.g. language preference) apply to all requests.
- When the local intercepting proxy is enabled (section 5.9), all rendered page traffic is routed through it, and the network waterfall is built from its capture.
- Rendered fetches obey the same limits and politeness rules; the mode is selectable per audit or per URL pattern.

### 5.7 Lifecycle and control

- Audit states: `configuring → running → (paused ⇄ running) → completed | cancelled | failed | interrupted`.
- **Pause** checkpoints the frontier, seen-set, and counters; **resume** rehydrates them; **cancel** drains workers and finalizes partial data; **interrupted** (process loss) is recoverable from the last checkpoint.
- Progress is emitted continuously, throttled to at most one message per second: counters (queued, fetched, errors, blocked), throughput, elapsed time, estimated remainder, and current phase.

### 5.8 Storage during crawl

- URL records, link edges, and extracted elements are persisted incrementally in transactional batches; the engine never requires the full dataset in memory.

### 5.9 Local proxy and traffic routing

- **Upstream proxy (optional):** crawl requests may be routed through a user-configured upstream proxy — HTTP-connect or SOCKS class — set globally or per audit. Optional proxy credentials are supplied by the user and encrypted at rest (section 9.1). A per-host bypass list exempts chosen hosts from proxying. A per-audit option allows fallback to direct connection when the proxy is unreachable; proxy failures are otherwise classified like any other fetch failure.
- **Built-in local intercepting proxy (optional):** the backend can run a forward proxy bound to the **loopback interface only**. When enabled, the headless rendering engine routes all page traffic through it, providing:
  - request/response capture feeding the network-waterfall summary (section 5.6);
  - user-defined local resource-blocking rules (for example, suppressing selected third-party resource classes during rendering);
  - deterministic local traffic routing for advanced crawl scenarios.
- The local proxy is a **crawl-traffic tool, not a tunnel**: it never bridges local services to remote clients, opens no non-loopback listener, and performs no enrollment or liveness reporting (section 3, row 4).
- All proxy configuration is local and user-supplied; no remote proxy auto-configuration or discovery channel exists.

### 5.10 Signed-request crawler access (optional)

- Some targets admit automated clients only when requests carry a valid cryptographic signature. The user may supply signature material (as custom request headers, section 5.1) together with an expiry; the engine extracts the signature-bearing headers from the general header set, retains the non-signature headers, and re-applies the signature headers to **in-scope** requests automatically.
- The engine tracks signature expiry, warns before and after it lapses, and classifies signature-rejection responses distinctly so the user can renew the material.
- Signature material is a user-owned secret, encrypted at rest (section 9.1); it is never derived from hardware or OS identity and never transmitted anywhere except the crawl target. This is a generic signed-access mechanism, not tied to any particular platform.

---

## 6. Scheduled Audits

### 6.1 Schedule model

- A **schedule** binds a reusable audit configuration (section 5.1) to a recurrence rule: either a fixed interval or a calendar-style rule (time of day plus day-of-week/month selectors), evaluated in the user's local timezone.
- Fields: name; project and configuration references; recurrence rule; enabled flag; optional limit overrides; optional completion actions (section 6.5); next-due timestamp; last-run reference.
- Schedules are created, edited, enabled, disabled, and deleted locally; no remote service participates in scheduling.

### 6.2 Execution rules

- The scheduler (section 2.7) computes and persists the next due time after each run; scheduling state survives restarts.
- **Catch-up policy:** if the machine was off or asleep at a due time, the schedule runs **once** at the next opportunity — no backlog of missed runs is replayed — and the run history records it as a catch-up run.
- **No overlap:** two audits of the same schedule never run simultaneously; a due time arriving while the previous run is still active is skipped and recorded as skipped-overlap.
- A scheduled run is an ordinary audit: the same limits resolution, lifecycle, checkpoints, and hub events apply; it is distinguishable in history by its trigger type (manual, scheduled, or catch-up).
- Global concurrency: at most a small fixed number of audits (scheduled or manual) run at once; excess due runs queue in due-time order.

### 6.3 Run history

- Each firing records: schedule reference; resulting audit reference (or failure reason); trigger type; started/finished timestamps; outcome, including completion-action results.
- History is viewable per schedule and pruned by retention (section 9.5).

### 6.4 UI and contract

- The main hub window gains a schedules surface: list with next-due and last-outcome, create/edit forms, enable toggle, a run-now action, and run history.
- REST resources and hub events are defined in section 10.

### 6.5 Completion actions (optional, user-authorized)

- Per schedule, the user may pre-authorize: a local OS notification; an export to one of their configured destinations (section 8.5); a completion message to a notification endpoint they configured themselves.
- Completion actions never run without prior explicit configuration; failure of an action is logged and reported in run history but **never fails the audit**.

---

## 7. Audit Engine

### 7.1 Inputs

- The crawl dataset (complete or in progress).
- The audit rule configuration: enabled categories and per-rule thresholds, all stored locally.
- Scope context: seed URL(s) and target domain.

### 7.2 Pipeline

1. **Fetch / reuse HTML** — the stored response body is reused from the content store (section 9.6); re-fetching occurs only on explicit refresh.
2. **Parse the DOM** — a tolerant parser produces a document tree even for malformed markup.
3. **Extract the element inventory** — structured facts are pulled from the tree (section 7.3); when enabled, user-defined custom extraction rules run in the same stage (section 7.9).
4. **Per-URL rule passes** — independent rules evaluate each URL's inventory.
5. **Cross-URL aggregation passes** — site-wide rules evaluate indexed views over the whole dataset (duplicates, orphans, patterns).
6. **Finding emission** — judgments are persisted with severity and evidence.
7. **Metric aggregation** — summary metrics are computed for reporting (section 8.1).
8. **Report readiness** — the audit is marked reportable; the UI is notified via the hub.

Execution modes: **incremental** (per-URL passes run during the crawl) and **full** (adds cross-URL passes after completion). A **re-audit** recomputes findings without re-fetching unless a refresh is requested.

### 7.3 Element extraction inventory

| Element group | Extracted facts |
|---|---|
| Document head | Title; meta description, robots, viewport, charset; canonical target; language/region alternates; social-graph cards; favicon relations |
| Headings | Ordered outline with levels and text |
| Anchors | Target, anchor text, relation flags, target-window behavior, position |
| Images | Source(s), alternative text, declared dimensions, lazy-loading attributes |
| Structured data | Embedded blocks, vocabulary, parsed fields |
| Resources | Scripts and stylesheets with placement and loading flags; counts and sizes |
| Text statistics | Visible word count, declared language |
| Tables | Detected data tables with row/column structure and header cells |
| Link relations | Pagination, alternate formats, and other declared relations |

### 7.4 Rule taxonomy (generic, industry-standard)

| Category | Example checks (abstract) |
|---|---|
| Indexability & status | Error statuses; redirect chains and loops; canonical missing/mismatched; robots meta directives; robots-blocked URLs; orphan pages; sitemap coverage; soft-404 detection (section 7.12) |
| Content quality | Missing, duplicate, or length-out-of-range titles and descriptions; heading-order violations; thin content; near-duplicate bodies; misspelled words in visible text (section 7.8); readability and content metrics (section 7.12) |
| Linking | Broken internal and external links; excessive redirect depth; uninformative anchor text; excessive outbound links; very deep pages; link-equity distribution (section 8.1) |
| Images | Missing or empty alternative text; broken sources; oversized payloads; missing dimensions |
| Performance heuristics | Total page weight; resource counts; render-blocking resources; uncompressed text resources; missing or short cache lifetimes; large document trees; slow responses. Rendered lab metrics and Core Web Vitals: section 7.11 |
| Security & protocol | Mixed content; non-HTTPS pages; insecure form actions; missing security headers; absent HTTP-to-HTTPS redirection |
| Structured data | Parse errors; missing required properties per vocabulary; deprecated types; rich-result eligibility (section 7.13) |
| Internationalization | Invalid language/region codes; missing return tags; missing self-reference; default-language handling |
| Mobile readiness | Viewport presence and validity; responsive-design signals; tap-target sizing and font legibility (section 7.14) |
| Accessibility | Automated accessibility-rule violations; ARIA attribute/role validity; form labels and landmarks; color-contrast signals (section 7.10) |
| Technology detection | Detected platform/framework/analytics signatures and categories from a bundled fingerprint catalogue (section 7.15) |

### 7.5 Finding model

Each finding carries:

- A **rule key** from the product's own taxonomy, plus its category.
- A **severity**: Error, Warning, Notice, or Pass — assigned by the rule definition, with thresholds configurable locally.
- The **affected URL(s)**, or a site-wide scope marker.
- **Evidence**: the extracted values that support the judgment.
- A **recommendation**: generic remediation guidance.
- A **first-seen audit** reference for delta tracking.
- An optional **remediation difficulty** indicator (effort estimate), independent of severity and priority.

**Near-duplicate detection principle:** page bodies are fingerprinted using overlapping token sequences (shingles); set-similarity estimation between fingerprints identifies clusters of substantially similar content. The mathematical principle is specified; the concrete hashing and estimation techniques are an implementation choice.

**Finding suppression and prioritization:** the user may suppress (ignore) an individual finding or an entire rule for a project. Suppressions are persisted, reversible, and listed in a dedicated suppressed view; suppressed findings are excluded from default counts and from scoring (section 8.1) but remain inspectable. Each rule may also carry a local **priority/importance** ranking independent of severity, used to order remediation lists. Suppression state is project-scoped and carries across re-audits of the same project.

### 7.6 Rule execution architecture

- Rules are **declarative conditions** over the element inventory and aggregate views; a generic evaluator applies them. Adding a rule is a data change, not an engine change.
- Per-URL passes are independent and parallelizable. Aggregation passes run once over indexed views (for example, grouping by title text, or computing reachability from seeds over the link graph).

### 7.7 Single-page analysis

- An ad-hoc mode fetches and audits a single URL without creating a project or crawl. The same pipeline applies; results are shown immediately and may optionally be saved as a lightweight local record.

### 7.8 Spelling checks

- Visible page text is checked against bundled per-language dictionaries; the dictionary is chosen from the page's declared language, falling back to the user's preferred checking language.
- A locally stored **user dictionary** (per language) lets the user add accepted words; user-dictionary entries suppress findings.
- Findings carry the offending tokens and their positions as evidence; exclusion of proper nouns and code-like tokens is handled by generic heuristics (implementation choice).
- Dictionary assets ship with the product per locale (section 13); checking works fully offline.

### 7.9 Custom Content Extraction

- **Purpose:** beyond the fixed built-in inventory (section 7.3), the user may define **custom extraction rules** that pull arbitrary content from crawled pages into named, typed datasets for reporting and export.
- **Configuration:** custom extraction is an optional, off-by-default part of the audit configuration (section 5.1). It consists of an ordered list of rules; each rule defines:
  - A user-chosen **name**, which becomes the dataset column label.
  - An **extraction method**: an element-selector path evaluated against the document tree, or a pattern expression evaluated against element text or raw markup.
  - A **capture target**: element text, a named attribute, inner markup, or a pattern capture group (group selection is configurable for pattern rules).
  - An **output type**: text, integer, decimal, or URL; URL values are resolved to absolute form against the document base.
  - Optional **path scoping**: include/exclude URL-path directives restricting the rule to matching pages.
- **Execution:** rules run during per-URL crawl processing, after DOM parsing, against the same parsed document as the built-in inventory (no additional fetches); in rendering mode they run against the post-render DOM. Selector rules capture one value per matched element; pattern rules capture one value per match (or per configured capture group). Values are coerced to the configured type; conversion failures are recorded as invalid markers and never fail the crawl.
- **Mandatory bounds:**
  - A per-rule, per-page **cap on stored values**; excess matches are discarded and counted.
  - A per-value **length cap** with safe truncation that never splits a multi-byte character; truncations are counted.
  - Rule evaluation is **time-bounded** so pathological patterns cannot stall fetch workers.
  - Extraction errors, discards, and truncations are logged locally and surfaced as rule-level diagnostics; they never fail the audit.
- **Storage:** each captured value persists as a row keyed by (audit, URL, rule, ordinal) in the extracted-content table (section 9.2); per-rule aggregates (pages matched, values captured, discards, truncations) are computed for reporting.
- **Reporting and export:** a dedicated **custom extraction explorer** presents the dataset with dynamic columns — one column per rule, multi-valued rules expanding to as many columns as the busiest page — with filtering, sorting, and pagination (section 8.2). The dataset is included in spreadsheet and CSV exports (section 8.4). Extracted values are also available as inputs to declarative audit rules (section 7.6).
- **Rule management and preview:** rules are managed in the audit configuration surface (add, edit, reorder, delete) with a live preview against a sample URL before the crawl starts.
- **Re-audit behavior:** changing extraction rules and re-auditing re-extracts from stored bodies in the content store (section 9.6) without re-fetching.

### 7.10 Accessibility auditing (optional category)

- An automated accessibility rule engine evaluates each page's document tree against a bundled, versioned rule set. When rendering mode is enabled the rendered tree is used; otherwise the parsed tree is used, and checks that require layout or computed style are marked as requiring rendering.
- Rule families include: image alternative text; ARIA attribute and role validity; form-label association; landmark and heading structure; link purpose; document language; and color-contrast signals.
- Each violation emits a finding with severity, the implicated element (a locator), and remediation guidance. Rules are individually enable-able and severity-mappable.
- Results aggregate into an accessibility report surface and the URL detail view, and contribute to audit scoring (section 8.1). The rule set ships as a bundled asset and updates only with product updates.

### 7.11 Rendered performance and Core Web Vitals (optional category)

- Requires rendering mode (section 5.6). For each rendered page the engine captures standard lab metrics — largest contentful paint, cumulative layout shift, total blocking time, first contentful paint, and time to first byte — using their publicly documented definitions.
- **Performance budgets:** the user defines per-metric thresholds; exceedance emits a finding. Budgets are stored with the audit configuration and applied per audit.
- **Code coverage:** the fraction of fetched stylesheet and script bytes actually used during render is measured; large unused payloads emit findings.
- **Page composition metrics:** total DOM nodes, maximum tree depth/width, image counts and sizes, and smallest rendered font size are captured for mobile and front-end heuristics.
- Per-request network timing already captured in the rendering network-waterfall summary (section 5.6) is reused here.
- Aggregations: metric distribution histograms, worst-performing pages, and budget pass-rate. Optional field-data enrichment from an external provider is an open scope decision (section 15) and is not required by this version.

### 7.12 Readability, content metrics, and soft-404 detection

- **Readability:** a standard reading-ease score and grade-level estimate (Flesch-class formulas over sentence, word, and syllable counts) are computed from visible text, per page and in aggregate.
- **Reading time:** estimated from word count and a configurable words-per-minute rate.
- **Text-to-HTML ratio:** visible-text length relative to markup size, as a thin-content signal.
- **Sentiment (optional):** polarity scoring against a bundled affect lexicon; disabled by default and enabled per audit.
- **Soft-404 detection:** pages returning a success status whose content matches error-like patterns (a configurable phrase list plus structural heuristics) are flagged as soft-404 findings, distinct from hard HTTP errors.
- All text metrics operate on content-area-scoped text when a content-area selector is configured (section 7.9).

### 7.13 Structured data validation

- Embedded structured data is parsed from JSON-LD and Microdata into a normalized graph resolved against a bundled open vocabulary.
- Validation covers syntax (well-formedness, context and type presence) and semantics (required and expected properties per type).
- **Rich-result eligibility:** for each supported search-gallery feature type, the engine checks the property set required for eligibility and reports eligible / ineligible / missing-required-field, naming the specific fields involved.
- Errors use a structured, human-readable taxonomy (e.g. missing type, invalid value, missing required field, malformed block) rather than free text only.
- Findings are emitted per block; a structured-data explorer lists every block, its type, and its validation outcome.

### 7.14 Mobile-friendliness (optional category)

- Uses the rendered document and the configured device-emulation profile (section 5.6).
- Checks: viewport meta presence and validity; tap-target sizing and spacing; font legibility; content-to-viewport fit; and responsive-design signals.
- Violations emit findings and contribute to a mobile-readiness aggregate and to audit scoring (section 8.1).

### 7.15 Technology detection (optional category)

- A bundled, versioned fingerprint catalogue is matched against DOM markers, script/source patterns, response headers, and meta generators to detect platforms, frameworks, analytics, and other technologies, grouped by category with a confidence indicator.
- Detections are stored per URL and surfaced in a technology explorer and an aggregate "technologies in use" view.
- The catalogue is a bundled asset; it is never fetched from a remote service at runtime and updates only with product updates.

### 7.16 Custom content search

- The user defines search items combining plain terms and/or regular expressions, with optional **negative terms** that must be absent for a match.
- Per-item URL-path include/exclude directives scope where the item runs; each item targets visible text (content-area-scoped when configured) and/or raw markup.
- Execution records per-URL match counts and matched snippets; an overview joins items with per-item affected-URL counts.
- Results are exposed through an explorer, CSV export, and the automation interface; the feature has an enable flag and re-runs on re-audit from stored bodies without network access.
- Mandatory bounds: regular-expression execution timeout and a maximum matches-per-page cap; failures are isolated and never fail the audit.

### 7.17 Custom JavaScript snippets (optional)

- Requires rendering mode. The user defines scripts executed in the rendered page, each with an execution timing, a timeout, URL-path matching, and declared result columns.
- Scripts run inside an isolated wrapper exposing a minimal host API (submit a data value, report an error, load an external script); return values are captured as typed results.
- A built-in example library and a single-URL test mode are provided.
- Results join URL data for a report surface and appear as dynamic columns in the query builder (section 8.7), with bounded row counts.
- Safety: execution is time-bounded, has no access to the local backend beyond the scoped wrapper API, and a snippet failure on one page never fails the audit or other pages.

### 7.18 Response-vs-rendered comparison

- When rendering mode is enabled, both the raw HTTP response body and the post-render document are retained for each page.
- The engine compares status, title and metadata, link sets, visible text, structured data, and resource sets between the two.
- Differences surface as findings (e.g. content or links present only after rendering, metadata altered by script) and as a side-by-side diff in the URL detail view, supporting a "rendered" report surface.

---

## 8. Local Reporting and Visualization

### 8.1 Aggregation into the database

- During and after an audit, the backend computes summary metrics into dedicated aggregate tables on the configured provider (section 2.4):
  - URL counts by status class, content type, depth, and indexability.
  - Finding counts by severity and category.
  - Crawl health: errors, redirects, blocked and skipped counts.
  - Distributions: response time, page weight, word count.
  - Link-graph statistics: orphan pages, hub pages, and reachability, computed by standard graph traversal from the seeds; **link-equity distribution** (inlink counts, percentile ranking, and concentration across pages).
  - **Audit scoring:** a per-category score and an overall score derived from finding counts and severities weighted by indexable-URL coverage; a disabled category contributes no score; suppressed findings are excluded (section 7.5). Scores are stored per audit so they are comparable across audits and drive a score-trend surface.
- Aggregates are keyed by audit and metric with a dimension payload, so dashboard reads are constant-cost lookups rather than full scans.

### 8.2 Report surfaces

| Surface | Content |
|---|---|
| Dashboard | Summary cards, severity/category charts, crawl-health panel, top-issue lists |
| Issue explorer | Filterable, sortable, groupable finding lists; drill-down to affected URLs; bulk export |
| URL explorer | Per-URL detail: status and timing, extracted elements, findings, inbound/outbound links, redirect chain, optional rendered screenshot |
| Visualizations | Site-structure tree by path; crawl-depth map; internal-link network graph (force-directed layout); redirect-chain diagrams; section treemap by URL count or weight |
| Audit comparison | Any two audits of a project: new/resolved findings and metric deltas |
| Content comparison | Any two audits of a project, per shared URL: side-by-side differences of title, headings, and visible text, plus status and metadata changes; rendered offline from the content store (section 9.6) |
| Custom extraction explorer | User-defined extraction dataset with dynamic per-rule columns (multi-valued rules expand to the busiest page's column count); filter, sort, paginate; per-rule match counts and diagnostics (section 7.9) |
| Accessibility | Violations grouped by rule family and severity, with implicated elements and remediation (section 7.10) |
| Performance / Core Web Vitals | Lab-metric distributions, worst pages, budget pass-rate, code-coverage summary (section 7.11) |
| Structured data | Every block with type, parsed fields, and validation/rich-result outcome (section 7.13) |
| Technology | Detected technologies by category with confidence and affected-URL counts (section 7.15) |
| Custom search | Per-item match overview and per-URL matched snippets (section 7.16) |
| Snippet results | Custom JavaScript snippet outputs joined to URLs, dynamic columns (section 7.17) |
| Rendered comparison | Response-vs-rendered differences per URL (section 7.18) |
| Scores | Per-category and overall audit scores with cross-audit trend (section 8.1) |
| Suppressed findings | User-suppressed findings and rules, reversible (section 7.5) |

### 8.3 Chart requirements

- Chart types: bar, stacked bar, pie/donut, line/time-series, histogram, treemap, and network graph.
- All charts render locally from aggregate endpoints; interactive filters propagate to the underlying lists.

### 8.4 Export

- Local file exports: a multi-sheet spreadsheet workbook (summary, URLs, findings, links, and custom extraction when enabled), CSV per dataset, a self-contained HTML report bundle, and an optional PDF summary. Custom extraction exports use the same dynamic-column expansion as the explorer (section 7.9).
- Exports may additionally be delivered to optional user-configured destinations (section 8.5).

### 8.5 Optional export destinations

- **Off by default;** no destination exists until the user creates one. Destination classes: a cloud drive/spreadsheet service, an object-storage endpoint, and a notification webhook — all addressed with the **user's own credentials**, never vendor accounts.
- Each destination stores: a user-chosen name; class; endpoint details; credential payload **encrypted at rest** (section 9.1).
- A **test-connection** action verifies reachability and credentials before first use.
- Transfers occur only when the user initiates an export to the destination, or pre-authorizes it as a schedule completion action (section 6.5).
- Delivery failures are retried with bounded backoff, logged locally, and surfaced in the UI; a destination failure **never fails the audit** or the local export.
- Disabling or deleting all destinations restores the guarantee of zero non-crawl outbound traffic.

### 8.6 Access

- Every report is viewable at any time, fully offline. **No authorization check of any kind gates report access.**

### 8.7 Standalone diagnostic and authoring tools

A set of self-contained tools operates without requiring a full audit; each is reachable from the shell and, where stateless, from the automation interface:

- **Robots tester:** the user supplies a robots.txt body (or points at a live one) and a list of test URLs; the tool returns the allow/disallow verdict per URL under the same parser used by the crawler (section 4.4), including the matched rule.
- **URL checker:** fetches a single URL and reports status, full redirect chain, selected response headers, and indexability signals.
- **Structured-data checker:** accepts pasted markup or a URL and returns parsed blocks with the validation and rich-result outcome (section 7.13).
- **Render tester:** renders a single URL and returns a screenshot, the post-render document, and console messages — a preview of rendering-mode behavior.
- **XML sitemap generator:** builds a sitemap from an audit's URL set, filterable by indexability and status, with optional change-frequency and priority hints; the result is exported as a standards-conformant sitemap file (and sitemap index when split by size).
- **Interactive query builder:** composes nested AND/OR filter rules over URL columns (operators such as contains, starts/ends-with, equals, in-set, between, regex, and numeric comparison), including dynamic custom-extraction and snippet columns; saved queries apply to the explorers and exports. A raw-SQL administration tool is an open scope decision (section 15) and is not required by this version.

---

## 9. Data Model (Abstract Schema)

### 9.1 Conventions

- One logical installation database, served by the configured provider (section 2.4); all crawl data is project-scoped.
- Surrogate integer keys; UTC timestamps; JSON text columns for flexible payloads.
- Projects and audits support soft delete with transactional pruning.
- **Secret protection (mandatory):** stored site credentials, destination credentials, external-database credentials, proxy credentials, and any local token or signing secrets are encrypted at rest using app-generated entropy persisted locally; key material is never derived from hardware or OS identity (section 3, row 1).

### 9.2 Table catalog

| Table | Purpose | Key fields (abstract) | Notes |
|---|---|---|---|
| application_settings | Single-row preferences and defaults | UI preferences; default crawl limits; default audit toggles; storage options; database provider selection and connection settings (credentials encrypted); theme/locale | Seeded at first run with the default file-based provider. **No identity, license, or account columns exist.** |
| projects | Grouping of audits | Name; description; user tags/labels; created/updated timestamps; scope summary | |
| crawl_configurations | Reusable input sets | Project reference; seeds; scope rules; depth; limit overrides; rendering mode; politeness; proxy routing; custom extraction rules; optional headers/cookies | Sensitive optional values are encrypted at rest (section 9.1) |
| audits | One crawl-and-audit run | Project reference; configuration snapshot; status; started/finished timestamps; counters; engine version | Configuration is snapshotted so later edits never mutate history |
| url_records | Per-URL crawl facts | Audit reference; canonical URL; host; path; depth; discovery source; status class; final status; redirect target; content type; size; timing; robots state; indexability flags | Unique per (audit, canonical URL) |
| url_elements | Extracted DOM inventory | URL reference; element group; payload JSON; ordinal | One row per extracted element group instance |
| link_edges | Directed link graph | Audit reference; source URL reference; canonical target; target URL reference (nullable for external); anchor text; relation flags; internal/external marker | Supports orphan and graph analysis |
| findings | Audit judgments | Audit reference; rule key; category; severity; URL reference (nullable for site-wide); evidence JSON; recommendation key | |
| metric_summaries | Precomputed aggregates | Audit reference; metric key; dimensions JSON; numeric value | Powers dashboards and charts |
| crawl_checkpoints | Resume state | Audit reference; serialized frontier; seen-set reference; counters; saved-at timestamp | Written on pause and periodically during runs |
| schema_migrations | Applied versions | Version; provider dialect; applied-at timestamp | Idempotent migration ledger, per provider |
| saved_views (optional) | User filter presets | Name; target surface; filter JSON | Local convenience only |
| schedule_definitions | Recurring audit rules | Project/configuration references; recurrence rule; enabled flag; limit overrides; completion actions JSON; next-due timestamp | Local only (section 6) |
| schedule_runs | Schedule firing history | Schedule reference; audit reference (nullable on failure); trigger type; outcome; timestamps | Pruned by retention |
| export_destinations | User-configured delivery targets | Name; class; endpoint details; encrypted credential payload; enabled flag | Off by default; none seeded |
| user_dictionary | Accepted spellings | Language; word; added-at timestamp | Suppresses spelling findings (section 7.8) |
| content_artifacts | Content-store index | Audit reference; URL reference; artifact kind (body, screenshot, rendered DOM); storage key; size; content hash | Points into the content store (section 9.6) |
| url_embeddings (optional) | Similarity artifacts | Audit reference; URL reference; embedding payload; model tag | Supports vector-similarity analysis (section 2.4) |
| extracted_content | Custom extraction values | Audit reference; URL reference; rule name; ordinal; typed value payload; value type; invalid/truncated flags | One row per captured value (section 7.9); pruned with retention |
| content_search_results | Custom search matches (section 7.16) | Audit reference; URL reference; search-item reference; match count; matched snippets | Per-item aggregates for the overview |
| snippet_results | Custom JavaScript snippet outputs (section 7.17) | Audit reference; URL reference; snippet reference; typed result columns | Dynamic columns; bounded rows |
| technology_detections | Detected technologies (section 7.15) | Audit reference; URL reference; technology key; category; confidence | Feeds technology explorer and aggregate |
| audit_scores | Category and overall scores (section 8.1) | Audit reference; category (or overall); score; contributing counts | Comparable across audits; drives score trend |
| finding_suppressions | User-suppressed findings/rules (section 7.5) | Project reference; rule key and/or finding scope; suppressed flag; timestamp | Project-scoped; reversible; excluded from counts/scores |
| automation_action_log | Local agent invocation history | Timestamp; transport; tool/resource name; argument summary; outcome | User-viewable and purgeable (section 11); pruned by retention |

### 9.3 Relationships and indexes

- Projects have many configurations; configurations have many audits; audits have many URL records, link edges, findings, and metric summaries; URL records have many element rows; internal link edges reference URL records on both ends.
- Schedules reference configurations and produce run-history rows linked to audits; content artifacts and embeddings belong to (audit, URL) pairs and are addressable across audits of the same project for comparison.
- Required indexes: unique (audit, canonical URL); (audit, status class); (audit, severity, category); (audit, rule key); link edges by (audit, source) and (audit, target); metrics by (audit, metric key); schedules by next-due time; run history by (schedule, started-at); content artifacts by (audit, URL, kind); extracted content by (audit, rule name) and (audit, URL); action log by timestamp.

### 9.4 First-run initialization (mandatory)

- Migrations create all tables on the configured provider, then seed **exactly one application-settings row** with deterministic defaults: the local file-based database provider, unlimited crawl ceilings, all audit categories enabled, local-only feature toggles on, default politeness on.
- First-run state additionally includes: no schedules, no export destinations, no external database configured, an empty user dictionary, and the local MCP automation interface disabled (section 11).
- Initialization completes with **zero network calls**; the product is fully usable immediately after first launch, including on an air-gapped machine.

### 9.5 Retention

- User-configurable retention per project (keep the last N audits, or an age limit). Pruning removes dependent rows transactionally, including content artifacts, embeddings, extracted content, schedule run history, and automation action-log entries; orphaned content-store files are reclaimed. Periodic integrity checks and compaction keep the database healthy on either provider. Diagnostic artifacts (logs, minidumps) have their own bounded local retention (section 10.5).

### 9.6 Content store

- Response bodies and rendered artifacts (screenshots, rendered DOM) are persisted as **compressed, managed files** in a dedicated directory on the local filesystem beside the database, independent of the database provider, indexed by the content-artifacts table; they are never held wholly in memory.
- Artifacts are keyed by (audit, URL, kind) and are addressable **across audits** of the same project, enabling re-audit without re-fetching (section 7.2) and content comparison (section 8.2).
- The store is pruned transactionally with audit retention (section 9.5); its disk usage is included in the user-visible storage reporting (section 13).

---

## 10. API and Real-Time Contract

### 10.1 Conventions

- Base URL: loopback HTTP. All bodies are JSON. Paths below are **new design artifacts**, not reproductions of any existing surface.
- List endpoints accept pagination (offset/limit), filtering, and sorting.
- Uniform response envelope: a data payload, optional pagination metadata, and an optional error object (machine-readable code plus human-readable message).
- No authentication headers are required for the shell-facing API. The only local credential concepts are the optional per-launch session token (section 2.2) and the automation-interface token (section 11); destination, proxy, and external-database credentials (sections 8.5, 5.9, 2.4) are user-owned secrets used solely for their respective outbound connections.

### 10.2 REST mapping

| Operation | Method & path | Input fields | Output fields |
|---|---|---|---|
| Health probe | GET /api/health | — | Readiness flag, uptime |
| Version info | GET /api/version | — | Product version, schema version, active runtime profile |
| Local metrics | GET /metrics | — | Standardized text exposition (Prometheus-class format) of local technical metric families (section 10.5) |
| Read settings | GET /api/settings | — | Full settings object (defaults guaranteed; secrets never returned in clear) |
| Update settings | PUT /api/settings | Any subset of settings fields | Updated settings object |
| Test database connection | POST /api/settings/database/test | Provider selection and connection fields | Reachability, authentication, and migration-compatibility verdict |
| Diagnostic artifacts | GET, DELETE /api/diagnostics/artifacts | Kind filter on list | Local log/minidump metadata (kind, created-at, size) or purge confirmation |
| List projects | GET /api/projects | Pagination, search text | Project summaries |
| Create project | POST /api/projects | Name, description, tags, scope summary | Created project |
| Read / update / delete project | GET, PUT, DELETE /api/projects/{id} | Project fields on update | Project or deletion confirmation |
| List / create configurations | GET, POST /api/projects/{id}/configurations | Configuration fields per section 5.1 | Configuration summaries / created configuration |
| Read / update / delete configuration | GET, PUT, DELETE /api/configurations/{id} | Configuration fields on update | Configuration or confirmation |
| Start audit | POST /api/audits | Configuration reference, optional limit overrides | Audit identifier and initial state (returns immediately; progress via hub) |
| List audits | GET /api/audits | Project filter, status filter, pagination | Audit summaries with counters |
| Read audit | GET /api/audits/{id} | — | Audit detail, counters, phase |
| Pause / resume / cancel | POST /api/audits/{id}/pause, /resume, /cancel | — | New state |
| Re-audit | POST /api/audits/{id}/reaudit | Optional refresh flag | New audit identifier linked to the same dataset |
| List URLs | GET /api/audits/{id}/urls | Filters (status class, content type, depth, indexability, text search), sorting, pagination | URL record summaries |
| Read URL detail | GET /api/audits/{id}/urls/{urlId} | — | Full record, element inventory, findings, inbound/outbound links, redirect chain |
| List links | GET /api/audits/{id}/links | Source/target filters, internal/external, pagination | Link edge rows |
| List findings | GET /api/audits/{id}/findings | Severity, category, rule key, URL filters; sorting; pagination | Finding rows with evidence |
| Dashboard summary | GET /api/audits/{id}/summary | — | Metric cards: counts by status/severity/category, crawl health |
| Chart series | GET /api/audits/{id}/charts/{metric} | Dimension filters, bucketing options | Series points (label, value, dimensions) |
| Graph data | GET /api/audits/{id}/graph | Graph type (structure tree, link network, redirect chains), depth/size limits | Nodes and edges |
| Compare audits | GET /api/audits/compare | Two audit identifiers | New/resolved findings, metric deltas |
| Single-page analysis | POST /api/analysis/single-page | Target URL, optional rendering flag, optional rule filters | Findings and element inventory for the page |
| Create export | POST /api/exports | Audit reference, format, dataset selection | Export job identifier |
| Download export | GET /api/exports/{id}/file | — | Local file stream (spreadsheet, CSV, HTML bundle, or PDF) |
| List / create schedules | GET, POST /api/projects/{id}/schedules | Schedule fields per section 6.1 | Schedule summaries / created schedule |
| Read / update / delete schedule | GET, PUT, DELETE /api/schedules/{id} | Schedule fields on update | Schedule or confirmation |
| Enable / disable schedule | POST /api/schedules/{id}/enable, /disable | — | New state and next-due time |
| Run schedule now | POST /api/schedules/{id}/run-now | — | Audit identifier (manual trigger) |
| Schedule run history | GET /api/schedules/{id}/runs | Pagination | Run-history rows |
| List / create destinations | GET, POST /api/destinations | Destination fields per section 8.5 | Destination summaries (credentials never returned in clear) / created destination |
| Update / delete destination | PUT, DELETE /api/destinations/{id} | Destination fields | Destination or confirmation |
| Test destination | POST /api/destinations/{id}/test | — | Reachability and credential verdict |
| User dictionary | GET, POST, DELETE /api/dictionary | Language filter; word on add | Dictionary entries or confirmation |
| Compare page content | GET /api/audits/compare/content | Two audit identifiers; canonical URL | Side-by-side title/heading/text differences and metadata changes |
| List extracted content | GET /api/audits/{id}/extracted-content | Rule filter, URL filter, column selection, sorting, pagination | Dynamic-column rows of custom extraction values (section 7.9) |
| Extraction summary | GET /api/audits/{id}/extracted-content/summary | — | Per-rule pages matched, values captured, discards, truncations |
| List search results | GET /api/audits/{id}/search-results | Item filter, URL filter, pagination | Custom search matches and per-item overview (section 7.16) |
| List snippet results | GET /api/audits/{id}/snippet-results | Snippet filter, URL filter, column selection, pagination | Dynamic-column snippet outputs (section 7.17) |
| List technologies | GET /api/audits/{id}/technologies | Category filter, pagination | Technology detections with confidence (section 7.15) |
| Get audit scores | GET /api/audits/{id}/scores | — | Per-category and overall scores (section 8.1) |
| Suppress / unsuppress finding | POST /api/projects/{id}/suppressions, DELETE /api/projects/{id}/suppressions/{key} | Rule key and/or finding scope | Suppression state (section 7.5) |
| Robots test | POST /api/tools/robots-test | Robots body or URL; test URLs; user-agent | Allow/disallow verdict and matched rule per URL (section 8.7) |
| URL check | POST /api/tools/url-check | Target URL | Status, redirect chain, headers, indexability (section 8.7) |
| Structured-data check | POST /api/tools/structured-data-check | Markup or URL | Parsed blocks with validation/rich-result outcome (section 7.13) |
| Render test | POST /api/tools/render-test | Target URL; device profile | Screenshot, rendered document, console messages (section 8.7) |
| Generate sitemap | POST /api/tools/sitemap-generate | Audit reference; filters; change-frequency/priority options | Sitemap file (or index) (section 8.7) |
| Saved queries | GET, POST, PUT, DELETE /api/queries | Query rule tree; name | Saved query-builder definitions (section 8.7) |

### 10.3 Real-time hub

| Event | Direction | Payload fields | Emission rule |
|---|---|---|---|
| crawl-progress | Server → shell | Audit identifier; queued/fetched/error/blocked counters; throughput; elapsed; estimated remainder; phase | At most once per second while running |
| url-batch | Server → shell | Audit identifier; batch of (URL identifier, canonical URL, status class) | Throttled batches, not per-URL messages |
| stage-changed | Server → shell | Audit identifier; previous state; new state | On every lifecycle transition |
| findings-updated | Server → shell | Audit identifier; severity/category count deltas | During incremental audit passes, throttled |
| audit-completed | Server → shell | Audit identifier; final counters; report readiness | Once, on completion |
| audit-failed | Server → shell | Audit identifier; error code; message | Once, on failure |
| backend-lifecycle | Server → shell | Readiness or shutdown notice | On startup readiness and graceful shutdown |
| schedule-fired | Server → shell | Schedule identifier; audit identifier; trigger type | On each scheduled or catch-up firing |
| schedule-run-completed | Server → shell | Schedule identifier; audit identifier; outcome; completion-action results | Once per scheduled run finalization |
| export-delivery | Server → shell | Export identifier; destination identifier; state (delivered, retrying, failed) | On destination delivery state changes |
| subscribe / unsubscribe | Shell → server | Audit identifier(s) | Client joins/leaves per-audit channels; no authorization beyond the local boundary |

### 10.4 Error and edge behavior

- Validation errors return field-level reasons in the error envelope.
- Long operations return immediately with an identifier; progress flows over the hub.
- Starting an audit that is already running is rejected idempotently with the current state.
- All core endpoints remain functional with no network connectivity; only destination delivery, test-connection actions (sections 8.5, 2.4), and proxied crawls touch user-configured endpoints, and none consults any vendor service.

### 10.5 Local metrics and diagnostics

- **Standardized local metrics:** the metrics endpoint (section 10.2) serves a **pull-only, loopback-only** exposition in a standardized text format (Prometheus-class). There is no push gateway, no remote export, and no scraping by any vendor service.
- Metric families (abstract): crawl counters and throughput; frontier depth; audit phase durations; findings emitted by severity; database read/write latency; content-store size; hub connection count; runtime memory and garbage-collection statistics.
- Metric labels are limited to local technical dimensions (audit identifier, phase, status class). **No user, machine, or account dimensions exist** (section 3, rows 1 and 6).
- **Native crash artifacts:** on abnormal backend termination, a native diagnostic **minidump** is written to a local diagnostics directory. Artifact count and total size are bounded; the oldest artifacts are reclaimed first. Minidumps are **never transmitted automatically**; they exist solely for user-initiated troubleshooting.
- **Local logs:** structured, user-viewable, and purgeable; they carry no identity dimensions.
- The settings surface and the recovery screen expose the diagnostics directory (open, inspect, purge); the diagnostic-artifacts endpoint (section 10.2) provides the same capabilities programmatically.

---

## 11. Local Automation Interface — Local MCP Server (Optional)

### 11.1 Mandate and boundary

- The local automation interface **shall implement a standardized Model Context Protocol (MCP) server**, conforming to the published protocol's capability negotiation, tool invocation, resource reading, and prompt semantics. It is a protocol-conformant surface, not an ad-hoc remote-procedure layer.
- The interface is **optional and off by default**, enabled in settings. When enabled, it is reachable only through the local transports below; it is never bridged, relayed, or exposed remotely (section 3, row 4).
- The MCP surface is a **semantic projection of the local API** (section 10): identical validation, limits resolution (section 4), lifecycle rules (section 5.7), and concurrency caps (section 6.2) apply. It exposes **no** settings, destination, credential, proxy, or database-provider management.
- Enabling the interface adds **no outbound traffic**.

### 11.2 Transport layers

| Transport | Binding | Client class | Authentication |
|---|---|---|---|
| stdio | Local process pipes | An agent spawns the packaged **stdio adapter** as a child process; the adapter bridges protocol messages to the backend's loopback API | Local process-spawn trust; the local token may optionally be required |
| SSE / streamable HTTP | Loopback HTTP endpoint | Resident local agents connecting over the loopback interface | Local token (required) |

- The stdio adapter is a thin, packaged local bridge process: it holds no domain logic, opens no non-loopback sockets, and terminates when its parent client or the backend exits.
- Both transports are strictly local. **No remote or cloud bridging, enrollment, heartbeat, or remote credential issuance exists** (section 3, row 4).

### 11.3 Tools — lifecycle control

Tools are the protocol's invocable actions. Names below are new design artifacts; each maps one-to-one onto the corresponding local API operation with identical semantics.

| Tool | Input fields | Behavior | Output |
|---|---|---|---|
| start-audit | Configuration reference; optional limit overrides | Starts an audit exactly as the REST start operation | Audit identifier and initial state |
| pause-audit | Audit identifier | Checkpoints and pauses | New state |
| resume-audit | Audit identifier | Rehydrates and resumes | New state |
| cancel-audit | Audit identifier | Drains workers, finalizes partial data | New state |
| re-audit | Audit identifier; optional refresh flag | Triggers a follow-up analysis pass over the same dataset | New audit identifier |
| analyze-single-page | Target URL; optional rendering flag; optional rule filters | Ad-hoc single-page audit | Findings and element inventory |
| run-schedule-now | Schedule identifier | Manual trigger of a schedule | Audit identifier |
| get-audit-progress | Audit identifier | Read-only progress snapshot (the polling contract, section 11.5) | State, counters, throughput, elapsed, estimated remainder, phase |

- Invalid lifecycle transitions are rejected with protocol error objects mirroring the API error envelope; duplicate starts are rejected idempotently with the current state.
- Every invocation is recorded in the local automation action log (section 9.2).

### 11.4 Resources and prompts — data consumption

**Resources** are read-only, addressable data views. Each is identified by a structured address combining resource kind, audit (or schedule) reference, and optional filters; the address templates are new design artifacts.

| Resource | Addressed by | Content |
|---|---|---|
| audit-summary | Audit reference | Metric cards: counts by status/severity/category, crawl health (same payload as the dashboard summary) |
| high-severity-findings | Audit reference; severity floor; pagination | Finding rows at or above the severity floor, with evidence and recommendations |
| url-detail | Audit reference; URL reference | Full URL record, element inventory, findings, links, redirect chain |
| audit-list | Optional project filter; pagination | Audit summaries with counters |
| schedule-status | Schedule reference | Enabled flag, next-due time, last outcome |
| crawl-progress | Audit reference | The same snapshot as the progress tool, in resource form |
| extracted-content | Audit reference; rule filter; pagination | Custom extraction values in dynamic-column form (section 7.9) |

**Prompts** are reusable, parameterized templates that compose resources into agent-ready context. They execute no side effects.

| Prompt | Parameters | Composition |
|---|---|---|
| audit-health-briefing | Audit reference | Summary metrics plus top issues, structured as a briefing for an agent to narrate |
| high-severity-triage | Audit reference | High-severity findings grouped by category with URL evidence, ordered for remediation planning |
| audit-delta-briefing | Two audit references | New/resolved findings and metric deltas, structured for change reporting |

### 11.5 Delegated AI control flows

The interface shall support the following architectural pattern for an external **local** agent negotiating multi-step actions:

1. **Capability discovery** — the agent performs the standard protocol handshake; the server advertises its tools, resources, and prompts.
2. **Intent mapping** — the agent reads configuration and audit-list resources, then invokes start-audit with the chosen configuration.
3. **Progress negotiation** — the agent polls get-audit-progress (or reads the crawl-progress resource) at a bounded cadence. The server enforces a minimum polling interval and returns retry hints when polled too fast; on push-capable transports the server may additionally emit progress and completion notifications so polling is optional.
4. **Completion detection** — the agent observes the terminal state (completed or failed) via the progress snapshot or a completion notification.
5. **Follow-up triggering** — upon completion, the agent may automatically invoke re-audit (optionally with refresh) or analyze-single-page for targeted verification. The follow-up is an ordinary audit: same limits, same lifecycle, same concurrency caps.
6. **Consumption** — the agent reads audit-summary and high-severity-findings resources, or invokes the briefing prompts, to produce its analysis.

**Guardrails:**

- Agent-invoked actions grant **no elevated capability**: everything remains subject to local validation, limits resolution, and the global concurrency cap.
- Long-running work never blocks the protocol channel; all tools return immediately with identifiers, and progress is obtained through the polling/notification contract.
- Conflicting lifecycle commands from concurrent local clients are resolved by the audit state machine: the first valid transition wins; later commands are rejected idempotently.
- All invocations are recorded in the local, user-viewable, purgeable action log for transparency.

### 11.6 Credentials and lifecycle

- Authentication uses a **locally generated random token**, viewable and rotatable by the user in settings; rotation invalidates existing clients immediately. The token is stored encrypted at rest (section 9.1) and never leaves the machine. It is never derived from hardware or OS identity (section 3, row 1).
- No enrollment, heartbeat, liveness reporting, or remote credential issuance exists.
- Disabling the interface closes all of its listeners and terminates any running stdio adapter.

---

## 12. Desktop Shell Requirements

- **Window catalog:** splash; main hub (projects, dashboards, and schedules); report window; visualization window; single-page analyzer; standalone tools (section 8.7); settings; backend-failure/recovery screen.
- **Shell duties:** spawn, monitor, and restart the backend; discover the backend port; load only local UI assets; provide native file dialogs for export/import; surface OS taskbar progress and local notifications; persist window state.
- **Settings surfaces** include: database provider selection with connection-test feedback; export-destination management with test-connection feedback; proxy configuration; user-dictionary management; the MCP automation-interface toggle with transport selection, token display, and rotation; diagnostics management (open, inspect, and purge local logs and crash artifacts); UI language selection.
- **Recovery screen** shows local diagnostic information and a pointer to the local crash-artifact directory after repeated backend failures.
- **UI state** derives entirely from the local API and hub. No entitlement-driven screens exist; the first-run experience proceeds directly to project creation.
- No remote content and no embedded views of vendor services. An optional embedded preview of crawled pages is sandboxed and loads only the crawled target.

---

## 13. Non-Functional Requirements

| Area | Requirement |
|---|---|
| Scale | Support audits of at least several hundred thousand URLs on commodity desktop hardware; streaming persistence keeps memory bounded; the external client-server database provider (section 2.4) supports high-volume deployments beyond the embedded default |
| Runtime performance profile | The backend shall run under a **server-grade runtime memory-management profile**: server garbage collection (the standard .NET runtime setting `System.GC.Server`) with concurrent collection enabled, sized for high-throughput concurrent crawling and large read-model working sets. The profile is fixed at packaging time, active by default, and reported by the version/diagnostics surface; the stated scale target is validated against it |
| Responsiveness | Dashboard reads from aggregates return in under one second at target scale; progress updates at most once per second |
| Reliability | Crash-safe writes (provider-appropriate durability plus transactions); resumable crawls; automatic backend restart with backoff; native crash minidumps captured locally (section 10.5) |
| Observability | Pull-only local metrics endpoint in a standardized exposition format; local structured logs; local native crash artifacts. All diagnostics remain on the local filesystem and are never transmitted automatically |
| Privacy | Zero outbound traffic beyond crawl targets (optionally proxied), transfers to user-configured destinations (off by default), and connections to a user-configured external database (optional); no analytics; logs, minidumps, and the automation action log are user-viewable and purgeable |
| Portability | Self-contained runtime; Windows primary, macOS and Linux secondary; no prerequisite installs |
| Storage | User-visible disk usage including the content store and diagnostics directory; retention and pruning; periodic compaction on either provider |
| Packaging | Self-contained asset manifest: engine locale packs, per-locale spell dictionaries, native database extensions per OS/architecture for the embedded provider, the stdio adapter, and media codecs for the embedded preview |
| Accessibility & localization | Keyboard-navigable UI; shipped UI localization with a user-selectable language and locale-aware formatting; bundled engine locale assets; the initial language set is a product decision, but the localization framework and asset pipeline are mandatory |

---

## 14. Acceptance Criteria

1. First launch on a machine with no network completes initialization and opens the main hub.
2. Process monitoring confirms no OS identity stores (registration stores, management instrumentation, platform I/O registries) are read at any point.
3. Network monitoring confirms that, with no destinations configured, no external database selected, and no schedule due, no outbound connection occurs during idle or reporting; during an audit, connections go only to crawl targets (or the configured proxy); destination transfers occur only to user-configured endpoints; external-database traffic occurs only to the user-configured server.
4. A fresh audit with no overrides runs with unlimited total and per-domain page ceilings; throughput and concurrency follow local defaults.
5. No license, account, or entitlement concept appears in any API payload, settings row, or UI screen.
6. Pause, resume, cancel, and crash recovery preserve data; interrupted audits resume from checkpoint.
7. All report surfaces render offline from local aggregates; exports produce valid local files.
8. All features are available without any remote resolution; toggles are local-only.
9. Schema migrations are idempotent and versioned; a settings row with deterministic defaults exists after any fresh install.
10. Single-page analysis returns findings without creating a crawl project.
11. A scheduled audit fires unattended, produces a normal audit and a run-history entry, and survives backend restarts without losing its next-due state.
12. A schedule missed while the machine was off runs exactly once at the next opportunity, recorded as a catch-up run.
13. Spelling findings appear with token-level evidence, honor the page language, and are suppressed by user-dictionary entries.
14. A configured destination receives an export using only user-supplied credentials; disabling all destinations restores zero non-crawl outbound traffic.
15. Content comparison between two audits renders fully offline from the content store; re-audit reuses stored bodies without network access.
16. With the MCP interface enabled, it completes the standard protocol handshake over both the loopback SSE and stdio transports, rejects invalid tokens, and is unreachable from non-loopback addresses; token rotation invalidates prior clients.
17. Stored credentials (site, destination, proxy, external database, automation token) are encrypted at rest and never derived from hardware or OS identity.
18. Configuring a user-owned external client-server database migrates the schema and runs a full audit with identical report output; reverting to the default file-based provider works via archive import; the connection-test action reports reachability and credential validity before first use.
19. The backend runs under the mandated server garbage-collection profile, verifiable through the version/diagnostics surface.
20. The local metrics endpoint serves the standardized exposition format on loopback only, with no identity dimensions; no metrics are pushed or transmitted anywhere.
21. An abnormal backend termination produces a local native minidump; no diagnostic artifact is ever transmitted automatically; the user can view and purge artifacts from the settings surface.
22. Crawl traffic honors the configured proxy routing; with the local intercepting proxy enabled, rendered traffic is captured for the network waterfall and no non-loopback listener is opened.
23. A local agent completes a delegated flow — start an audit, poll progress, detect completion, trigger a re-audit, and read high-severity findings — using only MCP tools, resources, and prompts, with every invocation recorded in the local action log.
24. With custom extraction enabled, selector and pattern rules produce typed values honoring path scoping, per-page value caps, and length caps; the explorer and exports show dynamic per-rule columns; re-audit with changed rules re-extracts from stored bodies without network access; extraction failures never fail the audit.
25. With accessibility enabled, rule violations are emitted with element locators and remediation, aggregate into the accessibility surface, and contribute to scoring; checks requiring rendering are flagged when rendering is off.
26. With rendering and performance enabled, lab Core Web Vitals metrics are captured per page, user-defined budgets emit exceedance findings, and code-coverage and composition metrics are reported; all run offline against the crawl target only.
27. Structured data is parsed from JSON-LD and Microdata, validated for required properties, and reported for rich-result eligibility with a structured error taxonomy.
28. Readability, reading-time, and text-to-HTML metrics are computed from visible text; soft-404 pages returning success status with error-like content are flagged distinctly from hard errors.
29. Technology detection matches only the bundled fingerprint catalogue, produces per-URL detections with confidence, and performs no remote catalogue fetch.
30. Custom search items honor positive and negative terms and path scoping, record per-URL match counts and snippets, and re-run on re-audit from stored bodies without network access.
31. Custom JavaScript snippets execute within their timeout and isolated wrapper, capture typed results as dynamic columns, and a snippet failure on one page never fails the audit or other pages.
32. Audit scores are computed per category and overall, exclude suppressed findings, and are comparable across audits; suppressing a finding or rule removes it from default counts and is reversible.
33. The standalone tools operate without a full audit: robots tester returns allow/disallow verdicts, the sitemap generator emits a standards-conformant sitemap from a filtered URL set, and the query builder composes nested AND/OR filters including dynamic extraction/snippet columns.
34. Signed-request access applies signature headers only to in-scope requests, tracks expiry, and stores the material encrypted at rest; sample-crawl mode, virtual-robots override, and pre-crawl start-URL validation behave as specified without altering limits resolution.

---

## 15. Open Scope Decisions

The following questions are recorded as unresolved and are **not required by this version**; each must be resolved by the product owner and recorded here as an explicit scope statement either way:

1. **Unattended/headless execution** — whether audits can be started from a command line without opening any window. (Scheduled audits per section 6 already run without user interaction once configured, and the local MCP interface per section 11 allows programmatic control while the application is running.)
2. **Manual product-update path** — whether a local, user-initiated update installation mechanism is in scope. No update checking of any kind exists in this version.
3. **Third-party search/analytics/backlink data enrichment** — whether to import the user's own search-console, analytics, or backlink-provider data (and optional field-performance data) into reports using the user's own credentials. These are license-independent, user-owned integrations but reintroduce outbound traffic to third parties, so they are deliberately left as an explicit product-owner choice rather than mandated. If adopted, they follow the optional/off-by-default, user-credential, encrypted-at-rest pattern of section 8.5.
4. **AMP analysis** — whether Accelerated Mobile Pages discovery and validation are in scope. Not required by this version.
5. **Raw-SQL administration tool** — whether a direct query interface over the local database is offered as a power-user/debug tool (the interactive query builder per section 8.7 is already specified). Not required by this version.
