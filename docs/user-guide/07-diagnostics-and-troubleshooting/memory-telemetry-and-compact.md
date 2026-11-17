# Memory Telemetry and Compact / Trim

CrawlForge shows you what it is doing with memory, live. A persistent status bar in the main hub and report windows refreshes every two seconds with the backend's working set, managed heap, garbage-collection counters, the embedded database's WAL size, and the interface's own JavaScript heap. When you want to reclaim memory, a single manual action — **Compact / Trim Working Set** — compacts the managed heap, checkpoints the WAL, and trims the process working set.

Everything on this page is local. Telemetry never leaves the machine, carries no identity dimensions, and never triggers automatic destructive actions. This guide explains each number, the advisory thresholds, and when (and how) to act.

## The persistent status bar

The status bar sits at the bottom of the main hub and report windows. It is always present while the shell is connected — during audits and while idle. While an audit is running, crawl throughput is appended to the display.

The bar shows:

| Display | Source | What it tells you |
|---|---|---|
| Backend working set | Backend OS counters | Total RAM the backend process currently holds |
| Managed heap | Backend runtime | Size of the managed heap under the Server GC profile |
| GC generation counts | Backend runtime | How many collections have run per generation (0 / 1 / 2) |
| WAL size | Embedded database provider | Current write-ahead log file size; hidden/null when an external database provider is active |
| Frontend JS heap | Measured locally in the renderer | Memory used by the interface itself |
| Crawl throughput | Backend (while running) | Appended only while an audit is in flight |

Advisory color thresholds are locally configurable. Defaults:

| Threshold | Default | Bounds | Notes |
|---|---|---|---|
| Working-set advisory | 2 GiB | Locally configurable in application settings | Advisory only — crossing it changes the display color, nothing else |
| WAL advisory | 256 MiB | Locally configurable in application settings | Applies to the embedded provider's WAL; advisory only |

> **Note:** Advisories never trigger automatic destructive actions. No threshold compacts, prunes, cancels, or restarts anything on its own. Every reclaim action is manual and under your control.

## The 2-second heartbeat

The numbers arrive over the real-time hub as a `system-health` event:

- Emitted **every 2 seconds** (small jitter permitted) whenever the shell is connected.
- Runs during audits **and while idle**, independent of per-audit subscriptions — you do not need to open an audit to see live memory.
- Status-bar freshness is within 2 seconds from backend sample to display, at less than 1% CPU overhead.
- The separate `crawl-progress` event keeps its own cadence of at most one update per second; the heartbeat does not change it.

### Heartbeat payload fields

| Field | Source | Meaning |
|---|---|---|
| `timestamp` | Backend | UTC emission time |
| `backendWorkingSetBytes` | Backend OS counters | Backend process working-set RAM |
| `managedHeapBytes` | Backend runtime | Managed heap size under the mandated Server GC profile |
| `gcGen0Counts` / `gcGen1Counts` / `gcGen2Counts` | Backend runtime | GC generation collection counts, aggregated across server-GC heaps |
| `sqliteWalBytes` | Embedded provider | Write-ahead log file size; **null when the external provider is active** |
| `auditId` (optional) | Backend | Present while an audit is running |

### The frontend JS heap is measured on your side

The interface's JavaScript heap is measured locally in the renderer at each heartbeat tick, via the standard performance-memory API where available. Where the API is not available, it is reported as unavailable — that is a platform capability note, not an error.

Two properties matter:

- The JS heap value is **composed into the status bar client-side**.
- It is **never sent to the backend**. The backend heartbeat carries backend numbers only.

## Reading the numbers

### Working set vs managed heap

- **Working set** (`backendWorkingSetBytes`) is the operating system's view: all RAM the backend process holds, including runtime overhead, native buffers, and cached pages.
- **Managed heap** (`managedHeapBytes`) is the runtime's view: the memory held by managed objects under the Server GC profile.

The working set is normally larger than the managed heap. A gap between them is not a leak — it includes native allocations and pages the OS has not yet reclaimed. The Compact / Trim action addresses both: it compacts the managed heap and asks the OS to trim the working set where the platform supports it.

### GC generation counts

The three counters tell you how the garbage collector has been working:

| Counter | What it counts | How to read it |
|---|---|---|
| `gcGen0Counts` | Generation 0 collections | Short-lived allocations; rises steadily during crawling — normal and cheap |
| `gcGen1Counts` | Generation 1 collections | Intermediate survivors; rises more slowly |
| `gcGen2Counts` | Generation 2 collections | Full collections of long-lived data; comparatively rare and more expensive |

Counts are aggregated across the server-GC heaps. Under `System.GC.Server` with concurrent collection enabled, the backend runs multiple heaps sized for high-throughput crawling — the counters you see are the aggregate, and collections run concurrently with application work. A rising gen0 count during a large audit is expected behavior, not a problem.

### WAL size (embedded provider only)

`sqliteWalBytes` is the current size of the write-ahead log file. During heavy ingestion the WAL grows as transactions accumulate; a checkpoint folds it back into the main database file. The Compact / Trim action performs a WAL checkpoint in **truncate mode** on the embedded provider, which is why WAL size typically drops sharply after a compact.

When you run on your own external database server, this field is null — durability uses the server's native transactional mechanism, and there is no embedded WAL to display.

### Memory behavior under the Server GC profile

The backend's runtime profile is fixed at packaging time: server garbage collection (`System.GC.Server`) with concurrent collection enabled, sized for high-throughput concurrent crawling and large read-model working sets. `GET /api/version` reports the active runtime profile.

Practical consequences for what you see:

- Memory is allowed to grow to support throughput; the profile favors performance over a minimal footprint.
- Streaming persistence keeps memory bounded at the target scale (several hundred thousand URLs) — growth during a large audit is bounded by design, not unbounded accumulation.
- The interface itself never retains full-dataset arrays: every large list is server-windowed, hub ingestion uses bounded ring buffers, charts dispose on unmount, and graph models are windowed. The JS heap you see reflects a bounded working window, not your whole dataset.

## Compact / Trim Working Set

The manual reclaim action is available in two places:

- The **status-bar button**.
- The **Ctrl+K command palette** (search for the compact action).

Under the hood it calls `POST /api/system/compact`, which performs:

| Step | What happens | Provider scope |
|---|---|---|
| Managed GC compaction | The runtime compacts the managed heap | Both providers |
| WAL checkpoint (truncate mode) | The write-ahead log is checkpointed and truncated | Embedded provider only |
| OS working-set trim | The process working set is trimmed where the platform supports it | Platform-dependent |

The action's contract:

| Property | Behavior |
|---|---|
| Response | Returns before/after snapshots and the actions performed |
| Rate limit | At least 10 seconds between invocations |
| Safety | Never interrupts an in-flight audit or transaction |
| Timing | Typically completes in under 5 seconds |
| Logging | Logged locally |

> **Note:** Compact is safe to run while an audit is in flight — it never interrupts the audit or its transactions. The rate limit simply prevents hammering the action; if you invoke it again within 10 seconds, wait and retry.

### Running a compact

1. Look at the status bar. Note the current working set, managed heap, and (on the embedded provider) WAL size.
2. Click the **Compact / Trim Working Set** button in the status bar, or open the Ctrl+K command palette and run the compact action.
3. Wait for the response — typically under 5 seconds.
4. Read the returned before/after snapshots and the list of actions performed. The status bar reflects the new numbers on its next heartbeat.

### What the before/after snapshots tell you

- **Managed heap before vs after** — how much the GC compaction reclaimed.
- **Working set before vs after** — how much the OS trim released (where supported).
- **WAL before vs after** — how much the checkpoint folded back into the database file (embedded provider).
- **Actions performed** — which of the three steps actually ran on your platform and provider. If the OS does not support working-set trim, that step is simply absent from the list; the other steps still run.

A compact that reclaims little is not a failure — it means there was little reclaimable at that moment. The snapshots exist so you can see exactly what happened, locally.

## On-demand snapshot

If you want the same payload outside the heartbeat cadence — for a script or a one-off check — `GET /api/system/health` returns the identical payload as the `system-health` event: timestamp, backend working set, managed heap, GC generation counts, WAL size (null on the external provider), and the optional running-audit identifier.

The standardized local metrics endpoint (`GET /metrics`) also exposes the runtime memory and GC families in Prometheus-class text format for your own local tooling — see [Logs, minidumps, and metrics](../07-diagnostics-and-troubleshooting/logs-minidumps-and-metrics.md).

## When to act

| What you see | What it means | What to do |
|---|---|---|
| Working-set advisory color (default 2 GiB) | The backend holds more RAM than your advisory threshold | Run Compact / Trim; if it stays high during a large audit, that is bounded streaming behavior — check available disk for the content store |
| WAL advisory color (default 256 MiB) | The embedded WAL has grown during ingestion | Run Compact / Trim — the truncate-mode checkpoint folds it back |
| WAL field absent/null | You are on the external database provider | Nothing — expected by design |
| JS heap reported unavailable | The renderer's performance-memory API is not available on this platform | Nothing — a capability note, not an error |
| Gen0 count climbing during a crawl | Normal short-lived allocation churn | Nothing — expected under Server GC |
| Repeated compacts reclaim little | Little reclaimable memory at that moment | Nothing — the snapshots confirm the action ran; advisories are informational |
| Backend restarts or a recovery screen appears | A crash, not a memory advisory | See [Logs, minidumps, and metrics](../07-diagnostics-and-troubleshooting/logs-minidumps-and-metrics.md) |

> **Tip:** Thresholds are preferences, not limits. Set them where you want early visual warning; the product will never act on them automatically. Both defaults (working-set 2 GiB, WAL 256 MiB) are stored in application settings and can be changed locally.

## How telemetry reaches the status bar

The interface keeps exactly one WebSocket hub connection to the backend. An event demultiplexer routes incoming events into bounded buffers, and the status bar subscribes to the `system-health` slice:

- Hub ingestion uses **bounded ring buffers** — for example, recent activity keeps at most 1,000 entries.
- Counters are **aggregated, never accumulated as raw event history**.
- The heartbeat flows independently of per-audit subscriptions; opening or closing an audit does not start or stop it.

This is why the status bar stays cheap: it renders a small, fixed-size snapshot every two seconds, not a growing history.

## Frontend memory hygiene

The interface is built so its own memory stays bounded regardless of dataset size:

| Hygiene rule | Effect |
|---|---|
| No surface retains full-dataset arrays | Every large list is server-windowed; the renderer holds only the visible window |
| Bounded ring buffers for hub ingestion | Recent-activity and event buffers cannot grow without limit |
| Charts dispose on unmount | Chart instances and their canvas memory are released when you leave a surface |
| Graph models are windowed | The link-graph model holds a bounded subset, with a level-of-detail cap |
| Window caches are LRU-bounded | Cached explorer windows evict least-recently-used entries |
| Plugin relays are coalesced and dropped under backpressure | A slow plugin cannot cause unbounded queue growth |
| Listeners detach on tab deactivation | Inactive tabs stop receiving updates |

If the frontend JS heap climbs while you work, it reflects the bounded working window (open explorers, charts, and graph views), not your whole dataset. Closing surfaces releases their memory through the rules above.

## Performance budgets

The telemetry and responsiveness targets the product is built against:

| Budget | Target |
|---|---|
| Heartbeat cadence | Every 2 seconds |
| Heartbeat overhead | < 1% CPU |
| Status-bar freshness | ≤ 2 seconds from backend sample to display |
| Compact action | < 5 seconds typical; before/after snapshot returned |
| Dashboard reads | < 1 second at target scale |
| Tabular scrolling at 500k+ rows | 60 FPS with at most 50 mounted DOM rows |

The scrolling budget is why explorers stay smooth at half a million rows: only a bounded window of rows is ever mounted in the DOM, and the server supplies windows on demand. See [Dashboards and virtualized explorers](../04-exploring-reports-and-visualizations/dashboards-and-virtualized-explorers.md).

## Throughput and politeness while running

While an audit is in flight, the status bar appends crawl throughput. Two related event streams keep their own cadences:

| Stream | Cadence | What it adds |
|---|---|---|
| `crawl-progress` | At most one update per second | Progress counters, plus throttled-host count and mean effective rate |
| `host-throttle` | On every AIMD adjustment, coalesced to at most one message per second per host | Per-host rate and concurrency before/after, trigger, TTFB sample, rolling baseline |
| `system-health` | Every 2 seconds | The memory payload described above, with the optional running `auditId` |

If you see throughput dip while the throttled-host count rises, the AIMD politeness controller is protecting a target host that is pushing back — that is correct behavior, not a memory problem. The per-host effective rate and concurrency gauges also appear in the local metrics endpoint for your own tooling. See [Crawl limits and AIMD politeness](../03-running-and-scheduling-audits/crawl-limits-and-aimd-politeness.md).

## What a compact does on each provider

| Step | Embedded provider | External provider |
|---|---|---|
| Managed GC compaction | Runs | Runs |
| WAL checkpoint (truncate mode) | Runs — `sqliteWalBytes` typically drops sharply | Not applicable — no embedded WAL; the step is absent from the actions list |
| OS working-set trim | Runs where the platform supports it | Runs where the platform supports it |

The returned actions-performed list tells you exactly which steps ran. On an external provider, a compact that reports only compaction (and possibly trim) is complete and correct — there is simply no WAL to checkpoint.

### A typical reading cycle

1. **During a large crawl:** working set and managed heap rise as streaming ingestion works; gen0 counts climb steadily; on the embedded provider the WAL grows with accumulated transactions. This is bounded, expected behavior.
2. **Advisory color appears:** your locally configured threshold was crossed. Nothing happens automatically.
3. **You run Compact / Trim:** the snapshots show what was reclaimed — managed heap down, working set trimmed where supported, WAL folded back on the embedded provider.
4. **Next heartbeat (within 2 seconds):** the status bar reflects the new numbers. The audit, if running, was never interrupted.

## What telemetry never does

- **Never leaves the machine.** The heartbeat flows over the loopback hub to your status bar; the JS heap never even reaches the backend. There is no push gateway, no remote export, and no vendor scraping.
- **Carries no identity dimensions.** No user, machine, or account fields exist in any telemetry payload, metric, or log.
- **Never triggers automatic destructive actions.** Advisories color the display; only you can compact, purge, or prune.
- **Never gates functionality.** No feature, limit, or report depends on telemetry values or any remote input.

## Related guides

- [Logs, minidumps, and metrics](logs-minidumps-and-metrics.md)
- [Database configuration](../01-getting-started/database-configuration.md)
- [Installation and first launch](../01-getting-started/installation-and-first-launch.md)
- [Dashboards and virtualized explorers](../04-exploring-reports-and-visualizations/dashboards-and-virtualized-explorers.md)
- [Crawl limits and AIMD politeness](../03-running-and-scheduling-audits/crawl-limits-and-aimd-politeness.md)
- [Creating projects and scopes](../02-managing-projects-and-crawls/creating-projects-and-scopes.md)

[← Documentation portal](../README.md)
