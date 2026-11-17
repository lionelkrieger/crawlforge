# Limits & AIMD Politeness

> **CrawlForge modular architecture — area of concern: `03-crawl-engine`.**
> Every source section below is reproduced **verbatim and unabridged** from the immutable master documents. No summarization, paraphrase, or omission has been applied.

| Source document | Version | Sections reproduced in this file |
|---|---|---|
| FS-CRAWL-001 — `functional-specification.md` | v1.4 | §4 (complete section) |
| FS-CRAWL-002 — `crawlforge-extensions-and-ui-stack.md` | v1.1 | §3 (complete section) |

**Notation.** Cross-references inside the reproduced text retain the source convention: a bare `§n` refers to **FS-CRAWL-001**; `FS-CRAWL-002 §n` refers to the extension specification. The [README traceability matrix](../README.md) maps every source section to its file(s) in this hierarchy.

---

> **Verbatim source — FS-CRAWL-001 v1.4: 4. Crawler Limits Subsystem — complete section, verbatim and unabridged**

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

---

> **Verbatim source — FS-CRAWL-002 v1.1: 3. AIMD Auto-Adaptive Politeness Controller — complete section, verbatim and unabridged**

## 3. AIMD Auto-Adaptive Politeness Controller

**Extends FS-CRAWL-001 §4.2, §4.4, §5.3.** Additional hooks: §4.1, §4.3, §5.1, §5.4, §5.7, §9.2, §10.2, §10.3, §10.5, §11.4.

### 3.1 Problem and placement

Static token-bucket rate limits either under-utilize fast servers or overwhelm fragile origins before HTTP 429/503 errors appear. The **AIMD controller** operates **per host sub-queue (§5.3)**, between politeness spacing (§4.4) and worker dispatch. It continuously adapts the effective per-host request rate and concurrency from observed Time-To-First-Byte (TTFB) and response health, using Additive Increase / Multiplicative Decrease congestion control.

Placement invariants:

- The controller **never exceeds** the resolved URL throughput ceiling or worker concurrency ceiling (§4.2, resolved via §4.1). It formalizes the parent's "per-host concurrency is still bounded adaptively" clause (§4.4) for the unset-ceiling case.
- Robots `crawl-delay` directives (§4.4) remain an **inviolable floor** on request spacing, independent of AIMD.
- Token-bucket spacing enforcement (§4.4) is unchanged; AIMD sets the bucket's refill rate.
- Disabling AIMD (local toggle, per audit or globally) reverts exactly to parent static behavior.

### 3.2 Observables

- **TTFB** is measured for every fetch: time from request start to first response byte. This is an additive clarification of the §5.4 "timing" record; in rendering mode the document request's TTFB is used (consistent with §7.11).
- **Rolling baseline `B_h`** per host: exponentially weighted moving average of healthy-sample TTFB (smoothing factor λ, default 0.2), established over a warm-up of M samples (default 10). Samples that trigger a decrease never update the baseline; the baseline is frozen during cooldown.
- **Response health:** 2xx/3xx without a challenge match = healthy. HTTP 429/503 = congestion signals. A `waf-challenge` detection (FS-CRAWL-002 §2) = congestion signal. Other errors follow parent backoff (§4.4) and do not contribute to increase streaks.

### 3.3 Parameters

All parameters are local configuration, merged through the §4.1 priority order, overridable per audit via the §5.1 *Politeness options* input, and validated per §4.3 (invalid overrides rejected with a descriptive local error; a running audit keeps its snapshot).

| Parameter | Symbol | Built-in default | Semantics |
|---|---|---|---|
| AIMD enabled | — | On | Off → parent static politeness behavior |
| Initial rate | R₀ | 1 req/s | Conservative starting rate per host |
| Additive step | α | 0.5 req/s | Linear increase per qualified success window |
| Multiplicative factor | β | 0.5 | Immediate rate halving on congestion |
| Rate floor | R_min | 0.1 req/s | Effective rate never drops below (≈ 1 request / 10 s) |
| Adaptive ceiling | R_max-auto | 10 req/s | Ceiling used only when the resolved §4.2 throughput ceiling is Unlimited |
| Success streak | K | 5 | Consecutive healthy responses required per increase |
| TTFB absolute threshold | T_abs | 800 ms | Increase-eligibility ceiling on every streak sample |
| Latency multiplier | κ | 2.5 | Decrease trigger: TTFB > κ × rolling baseline |
| Cooldown | D | 10 s | No increases for this duration after a decrease |
| Other-5xx decrease | — | Off | Optional extension of decrease triggers to 5xx beyond 429/503 |

### 3.4 Control law (mathematical contract)

Let `R_h` be the effective rate and `C_h` the effective concurrency for host h. The following recurrences define the required behavior; concrete data structures are an implementation choice.

**Additive Increase.** When K consecutive responses from host h are healthy, every sample's TTFB ≤ T_abs, and no cooldown is active:

> R_h ← min(R_h + α, R_cap_h);  streak ← 0

where `R_cap_h` = the resolved §4.2 per-host throughput ceiling when set, else R_max-auto; additionally R_h ≤ 1 / crawl-delay whenever robots declares a crawl-delay (§4.4). When the §4.2 ceiling is global, the scheduler additionally ensures the sum of active host rates never exceeds it.

**Multiplicative Decrease.** Immediately on any trigger in { TTFB > κ × B_h; WAF soft-block detected (FS-CRAWL-002 §2); HTTP 429; HTTP 503 } (plus other 5xx when enabled):

> R_h ← max(β × R_h, R_min);  C_h ← max(1, ⌊β × C_h⌋);  streak ← 0;  cooldown ← D

**Retry-after interplay (§4.4):** an explicit retry-after hint pauses the host sub-queue for max(retry-after, D); the rate decrease still applies.

**Concurrency coupling:** C_h = clamp(⌈R_h × L_h⌉, 1, C_share_h), where L_h is the EWMA of full response latency and C_share_h is a work-conserving fair share of the §4.2 worker concurrency across active hosts (redistribution is an implementation choice). Invariant: Σ C_h ≤ the resolved worker concurrency ceiling.

**Cooldown expiry:** increases resume; the baseline re-warms from new healthy samples.

**Determinism:** given the same response sequence and parameters, the adjustment sequence is reproducible — a testability requirement exercised by criterion #36.

### 3.5 Lifecycle, checkpointing, and recovery

- Controller state per (audit, host): R_h, C_h, B_h, streak, cooldown-until, last-adjustment metadata.
- **Pause (§5.7):** state is serialized into the `crawl_checkpoints` payload (additive field, §9.2); **resume** rehydrates it exactly.
- **Crash recovery (§2.3, §2.7):** state is re-derived conservatively — hosts restart at R₀ with fresh warm-up. No scheduling state exists only in memory.
- **Completion:** final per-host state is summarized into `metric_summaries` under the `host-throttle` metric key (§8.1 pattern).

### 3.6 Persistence (extends §9.2)

| Object | Kind | Key fields (abstract) | Notes |
|---|---|---|---|
| `host_throttle_events` | New table | Audit reference; host; timestamp; trigger (`additive-increase`, `latency-spike`, `waf-soft-block`, `http-429`, `http-503`, `retry-after`, `cooldown-expiry`, `manual-override`); rate before/after; concurrency before/after; TTFB sample; baseline at event | Bounded: latest N events per host per audit retained (default 500, configurable); pruned with retention (§9.5); index (audit, host, timestamp) |
| `crawl_checkpoints` | Additive payload field | Serialized per-host controller state | FS-CRAWL-002 §3.5 pause/resume |
| `metric_summaries` | New metric key | `host-throttle` (dimensions: host) | Final-state summary |

### 3.7 Telemetry and contracts (extends §10.2, §10.3, §10.5, §11.4)

- **New hub event `host-throttle`** (server → shell): audit identifier; host; trigger; rate before/after; concurrency before/after; TTFB sample; rolling baseline. Emission: on every adjustment, coalesced to at most one message per second per host (parent cadence discipline, §5.7).
- **`crawl-progress` additive fields:** throttled-host count and mean effective rate.
- **REST:** GET `/api/audits/{id}/hosts` — current per-host controller state and adjustment summaries; `includeHistory=true` adds recent events with pagination. POST `/api/audits/{id}/hosts/{host}/override` — pin a manual rate or clear the pin; a pinned host suspends AIMD until cleared and emits a `manual-override` event.
- **MCP resource `host-throttle`** (§11.4 address pattern): audit reference + optional host filter; read-only current state and recent adjustments. The `crawl-progress` resource gains the throttle summary fields.
- **Local metrics (§10.5):** additive families — per-host effective rate and concurrency gauges, adjustment counters by trigger. Host is a local technical dimension; no identity dimensions (§3 rows 1, 6).

### 3.8 UI requirements

- **Live per-host throttle panel** (report window): effective-rate sparkline with TTFB baseline overlay (Apache ECharts, FS-CRAWL-002 §8), trigger badges, and a virtualized adjustment log (FS-CRAWL-002 §7).
- **Manual pin control** per host, with clear visual state when AIMD is suspended for that host.
- **Audit configuration form:** AIMD parameter group with client-side validation mirroring §4.3 (Mantine forms, FS-CRAWL-002 §8).
- **Dashboard:** throttled-host count card during running audits.

**Acceptance hook:** FS-CRAWL-002 §9, criterion #36.

---
