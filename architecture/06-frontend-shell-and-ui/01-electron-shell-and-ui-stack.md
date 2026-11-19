# Electron Shell & Mandated UI Stack

> **CrawlForge modular architecture — area of concern: `06-frontend-shell-and-ui`.**
> Every source section below is reproduced **verbatim and unabridged** from the immutable master documents. No summarization, paraphrase, or omission has been applied.

| Source document | Version | Sections reproduced in this file |
|---|---|---|
| FS-CRAWL-001 — `baseline/functional-specification.md` | v1.4 | §8.3, §12 (complete section) |
| FS-CRAWL-002 — `baseline/crawlforge-extensions-and-ui-stack.md` | v1.1 | §7 heading & introduction, §7.2, §7.4, §7.5, §8 (complete section) |

**Notation.** Cross-references inside the reproduced text retain the source convention: a bare `§n` refers to **FS-CRAWL-001**; `FS-CRAWL-002 §n` refers to the extension specification. The [README traceability matrix](../README.md) maps every source section to its file(s) in this hierarchy.

---

> **Verbatim source — FS-CRAWL-001 v1.4: 8.3 Chart requirements — verbatim and unabridged**

### 8.3 Chart requirements

- Chart types: bar, stacked bar, pie/donut, line/time-series, histogram, treemap, and network graph.
- All charts render locally from aggregate endpoints; interactive filters propagate to the underlying lists.

---

> **Verbatim source — FS-CRAWL-001 v1.4: 12. Desktop Shell Requirements — complete section, verbatim and unabridged**

## 12. Desktop Shell Requirements
> Capabilities: CAP-L-X06

- **Window catalog:** splash; main hub (projects, dashboards, and schedules); report window; visualization window; single-page analyzer; standalone tools (section 8.7); settings; backend-failure/recovery screen.
- **Shell duties:** spawn, monitor, and restart the backend; discover the backend port; load only local UI assets; provide native file dialogs for export/import; surface OS taskbar progress and local notifications; persist window state.
- **Settings surfaces** include: database information (location and size of the local database; provider selection is deferred, CR-028); export-destination management with test-connection feedback; proxy configuration; user-dictionary management; the MCP automation-interface toggle with transport selection, token display, and rotation; diagnostics management (open, inspect, and purge local logs and crash artifacts); UI language selection. **[Amended by CR-028, D-020]**
- **Recovery screen** shows local diagnostic information and a pointer to the local crash-artifact directory after repeated backend failures.
- **UI state** derives entirely from the local API and hub. No entitlement-driven screens exist; the first-run experience proceeds directly to project creation.
- No remote content and no embedded views of vendor services. An optional embedded preview of crawled pages is sandboxed and loads only the crawled target.

---

---

> **Verbatim source — FS-CRAWL-002 v1.1: 7. Real-Time Memory Profiling and UI Virtualization Mandates — section heading & introduction (verbatim); this section's subsections are distributed across the sibling files listed in the README traceability matrix**

## 7. Real-Time Memory Profiling and UI Virtualization Mandates

**Extends FS-CRAWL-001 §10.3, §12, §13.** Additional hooks: §8.2, §10.1, §10.2, §10.5.

---

> **Verbatim source — FS-CRAWL-002 v1.1: 7.2 Live status-bar telemetry — verbatim and unabridged**

### 7.2 Live status-bar telemetry

- **New hub event `system-health`** (server → shell), emitted **every 2 seconds** (small jitter permitted) whenever the shell is connected — during audits and while idle, independent of per-audit subscriptions. This extends §10.3 alongside the existing `crawl-progress` event, whose ≤ 1/s cadence (§5.7) is unchanged.

| Payload field | Source | Semantics |
|---|---|---|
| `timestamp` | Backend | UTC emission time |
| `backendWorkingSetBytes` | Backend OS counters | Backend process working-set RAM |
| `managedHeapBytes` | Backend runtime | Managed heap size under the mandated server-GC profile (§13) |
| `gcGen0Count`, `gcGen1Count`, `gcGen2Count` | Backend runtime | GC generation collection counts, aggregated across server-GC heaps | **[Amended by CR-025, D-020]**
| `sqliteWalBytes` | Embedded provider | Write-ahead log file size (§2.4); null when the external provider is active |
| `auditId` (optional) | Backend | Present while an audit is running |

- **Frontend JS heap** is measured locally in the renderer at each heartbeat tick via the standard performance-memory API where available (otherwise reported as unavailable). It is composed into the status bar client-side and never sent to the backend.
- **Persistent bottom status bar** in the main hub and report windows (§12): backend working set, managed heap, GC generation counts, WAL size, frontend JS heap; crawl throughput appended while running. Advisory color thresholds are locally configurable (defaults: working-set advisory 2 GiB, WAL advisory 256 MiB); advisories never trigger automatic destructive actions.
- **Manual "Compact / Trim Working Set" action** (status-bar button and command palette, FS-CRAWL-002 §8.2): POST `/api/system/compact` performs managed GC compaction, a SQLite WAL checkpoint (truncate mode, embedded provider), and an OS working-set trim where the platform supports it. It returns before/after snapshots and the actions performed; is rate-limited (≥ 10 s between invocations); never interrupts an in-flight audit or transaction; and is logged locally.
- **On-demand snapshot:** GET `/api/system/health` returns the same payload as the heartbeat.

---

> **Verbatim source — FS-CRAWL-002 v1.1: 7.4 Frontend memory hygiene — verbatim and unabridged**

### 7.4 Frontend memory hygiene

- Hub ingestion uses bounded ring buffers (e.g. recent-activity ≤ 1,000 entries); counters are aggregated, never accumulated as raw event history.
- Charts dispose on unmount; graph models are windowed (FS-CRAWL-002 §8.5); window caches are LRU-bounded (FS-CRAWL-002 §7.1).
- Plugin `onDataUpdate` relays are coalesced and dropped under backpressure (FS-CRAWL-002 §6.5); listeners detach on tab deactivation.
- No surface retains full-dataset arrays; every large list is server-windowed.

---

> **Verbatim source — FS-CRAWL-002 v1.1: 7.5 Performance budgets — verbatim and unabridged**

### 7.5 Performance budgets

| Budget | Target |
|---|---|
| Tabular scrolling at 500k+ rows | 60 FPS by the composite gate of criterion #40 (median ≤ 16.7 ms, dropped ≤ 1%, single-vsync ≥ 95%); mounted DOM rows ≤ 50 | **[Amended by CR-019, D-019]**
| Heartbeat cadence / overhead | Every 2 s; < 1% CPU overhead |
| Status-bar freshness | ≤ 2 s from backend sample to display |
| Compact action | < 5 s typical; before/after snapshot returned |
| Dashboard reads | < 1 s at target scale (parent §13, unchanged) |

**Acceptance hooks:** FS-CRAWL-002 §9, criteria #40 and #41.

---

---

> **Verbatim source — FS-CRAWL-002 v1.1: 8. Mandated Frontend Technology Stack — complete section, verbatim and unabridged**

## 8. Mandated Frontend Technology Stack

**Concrete architecture for FS-CRAWL-001 §12.** Additional hooks: §2.1, §2.2, §8.2, §8.3, §8.7, §13.

### 8.1 Renderer architecture

- The Electron renderer (§2.1) loads only the local UI bundle (§12). `contextIsolation` is enabled, Node integration is disabled in the renderer, and a minimal preload bridge exposes only shell duties (native file dialogs, backend port discovery per §2.2).
- **Component runtime:** React (required by the mandated Mantine component system), authored in a typed JavaScript superset; built by a local bundler (Vite-class) into an offline bundle. No remote content, no CDN, no remote fonts (§12).
- **State:** a single normalized frontend state store (Zustand-class lightweight store) with selector subscriptions; it is the contract surface for plugin lifecycle hooks (FS-CRAWL-002 §6.5). One WebSocket hub connection (§2.2) feeds an event demultiplexer with bounded ring buffers (FS-CRAWL-002 §7.4).
- **Routing:** local (hash/memory) routing only; window state persists per §12.

### 8.2 UI component system — Mantine UI (v7/v8)

- **Dark-first enterprise theme:** dark by default, light optional; persisted in `application_settings` theme (§9.2).
- **AppShell layout:** header (audit switcher, global search), navbar (projects, reports, tools, settings), and the persistent bottom status bar (FS-CRAWL-002 §7.2).
- **Dockable split panes:** resizable, dockable pane system for explorer + URL-detail side-by-side work; layouts persist per window (§12 window state). The concrete dock-manager implementation is an implementation choice within this contract.
- **Ctrl+K Spotlight command palette:** fuzzy search over navigation targets, audits, saved queries (§8.7), standalone tools (§8.7), settings actions, and plugin-registered commands (FS-CRAWL-002 §6); fully keyboard-operable (§13 accessibility).
- **Complex configuration forms:** audit configuration (§5.1), custom extraction rules (§7.9), content scope (FS-CRAWL-002 §4), AIMD parameters (FS-CRAWL-002 §3), WAF signatures (FS-CRAWL-002 §2), plugin permissions (FS-CRAWL-002 §6). Client-side validation mirrors §4.3; server field-level errors (§10.4) render inline.

### 8.3 Virtualized data grids — TanStack Table + TanStack Virtual (or AG Grid Community)

**Mandate:** TanStack Table + TanStack Virtual (headless pair, primary choice) **or** AG Grid Community with its server-side row model. Either implementation must satisfy the full capability checklist and the FS-CRAWL-002 §7.1 virtualization contract:

| Capability | Requirement |
|---|---|
| Scale | 1M+ rows via the windowed server-side row model (FS-CRAWL-002 §7.1); never a full client dataset |
| Column pinning | Left/right pinning with bounded pin count |
| Dynamic columns | Custom-extraction per-rule columns, multi-valued expansion to the busiest page (§7.9, §8.2) |
| Sorting | Multi-column sorting, executed server-side (§10.2) |
| Filtering | Instant (debounced) filtering; query-builder integration (§8.7) |
| Interaction | Row selection, column resize/reorder, density modes, keyboard grid navigation (§13) |
| Export handoff | Selection/filter state handed to the export flow (§8.4) |

### 8.4 High-density charting — Apache ECharts

- **Canvas rendering** (WebGL extension for very large series); **no SVG DOM** for large datasets — preventing DOM freezing at scale.
- Chart families: histograms (response time, word count, CWV distributions §7.11), treemaps (section treemap §8.2), score trends (§8.1 `audit_scores`), stacked bars (severity/category §8.1), line/time-series, pie/donut (§8.3).
- Data arrives pre-bucketed from GET `/api/audits/{id}/charts/{metric}` (§10.2); interactive chart filters propagate to the underlying explorers (§8.3).
- Theme synchronized with the Mantine dark-first palette; lazy initialization, dispose on unmount, resize-observer driven (FS-CRAWL-002 §7.4).

### 8.5 Network and link graph visualization — Sigma.js + Graphology

- **WebGL force-directed rendering** (ForceAtlas2-class layout) for the internal-link network graph and crawl-depth map (§8.2 Visualizations); layout computation runs in a Web Worker so the UI thread never blocks.
- Data source: GET `/api/audits/{id}/graph` (§10.2) with graph type and depth/size limits.
- **Level-of-detail budget:** default render cap 20,000 nodes / 60,000 edges (configurable); beyond the cap the server aggregates/samples and the UI shows a "showing X of Y" banner. Graphology holds the bounded in-memory graph model.
- Node sizing by link-equity percentile (§8.1); coloring by status class or depth; hover/selection synchronized with the URL explorer; depth-map variant uses a hierarchical/radial layout.

### 8.6 Visual CSS/XPath selector picker — Finder (`@medv/finder`)

- Embedded into the **Single-Page Analyzer (§7.7)** and the **Render Tester (§8.7)**: the user points and clicks any rendered DOM element to auto-generate a **resilient CSS selector** (shortest unique path with class/id optimization); an **XPath** alternative is generated deterministically on request.
- Interaction: hover highlight overlay, click to capture, live match-count preview, resilience options (prefer stable ids/attributes; avoid positional fragility where a unique alternative exists).
- **"Send to" actions:** captured selectors flow directly into a Custom Extraction rule (§7.9) or a Content Area Scoping include/exclude list (FS-CRAWL-002 §4).
- The picker operates inside the sandboxed embedded preview (§12: loads only the crawled target) against the post-render DOM, communicating through the message bridge; it works offline against stored rendered artifacts (§9.6).

### 8.7 Stack summary

| Concern | Mandated library / class | Role | Parent hook |
|---|---|---|---|
| Component system | Mantine UI v7/v8 (React) | Dark-first shell, dockable panes, Ctrl+K palette, forms | §12, §8.2 |
| Data grids | TanStack Table + TanStack Virtual, or AG Grid Community | 1M+ row virtualized explorers, pinning, dynamic columns | §8.2, §7.9 |
| Charts | Apache ECharts (Canvas/WebGL) | Histograms, treemaps, CWV distributions, score trends | §8.3, §7.11, §8.1 |
| Link graphs | Sigma.js + Graphology | WebGL force-directed link network, depth maps | §8.2 |
| Selector picker | Finder (`@medv/finder`) | Point-and-click resilient CSS/XPath generation | §7.7, §8.7, §7.9 |
| State store | Zustand-class normalized store | Selector subscriptions; plugin hook surface | §12, FS-CRAWL-002 §6 |
| Shell | Electron (contextIsolation, no renderer Node) | Window host, local bundle, preload bridge | §2.1, §2.2 |

### 8.8 State store contract (plugin hook foundation)

Normalized slices: settings, projects, audits, windowed URL/finding/link/n-gram pages, host-throttle state, system-health, plugin registry. Selector subscriptions drive rendering; the hub demultiplexer updates slices immutably; the plugin bridge (FS-CRAWL-002 §6.4) relays coalesced read-only slices and hub events. No slice ever holds a full dataset (FS-CRAWL-002 §7.4).

### 8.9 Offline, packaging, and licensing mandates
> Decisions: D-003, D-010, D-011 · Change records: CR-004, CR-007

- All libraries and assets bundle locally; no CDN, no remote fonts or icons (local icon set); CSP `default-src 'self'` with exactly two exception classes: the sandboxed crawl preview (§12) and user-granted plugin destinations (FS-CRAWL-002 §6.4).
- Bundled third-party libraries and assets must carry licences compatible with distribution under AGPL-3.0 (permissive licences such as MIT and Apache-2.0 qualify); a local attribution file listing every bundled component and its licence ships with the bundle. **[Amended by CR-004, D-010, D-011]**
- The §13 packaging asset manifest gains: UI bundle, local icon set, attribution file, bundled WAF signature catalogue (FS-CRAWL-002 §2.3), and per-language stop-word lists (FS-CRAWL-002 §5.2).
- The packaging asset manifest also includes the pinned rendering browser build and its licence notices (§5.6.3), and the product `LICENSE` file (§1.4 of `01-clean-room-and-exclusions.md`). **[Added by CR-007 and CR-004]**
- Every new surface is localizable through the parent localization framework (§13); plugin labels localize via manifest maps (FS-CRAWL-002 §6.3).

---

---

## Amendment history

### CR-004 (2026-11-17; D-010, D-011)

Replaced text in §8.9:

> - Permissive open-source license compliance; a local attribution file ships with the bundle.
### CR-019 (2026-11-17; D-019)

Replaced text in §7.5 performance budgets (scrolling row):

> | Tabular scrolling at 500k+ rows | 60 FPS, p95 frame ≤ 16.7 ms; mounted DOM rows ≤ 50 |
### CR-025 (2026-11-17; D-020)

Replaced text in live status-bar telemetry table:

> | `gcGen0/1/2Counts` | Backend runtime | GC generation collection counts, aggregated across server-GC heaps |
### CR-028 (2026-11-17; D-020)

Replaced text in §12 settings surfaces:

> - **Settings surfaces** include: database provider selection with connection-test feedback; export-destination management with test-connection feedback; proxy configuration; user-dictionary management; the MCP automation-interface toggle with transport selection, token display, and rotation; diagnostics management (open, inspect, and purge local logs and crash artifacts); UI language selection.
