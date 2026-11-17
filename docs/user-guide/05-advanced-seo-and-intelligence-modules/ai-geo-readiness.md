# AI Search (GEO) Readiness

CrawlForge's GEO module answers two questions about your site's readiness for AI-driven search:

1. **Is your `llms.txt` documentation layer present, well-formed, and usable?** At crawl start, CrawlForge probes `/llms.txt` and `/llms-full.txt` on every in-scope host, validates the structure of what it finds, and verifies every link in the file.
2. **Which AI agents are allowed to do what?** A governance matrix evaluates your robots.txt, response headers, and meta directives against a bundled catalogue of search and AI crawler agents, and reports allow/deny/unknown verdicts separately for **search indexing**, **AI RAG citation**, and **LLM training**.

Everything runs locally against the data your crawl already collected. There is no remote `llms.txt` registry, no auto-publishing, and no submission of anything to any AI vendor. Generated files are local exports that you publish yourself, on your own terms.

> **Note:** The governance matrix is a **report, not a gate**. It never alters crawl behavior — CrawlForge's own crawling continues to follow your configured robots compliance and politeness settings regardless of what the matrix shows.

## What gets checked, and when

### Automatic discovery at crawl start

Alongside the robots.txt fetch, CrawlForge probes two paths on **each in-scope host**:

- `/llms.txt`
- `/llms-full.txt`

Both probes are recorded as ordinary URL records (`url_records`) with their HTTP status and content type. That means:

- The files appear in the URL explorer like any other crawled URL.
- Their status, headers, and findings are inspectable in URL detail.
- No separate setup or toggle is needed to trigger the probes — they are part of every crawl's start sequence.

### Structural validation of `llms.txt`

A retrieved `llms.txt` is validated against the convention's expected structure:

| Structural element | Requirement |
|---|---|
| H1 title | Present |
| Blockquote summary | Present |
| H2 sections | Present, containing link lists |

Any structural failure emits `geo.llms-txt-malformed` (**Warning**) with **per-element evidence**, so you can see exactly which element is missing or malformed rather than guessing.

The structural facts are persisted in the URL's element inventory as the `llms_structure` payload: H1/blockquote/section presence, linked-URL count, and broken-link count.

### Link verification

Every link in the file is checked:

- **In scope and robots-allowed** → the link target is enqueued as a discovered URL and crawled normally.
- **Otherwise** → the target is verified for status.
- **Broken or non-200 targets** → `geo.llms-txt-broken-link` (**Warning**) is emitted **per target**, so each dead documentation link is individually actionable.

> **Tip:** Because in-scope links are enqueued into the crawl, a well-linked `llms.txt` also acts as a discovery seed — pages listed there are fetched even if your internal linking would not have reached them quickly.

### Missing files

The `llms.txt` convention is optional, so absence is informational, not an error:

- No `llms.txt` on a host → `geo.llms-txt-missing` (**Notice**).
- `llms.txt` exists but `llms-full.txt` is absent → a **Notice** annotation.

Use these Notices to decide whether adopting the convention is worthwhile for the site — not as defects to "fix" under pressure.

## GEO findings reference

All `geo.*` rule keys live in the additive **AI Search (GEO)** category. Filter the Issue explorer by this category to see the full GEO picture in one list.

| Rule key | Severity | Condition | Evidence |
|---|---|---|---|
| `geo.llms-txt-missing` | Notice | `/llms.txt` absent on an in-scope host (the convention is optional) | Host and probe status |
| `geo.llms-txt-malformed` | Warning | Retrieved `llms.txt` fails structural validation (H1, blockquote summary, H2 link-list sections) | Per-element structural evidence |
| `geo.llms-txt-broken-link` | Warning | A link target in `llms.txt` is broken or returns non-200 | Per-target link and status |
| `geo.governance-conflict` | Notice | Directive sources contradict each other for an agent (e.g. robots.txt allows while `X-Robots-Tag` or meta denies) | Both conflicting evidence values |

An absent `llms-full.txt` while `llms.txt` exists is surfaced as a Notice annotation (no separate rule key).

Like all findings, GEO findings carry a recommendation, a first-seen audit reference for delta tracking, and support project-scoped **suppression**: suppressed findings are excluded from default counts and scoring, remain inspectable in the Suppressed findings view, and the suppression carries across re-audits of the same project.

## The AI bot governance matrix

### The bundled agent catalogue

CrawlForge ships a **bundled, versioned agent-profile catalogue**. Each profile carries an agent token and a purpose class:

| Agent token | Purpose class |
|---|---|
| `Googlebot` | `search-index` |
| `Bingbot` | `search-index` |
| `OAI-SearchBot` | `ai-rag-citation` |
| `PerplexityBot` | `ai-rag-citation` |
| `GPTBot` | `llm-training` |
| `ClaudeBot` | `llm-training` |
| `Google-Extended` | `llm-training` |
| `CCBot` | `llm-training` |
| `Applebot-Extended` | `llm-training` |

The three purpose classes map to the three questions the matrix answers:

- `search-index` — may this agent index the page for **search results**?
- `ai-rag-citation` — may this agent retrieve the page for **AI answer generation with citation** (retrieval-augmented generation)?
- `llm-training` — may this agent use the page for **model training**?

The catalogue is a packaged asset: it ships with the product, is never fetched from a remote service at runtime, and updates only with product updates. The agent-catalogue version in effect is snapshotted into every audit, so historical results remain reproducible.

### What is evaluated per agent

For each agent, the matrix evaluates three directive sources:

1. **robots.txt rules** — parsed with the same parser the crawler obeys (and the same parser used by the standalone Robots tester).
2. **`X-Robots-Tag` response headers** — including agent-specific directives and the `noai` / `noimageai` directives.
3. **HTML meta directives** — `<meta name="robots">` plus agent-specific meta tags from the element inventory.

### How verdicts are computed

The evaluation grid is **agent × path prefix × sampled URLs**:

- Path prefixes are derived from your robots.txt rules and the URL tree.
- For each combination, CrawlForge computes an **allow / deny / unknown** verdict for each of the three purposes: search indexing, AI RAG citation, and LLM training.

`unknown` means the directive sources do not yield a determinate verdict for that agent/purpose/scope combination — treat it as "not explicitly governed", and decide whether you want an explicit policy there.

### Contradictions between sources

When sources disagree — for example, robots.txt allows an agent while `X-Robots-Tag` or a meta tag denies it — CrawlForge emits `geo.governance-conflict` (**Notice**) carrying **both evidence values**. The matrix never silently picks a winner; you see the contradiction and resolve it in your own configuration.

> **Warning:** Conflicting directives are genuinely risky in production: different consumers may honor different sources. Treat every `geo.governance-conflict` as a decision point, not noise.

### Reading the matrix grid

The site-wide matrix surface presents:

- **Rows** — path prefixes and URLs.
- **Columns** — agents from the catalogue.
- **Purpose toggles** — switch the grid between indexing, RAG, and training views.
- **Conflict badges** — flag cells where directive sources contradict.

The grid is virtualized: only visible rows (plus a small overscan buffer) are ever mounted — at most 50 DOM rows — with server-side paging (default page size 200), so the matrix stays smooth even on sites with hundreds of thousands of URLs. Sorting and filtering execute server-side.

## The GEO Readiness report surface

The **GEO Readiness** surface is a dedicated report, additive to the standard report catalog. It contains:

- **`llms.txt` / `llms-full.txt` status cards per host** — file status, structural evidence, and a broken-link drill-down.
- **The governance matrix grid** — agent columns, purpose toggles, conflict badges, virtualized for scale.

The dashboard also carries GEO metric cards backed by precomputed aggregates:

| Metric key | Dimension | What it summarizes |
|---|---|---|
| `geo-readiness` | check | Readiness results per GEO check |
| `governance-coverage` | — | Coverage of the governance evaluation |

GEO findings stream into the UI over the existing `findings-updated` and `url-batch` hub events during a crawl — there is no separate GEO progress channel to watch.

## Step-by-step: interpreting GEO readiness after an audit

1. **Run a normal audit.** The `/llms.txt` and `/llms-full.txt` probes happen automatically at crawl start on every in-scope host. Make sure the hosts you care about are inside the audit's scope.
2. **Open the GEO Readiness surface** and start with the per-host status cards. Note which hosts serve `llms.txt`, which serve `llms-full.txt`, and the HTTP status of each.
3. **Triage missing files first.** `geo.llms-txt-missing` is a Notice — the convention is optional. Decide per host whether adopting `llms.txt` fits the site's AI-search strategy before investing in fixes.
4. **Fix structural failures.** For each `geo.llms-txt-malformed` Warning, read the per-element evidence: is the H1 title missing, the blockquote summary absent, or are H2 sections missing their link lists? Correct the file accordingly.
5. **Drill into broken links.** Expand the broken-link drill-down on the host card. Each `geo.llms-txt-broken-link` Warning names one non-200 or broken target. Update or remove the link, and remember that in-scope targets were also enqueued into the crawl — check their URL records for the full status picture.
6. **Switch to the governance matrix.** Choose a purpose toggle — start with **indexing**, then **RAG**, then **training** — and scan the agent columns for deny/unknown verdicts on prefixes that matter to the business.
7. **Investigate `unknown` verdicts.** These are scopes where your directives do not determinately govern an agent. Decide whether an explicit allow or deny is warranted, especially for `llm-training` agents on proprietary content.
8. **Resolve every conflict badge.** Open the `geo.governance-conflict` evidence, compare the two conflicting values (robots vs header vs meta), and align your sources on the policy you actually want.
9. **Apply changes on the site, then verify.** Edit robots.txt, `X-Robots-Tag` headers, or meta tags on your server. Then run a **new audit** — or a re-audit **with the refresh flag** — so the changed directives are actually re-fetched; a plain re-audit recomputes findings from stored data without re-fetching.
10. **Compare audits.** Use audit comparison to confirm the expected finding deltas (resolved conflicts, fixed links) between the old and new runs.
11. **Suppress what you accept.** If a Notice reflects a deliberate policy choice (e.g. you intentionally do not publish `llms.txt`), suppress the finding or rule at project scope. Suppressions are reversible and excluded from default counts and scoring.

> **Tip:** Work purpose-by-purpose, not agent-by-agent. Most policy decisions are "we want citation traffic but not training traffic" — the purpose toggles let you audit that exact stance across all agents at once.

## The `llms.txt` Generator & Validator tool

The standalone tools window includes an **`llms.txt` Generator & Validator** — it works without requiring you to re-crawl, and it never publishes anything.

### Generating from an audit

The generator builds a draft from the audit's **top-scoring indexable pages**, under this ranking contract:

- Pages must be **indexable**.
- Pages must have **no Error findings**.
- Pages are ordered by **descending content-quality composite**, built from word count, readability in range, and title/description presence. (The exact composite weighting is an implementation choice.)
- Selected pages are **grouped by directory/category**.

The generated file follows the convention's structure:

| Section | Content |
|---|---|
| H1 | Site name |
| Blockquote | Summary — the site meta description or your own input |
| H2 sections | One per group, each with a markdown link list (page title + absolute URL) |

An optional **`llms-full.txt` variant** adds bounded content excerpts to the output.

Generation inputs (REST: `POST /api/tools/llms-generate`):

| Input | Notes |
|---|---|
| Audit reference | The audit whose pages feed the ranking |
| Grouping options | How pages are grouped into H2 sections |
| Site metadata | Site name and summary text (meta description or user input) |
| Full variant flag | Also produce the `llms-full.txt` variant with bounded excerpts |

### Validating a draft

The validator accepts a **pasted/uploaded body or a URL** and returns:

- The **structural verdict** under the same rules used during crawls (H1, blockquote summary, H2 link-list sections).
- **Link-check results** for the links in the body.

Use it to iterate on a draft before it ever reaches your server, or to check a competitor's or client's existing file.

### Preview and export boundary

- The tool shows a **preview before export**.
- Output is a **local file export** only.
- Nothing is ever auto-published, and nothing is submitted to any vendor — there is no remote `llms.txt` registry and no AI-vendor submission endpoint in the product.

> **Note:** Publishing the generated file to your web root is your action, outside CrawlForge. After publishing, re-crawl (or re-audit with refresh) to validate the live file and its links.

## REST and MCP reference

All endpoints are served on the loopback interface only, with JSON bodies and the standard response envelope (data payload, optional pagination metadata, optional error object). List endpoints use offset/limit pagination with filtering and sorting.

| Operation | Method & path | Input | Output |
|---|---|---|---|
| llms.txt results | `GET /api/audits/{id}/geo/llms-txt` | Host filter | Per-host file status, structural verdict, linked/broken counts |
| Governance matrix | `GET /api/audits/{id}/geo/governance` | Agent, purpose, scope filters; pagination | Matrix rows with verdicts and evidence |
| Generate llms.txt | `POST /api/tools/llms-generate` | Audit reference; grouping options; site metadata; full variant flag | Generated file (local export) |
| Validate llms.txt | `POST /api/tools/llms-validate` | Markup body or URL | Structural verdict + link-check results |

GEO findings also appear in the generic findings list (`GET /api/audits/{id}/findings`, filterable by category **AI Search (GEO)** or by rule key), and per-URL structural facts appear in URL detail (`GET /api/audits/{id}/urls/{urlId}`).

### MCP access for local agents

If you enable the optional local MCP interface (off by default; loopback/stdio transports only), the read-only **`geo-readiness`** resource exposes llms.txt validation results plus a governance summary, addressed by audit reference with an optional agent filter.

The MCP boundary holds: **tool invocation and configuration are not exposed via MCP** — generation, validation, and settings changes stay in the UI/REST surface.

## Where the data lives, and retention

| Object | Content | Lifecycle |
|---|---|---|
| `url_elements` → `llms_structure` | H1/blockquote/section presence, linked-URL count, broken count (JSON payload) | Pruned with audit retention |
| `governance_matrix` | Audit reference; agent key; scope kind (`prefix` \| `url`); scope value; purpose (`search-index` \| `ai-rag-citation` \| `llm-training`); verdict; evidence JSON | Pruned with audit retention |
| `audits` snapshot | Agent-catalogue version; GEO toggles in effect at audit time | Snapshot — later catalogue changes never mutate history |
| `metric_summaries` | `geo-readiness` (dimension: check); `governance-coverage` | Dashboard card aggregates |

Retention is user-configurable per project (keep the last N audits, or an age limit); pruning removes dependent rows transactionally. Nothing in this module leaves your machine.

## Related guides

- [MarTech & Consent Mode Auditing](martech-and-consent-mode-auditing.md) — the sibling tag-integrity module for analytics and consent auditing.
- [Offline Server Log Analyzer](offline-server-log-analyzer.md) — verify which AI and search bots actually hit your site, using the same purpose vocabulary.
- [WAF & Soft-Block Detection](waf-and-soft-block-detection.md) — keep challenge pages from polluting the crawl data GEO checks rely on.
- [Semantic Internal Linking](semantic-internal-linking.md) — strengthen the internal linking around the pages you feature in `llms.txt`.
- [N-Grams & Topical Density](n-grams-and-topical-density.md) — assess the content quality of the pages the generator ranks.
- [Dashboards & Virtualized Explorers](../04-exploring-reports-and-visualizations/dashboards-and-virtualized-explorers.md) — how report surfaces, filters, and virtualized grids work.

[← Documentation portal](../README.md)
