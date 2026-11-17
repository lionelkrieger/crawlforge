# Process Model & Lifecycle

> **CrawlForge modular architecture — area of concern: `01-foundation-and-governance`.**
> Every source section below is reproduced **verbatim and unabridged** from the immutable master documents. No summarization, paraphrase, or omission has been applied.

| Source document | Version | Sections reproduced in this file |
|---|---|---|
| FS-CRAWL-001 — `baseline/functional-specification.md` | v1.4 | §2 heading & introduction, §2.1, §2.2, §2.3, §2.5, §2.7, §13 (complete section) |
| FS-CRAWL-002 — `baseline/crawlforge-extensions-and-ui-stack.md` | v1.1 | §6.2, §8.9 |

**Notation.** Cross-references inside the reproduced text retain the source convention: a bare `§n` refers to **FS-CRAWL-001**; `FS-CRAWL-002 §n` refers to the extension specification. The [README traceability matrix](../README.md) maps every source section to its file(s) in this hierarchy.

---

> **Verbatim source — FS-CRAWL-001 v1.4: 2. Architectural Foundation — section heading & introduction (verbatim); this section's subsections are distributed across the sibling files listed in the README traceability matrix**

## 2. Architectural Foundation

---

> **Verbatim source — FS-CRAWL-001 v1.4: 2.1 Process model — verbatim and unabridged**

### 2.1 Process model

The product consists of two cooperating processes with a strict separation of concerns:

| Process | Technology class | Responsibility |
|---|---|---|
| Desktop shell | Chromium-embedded window host (Electron-class) | Window lifecycle, native OS integration, loading the local UI bundle, spawning and supervising the backend. Contains **no domain logic**. |
| Local backend | Self-contained local HTTP service (ASP.NET Core-class) | All domain logic: crawl engine, audit engine, persistence, reporting aggregation, real-time event hub, local MCP server, metrics. Ships with its own runtime; no external prerequisites. |

- The shell spawns the backend as a child process, monitors its health, and restarts it on failure.
- The UI is a local web application loaded from the packaged bundle; it never loads remote content.

---

> **Verbatim source — FS-CRAWL-001 v1.4: 2.2 Communication channels — verbatim and unabridged**

### 2.2 Communication channels

- **HTTP request/response** (JSON) for all commands and queries, including the local metrics endpoint (section 10.5).
- **A persistent WebSocket channel** (SignalR-class hub protocol) for server-push events: crawl progress, stage transitions, completion, errors.
- **Local agent transports (optional):** the local automation interface (section 11) additionally speaks the standard agent transport mechanisms — **standard input/output (stdio)** for locally spawned clients, and **Server-Sent Events (SSE) / streamable HTTP over loopback** for resident local clients. Both are strictly local; no remote or cloud bridging of any kind exists.
- The backend binds to the **loopback interface only**; it is never reachable from other machines.
- **Port strategy:** a configured default loopback port is attempted first; if occupied, the backend falls back to an operating-system-assigned ephemeral port. The shell discovers the actual port through a local handshake (a port descriptor written to the per-user application data directory, or an equivalent spawn-time handshake). This specification mandates no literal port number.
- **Optional local hardening:** a per-launch random session token may be generated at spawn and shared between shell and backend. It is explicitly **not** derived from hardware, OS stores, or any persistent machine identity, and it never leaves the machine.

---

> **Verbatim source — FS-CRAWL-001 v1.4: 2.3 Startup, shutdown, and recovery — verbatim and unabridged**

### 2.3 Startup, shutdown, and recovery

**Startup sequence:**

1. Shell launches and displays a splash window.
2. Shell spawns the backend process under the mandated runtime performance profile (section 13).
3. Backend applies schema migrations and first-run seeding (section 9.4) against the configured database provider, then opens the API and hub.
4. Backend signals readiness; shell confirms via a health probe.
5. Shell loads the main window; the UI connects to the hub and hydrates from the API.

**Shutdown sequence:** the shell requests a graceful stop; the backend checkpoints any running crawl, drains workers and background services, closes the database, and exits; the shell then exits.

**Crash recovery:** if the backend exits unexpectedly, a local native diagnostic minidump is captured (section 10.5) and the shell restarts the backend with bounded backoff. On restart, the backend marks any audit that was running as *interrupted* and offers resume from its checkpoint. After repeated failures, the shell shows a local recovery screen with diagnostic information, a pointer to the local crash artifacts, and a retry action.

---

> **Verbatim source — FS-CRAWL-001 v1.4: 2.5 Feature availability — verbatim and unabridged**

### 2.5 Feature availability

- **No remote feature-flag resolution exists.** Every feature is enabled by default, locally.
- Optional local-only toggles (user preferences) may disable or hide functionality; their values are stored locally and are never fetched from any service.

---

> **Verbatim source — FS-CRAWL-001 v1.4: 2.7 Background services — verbatim and unabridged**

### 2.7 Background services

The backend hosts long-lived local background services alongside the API:

- The **audit scheduler** (section 6), retention pruning and integrity maintenance (section 9.5), and export-delivery retries (section 8.5).
- Services start only after migrations and seeding complete, and before the readiness signal is emitted.
- On graceful shutdown every service is drained before the database closes; on crash recovery each service re-derives its state from persisted data — no scheduling state exists only in memory.
- A failing background service is logged locally and restarted with bounded backoff; it never takes down the API or an in-flight audit.

---

---

> **Verbatim source — FS-CRAWL-001 v1.4: 13. Non-Functional Requirements — complete section, verbatim and unabridged**

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

---

> **Verbatim source — FS-CRAWL-002 v1.1: 6.2 Plugin directories and discovery — verbatim and unabridged**

### 6.2 Plugin directories and discovery

- **Per-user directory:** `%APPDATA%\CrawlForge\plugins\` on Windows; platform equivalents per §13 portability: `~/Library/Application Support/CrawlForge/plugins` (macOS), `~/.config/CrawlForge/plugins` (Linux).
- **Workspace-local directory:** `plugins\` at the workspace root; loaded only while that workspace/project is active.
- **Discovery:** scanned at shell startup. Insertion into the §2.3 startup sequence: after step 5 (main window loads, UI hydrates), the shell scans both directories, validates manifests, loads enabled plugins, and invokes `onLoad`. Plugin failures never block the main window or the backend. A manual **rescan** action exists in settings.
- **Structure:** each plugin is a directory containing a `plugin.json` manifest plus local module files (JS entry, optional HTML/CSS/assets). All code is local; remote code loading is prohibited.
- **Precedence and trust:** on plugin-id collision, the workspace-local plugin overrides the per-user one, with a visible UI warning. Workspace plugins require a one-time local trust prompt per workspace before first load.

---

> **Verbatim source — FS-CRAWL-002 v1.1: 8.9 Offline, packaging, and licensing mandates — verbatim and unabridged**

### 8.9 Offline, packaging, and licensing mandates

- All libraries and assets bundle locally; no CDN, no remote fonts or icons (local icon set); CSP `default-src 'self'` with exactly two exception classes: the sandboxed crawl preview (§12) and user-granted plugin destinations (FS-CRAWL-002 §6.4).
- Permissive open-source license compliance; a local attribution file ships with the bundle.
- The §13 packaging asset manifest gains: UI bundle, local icon set, attribution file, bundled WAF signature catalogue (FS-CRAWL-002 §2.3), and per-language stop-word lists (FS-CRAWL-002 §5.2).
- Every new surface is localizable through the parent localization framework (§13); plugin labels localize via manifest maps (FS-CRAWL-002 §6.3).

---
