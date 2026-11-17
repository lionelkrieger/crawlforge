# Metrics & Diagnostics

> **CrawlForge modular architecture — area of concern: `05-api-hub-and-mcp`.**
> Every source section below is reproduced **verbatim and unabridged** from the immutable master documents. No summarization, paraphrase, or omission has been applied.

| Source document | Version | Sections reproduced in this file |
|---|---|---|
| FS-CRAWL-001 — `functional-specification.md` | v1.4 | §10.5 |
| FS-CRAWL-002 — `crawlforge-extensions-and-ui-stack.md` | v1.1 | §3.7, §7.3 |

**Notation.** Cross-references inside the reproduced text retain the source convention: a bare `§n` refers to **FS-CRAWL-001**; `FS-CRAWL-002 §n` refers to the extension specification. The [README traceability matrix](../README.md) maps every source section to its file(s) in this hierarchy.

---

> **Verbatim source — FS-CRAWL-001 v1.4: 10.5 Local metrics and diagnostics — verbatim and unabridged**

### 10.5 Local metrics and diagnostics

- **Standardized local metrics:** the metrics endpoint (section 10.2) serves a **pull-only, loopback-only** exposition in a standardized text format (Prometheus-class). There is no push gateway, no remote export, and no scraping by any vendor service.
- Metric families (abstract): crawl counters and throughput; frontier depth; audit phase durations; findings emitted by severity; database read/write latency; content-store size; hub connection count; runtime memory and garbage-collection statistics.
- Metric labels are limited to local technical dimensions (audit identifier, phase, status class). **No user, machine, or account dimensions exist** (section 3, rows 1 and 6).
- **Native crash artifacts:** on abnormal backend termination, a native diagnostic **minidump** is written to a local diagnostics directory. Artifact count and total size are bounded; the oldest artifacts are reclaimed first. Minidumps are **never transmitted automatically**; they exist solely for user-initiated troubleshooting.
- **Local logs:** structured, user-viewable, and purgeable; they carry no identity dimensions.
- The settings surface and the recovery screen expose the diagnostics directory (open, inspect, purge); the diagnostic-artifacts endpoint (section 10.2) provides the same capabilities programmatically.

---

---

> **Verbatim source — FS-CRAWL-002 v1.1: 3.7 Telemetry and contracts (extends §10.2, §10.3, §10.5, §11.4) — verbatim and unabridged**

### 3.7 Telemetry and contracts (extends §10.2, §10.3, §10.5, §11.4)

- **New hub event `host-throttle`** (server → shell): audit identifier; host; trigger; rate before/after; concurrency before/after; TTFB sample; rolling baseline. Emission: on every adjustment, coalesced to at most one message per second per host (parent cadence discipline, §5.7).
- **`crawl-progress` additive fields:** throttled-host count and mean effective rate.
- **REST:** GET `/api/audits/{id}/hosts` — current per-host controller state and adjustment summaries; `includeHistory=true` adds recent events with pagination. POST `/api/audits/{id}/hosts/{host}/override` — pin a manual rate or clear the pin; a pinned host suspends AIMD until cleared and emits a `manual-override` event.
- **MCP resource `host-throttle`** (§11.4 address pattern): audit reference + optional host filter; read-only current state and recent adjustments. The `crawl-progress` resource gains the throttle summary fields.
- **Local metrics (§10.5):** additive families — per-host effective rate and concurrency gauges, adjustment counters by trigger. Host is a local technical dimension; no identity dimensions (§3 rows 1, 6).

---

> **Verbatim source — FS-CRAWL-002 v1.1: 7.3 Metrics integration (extends §10.5) — verbatim and unabridged**

### 7.3 Metrics integration (extends §10.5)

The parent metric family "runtime memory and garbage-collection statistics" is formalized into: working-set bytes, managed-heap bytes, GC generation counters, and WAL-size gauges, exposed on GET `/metrics` in the standardized local exposition. Labels remain local technical dimensions only; no identity dimensions (§3 rows 1, 6). Telemetry never leaves the machine.
