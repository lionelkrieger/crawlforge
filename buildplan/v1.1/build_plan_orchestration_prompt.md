# Prompt: Generate Parallel Multi-Agent Build Plan (`E:\Crawlforge\buildplan`)

**Role:** You are the Principal Build & Orchestration Architect for the **CrawlForge** Clean-Room Development Team operating in `E:\Crawlforge`.

**Context:** 
The specification decomposition and fidelity audit are 100% complete (`E:\Crawlforge\architecture\FIDELITY-AUDIT-REPORT.md`). The workspace has three strict directory boundaries:
1. `E:\Crawlforge\architecture\` — The verified, read-only modular architecture specifications (26 domain files + `README.md`).
2. `E:\Crawlforge\buildplan\` — The target directory where you will generate the multi-agent build orchestration files (YAML state machines and agent operating instructions).
3. `E:\Crawlforge\app\` — The target directory where the application source code, assets, and tests will be built by the coding agents in subsequent execution steps.

**Strict Constraints:**
- **Do NOT modify or delete** the root master files (`functional-specification.md`, `crawlforge-extensions-and-ui-stack.md`) or any file inside `E:\Crawlforge\architecture\`.
- **Context-Specific Referencing Only:** Coding agents must **never** read the monolithic root specification files (`FS-CRAWL-001` or `FS-CRAWL-002`). Every workpiece in your build plan must reference *only* the specific 1 to 3 modular `.md` files inside `E:\Crawlforge\architecture\` required for that exact bounded task.
- **Optimize for Maximum Parallelism:** Structure the phases and bounded workpieces using a **Contract-First (Interface/Schema Stub) Strategy** so that multiple parallel coding agents can execute simultaneously within each phase without file-lock collisions or merge conflicts.
- **Do NOT write application code in `E:\Crawlforge\app\` yet.** Your sole deliverable in this turn is the complete build orchestration suite inside `E:\Crawlforge\buildplan\`.

---

## Required Deliverables in `E:\Crawlforge\buildplan\`

Create `E:\Crawlforge\buildplan\` and generate the following exact files:

```text
E:\Crawlforge\buildplan\
│   AGENT-INSTRUCTIONS.md       <-- Master operating manual & concurrency protocol for coding agents
│   index.yaml                  <-- Master build status ledger, dependency DAG & phase tracker
│
└───phases\
        phase-00-contracts-and-scaffold.yaml      <-- Solution scaffold, shared interfaces/DTOs & governance
        phase-01-persistence-and-storage.yaml     <-- SQLite/MySQL providers, 40-table migrations, Content Store
        phase-02-crawl-engine-and-politeness.yaml <-- Frontier, AIMD, WAF 200 OK, List Mode, Proxy & Rendering
        phase-03-core-audit-and-analysers.yaml    <-- Pipeline, Scoping, SEO rules, CWV, Accessibility, Custom Extraction/JS
        phase-04-advanced-intelligence.yaml       <-- N-Grams, Local ONNX/Ollama Vectors, GEO/llms.txt, MarTech, Log Analyzer
        phase-05-api-hub-exports-and-mcp.yaml     <-- 88 REST routes, 19 Hub events, Exports, /metrics, Local MCP Server
        phase-06-electron-ui-and-plugins.yaml     <-- Electron shell, Mantine v7/v8, 500k Virtual Grids, ECharts, Sigma, Finder, Plugins
        phase-07-integration-and-acceptance.yaml  <-- Full end-to-end verification of all 48 Acceptance Criteria
```

---

## Detailed Specification for Each Deliverable

### 1. `E:\Crawlforge\buildplan\AGENT-INSTRUCTIONS.md`
Write a comprehensive, authoritative operating guide for autonomous coding agents containing:
- **Workspace Topology:** Define the exact directory layout to be created inside `E:\Crawlforge\app\` (e.g., `E:\Crawlforge\app\src\backend\`, `E:\Crawlforge\app\src\frontend\`, `E:\Crawlforge\app\src\mcp-stdio-adapter\`, `E:\Crawlforge\app\assets\`, `E:\Crawlforge\app\tests\`).
- **Context-Isolation Rule:** Agents must *only* load the `context_files` (from `E:\Crawlforge\architecture\`) listed in their assigned workpiece YAML. Loading root monolithic files or unrelated architecture folders is strictly forbidden.
- **File-Ownership & Parallel Safety Rule:** Each workpiece defines `owned_paths` inside `E:\Crawlforge\app\`. An agent executing a workpiece may only create or modify files within its `owned_paths`. Shared interfaces/contracts in `CrawlForge.Contracts` are locked in Phase 00; if a later workpiece needs a contract change, it must flag a blocker rather than mutating shared contracts mid-phase.
- **State Machine Protocol:** How an agent claims a workpiece in `phase-XX-*.yaml` and `index.yaml` (`pending` -> `in_progress` -> `verifying` -> `completed` | `blocked`), records `assigned_agent`, `started_at`, `completed_at`, and runs the workpiece's `verification_commands` before marking `completed`.
- **Clean-Room & Negative-Requirement Guardrails:** Explicit reminder of the 17 negative requirements (`01-foundation-and-governance\01-clean-room-and-exclusions.md` — zero OS registry/WMI/machine-ID reads, zero telemetry, zero remote feature flags, loopback-only bindings).

### 2. `E:\Crawlforge\buildplan\index.yaml`
Create the master orchestration index in valid, cleanly structured YAML:
- **`metadata`:** Project name (`CrawlForge`), target build root (`E:\Crawlforge\app`), architecture root (`E:\Crawlforge\architecture`), version, total phases (`8`), total workpieces, and total acceptance criteria (`48`).
- **`status_summary`:** Counts of phases and workpieces by status (`pending`, `in_progress`, `completed`, `blocked`).
- **`phases`:** Array of all 8 phases (`phase-00` through `phase-07`), each recording:
  - `phase_id`, `name`, `file` (relative path to `phases/phase-XX-*.yaml`), `status` (`pending`), `depends_on_phases` (array of phase IDs), `max_parallel_agents` (how many agents can run simultaneously in this phase), `workpiece_ids` (list of all workpieces in the phase), and `gated_acceptance_criteria` (which of the `#1–#48` criteria are verified in this phase).
- **`acceptance_criteria_ledger`:** A complete tracking array for Criteria `#1` through `#48` mapping each criterion ID to its owning `phase_id`, `workpiece_id`, and `status: pending`.

### 3. `E:\Crawlforge\buildplan\phases\phase-00-*.yaml` through `phase-07-*.yaml`
Decompose the build into **bounded, parallelizable workpieces** across the 8 phase YAML files.

**Parallelism Strategy to Enforce:**
- **Phase 00 (`phase-00-contracts-and-scaffold.yaml`)** is the **Synchronization Foundation**. It scaffolds the `.sln`, `.csproj` files (`System.GC.Server: true`), `package.json`, and writes all shared C# interfaces, enums, DTOs, and TypeScript contract types (`CrawlForge.Contracts`) plus the encryption-at-rest and negative-requirement static analyzers.
- **Phases 01 through 06** are **Highly Parallel Waves**. Because Phase 00 locks the shared interfaces and DTOs, workpieces inside Phases 01–06 can be executed by **3 to 8 parallel agents simultaneously**, each working in isolated project folders (`owned_paths`) against the Phase 00 interfaces.

**Required Schema for Every Workpiece inside `phase-XX-*.yaml`:**
Every workpiece entry in the `workpieces:` list must contain these exact YAML fields:

```yaml
- workpiece_id: "WP-02-03"
  title: "Pre-Audit WAF & Challenge Fingerprinting Pass (200 OK)"
  phase_id: "phase-02"
  status: "pending" # pending | in_progress | verifying | completed | blocked
  parallel_group: "wave-2A" # Workpieces in the same wave have zero file overlap and run concurrently
  depends_on_workpieces: ["WP-00-02", "WP-01-02"]
  context_files:
    # ONLY the exact modular files from E:\Crawlforge\architecture\ needed for this workpiece
    - "E:\\Crawlforge\\architecture\\03-crawl-engine\\02-frontier-fetching-and-waf.md"
    - "E:\\Crawlforge\\architecture\\02-data-and-persistence\\02-complete-schema-catalog.md"
  owned_paths:
    # Strict file/directory boundary inside E:\Crawlforge\app\ to guarantee zero merge conflicts
    - "E:\\Crawlforge\\app\\src\\backend\\CrawlForge.Crawler\\Waf\\"
    - "E:\\Crawlforge\\app\\assets\\waf-signatures\\"
    - "E:\\Crawlforge\\app\\tests\\CrawlForge.Crawler.Tests\\Waf\\"
  deliverables:
    - "Stage A (header/cookie) and Stage B (bounded pre-parse) WAF fingerprinting evaluator"
    - "Bundled WAF signature catalogue JSON asset (Cloudflare, DataDome, Akamai, Imperva, PerimeterX, Generic)"
    - "2-facet corroboration guard, 0.8 default threshold, and render-resolution policy"
    - "waf-challenge-detected and site-wide waf-block-rate (1% Warning / 5% Error, min 20) findings"
  interfaces_implemented:
    - "IWafChallengeEvaluator"
    - "IWafSignatureCatalogue"
  verification_commands:
    - "dotnet test E:\\Crawlforge\\app\\tests\\CrawlForge.Crawler.Tests --filter FullyQualifiedName~Waf"
  acceptance_criteria_refs: [35]
```

---

## Phase Breakdown & Parallel Workpiece Requirements

Ensure the 8 phase YAML files cover 100% of the architecture across roughly **35–45 granular, bounded workpieces** (mapping exact file paths to the 26 domain files in `E:\Crawlforge\architecture\`):

1. **`phase-00-contracts-and-scaffold.yaml`:**
   - Monorepo scaffold in `E:\Crawlforge\app\` (.NET 10 solution + Electron/React/Vite/Mantine workspace).
   - Shared C# domain interfaces/DTOs & TypeScript API/Hub contracts.
   - App-generated local entropy secret encryption (`ISecretProtector`), loopback port handshake, Server GC runtime config, native minidump crash handler, and static negative-requirement verifier (Criteria `#1, #2, #5, #17, #19, #21`).
2. **`phase-01-persistence-and-storage.yaml` (Parallel Wave):**
   - **Agent A:** SQLite provider (WAL, native extension loader for regex, delimited-text, vector-similarity, math/stats).
   - **Agent B:** MySQL external provider & connection tester.
   - **Agent C:** Complete 40-object schema migrations (25 baseline + 14 extension tables + `ngram_site_summaries` view + all indexes) & first-run deterministic seeding.
   - **Agent D:** CQRS read/write repositories, compressed Content Store (`content_artifacts`), retention pruner, and project archive export/import (Criteria `#9, #15, #18`).
3. **`phase-02-crawl-engine-and-politeness.yaml` (Parallel Wave):**
   - **Agent A:** Unlimited Limits Resolver, robots.txt parser, virtual robots override, and AIMD Auto-Adaptive Politeness Controller ($R_0, \alpha, \beta, \kappa$, EWMA TTFB, `host_throttle_events`).
   - **Agent B:** URL canonicalization, on-the-fly regex URL rewriter, Priority Frontier, per-host sub-queues, and pause/resume/crash checkpointing.
   - **Agent C:** HTTP fetcher, hop-by-hop redirect recorder, Signed-Request Crawler Access, Pre-crawl validation, and Pre-Audit WAF `200 OK` Fingerprinting Pass.
   - **Agent D:** List Mode ingestion (`.txt`/`.csv`/clipboard) & Redirect Migration Matrix Validator (5-tier verdict precedence).
   - **Agent E:** Headless Chromium rendering pool (stealth, infinite-scroll, console capture) & Built-in Loopback Intercepting Proxy (waterfall capture, resource blocking).
   - **Agent F:** Scheduled Audits background service (catch-up, no-overlap, completion actions) (Criteria `#3, #4, #6, #11, #12, #22, #34, #35, #36, #43`).
4. **`phase-03-core-audit-and-analysers.yaml` (Parallel Wave):**
   - **Agent A:** 8-step Audit Pipeline orchestrator, tolerant DOM parser, Element Extraction Inventory, and Content Area Scoping (`Scoped Visible Text Tree`, `content_scope_profiles`).
   - **Agent B:** Indexability, Status, Soft-404, HTTP Header vs. HTML Equivalency (`X-Robots-Tag`, `Link` canonical/hreflang), and Response-vs-Rendered comparison.
   - **Agent C:** Content Quality (shingle near-duplicates, Flesch readability, Hunspell spelling checks, Pixel-Width SERP & Social Card measurement with bundled font metrics).
   - **Agent D:** DOM-Zone Link Classification (`in-content`, `nav`, `header`, `footer`, `sidebar`, `breadcrumb`) & Weighted PageRank / Link Equity calculator.
   - **Agent E:** Structured Data & Rich-Result validator (JSON-LD/Microdata), Internationalization, Mobile-Friendliness, and bundled Technology Detection.
   - **Agent F:** Automated Accessibility engine & Rendered Core Web Vitals / Performance Budgets / Code Coverage.
   - **Agent G:** Custom Content Extraction (CSS/XPath/Regex, typed values, bounds), Custom Content Search, Custom JS Snippets, Audit Scoring, and Finding Suppression (Criteria `#10, #13, #24, #25, #26, #27, #28, #29, #30, #31, #32, #37, #48`).
5. **`phase-04-advanced-intelligence.yaml` (Parallel Wave):**
   - **Agent A:** 4-Zone N-Gram & Topical Density Analyzer (`ngram_frequencies`, `ngram_site_summaries`, stop words, keyword cannibalization, topical gaps).
   - **Agent B:** Local Vector Embeddings & Semantic Internal Linking Pipeline (bundled ONNX + loopback-only daemon validator, `url_embeddings`, semantic cannibalization $\ge 0.88$, link opportunities $0.75\text{–}0.87$, low-relevance links $< 0.35$).
   - **Agent C:** AI Search (GEO) Readiness (`llms.txt` / `llms-full.txt` probe & validator, AI Bot Governance Matrix, `llms.txt` Generator tool).
   - **Agent D:** MarTech, Analytics & Tag Integrity Auditor (GTM/GA4/Ads/Pixel/Clarity inventory, `window.dataLayer` & Consent Mode v2 inspector).
   - **Agent E:** 100% Offline Server Log File Analyzer (streaming parser, bundled bot CIDR verifier, optional consented DNS check, True Orphans / Budget Waste / Uncrawled Indexable correlation) (Criteria `#38, #44, #45, #46, #47`).
6. **`phase-05-api-hub-exports-and-mcp.yaml` (Parallel Wave):**
   - **Agent A:** Core & Extended Loopback REST Controllers (all 88 routes, uniform envelope, pagination, staging-vs-prod host-mapped comparison).
   - **Agent B:** SignalR Real-Time WebSocket Hub (all 19 events, throttling rules, 2-second `system-health` heartbeat, and `POST /api/system/compact`) + `/metrics` Prometheus exposition.
   - **Agent C:** Local Export Engine (multi-sheet Excel, CSV, HTML bundle, PDF, XML sitemap) & Encrypted User Export Destinations.
   - **Agent D:** Local MCP Server (`stdio` adapter process + loopback `SSE`, all 8 tools, 15 resources, 5 prompts, delegated AI control flows, action log) (Criteria `#7, #8, #14, #16, #20, #23, #41`).
7. **`phase-06-electron-ui-and-plugins.yaml` (Parallel Wave):**
   - **Agent A:** Electron main process, window catalog, backend supervisor, port discovery handshake, preload bridge, and recovery screen.
   - **Agent B:** Mantine UI v7/v8 AppShell, dark-first theme, dockable split panes, `Ctrl+K` Spotlight command palette, persistent Memory/GC/WAL status bar, and Zustand normalized store.
   - **Agent C:** Virtualized Data Grids (TanStack Table + TanStack Virtual, $\le 50$ DOM rows, 60 FPS @ 500k+ rows, dynamic extraction columns) across all Explorer surfaces + Interactive Query Builder.
   - **Agent D:** High-Density Dashboards & Charts (Apache ECharts Canvas/WebGL) + Force-Directed Link & Semantic Network Graphs (Sigma.js + Graphology Web Worker).
   - **Agent E:** Single-Page Analyzer, SERP & Social Card Pixel Preview, Standalone Tools UI, and `@medv/finder` Visual CSS/XPath Selector Picker.
   - **Agent F:** Local UI Plugin System (directory scanner, `plugin.json` validator, sandboxed iframe bridge, deterministic `onLoad` / `onTabActivate` / `onDataUpdate` / `onCrawlComplete` hooks) (Criteria `#33, #39, #40, #42`).
8. **`phase-07-integration-and-acceptance.yaml`:**
   - Full end-to-end integration test suite, packaging asset verification, offline/air-gap network boundary audit, 500k-row scale & 60 FPS scroll benchmark, and final sign-off across all 48 Acceptance Criteria.

---

## Execution Instructions
Inspect `E:\Crawlforge\architecture\README.md` first to confirm the exact filenames of all 26 domain files in `E:\Crawlforge\architecture\`, then create `E:\Crawlforge\buildplan\`, `AGENT-INSTRUCTIONS.md`, `index.yaml`, and all 8 `phases\phase-XX-*.yaml` files in full without placeholders or truncation.