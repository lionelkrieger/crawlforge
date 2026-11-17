# Real-Time WebSocket Hub

> **CrawlForge modular architecture — area of concern: `05-api-hub-and-mcp`.**
> Every source section below is reproduced **verbatim and unabridged** from the immutable master documents. No summarization, paraphrase, or omission has been applied.

| Source document | Version | Sections reproduced in this file |
|---|---|---|
| FS-CRAWL-001 — `functional-specification.md` | v1.4 | §10.3 |
| FS-CRAWL-002 — `crawlforge-extensions-and-ui-stack.md` | v1.1 | §7.2, Appendix C (complete) |

**Notation.** Cross-references inside the reproduced text retain the source convention: a bare `§n` refers to **FS-CRAWL-001**; `FS-CRAWL-002 §n` refers to the extension specification. The [README traceability matrix](../README.md) maps every source section to its file(s) in this hierarchy.

---

> **Verbatim source — FS-CRAWL-001 v1.4: 10.3 Real-time hub — verbatim and unabridged**

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

---

> **Verbatim source — FS-CRAWL-002 v1.1: 7.2 Live status-bar telemetry — verbatim and unabridged**

### 7.2 Live status-bar telemetry

- **New hub event `system-health`** (server → shell), emitted **every 2 seconds** (small jitter permitted) whenever the shell is connected — during audits and while idle, independent of per-audit subscriptions. This extends §10.3 alongside the existing `crawl-progress` event, whose ≤ 1/s cadence (§5.7) is unchanged.

| Payload field | Source | Semantics |
|---|---|---|
| `timestamp` | Backend | UTC emission time |
| `backendWorkingSetBytes` | Backend OS counters | Backend process working-set RAM |
| `managedHeapBytes` | Backend runtime | Managed heap size under the mandated server-GC profile (§13) |
| `gcGen0/1/2Counts` | Backend runtime | GC generation collection counts, aggregated across server-GC heaps |
| `sqliteWalBytes` | Embedded provider | Write-ahead log file size (§2.4); null when the external provider is active |
| `auditId` (optional) | Backend | Present while an audit is running |

- **Frontend JS heap** is measured locally in the renderer at each heartbeat tick via the standard performance-memory API where available (otherwise reported as unavailable). It is composed into the status bar client-side and never sent to the backend.
- **Persistent bottom status bar** in the main hub and report windows (§12): backend working set, managed heap, GC generation counts, WAL size, frontend JS heap; crawl throughput appended while running. Advisory color thresholds are locally configurable (defaults: working-set advisory 2 GiB, WAL advisory 256 MiB); advisories never trigger automatic destructive actions.
- **Manual "Compact / Trim Working Set" action** (status-bar button and command palette, FS-CRAWL-002 §8.2): POST `/api/system/compact` performs managed GC compaction, a SQLite WAL checkpoint (truncate mode, embedded provider), and an OS working-set trim where the platform supports it. It returns before/after snapshots and the actions performed; is rate-limited (≥ 10 s between invocations); never interrupts an in-flight audit or transaction; and is logged locally.
- **On-demand snapshot:** GET `/api/system/health` returns the same payload as the heartbeat.

---

> **Verbatim source — FS-CRAWL-002 v1.1: Appendix C — Additive Real-Time Hub Events (extends FS-CRAWL-001 §10.3) — complete section, verbatim and unabridged**

## Appendix C — Additive Real-Time Hub Events (extends FS-CRAWL-001 §10.3)

| Event | Direction | Payload fields | Emission rule |
|---|---|---|---|
| `waf-blocks` | Server → shell | Audit identifier; batch of (URL identifier, canonical URL, provider, challenge class, confidence) | Throttled batches, at most once per second |
| `host-throttle` | Server → shell | Audit identifier; host; trigger; rate before/after; concurrency before/after; TTFB sample; rolling baseline | On every adjustment, coalesced ≤ 1/s per host |
| `system-health` | Server → shell | Timestamp; backend working-set bytes; managed heap bytes; GC generation counts; WAL bytes (nullable); optional audit identifier | Every 2 seconds while the shell is connected, idle or running |
| `matrix-row-validated` | Server → shell | Audit identifier; batch of (URL identifier, source URL, verdict) | Throttled batches, at most once per second (FS-CRAWL-002 §10.8) |
| `log-import-progress` | Server → shell | Import identifier; lines parsed/accepted/rejected; phase | At most once per second while importing (FS-CRAWL-002 §11.8) |
| `log-import-completed` | Server → shell | Import identifier; final counters; correlation summary counts | Once per import finalization (FS-CRAWL-002 §11.8) |
| `embedding-progress` | Server → shell | Audit identifier; embedded count; total eligible; provider; phase | At most once per second while running (FS-CRAWL-002 §12.9) |
| `embedding-completed` | Server → shell | Audit identifier; final counts; pass summaries (clusters, opportunities, low-relevance links) | Once per job finalization (FS-CRAWL-002 §12.9) |

Additive payload fields on parent events: `crawl-progress` gains `wafBlocked`, throttled-host count, mean effective rate, and optional matrix counters (validated/total) while a migration matrix is attached; `url-batch` gains optional block reason/provider on blocked entries; `audit-completed` final counters gain the WAF sub-count. Analytics detections and GEO findings ride the existing `findings-updated` / `url-batch` events (no new events for FS-CRAWL-002 §§13–15). Parent emission rules and the subscribe/unsubscribe contract are unchanged.
