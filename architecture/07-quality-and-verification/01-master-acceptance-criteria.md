# Master Acceptance Criteria

> **CrawlForge modular architecture — area of concern: `07-quality-and-verification`.**
> Every source section below is reproduced **verbatim and unabridged** from the immutable master documents. No summarization, paraphrase, or omission has been applied.

| Source document | Version | Sections reproduced in this file |
|---|---|---|
| FS-CRAWL-001 — `baseline/functional-specification.md` | v1.4 | §14 (complete section) |
| FS-CRAWL-002 — `baseline/crawlforge-extensions-and-ui-stack.md` | v1.1 | §9 (complete section) |

**Notation.** Cross-references inside the reproduced text retain the source convention: a bare `§n` refers to **FS-CRAWL-001**; `FS-CRAWL-002 §n` refers to the extension specification. The [README traceability matrix](../README.md) maps every source section to its file(s) in this hierarchy.

---

> **Verbatim source — FS-CRAWL-001 v1.4: 14. Acceptance Criteria — complete section, verbatim and unabridged**

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

---

> **Verbatim source — FS-CRAWL-002 v1.1: 9. Additional Acceptance Criteria — complete section, verbatim and unabridged**

## 9. Additional Acceptance Criteria

**Appends to FS-CRAWL-001 §14** (which ends at criterion #34). Criteria #35–#48 below are numbered continuously with the parent list and are mandatory for the extended product. Criteria #35–#42 cover the v1.0 modules (§§2–8); criteria #43–#48 cover the v1.1 modules (§§10–15).

35. **WAF 200 OK detection.** Against a test origin serving a challenge page with provider markers under HTTP 200 OK, the URL is recorded `blocked` with sub-classification `waf-challenge` and the correct provider name — never `fetched`; no thin-content or duplicate-title finding is emitted for it; the dedicated WAF-Block diagnostic alert appears; detection runs fully offline from the bundled catalogue; re-audit re-detects from the stored body without network access; a clean 200 page matching only a single generic title phrase is not blocked (corroboration guard, FS-CRAWL-002 §2.3.3).
36. **AIMD rate adaptation.** Against a simulated origin, consecutive healthy responses below the TTFB threshold raise the effective per-host rate by α up to the resolved §4.2 ceiling (never above it; robots crawl-delay always honored); a TTFB spike above κ × rolling baseline, a WAF soft-block, or an HTTP 429/503 immediately halves the rate (× β) down to the floor; `host-throttle` adjustments stream over the hub with trigger codes; pause/resume preserves controller state; disabling AIMD reverts exactly to static parent behavior.
37. **Boilerplate scoping.** Configured Include/Exclude selectors (CSS and XPath) produce a Scoped Visible Text Tree; word counts, Flesch readability, spelling, shingle near-duplicate detection, custom content search, and N-gram body/headings zones all operate on scoped text; head facts and link discovery are unchanged; invalid selectors are rejected at configuration time; re-audit re-derives scoped metrics from the content store without network access; an unconfigured project reproduces baseline results exactly.
38. **N-gram cannibalization detection.** Two indexable URLs sharing an identical primary 2-gram/3-gram in Titles/H1s produce a cannibalization finding with cluster evidence; `ngram_frequencies` and `ngram_site_summaries` are populated with per-zone TF and site-wide DF; GET `/api/audits/{id}/ngrams` and GET `/api/audits/{id}/ngrams/cannibalization` return the data; the MCP `ngram-analysis` resource reads it; per-language stop-word filtering is honored; mandatory bounds discard-and-count without failing the audit.
39. **Local plugin lifecycle execution.** A local plugin with a valid manifest loads at shell startup; `onLoad` registers a report tab, a column formatter, and a filter preset; `onTabActivate` fires with the audit identifier and filter state; `onDataUpdate` arrives coalesced at most once per second during a crawl; `onCrawlComplete` fires exactly once when the audit reaches `completed`; network access is zero without a grant and restricted to user destination rules with one; a failing plugin is isolated and never crashes the shell or affects the audit.
40. **60 FPS virtual scrolling at 500k rows.** With 500,000+ URL rows, the URL explorer (and the Issue, Link, Custom Extraction, and N-Gram explorers) mount at most 50 DOM rows and sustain 60 FPS scrolling (p95 frame time ≤ 16.7 ms); multi-column sorting, instant filtering, column pinning, and dynamic extraction columns remain functional; keyboard navigation and ARIA row semantics are preserved.
41. **Live memory status-bar telemetry.** A `system-health` heartbeat arrives every 2 seconds reporting backend working-set RAM, managed heap size, GC generation counts, and SQLite WAL file size; frontend JS heap usage appears in the persistent bottom status bar; the manual Compact / Trim Working Set action reduces the working set and checkpoints the WAL, reporting before/after snapshots; the same families appear on GET `/metrics`; no telemetry is transmitted off the machine.
42. **Point-and-click CSS selector generation.** In the Single-Page Analyzer and Render Tester, clicking a rendered element generates a resilient CSS selector (with an XPath alternative) and a correct match-count preview; captured selectors can be sent into a Custom Extraction rule and into Content Area Scoping include/exclude lists; the preview sandbox loads only the crawled target; generation works offline against stored rendered artifacts.
43. **List Mode & Redirect Migration Matrix.** A `.txt`, `.csv`, or clipboard URL list ingests as seeds with discovery source `list`; `list-only` crawls at depth 0 with link discovery disabled, and `list-bounded` skips off-list discoveries; a two-column migration matrix fetches every Source_URL, records the full hop chain, and emits the correct verdict among `redirect-matrix.pass`, `redirect-matrix.target-mismatch`, `redirect-matrix.chain-exceeded`, `redirect-matrix.final-non-200`, and `redirect-matrix.canonical-mismatch` against fixtures; regex rewrite rules transform canonicalization deterministically with invalid patterns rejected; staging↔production host mapping aligns audit and content comparison by path.
44. **Offline Server Log import & correlation.** Apache/Nginx Combined, IIS W3C, and Cloudflare JSON/CSV logs (plain and gzip) import from local disk with zero network calls; bot families classify with offline CIDR verification and DNS verification occurs only on explicit user trigger; correlation against an audit produces True Orphans (search-bot hits never discovered by the crawl), Crawl Budget Waste (bot hits on non-200/non-indexable/parameter-trap URLs), and Uncrawled Indexable Pages (indexable URLs with zero search-bot hits); the Log File Analyzer surface is virtualized and the MCP `log-correlation` resource reads the same data.
45. **Local embeddings & semantic internal linking.** The bundled ONNX model or a loopback daemon populates `url_embeddings` (non-loopback endpoints are rejected at configuration); semantic cannibalization fires at cosine ≥ 0.88; link opportunities in the 0.75–0.87 band exclude pairs already linked in `link_edges` and include anchor candidates from the target's top N-grams; low-relevance existing links are flagged below the floor; provider failure never fails the audit; no off-machine traffic occurs.
46. **`llms.txt` validation & AI governance matrix.** `/llms.txt` and `/llms-full.txt` are probed at crawl start alongside robots.txt; structural validation and broken-link findings are emitted; the governance matrix evaluates robots.txt, `X-Robots-Tag`, and meta directives across the bundled agent profiles and reports search-indexing vs AI-RAG-citation vs LLM-training verdicts per path prefix/URL; the Generator & Validator tool produces a standards-conformant `llms.txt` from top-scoring indexable pages grouped by directory, exported locally only.
47. **Analytics, GTM/GA4 & Consent Mode auditing.** Tag/container inventory (GTM, GA4, Google Ads, Meta Pixel, LinkedIn Insight, Clarity, custom patterns) is extracted from DOM parsing and local proxy capture; in rendering mode the post-render `dataLayer` snapshot and Consent Mode v2 defaults/updates (`ad_storage`, `analytics_storage`, `ad_user_data`, `ad_personalization`) are captured and correlated with `gcs`/`gcd` hit parameters; `analytics.missing-container`, `analytics.duplicate-container`, `analytics.datalayer-malformed`, and `analytics.consent-mode-missing` fire correctly on fixtures; the Analytics & Tagging Explorer and MCP `analytics-coverage` resource present the results.
48. **Zone-weighted equity, SERP pixels & header equivalency.** Every `link_edge` carries a `dom_zone` classification; weighted link equity differs from unweighted according to configured zone weights with in-content links weighted highest; pixel-width measurement emits `title-pixel-truncation` and `description-pixel-truncation` against configured budgets with a live SERP & Social Card Preview; `X-Robots-Tag`/`Link` header vs HTML conflicts emit `indexability.header-html-conflict`, `canonical.header-html-conflict`, and `hreflang.header-html-conflict` as Error findings with dual evidence.

---
