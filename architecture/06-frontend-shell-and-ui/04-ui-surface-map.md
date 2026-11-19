# UI Surface Map and Capability Bindings

> **CrawlForge architecture v2.0 — area of concern: `06-frontend-shell-and-ui`.**
> Added by change record CR-018 (decision D-018: every feature is bound to a reachable place in the UI). Not derived from the baseline documents.

| Source | Version | Sections |
|---|---|---|
| CR-018 (`changes/CR-018.md`) | 2026-11-17 | complete file |

---

### 1. Surface catalogue
> Decisions: D-018 · Change records: CR-018

Every screen and panel of the product, with its navigation path. A surface not listed here does not exist; a capability may only be bound to listed surfaces. Every surface must be reachable from the main hub through its stated path.

| ID | Surface | Navigation path |
|---|---|---|
| UI-01 | Main hub › Projects | Shell start screen (first run proceeds to New project) |
| UI-02 | Main hub › Project overview | Main hub › Projects › select project |
| UI-03 | Project › Configurations | Project overview › Configurations |
| UI-04 | Configuration editor | Project › Configurations › open/new configuration |
| UI-05 | Project › Integrations | Project overview › Integrations |
| UI-06 | Project › Segments | Project overview › Segments (also segment picker in every report surface) |
| UI-07 | Project › Audit history and retention | Project overview › Audits |
| UI-08 | Main hub › Clients | Main hub › Clients |
| UI-09 | Main hub › Schedules | Main hub › Schedules |
| UI-10 | Audit run view | Project overview › Start audit, or Audits › running audit |
| UI-11 | Report › Dashboard | Audits › open audit (report window opens on Dashboard) |
| UI-12 | Report › Issue explorer | Report window navigation |
| UI-13 | Report › URL explorer and URL detail | Report window navigation; drill-down from any surface |
| UI-14 | Visualization window | Report window › Visualizations |
| UI-15 | Report › Comparison (audit and content) | Report window › Compare, or Project › Audits › select two |
| UI-16 | Report › Specialist surfaces | Report window navigation: Custom extraction, Custom search, Snippet results, Accessibility, Performance, Structured data, Technology, Rendered comparison, Scores, Suppressed findings, Templates, N-grams, Semantic linking, AI search readiness, Analytics and tagging, Link equity, List mode and redirect matrix, WAF, Search performance |
| UI-17 | Log file analyzer | Project overview › Logs |
| UI-18 | Single-page analyzer | Main hub toolbar › Analyze a page |
| UI-19 | Standalone tools | Main hub toolbar › Tools: robots tester, URL checker, structured-data checker, render tester, sitemap generator, query builder, llms.txt generator |
| UI-20 | Export dialog | Report window › Export (every surface) |
| UI-21 | Settings | Main hub toolbar › Settings: database, export destinations, proxy, browser, user dictionary, integrations credentials, configuration templates, MCP automation, diagnostics, language, release check |
| UI-22 | Plugin manager | Settings › Plugins |
| UI-23 | Status bar | Always visible in every window |
| UI-24 | Recovery screen | Shown automatically after repeated backend failures |

The configuration editor (UI-04) has these tabs: Scope; Crawl as and capture; Limits; Rules; Directives; Resources and external scope; Authentication and cookies; Click discovery and site types; Consent states; Analysis categories; Thresholds; Extraction and search; Advanced.

**Undelivered features (added by CR-022, D-019):** tabs and controls whose engine is not yet delivered render **disabled**, with a short note "Available in a later build" naming the feature. They are never hidden and never functional stubs. A disabled control does not satisfy a UI binding; the binding is met only when the control works end to end.

### 2. Capability bindings
> Decisions: D-018 · Change records: CR-018

- Every in-scope capability has a binding with three columns: **Configure** (where it is switched on or set up), **Trigger** (how it is run) and **View** (where its results appear), each naming surfaces from §1 or `-` where not applicable.
- A capability may be **exempt** only when it is delivered solely through the local API or MCP; exempt capabilities are listed with that reason, and their invocations are visible in the MCP action log (UI-21).
- The binding table is part of this file and is maintained through change records like any other specification text.
- Every view binding must reach the actual data: a surface that renders a placeholder or empty frame does not satisfy a binding.

### 3. Verification
> Decisions: D-018 · Change records: CR-018

- `check_traceability.py` fails when an in-scope capability has no binding and no exemption, when a binding names a surface not in §1, or when a surface in §1 is bound by no capability (dead surface).
- **Acceptance (amends `03-fixture-site.md` §2):** for every capability with a UI binding, end-to-end acceptance includes reaching it through its navigation path in the running product (configure, trigger and view as bound). A capability whose bound surface is not reachable is not complete, whatever its backend tests show.

### 4. Binding table
> Decisions: D-018 · Change records: CR-018

| Capability | Title | Configure | Trigger | View |
|---|---|---|---|---|
| CAP-A-001 | HTTP and HTTPS crawling | UI-04 Limits / Advanced | UI-10 | UI-13 |
| CAP-A-002 | HTML link discovery | UI-04 Directives | UI-10 | UI-13, UI-14 |
| CAP-A-003 | Internal and external link analysis | UI-04 Directives | UI-10 | UI-13, UI-14 |
| CAP-A-004 | JavaScript-rendered crawling (Chromium) | UI-04 Crawl as and capture | UI-10 | UI-13 rendered tab, UI-19 render tester |
| CAP-A-005 | Configurable user agents, including Googlebot simulation | UI-04 Crawl as and capture; UI-04 Limits; UI-04 | UI-10 | UI-10; UI-10 pre-crawl validation, UI-13 |
| CAP-A-006 | robots.txt handling | UI-04 Crawl as and capture; UI-04 Limits | UI-10 | UI-10 |
| CAP-A-007 | Meta robots handling | UI-04 Analysis categories and thresholds | UI-10 | UI-13 element inventory; UI-12, UI-13, UI-11 |
| CAP-A-008 | XML sitemap discovery and crawling | UI-04 Directives | UI-10 | UI-13, UI-14 |
| CAP-A-009 | Canonical URL detection | - | UI-10 | UI-13 element inventory |
| CAP-A-010 | Redirect chain detection | UI-04 Limits / Advanced | UI-10 | UI-13 |
| CAP-A-011 | Redirect loop detection | UI-04 Limits / Advanced | UI-10 | UI-13 |
| CAP-A-012 | Query parameter handling | UI-04 Rules | UI-10 | UI-13, UI-12 |
| CAP-A-013 | URL normalisation | UI-04 Rules | UI-10 | UI-13, UI-12 |
| CAP-A-014 | Pagination handling | UI-04 Directives | UI-10 | UI-13, UI-14 |
| CAP-A-015 | Infinite-scroll handling | UI-04 Crawl as and capture | UI-10 | UI-13 rendered tab, UI-19 render tester |
| CAP-A-016 | Crawl depth limit | UI-04 Limits | UI-10 | UI-10 |
| CAP-A-017 | URL count limit | UI-04 Limits | UI-10 | UI-10 |
| CAP-A-018 | Crawl time limit | UI-04 Limits | UI-10 | UI-10 |
| CAP-A-019 | Configurable concurrency | UI-04 Limits | UI-10 | UI-10 |
| CAP-A-020 | Configurable request rate | UI-04 Limits | UI-10 | UI-10 |
| CAP-A-021 | Authentication support | UI-04 | UI-10 | UI-10 pre-crawl validation, UI-13 |
| CAP-A-022 | Cookie support | UI-04 | UI-10 | UI-10 pre-crawl validation, UI-13 |
| CAP-A-023 | Custom request headers | UI-04 | UI-10 | UI-10 pre-crawl validation, UI-13 |
| CAP-A-024 | Proxy support | UI-21 Proxy; UI-04 Advanced | UI-10 | UI-13 |
| CAP-A-025 | Asset discovery: CSS | UI-04 Limits / Advanced | UI-10 | UI-13 |
| CAP-A-026 | Asset discovery: JS | UI-04 Limits / Advanced | UI-10 | UI-13 |
| CAP-A-027 | Asset discovery: images | UI-04 Limits / Advanced | UI-10 | UI-13 |
| CAP-A-028 | Asset discovery: fonts | UI-04 Limits / Advanced | UI-10 | UI-13 |
| CAP-A-029 | Asset discovery: PDFs and other documents | UI-04 Limits / Advanced | UI-10 | UI-13 |
| CAP-A-030 | Crawl inclusion rules | UI-04 | UI-10 | UI-10 pre-crawl validation, UI-13 |
| CAP-A-031 | Crawl exclusion rules | UI-04 | UI-10 | UI-10 pre-crawl validation, UI-13 |
| CAP-A-032 | Crawl pause | - | UI-10 controls; UI-02 audit list | UI-10 |
| CAP-A-033 | Crawl resume | - | UI-10 controls; UI-02 audit list | UI-10 |
| CAP-A-034 | Interruption recovery | - | UI-10 resume; UI-10 controls; UI-02 audit list | UI-24, UI-10; UI-10 |
| CAP-A-035 | Original HTML response vs rendered DOM comparison (what the server delivered and what JavaScript changed) | UI-04 Crawl as and capture | UI-10 | UI-16 Rendered comparison, UI-13 diff |
| CAP-A-X01 | AIMD adaptive per-host throttle | UI-04 Limits (adaptive throttle, manual pin) | UI-10 | UI-10 host throttle panel |
| CAP-A-X02 | Pre-crawl start-URL validation | UI-04 | UI-10 | UI-10 pre-crawl validation, UI-13 |
| CAP-A-X03 | Loopback intercepting proxy (network capture) | UI-21 Proxy; UI-04 Advanced | UI-10 | UI-13 |
| CAP-A-X04 | Stealth rendering profile | UI-04 Crawl as and capture | UI-10 | UI-13 rendered tab, UI-19 render tester |
| CAP-A-X05 | List Mode (URL-list seeds) | UI-04 Scope (URL list) | UI-10 | UI-16 List mode |
| CAP-A-X06 | URL rewrite and host-mapping rules | UI-04 Rules; UI-21 Diagnostics | UI-10 | UI-13; UI-21 Diagnostics |
| CAP-A-X07 | Sample-crawl mode | UI-04 | UI-10 | UI-10 pre-crawl validation, UI-13 |
| CAP-A-X08 | GSC/GA4 export import as crawl source | UI-05 CSV import | UI-05 | UI-16 Search performance |
| CAP-A-X09 | Capture modes (non-JS / JS / dual / adaptive) | UI-04 Crawl as and capture | UI-10 | UI-13 capture source, UI-16 Rendered comparison |
| CAP-A-X10 | Direct CDP rendering layer | UI-04 Advanced (rendering) | UI-10 | UI-13 network waterfall and console |
| CAP-A-X11 | Pinned bundled browser | UI-21 Browser | UI-10 | UI-10 and UI-11 browser version |
| CAP-A-X12 | Per-audit cookie jar | UI-04 Authentication and cookies | UI-10 | UI-13 |
| CAP-A-X13 | Crawler-trap detection | UI-04 Advanced (trap thresholds) | UI-10 | UI-10 live alert, UI-12 |
| CAP-A-X14 | User-agent comparison | UI-04 Advanced (UA comparison) | UI-10 | UI-12, UI-13 |
| CAP-A-X15 | Postback navigation detection | UI-04 Click discovery and site types | UI-10 | UI-12 |
| CAP-A-X16 | Hash-route handling | UI-04 Click discovery and site types | UI-10 | UI-12 |
| CAP-A-X17 | Non-crawlable navigation inventory | UI-04 Click discovery and site types | UI-10 | UI-12 non-crawlable navigation, UI-13 discovered by click |
| CAP-A-X18 | Service-worker bypass and detection | UI-04 Click discovery and site types | UI-10 | UI-12 |
| CAP-A-X19 | Consent-state rendering | UI-04 Consent states | UI-10 | UI-16 Analytics and tagging |
| CAP-A-X20 | Custom JS snippets during rendering | UI-04 Extraction and search (snippets, test mode) | UI-10 | UI-16 Snippet results |
| CAP-A-X21 | Directive-handling toggles | UI-04 Directives | UI-10 | UI-12, UI-13 |
| CAP-A-X22 | Resource and external scope toggles | UI-04 Resources and external scope | UI-10 | UI-13 |
| CAP-B-036 | Indexability: noindex | UI-04 Analysis categories and thresholds | UI-10 | UI-12, UI-13, UI-11 |
| CAP-B-037 | Indexability: robots restrictions | UI-04 Analysis categories and thresholds | UI-10 | UI-12, UI-13, UI-11 |
| CAP-B-038 | Indexability: canonical conflicts | UI-04 Analysis categories and thresholds | UI-10 | UI-12, UI-13, UI-11; UI-12, UI-13 |
| CAP-B-039 | Metadata: missing titles | UI-04 Analysis categories and thresholds | UI-10 | UI-12, UI-13, UI-11 |
| CAP-B-040 | Metadata: duplicate titles | UI-04 Analysis categories and thresholds | UI-10 | UI-12, UI-13, UI-11 |
| CAP-B-041 | Metadata: missing descriptions | UI-04 Analysis categories and thresholds | UI-10 | UI-12, UI-13, UI-11 |
| CAP-B-042 | Metadata: duplicate descriptions | UI-04 Analysis categories and thresholds | UI-10 | UI-12, UI-13, UI-11 |
| CAP-B-043 | Headings: H1/H2 hierarchy | UI-04 Analysis categories and thresholds | UI-10 | UI-12, UI-13, UI-11 |
| CAP-B-044 | Headings: missing headings | UI-04 Analysis categories and thresholds | UI-10 | UI-12, UI-13, UI-11 |
| CAP-B-045 | Headings: duplicated headings | UI-04 Analysis categories and thresholds | UI-10 | UI-12, UI-13, UI-11 |
| CAP-B-046 | Links: broken internal links | UI-04 Analysis categories and thresholds | UI-10 | UI-12, UI-13, UI-11 |
| CAP-B-047 | Links: broken external links | UI-04 Analysis categories and thresholds | UI-10 | UI-12, UI-13, UI-11 |
| CAP-B-048 | Links: orphan pages | UI-04 Analysis categories and thresholds | UI-10 | UI-11, UI-16 Scores, UI-14; UI-12, UI-13, UI-11 |
| CAP-B-049 | Redirects: 3xx chains | UI-04 Analysis categories and thresholds | UI-10 | UI-12, UI-13, UI-11 |
| CAP-B-050 | Redirects: loops | UI-04 Analysis categories and thresholds | UI-10 | UI-12, UI-13, UI-11 |
| CAP-B-051 | Redirects: inappropriate redirects | UI-04 Analysis categories and thresholds | UI-10 | UI-12, UI-13, UI-11 |
| CAP-B-052 | Sitemaps: missing URLs | UI-04 Analysis categories and thresholds | UI-10 | UI-12, UI-13, UI-11 |
| CAP-B-053 | Sitemaps: non-indexable URLs listed | UI-04 Analysis categories and thresholds | UI-10 | UI-12, UI-13, UI-11 |
| CAP-B-054 | Sitemaps: redirecting URLs listed | UI-04 Analysis categories and thresholds | UI-10 | UI-12, UI-13, UI-11 |
| CAP-B-055 | Images: missing alt text | UI-04 Analysis categories and thresholds | UI-10 | UI-12, UI-13, UI-11 |
| CAP-B-056 | Images: oversized images | UI-04 Analysis categories and thresholds | UI-10 | UI-12, UI-13, UI-11 |
| CAP-B-057 | Images: format checks | UI-04 Analysis categories and thresholds | UI-10 | UI-12, UI-13, UI-11 |
| CAP-B-058 | International: hreflang validation | UI-04 Analysis categories and thresholds | UI-10 | UI-12, UI-13, UI-11 |
| CAP-B-059 | International: language declarations | UI-04 Analysis categories and thresholds | UI-10 | UI-12, UI-13, UI-11 |
| CAP-B-060 | International: reciprocal hreflang references | UI-04 Analysis categories and thresholds | UI-10 | UI-12, UI-13, UI-11 |
| CAP-B-061 | Content: thin content | UI-04 Analysis categories and thresholds | UI-10 | UI-12, UI-13, UI-11 |
| CAP-B-062 | Content: duplicate content | UI-04 Analysis categories and thresholds | UI-10 | UI-12, UI-13, UI-11 |
| CAP-B-063 | Content: near-duplicate content | UI-04 Analysis categories and thresholds; UI-04 Thresholds; UI-12 suppress | UI-10 | UI-12, UI-13, UI-11; UI-12, UI-16 Suppressed findings |
| CAP-B-064 | URL structure: parameters | UI-04 Rules | UI-10 | UI-13, UI-12 |
| CAP-B-065 | URL structure: inconsistent trailing slashes | UI-04 Rules | UI-10 | UI-13, UI-12 |
| CAP-B-066 | URL structure: duplicate URLs | UI-04 Rules | UI-10 | UI-13, UI-12 |
| CAP-B-067 | Structured data: Schema.org validation | UI-04 Analysis categories | UI-10; UI-19 structured-data checker | UI-16 Structured data |
| CAP-B-068 | Structured data: Google rich-result eligibility | UI-04 Analysis categories | UI-10; UI-19 structured-data checker | UI-16 Structured data |
| CAP-B-X01 | Redirect migration matrix | - | - | UI-23, UI-10 |
| CAP-B-X02 | Header/HTML directive equivalency | - | UI-10 | UI-12, UI-13 |
| CAP-B-X03 | Soft-404 detection | UI-04 Analysis categories and thresholds | UI-10 | UI-12, UI-13 |
| CAP-B-X04 | SERP pixel measurement | - | UI-10 | UI-13, UI-12 |
| CAP-B-X05 | Client-side redirect detection | UI-04 Click discovery and site types | UI-10 | UI-12 |
| CAP-B2-069 | Extract JSON-LD | UI-04 Analysis categories | UI-10; UI-19 structured-data checker | UI-16 Structured data |
| CAP-B2-070 | Extract Microdata | UI-04 Analysis categories | UI-10; UI-19 structured-data checker | UI-16 Structured data |
| CAP-B2-071 | Extract RDFa | UI-04 Analysis categories | UI-10; UI-19 structured-data checker | UI-16 Structured data |
| CAP-B2-072 | Parse multiple entities per page | UI-04 Analysis categories | UI-10; UI-19 structured-data checker | UI-16 Structured data |
| CAP-B2-073 | Identify entity types and relationships | UI-04 Analysis categories | UI-10; UI-19 structured-data checker | UI-16 Structured data |
| CAP-B2-074 | Validate properties against Schema.org | UI-04 Analysis categories | UI-10; UI-19 structured-data checker | UI-16 Structured data |
| CAP-B2-075 | Identify missing required properties | UI-04 Analysis categories | UI-10; UI-19 structured-data checker | UI-16 Structured data |
| CAP-B2-076 | Identify missing recommended properties | UI-04 Analysis categories | UI-10; UI-19 structured-data checker | UI-16 Structured data |
| CAP-B2-080 | Group errors by template and entity type | - | UI-10 | UI-16 Templates; UI-12 group by template |
| CAP-C-081 | Lighthouse results via PageSpeed Insights | UI-05 toggles | UI-05 sync; UI-13 URL Inspection for selected URLs | UI-16 Search performance |
| CAP-C-082 | Core Web Vitals: LCP | UI-04 Analysis categories (performance budgets) | UI-10 | UI-16 Performance |
| CAP-C-083 | Core Web Vitals: INP | UI-05 toggles | UI-05 sync; UI-13 URL Inspection for selected URLs | UI-16 Search performance |
| CAP-C-084 | Core Web Vitals: CLS | UI-04 Analysis categories (performance budgets) | UI-10 | UI-16 Performance |
| CAP-C-085 | Time to First Byte | UI-04 Limits / Advanced; UI-04 Analysis categories (performance budgets) | UI-10 | UI-13; UI-16 Performance |
| CAP-C-086 | Resource loading waterfalls | UI-04 Crawl as and capture | UI-10 | UI-13 rendered tab, UI-19 render tester |
| CAP-C-087 | Large image detection | UI-04 Analysis categories and thresholds | UI-10 | UI-12, UI-13, UI-11 |
| CAP-C-088 | Unused CSS | UI-04 Analysis categories (performance budgets) | UI-10 | UI-16 Performance |
| CAP-C-089 | Unused JavaScript | UI-04 Analysis categories (performance budgets) | UI-10 | UI-16 Performance |
| CAP-C-090 | Render-blocking resources | UI-04 Analysis categories and thresholds | UI-10 | UI-12, UI-13, UI-11 |
| CAP-C-091 | Compression checks | UI-04 Analysis categories and thresholds | UI-10 | UI-12, UI-13, UI-11 |
| CAP-C-092 | Caching checks | UI-04 Analysis categories and thresholds | UI-10 | UI-12, UI-13, UI-11 |
| CAP-C-093 | Mobile viewport checks | UI-04 Analysis categories | UI-10 | UI-12 |
| CAP-C-094 | Responsive checks | UI-04 Analysis categories | UI-10 | UI-12 |
| CAP-C-095 | Page weight | UI-04 Analysis categories and thresholds | UI-10 | UI-12, UI-13, UI-11 |
| CAP-C-096 | Resource counts | UI-04 Analysis categories and thresholds | UI-10 | UI-12, UI-13, UI-11 |
| CAP-C-097 | Slow server response detection | UI-04 Analysis categories and thresholds | UI-10 | UI-12, UI-13, UI-11 |
| CAP-C-098 | Performance results at URL level | UI-04 Analysis categories | UI-10 | UI-16 Performance |
| CAP-C-099 | Performance results at template level | - | UI-10 | UI-16 Templates; UI-12 group by template |
| CAP-C-X01 | Mobile-friendliness rule set | UI-04 Analysis categories | UI-10 | UI-12 |
| CAP-C-X02 | Performance budgets | UI-04 Analysis categories (performance budgets) | UI-10 | UI-16 Performance |
| CAP-C-X03 | Local lab rendering probe | UI-04 Analysis categories (performance budgets) | UI-10 | UI-16 Performance |
| CAP-C-X04 | Page composition metrics | UI-04 Analysis categories (performance budgets) | UI-10 | UI-16 Performance |
| CAP-C-X05 | ViewState weight measurement | UI-04 Click discovery and site types | UI-10 | UI-12 |
| CAP-D1-100 | WCAG 2.2 AA checks (where automatable) | UI-04 Analysis categories | UI-10 | UI-16 Accessibility |
| CAP-D1-101 | Missing image alternatives | UI-04 Analysis categories | UI-10 | UI-16 Accessibility |
| CAP-D1-102 | Form labels | UI-04 Analysis categories | UI-10 | UI-16 Accessibility |
| CAP-D1-103 | Colour contrast | UI-04 Analysis categories | UI-10 | UI-16 Accessibility |
| CAP-D1-104 | Heading structure | UI-04 Analysis categories | UI-10 | UI-16 Accessibility |
| CAP-D1-105 | Landmark structure | UI-04 Analysis categories | UI-10 | UI-16 Accessibility |
| CAP-D1-107 | Accessible names | UI-04 Analysis categories | UI-10 | UI-16 Accessibility |
| CAP-D1-108 | Link purpose | UI-04 Analysis categories | UI-10 | UI-16 Accessibility |
| CAP-D1-109 | ARIA misuse | UI-04 Analysis categories | UI-10 | UI-16 Accessibility |
| CAP-D1-110 | Distinguish machine-detectable violations from issues needing human assessment | UI-04 Analysis categories | UI-10 | UI-16 Accessibility |
| CAP-D1-X01 | Accessibility rule-set configuration and versioning | UI-04 Analysis categories | UI-10 | UI-16 Accessibility |
| CAP-D2-111 | HTTPS usage | UI-04 Analysis categories and thresholds | UI-10 | UI-12, UI-13, UI-11 |
| CAP-D2-112 | Certificate validity | UI-04 Limits / Advanced | UI-10 | UI-13 |
| CAP-D2-113 | HTTP security headers | UI-04 Analysis categories and thresholds | UI-10 | UI-12, UI-13, UI-11 |
| CAP-D2-114 | Mixed content | UI-04 Analysis categories and thresholds | UI-10 | UI-12, UI-13, UI-11 |
| CAP-D2-115 | Cookie security attributes | UI-04 Analysis categories and thresholds | UI-10 | UI-12, UI-13, UI-11 |
| CAP-D2-116 | Insecure external resources | UI-04 Analysis categories and thresholds | UI-10 | UI-12, UI-13, UI-11 |
| CAP-D2-117 | Information disclosure | UI-04 Analysis categories and thresholds | UI-10 | UI-12, UI-13, UI-11 |
| CAP-D2-118 | Basic TLS configuration checks | UI-04 Limits / Advanced | UI-10 | UI-13 |
| CAP-D2-X01 | WAF detection and fingerprinting | UI-04 Advanced | UI-10 | UI-10 WAF alert, UI-16 WAF |
| CAP-E-119 | Internal link graph | - | UI-10 | UI-14 link graph |
| CAP-E-120 | Click depth | - | UI-10 | UI-11, UI-16 Scores, UI-14 |
| CAP-E-121 | Crawl depth | - | UI-10 | UI-11, UI-16 Scores, UI-14 |
| CAP-E-122 | Orphan page identification | UI-04 Analysis categories and thresholds | UI-10 | UI-11, UI-16 Scores, UI-14; UI-12, UI-13, UI-11 |
| CAP-E-123 | Inlink counts | - | UI-10 | UI-11, UI-16 Scores, UI-14 |
| CAP-E-124 | Outlink counts | - | UI-10 | UI-11, UI-16 Scores, UI-14 |
| CAP-E-125 | Anchor-text analysis | UI-04 Analysis categories and thresholds | UI-10 | UI-12, UI-13, UI-11 |
| CAP-E-126 | Navigation analysis | UI-04 Advanced (zone weights) | UI-10 | UI-16 Link equity, UI-13 |
| CAP-E-127 | Breadcrumb analysis | UI-04 Advanced (zone weights) | UI-10 | UI-16 Link equity, UI-13 |
| CAP-E-128 | Page template identification | - | UI-10 | UI-16 Templates; UI-12 group by template |
| CAP-E-129 | Content clustering | UI-21 Embedding provider; UI-04 Analysis categories | UI-10 | UI-16 Semantic linking |
| CAP-E-130 | URL taxonomy | - | UI-10 | UI-14 site-structure tree |
| CAP-E-132 | Internal PageRank / link equity modelling | UI-04 Advanced (zone weights) | UI-10 | UI-11, UI-16 Scores, UI-14; UI-16 Link equity |
| CAP-E-137 | Primary content sections of the site | - | UI-10 | UI-14 section treemap |
| CAP-E-138 | Site relationship graph (pages, links, entities, templates) with visual navigation | - | UI-10 | UI-14 link network graph |
| CAP-E-X01 | Technology detection | UI-04 Analysis categories | UI-10 | UI-16 Technology |
| CAP-E-X02 | DOM-zone link classification | UI-04 Advanced (zone weights) | UI-10 | UI-16 Link equity, UI-13 |
| CAP-E-X03 | Whole-graph aggregate analytics | - | UI-10 | UI-11, UI-16 Scores, UI-14 |
| CAP-F-139 | Duplicate content detection | UI-04 Analysis categories and thresholds | UI-10 | UI-12, UI-13, UI-11 |
| CAP-F-140 | Near-duplicate content detection | UI-04 Thresholds; UI-12 suppress | UI-10 | UI-12, UI-16 Suppressed findings |
| CAP-F-141 | Word count | - | UI-10 | UI-13 element inventory |
| CAP-F-142 | Text density | UI-04 Analysis categories and thresholds | UI-10 | UI-12, UI-13 |
| CAP-F-143 | N-gram analysis | UI-04 Analysis categories | UI-10 | UI-16 N-grams |
| CAP-F-144 | Keyword and phrase extraction | UI-04 Analysis categories | UI-10 | UI-16 N-grams |
| CAP-F-146 | Topic clustering | UI-21 Embedding provider; UI-04 Analysis categories | UI-10 | UI-16 Semantic linking |
| CAP-F-148 | Thin content detection | UI-04 Analysis categories and thresholds | UI-10 | UI-12, UI-13, UI-11; UI-12, UI-13 |
| CAP-F-149 | Missing content coverage | UI-04 Analysis categories | UI-10 | UI-16 N-grams, UI-12 |
| CAP-F-X01 | Keyword cannibalization and topical-gap findings | UI-04 Analysis categories | UI-10 | UI-16 N-grams, UI-12 |
| CAP-F-X02 | Spelling and readability analysis | UI-21 User dictionary; UI-04 Analysis categories; UI-04 Analysis categories and thresholds | UI-10 | UI-12; UI-12, UI-13 |
| CAP-F-X03 | Sentiment scoring (optional) | UI-04 Analysis categories and thresholds | UI-10 | UI-12, UI-13 |
| CAP-G-153 | Custom extraction: CSS selectors | UI-04 Extraction and search (with preview) | UI-10 | UI-16 Custom extraction |
| CAP-G-154 | Custom extraction: XPath | UI-04 Extraction and search (with preview) | UI-10 | UI-16 Custom extraction |
| CAP-G-155 | Custom extraction: regex | UI-04 Extraction and search (with preview) | UI-10 | UI-16 Custom extraction |
| CAP-G-156 | Extraction output types: inner text, inner HTML, whole element, specific attribute | UI-04 Extraction and search (with preview) | UI-10 | UI-16 Custom extraction |
| CAP-G-157 | Extraction source selectable: raw HTML, rendered DOM, or both | UI-04 Extraction and search (with preview) | UI-10 | UI-16 Custom extraction |
| CAP-G-158 | Multiple matches per page per rule | UI-04 Extraction and search (with preview) | UI-10 | UI-16 Custom extraction |
| CAP-G-159 | Test extraction rule against a single URL before crawling | UI-04 Extraction and search (with preview) | UI-10 | UI-16 Custom extraction |
| CAP-G-160 | Custom search: pages containing a text or regex | UI-04 Extraction and search | UI-10 | UI-16 Custom search |
| CAP-G-161 | Custom search: pages not containing a text or regex | UI-04 Extraction and search | UI-10 | UI-16 Custom search |
| CAP-G-162 | Main content extraction (boilerplate removal of navigation, header, footer) | UI-04 Extraction and search (content area) | UI-10 | UI-13 scoped text |
| CAP-G-163 | Analysis on main content (word count, duplicates, n-grams use extracted main content) | UI-04 Extraction and search (content area) | UI-10 | UI-13 |
| CAP-G-164 | Full page text export (plain text) | UI-20 | UI-20 | UI-20 |
| CAP-G-165 | Full page content export (Markdown) | UI-20 | UI-20 | UI-20 |
| CAP-G-166 | Extracted values usable as filters | UI-06; UI-19 query builder | UI-12 | UI-12 filters |
| CAP-G-167 | Extracted values included in exports | UI-20 | UI-20 | UI-20 |
| CAP-G-X01 | Single-page analysis and render testing tools | - | UI-18; UI-19 render tester | UI-18, UI-19 |
| CAP-H-168 | Interactive dashboard | - | UI-10 | UI-11 |
| CAP-H-169 | Searchable URL inventory | - | UI-10 | UI-13 |
| CAP-H-170 | Advanced filtering | UI-19 query builder | UI-12 | UI-12, UI-13 filters |
| CAP-H-171 | Segmentation | UI-06 | UI-06 | segment picker in UI-11..UI-16 |
| CAP-H-172 | Custom issue rules | UI-04 Rules (custom issue rules) | UI-10 | UI-12 |
| CAP-H-173 | CSV export | UI-20 | UI-20 | UI-20 |
| CAP-H-174 | XLSX export | UI-20 | UI-20 | UI-20 |
| CAP-H-175 | JSON export | UI-20 | UI-20 | UI-20 |
| CAP-H-177 | PDF reports | UI-20 | UI-20 | UI-20 |
| CAP-H-178 | Crawl comparison | - | UI-15 | UI-15 |
| CAP-H-179 | Historical audit storage | UI-07 | UI-10; UI-07 | UI-11, UI-16 Scores, UI-14; UI-07 |
| CAP-H-180 | Issue trend over time (resolved, remaining, newly introduced) | - | UI-15 | UI-15, UI-16 Scores |
| CAP-H-181 | Google Search Console integration | UI-21 Integrations credentials; UI-05; UI-05 toggles | UI-05 sync; UI-05; UI-13 URL Inspection for selected URLs; UI-09; UI-10 | UI-05; UI-16 Search performance; UI-12 orphan candidates, UI-16 Search performance |
| CAP-H-182 | Google Analytics 4 integration | UI-21 Integrations credentials; UI-05; UI-05 toggles | UI-05 sync; UI-05; UI-13 URL Inspection for selected URLs; UI-09; UI-10 | UI-05; UI-16 Search performance; UI-12 orphan candidates, UI-16 Search performance |
| CAP-H-183 | PageSpeed Insights integration | UI-21 Integrations credentials; UI-05; UI-05 toggles | UI-05 sync; UI-05; UI-13 URL Inspection for selected URLs; UI-09; UI-10 | UI-05; UI-16 Search performance; UI-12 orphan candidates, UI-16 Search performance |
| CAP-H-185 | MCP server access | UI-21 MCP automation | - | UI-21 MCP automation |
| CAP-H-188 | Scheduled crawls | UI-09 | UI-09 | UI-09 |
| CAP-H-190 | Multi-project management | UI-01 | UI-01 | UI-01 |
| CAP-H-X02 | Multi-protocol export destinations | UI-21 Export destinations | UI-20 | UI-20 |
| CAP-H-X03 | XML sitemap generation | - | UI-19 sitemap generator | UI-19 |
| CAP-H-X04 | Automation action log | UI-21 MCP automation | - | UI-21 action log |
| CAP-H-X05 | Server log import and correlation | UI-17 | UI-17 import | UI-17 |
| CAP-H-X06 | MarTech and consent coverage analysis | UI-08 client context; UI-04 Analysis categories | UI-10 | UI-16 Analytics and tagging |
| CAP-I-192 | Prioritise findings by severity | UI-04 Thresholds; UI-12 suppress | UI-10 | UI-12, UI-16 Suppressed findings |
| CAP-I-193 | Prioritise by number of affected URLs | - | UI-10 | UI-12 group and sort by affected URLs |
| CAP-I-194 | Prioritise by business importance | UI-04 Thresholds; UI-12 suppress | UI-10 | UI-12, UI-16 Suppressed findings |
| CAP-I-196 | Prioritise by remediation effort | UI-04 Thresholds; UI-12 suppress | UI-10 | UI-12, UI-16 Suppressed findings |
| CAP-I-197 | Template-level issue grouping (shared problems across a template) | - | UI-10 | UI-16 Templates; UI-12 group by template |
| CAP-I-199 | Change detection between crawls: structural changes | - | UI-15 | UI-15 |
| CAP-I-200 | Change detection between crawls: newly introduced problems | - | UI-15 | UI-15 |
| CAP-I-201 | Change detection between crawls: disappearing content | - | UI-15 | UI-15 content comparison |
| CAP-I-202 | Change detection between crawls: deteriorating technical health | - | UI-10 | UI-11, UI-16 Scores, UI-14 |
| CAP-I-X01 | Finding suppression | UI-12 suppress | UI-12 | UI-16 Suppressed findings |
| CAP-I-X02 | Audit scoring and recompute | UI-04 Analysis categories | UI-10 | UI-16 Scores |
| CAP-J-204 | Plain-language explanation of individual findings | - | UI-10 | UI-12 finding detail (recommendation text); MCP |
| CAP-J-207 | Recommendations for interventions | - | UI-10 | UI-12 finding detail (recommendation); MCP |
| CAP-J-208 | Evidence references: every recommendation links to affected URLs and underlying data | - | UI-10 | UI-12 finding detail with affected URLs and evidence; MCP |
| CAP-J-209 | Custom investigations: operator questions about the crawl | UI-21 MCP automation | - | UI-21 action log; MCP |
| CAP-J-213 | Constraint: AI only interprets collected crawl data; never invents findings | - | - | UI-12 every finding shows evidence and source rule; MCP |
| CAP-J-X01 | GEO llms.txt generation and validation | UI-04 Analysis categories | UI-10; UI-19 llms.txt generator | UI-16 AI search readiness; UI-19 |
| CAP-J-X02 | AI-bot governance matrix | UI-04 Analysis categories | UI-10 | UI-16 AI search readiness |
| CAP-K-X01 | Configuration editor | UI-03, UI-04 | - | UI-03, UI-04 |
| CAP-K-X02 | Per-configuration analysis category toggles | UI-04 Analysis categories | - | UI-04 |
| CAP-K-X03 | Per-project issue thresholds | UI-04 Thresholds | - | UI-04 |
| CAP-K-X04 | Client context | UI-08; UI-02 override | - | UI-08 |
| CAP-K-X05 | Per-project retention and storage reporting | UI-07 | UI-07 | UI-07 |
| CAP-L-X01 | Platform security and diagnostics infrastructure | UI-21; UI-21 Diagnostics | - | UI-21 Diagnostics |
| CAP-L-X02 | Project archive export/import and provider switching | UI-07 archive export/import | UI-07 | UI-07 |
| CAP-L-X04 | Local plugin system | UI-22 | UI-22 | UI-22 |
| CAP-L-X05 | Real-time hub events and metrics observability | - | - | UI-23, UI-10, UI-21 Diagnostics |
| CAP-L-X06 | Electron native shell integration | UI-21 | - | all windows |

### 5. Exempt capabilities (MCP or API only)
> Decisions: D-008, D-018 · Change records: CR-018

Delivered through the local API and MCP (AI client), with invocations visible in the MCP action log (UI-21).

| Capability | Title |
|---|---|
| CAP-H-184 | API access |
| CAP-H-186 | Structured JSON output for other systems and agents |
| CAP-J-203 | Website overview (structure, purpose, content, technical architecture) |
| CAP-J-210 | Change analysis between crawls |
| CAP-J-212 | Machine-readable findings export |
