# Offline Server Log Analyzer

The Log File Analyzer imports your server access logs from local disk and correlates bot traffic against your crawl results. It answers three questions a crawl alone cannot:

1. **True orphans** — which URLs do search bots actually request that your crawl *never discovered*?
2. **Crawl-budget waste** — where are bots burning requests on errors, parameter traps, and non-indexable pages?
3. **Uncrawled indexable pages** — which indexable pages in your crawl have search bots *never requested* in the log window?

**Ingestion, parsing, classification, and correlation are 100% offline — zero network calls.** The module works fully air-gapped. The single optional exception is user-triggered DNS verification of bot hits: off by default, disclosed and consented per import, DNS queries only. With it disabled, the log module generates no outbound traffic of any kind.

> **Note:** CrawlForge never pulls logs from an API. Cloudflare logs arrive as **export files you provide**, exactly like Apache, Nginx, and IIS files. There is no Cloudflare API integration, no remote bot-list or IP-reputation service, and no log-enrichment fetch of any kind.

## Supported formats and files

### The four supported log sources

Four log sources are covered, in three format families:

| Format | Sources | Notes |
|---|---|---|
| Apache/Nginx Combined | Apache, Nginx | Both servers share the Combined access-log format |
| IIS W3C | IIS | Header-driven field mapping (the file's own header declares its fields) |
| Cloudflare JSON/CSV export | Cloudflare | User-provided export files — never API pulls |

Format **auto-detection** runs on import, with a **manual override** when you need it. Per-format **field mapping is configurable**, so non-standard layouts can be mapped without editing the files.

### Compression and size

- Files may be **plain or gzip-compressed**.
- Ingestion is **streamed line-by-line with bounded memory** — the file is never loaded wholesale into RAM.
- **File size is bounded only by disk.** Multi-gigabyte monthly logs are normal input, not an edge case.

### Parsing behavior

- **Malformed lines are counted and skipped**, with a **bounded error sample** retained so you can see what was rejected without flooding storage.
- **Timestamps are normalized to UTC.**
- You select the **analysis time window** — the period of traffic the import represents and that correlation views evaluate.
- Imports run as a **background service** with live progress. An import **never blocks or fails a crawl** — you can import logs while an audit is running.

## Bot classification and verification

### The bundled bot catalogue

Classification uses a **bundled, versioned user-agent pattern catalogue** — a packaged asset that ships with the product and updates only with product updates. It defines these families:

- `Googlebot Smartphone`
- `Googlebot Desktop`
- `Bingbot`
- `OAI-SearchBot`
- `GPTBot`
- `ClaudeBot`
- `PerplexityBot`
- `Applebot`

plus three classification buckets: `other-bot`, `human`, and `unknown`.

Each family carries a **purpose class**: `search`, `ai-search`, or `ai-training` — the same vocabulary used by the AI bot governance matrix in the GEO module (note that the log catalogue splits Googlebot into Smartphone/Desktop variants and is a distinct asset from the GEO agent profiles). The correlation views key on **search-bot hits** — traffic from the search-purpose families — because that is the traffic that consumes search crawl budget.

### Verification tiers

Every classified hit carries a verification tier:

| Tier | How it is established | Network use |
|---|---|---|
| `claimed` | User-agent pattern match only | None |
| `cidr-verified` | Offline match of the hit's IP against **bundled published CIDR ranges** for that family | None |
| `dns-verified` | Optional **user-triggered** reverse-then-forward DNS confirmation | DNS queries only, with explicit per-import consent |

- **`claimed`** is the baseline: the user-agent says it is Googlebot, but nothing has corroborated that.
- **`cidr-verified`** is the offline workhorse: the source IP falls inside the published range bundled for the family. Spoofed user-agents from outside those ranges stay at `claimed`.
- **`dns-verified`** is the strongest tier and the only one that can touch the network — and only when you explicitly ask for it.

The import manager shows **verification-tier badges** so the trust level of your bot traffic is always visible.

### CIDR ranges: bundled and user-local

- Bundled CIDR ranges are **packaged assets**, updated only with product updates. They are never fetched remotely at runtime.
- **User-local CIDR additions** persist in application settings (`application_settings`, JSON) — useful for internal crawlers, monitoring services, or ranges you trust that the bundled catalogue does not cover.
- No remote fetch of ranges or reputation data ever occurs.

### DNS verification: the consent model

DNS verification is deliberately hard to trigger by accident:

- **Off by default.** It never runs automatically — not during import, not during correlation.
- An **explicit per-import consent flag** is required.
- The UI action opens a **consent dialog showing exactly what will be queried** before anything leaves the machine.
- The sample is **bounded** — you choose a bounded sample size; verification runs on sampled IPs, not the whole log.
- **DNS queries only**, against the system or user-configured resolver: reverse DNS followed by forward confirmation.
- **Results are logged locally.** Nothing is reported anywhere else.

> **Warning:** DNS verification is the *only* outbound action this module can ever perform. If your engagement requires a strictly air-gapped workflow, simply never trigger it — `claimed` and `cidr-verified` tiers are fully offline and sufficient for most crawl-budget analysis.

## How log data is aggregated

Ingestion produces one aggregate row **per (import, canonical URL)**:

| Aggregate field | Notes |
|---|---|
| Total hits | All requests to the canonical URL in the window |
| Hits by bot family | Per-family breakdown (JSON) |
| Search-bot hits | The search-purpose subset that drives correlation |
| Status-class distribution | Counts per status class (JSON) |
| First / last seen | Timestamp bounds within the window |
| Bytes served | When present in the log format |

### Canonicalization and the query policy

Log URLs are canonicalized with the same rules the crawl engine uses, **under the target audit's query policy**:

- Scheme and host lowercased; default ports removed; fragments discarded.
- Consistent trailing-slash policy; percent-encoding normalized.
- Query parameters sorted and filtered by the audit's policy — **keep all**, **keep a whitelist**, or **strip all** — with known session-tracking parameters stripped.

This is what makes the join between log traffic and crawl records reliable: both sides key on the same canonical URL form.

> **Tip:** Choose the audit's query-parameter policy deliberately before correlating. A "strip all" policy collapses parameter variants into one canonical URL (hiding parameter traps from the join); "keep all" preserves them (making query-variant counts visible). The Crawl Budget Waste view's parameter-trap detection depends on this choice.

Aggregates persist in `log_url_aggregates`, indexed for correlation and virtualized browsing, and are pruned with their import.

## The three correlation views

Correlation joins log aggregates against the crawl's URL records (`url_records`) for a **chosen audit of the project**. Each view is **recomputable on demand against any project audit** — import once, correlate against as many audits as you like (the join is an indexed read-model operation).

### True Orphans

**Definition:** URLs with search-bot hits in the window that were **never discovered** by the crawl — absent from the crawl's seen-set.

These are pages search engines know about but your internal linking and sitemaps do not surface to a crawler. They are "true" orphans because bot traffic proves the URL exists and is being requested — not merely a page your crawl happened to miss within its limits.

- Finding: `logs.true-orphan` — **Warning**.
- Evidence: hit count, bot families, sample timestamps.
- Results are **scope-filtered to project hosts**; out-of-scope URLs are **reported separately**, so cross-host traffic does not pollute the orphan list.

### Crawl Budget Waste

**Definition:** search-bot hit frequency **≥ threshold (default 10)** on:

- **non-200 URLs** (bots repeatedly hitting errors),
- **parameter traps** (high query-variant counts), or
- **non-indexable pages**.

- Finding: `logs.crawl-budget-waste` — **Warning**.
- Evidence: status mix, indexability, variant count.
- The threshold is local configuration; the default is 10 search-bot hits in the window.

### Uncrawled Indexable Pages

**Definition:** indexable URLs **found in the crawl** with **zero** search-bot hits in the window.

Despite the name, these pages *were* crawled by CrawlForge — the point is that search bots never requested them during the log window. They are candidates for discovery or prioritization problems.

- Finding: `logs.uncrawled-indexable` — **Notice**.
- Evidence: crawl depth, inlink count.
- A **window-length caveat is surfaced**: a short log window can understate bot activity, so treat zero-hit results in light of how much traffic the window covers.

### Findings and metrics

All three rule keys live in the additive **Log Intelligence** category; suppression follows the standard project-scoped, reversible finding model (suppressed findings are excluded from default counts and scoring but remain inspectable).

| Rule key | Severity | View |
|---|---|---|
| `logs.true-orphan` | Warning | True Orphans |
| `logs.crawl-budget-waste` | Warning | Crawl Budget Waste |
| `logs.uncrawled-indexable` | Notice | Uncrawled Indexable Pages |

Dashboard cards are backed by precomputed metric summaries:

| Metric key | Dimensions |
|---|---|
| `log-true-orphans` | — |
| `log-budget-waste` | — |
| `log-uncrawled-indexable` | — |
| `log-bot-hits` | family, verification tier |

## The Log File Analyzer surface

The **Log File Analyzer** is a dedicated report surface with:

- **Import manager** — drag-drop or native file dialog, format auto-detect, window picker, verification-tier badges, and live progress display.
- **Correlation tabs** — True Orphans / Budget Waste / Uncrawled Indexable, each virtualized (at most 50 mounted DOM rows; server-side paging, default page size 200), with **evidence drill-down to URL detail**.
- **Bot-family breakdown charts and a hit-timeline histogram** — see which families dominate traffic and when it occurred.
- **DNS verification action** — with the explicit consent dialog showing exactly what will be queried.

## Step-by-step: import and analysis workflow

The workflow is chunked into phases. Each phase is self-contained — you can stop after any phase and resume later; imports and aggregates persist locally.

### Phase 1 — Prepare the log files

1. **Obtain access logs** covering the period you want to analyze: from your web server (Apache/Nginx/IIS) or as a Cloudflare JSON/CSV export. Save them to local disk.
2. **Keep them as-is.** Plain or gzip-compressed files both import directly — no need to decompress, split, or reformat.
3. **Decide the analysis window.** Pick a period that matches your question (e.g. the last 30 days of bot traffic). Timestamps are normalized to UTC on import, so mixed-timezone files are handled consistently.
4. **Have a crawl ready.** Correlation needs an audit of the same project to join against. If you do not have one yet, run a crawl first — or import now and correlate later; imports attach to the **project**, not to a specific audit.

### Phase 2 — Import a log file

1. Open the **Log File Analyzer** surface and its import manager.
2. **Add the file** by drag-drop or the native file dialog (the REST contract also accepts a local path reference).
3. **Confirm the format.** Auto-detection proposes Apache/Nginx Combined, IIS W3C, or Cloudflare JSON/CSV; override manually if needed. For IIS W3C, field mapping is driven by the file's header; per-format field mapping is configurable for non-standard layouts.
4. **Set the analysis time window** with the window picker.
5. **Choose the verification mode.** Leave DNS verification off for a fully offline import — `claimed` and `cidr-verified` classification runs without any network access. DNS verification is a separate, later, explicitly consented action (Phase 6).
6. **Start the import.** You receive an import identifier immediately; the import runs as a background service.
7. **Watch progress.** The import manager streams live counters — lines parsed / accepted / rejected — and the current phase, updated at most once per second. You can keep working, or run a crawl concurrently; the import never blocks or fails it.

### Phase 3 — Verify the import

1. Wait for the import to finalize (status moves from `importing` to `ready`, or `failed`). A completion event delivers the final counters and correlation summary counts.
2. **Check the counters:** lines parsed vs accepted vs rejected. A small rejected count is normal (malformed lines are skipped, not fatal).
3. **Inspect the bounded error sample** if rejection counts look high — typically a format mismatch or a non-standard field layout. Fix the format override or field mapping and re-import.
4. **Review the import record:** file name, format, compression, size, window start/end, verification mode, and imported-at timestamp. Import records are listed per project with status and counters.

### Phase 4 — Correlate against an audit

1. **Choose the audit** of the project to correlate against — usually your most recent complete crawl of the production site.
2. Open the **correlation tabs**. Each view recomputes on demand against the chosen audit; switching audits re-joins the same import without re-importing.
3. Start with the **summary** view for overall counts, then work the three tabs in the order below.
4. Remember the join uses the **audit's query-parameter policy** for canonicalization — if variant-level questions matter, correlate against an audit whose policy preserves query parameters.

### Phase 5 — Interpret and act, view by view

**True Orphans first (Warning):**

1. Sort by hit count — the most-requested undiscovered URLs are the highest-value fixes.
2. Read the evidence: which families request the URL, and sample timestamps confirming sustained traffic.
3. Check the separately reported out-of-scope URLs — they may reveal hosts or sections your crawl scope should cover.
4. Act: add internal links or sitemap entries so the crawl (and search engines) can discover the URL through site structure; then re-crawl and confirm the orphan resolves.

**Crawl Budget Waste second (Warning):**

1. Sort by search-bot hit frequency; the default threshold is 10 hits in the window.
2. Read the evidence per URL: **status mix** (are bots stuck on 404s/5xx?), **indexability** (are bots wasting requests on noindex pages?), **variant count** (parameter traps).
3. Act: fix or redirect broken URLs, block or consolidate parameter traps, and align robots/indexability signals so bots spend budget on indexable 200s.

**Uncrawled Indexable Pages last (Notice):**

1. Read the evidence: **crawl depth** and **inlink count** — deep, weakly linked pages are the usual suspects.
2. Heed the **window-length caveat**: zero bot hits in a short window is weaker evidence than in a long one.
3. Act: strengthen internal linking and sitemap priority for pages you want crawled; consider whether the page deserves its indexability at all.

> **Tip:** The three views form a budget narrative: orphans are budget *missing* valuable pages, waste is budget *spent* on bad pages, and uncrawled indexable pages are the *consequence* — good pages starved of attention. Present them to clients in that order.

### Phase 6 — Optional: DNS verification of bot hits

Skip this phase entirely for air-gapped work.

1. From the import, trigger the **DNS verification action**.
2. Read the **consent dialog** — it shows exactly what will be queried. Nothing is sent until you explicitly consent for this import.
3. **Set a bounded sample size.** Verification runs on sampled IPs, not the full log.
4. Confirm. CrawlForge performs **reverse-then-forward DNS confirmation** using the system or user-configured resolver — DNS queries only.
5. Review **verification results per sampled IP**; confirmed hits upgrade to the `dns-verified` tier, visible in the tier badges and the `log-bot-hits` metric (dimensioned by verification tier).
6. Results are logged locally. Use the upgraded tiers to judge how much of your "bot" traffic is genuine before acting on it.

### Phase 7 — Manage imports and retention

1. **List imports** per project to see all records with status and counters.
2. **Purge an import** when it is no longer needed — deletion removes the import **and its aggregates transactionally**, with confirmation.
3. **Retention** applies automatically: imports are pruned with project retention (keep the last N audits, or an age limit), and aggregates are pruned with their import.
4. **Re-correlate freely.** Because views recompute on demand, one import can serve many audits over time — re-import only when you need a new traffic window.

## REST, hub, and MCP reference

All endpoints are loopback-only, JSON, with the standard response envelope and offset/limit pagination.

| Operation | Method & path | Input | Output |
|---|---|---|---|
| Import log file | `POST /api/projects/{id}/logs/import` | File upload or local path reference; format; time window; verification mode | Import identifier (immediate; progress over hub) |
| List imports | `GET /api/projects/{id}/logs` | Pagination | Import records with status and counters |
| Purge import | `DELETE /api/logs/{id}` | — | Confirmation; aggregates removed transactionally |
| Correlation | `GET /api/audits/{id}/logs/correlation` | Import reference; `view=summary\|orphans\|waste\|uncrawled`; filters; pagination | Correlation rows with evidence |
| DNS verification | `POST /api/logs/{id}/verify-dns` | Explicit consent flag; bounded sample size | Verification results per sampled IP |

### Live progress events

| Event | Payload | Emission |
|---|---|---|
| `log-import-progress` | Import identifier; lines parsed/accepted/rejected; phase | At most once per second while importing |
| `log-import-completed` | Import identifier; final counters; correlation summary counts | Once per import finalization |

### MCP access for local agents

With the optional local MCP interface enabled (off by default), the read-only **`log-correlation`** resource projects the correlation endpoint, addressed by audit reference, import reference, view, and pagination.

The MCP boundary holds: **import management is not exposed via MCP** — importing, purging, and DNS verification stay in the UI/REST surface.

## Where the data lives, and retention

| Object | Content | Lifecycle |
|---|---|---|
| `log_imports` | Project reference; file name; format; compression; size bytes; lines parsed/accepted/rejected; window start/end; verification mode; status (`importing` \| `ready` \| `failed`); imported-at | Pruned with project retention |
| `log_url_aggregates` | Import reference; canonical URL; host; total hits; search-bot hits; hits-by-family JSON; status-class counts JSON; first/last seen; bytes | Pruned with the import |
| `application_settings` | User-local bot CIDR additions (JSON) | Persistent local configuration |
| `metric_summaries` | `log-true-orphans`, `log-budget-waste`, `log-uncrawled-indexable`, `log-bot-hits` (dimensions: family, verification tier) | Dashboard card aggregates |

Everything — raw parsing, aggregates, correlation results, DNS verification logs — stays on your machine. Zero telemetry; no accounts, licenses, or update checks; the only possible outbound action is the DNS verification you explicitly consent to, per import.

## Related guides

- [AI Search (GEO) Readiness](ai-geo-readiness.md) — govern the same bot families' access with the governance matrix (shared purpose vocabulary).
- [MarTech & Consent Mode Auditing](martech-and-consent-mode-auditing.md) — audit the tags on the pages your logs show bots visiting.
- [Semantic Internal Linking](semantic-internal-linking.md) — fix true orphans and weakly linked pages with semantic link recommendations.
- [N-Grams & Topical Density](n-grams-and-topical-density.md) — assess whether uncrawled indexable pages deserve more crawl attention.
- [WAF & Soft-Block Detection](waf-and-soft-block-detection.md) — distinguish WAF-blocked crawl URLs from genuine log-side errors.
- [Dashboards & Virtualized Explorers](../04-exploring-reports-and-visualizations/dashboards-and-virtualized-explorers.md) — how report surfaces, filters, and virtualized grids work.

[← Documentation portal](../README.md)
