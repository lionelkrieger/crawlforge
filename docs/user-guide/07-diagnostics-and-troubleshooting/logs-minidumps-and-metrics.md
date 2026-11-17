# Logs, Minidumps, and Metrics

CrawlForge keeps three kinds of diagnostic artifacts, all strictly local: structured logs, native crash minidumps, and a pull-only metrics endpoint. They exist for one purpose — your own troubleshooting. None of them is ever transmitted automatically, none carries identity dimensions, and all are user-viewable and purgeable.

This guide covers where each artifact lives, how to inspect and purge them, what the metrics endpoint exposes, and practical workflows for the problems you are most likely to investigate.

> **Note:** There is no telemetry, no remote crash reporting, and no automatic transmission of any diagnostic data. Logs, minidumps, and the automation action log are produced and stored only on the local filesystem. The metrics endpoint is pull-only and loopback-only — no push gateway, no remote export, and no scraping by any vendor service.

## Local structured logs

The backend writes structured logs to the local diagnostics directory inside your per-user application data directory (`%APPDATA%\CrawlForge` on Windows, `~/Library/Application Support/CrawlForge` on macOS, `~/.config/CrawlForge` on Linux).

| Property | Behavior |
|---|---|
| Format | Structured local log entries |
| Identity dimensions | None — no user, machine, or account enrichment of any kind |
| Visibility | User-viewable from the settings surface and the recovery screen |
| Purgeability | User-purgeable from the same surfaces |
| Retention | Bounded local retention, separate from project audit retention |
| Transmission | Never transmitted automatically |

Logs record the product's own operational events: startup and migration outcomes, background-service failures and restarts (a failing background service is logged locally and restarted with bounded backoff), the Compact / Trim action, and locally blocked plugin network requests. They are the first place to look when something does not behave as expected.

> **Tip:** Diagnostic artifacts (logs and minidumps) have their own bounded local retention, independent of the per-project audit retention that prunes crawl data. Purging diagnostics never touches your projects, audits, or reports.

## Native crash minidumps

On abnormal backend termination, a native diagnostic **minidump** is written to the local diagnostics directory. Minidumps capture the process state at the moment of the crash for user-initiated troubleshooting.

| Property | Behavior |
|---|---|
| Trigger | Abnormal backend termination only |
| Location | Local diagnostics directory |
| Count and size | Bounded; the oldest artifacts are reclaimed first |
| Transmission | Never transmitted automatically |
| Purpose | Solely user-initiated troubleshooting |
| Management | Viewable and purgeable from settings and the recovery screen |

### How crash recovery uses them

When the backend exits unexpectedly, the shell handles recovery locally:

1. A minidump is captured in the diagnostics directory.
2. The shell restarts the backend with bounded backoff.
3. On restart, the backend marks any audit that was running as *interrupted* and offers resume from its checkpoint.
4. After repeated failures, the shell shows a local **recovery screen** with diagnostic information, a pointer to the local crash-artifact directory, and a retry action.

Everything in this flow stays on your machine. The recovery screen is your entry point when the backend will not stay up.

## Managing diagnostic artifacts

Three surfaces expose the same capabilities — open, inspect, purge:

| Surface | What you can do |
|---|---|
| Settings → diagnostics management | Open the diagnostics directory, inspect logs and crash artifacts, purge them |
| Recovery screen | Local diagnostic information plus a pointer to the crash-artifact directory after repeated backend failures |
| Diagnostic-artifacts API | The same capabilities programmatically (below) |

### The diagnostic-artifacts endpoint

| Operation | Method & path | Input | Output |
|---|---|---|---|
| List artifacts | `GET /api/diagnostics/artifacts` | Kind filter (log or minidump) | Local artifact metadata: kind, created-at, size |
| Purge artifacts | `DELETE /api/diagnostics/artifacts` | — | Purge confirmation |

In normal use you never call these endpoints yourself — the settings surface uses them for you. They are documented so you know exactly what "inspect" and "purge" operate on: local metadata and local files, nothing else.

### Inspecting and purging

1. Open Settings → diagnostics management (or the recovery screen if the backend is failing).
2. Open the diagnostics directory to inspect the raw local files, or review the artifact list (kind, created-at, size).
3. Purge when you are done troubleshooting. Purging removes local artifacts only; it has no effect on projects, audits, the content store, or settings.

> **Warning:** Minidumps and logs can contain details of the pages you crawled and the operations you ran (they carry no identity dimensions, but they are still your data). Purge them if you are sharing a machine or decommissioning an installation — and remember they were never sent anywhere.

## Pull-only local metrics

The backend serves a standardized metrics exposition at `GET /metrics` in Prometheus-class text format. It is designed for your own local monitoring tooling, should you want it.

| Property | Behavior |
|---|---|
| Endpoint | `GET /metrics` on the loopback backend |
| Format | Standardized text exposition (Prometheus-class) |
| Direction | Pull-only — your scraper fetches; the product never pushes |
| Reachability | Loopback-only — never reachable from other machines |
| Push gateway | None |
| Remote export | None |
| Vendor scraping | None — no vendor service scrapes or receives these metrics |

### Metric families

| Family | What it covers |
|---|---|
| Crawl counters and throughput | Requests, outcomes, and crawl rate |
| Frontier depth | Current crawl frontier depth |
| Audit phase durations | Time spent per audit phase |
| Findings emitted | Counts by severity |
| Database latency | Read/write latency on the configured provider |
| Content-store size | Disk held by stored response bodies and rendered artifacts |
| Hub connections | Current real-time hub connection count |
| Runtime memory and GC | Working-set bytes, managed-heap bytes, GC generation counters, and WAL-size gauges |
| Per-host politeness (AIMD) | Per-host effective rate and concurrency gauges; adjustment counters by trigger |

The runtime memory and GC family is the same data you see in the status bar heartbeat, formalized for scraping: working-set bytes, managed-heap bytes, GC generation counters, and WAL-size gauges. The [memory telemetry guide](memory-telemetry-and-compact.md) explains how to read those numbers.

### Labels: local technical dimensions only

Metric labels are limited to local technical dimensions:

- Audit identifier
- Phase
- Status class
- Host (for the per-host politeness families)

**No user, machine, or account dimensions exist** in any metric. There is nothing to anonymize because nothing identifying is ever recorded.

### Scraping with your own local tooling

If you run a local Prometheus-class scraper, you can point it at the backend:

1. Run your scraper on the same machine (the endpoint is loopback-only by design).
2. Configure a scrape target at the backend's loopback address and port, with the metrics path `/metrics`. The shell discovers the backend port through the local handshake; when the configured default port is occupied, the backend uses an operating-system-assigned ephemeral port, so read the current port from the local handshake rather than hard-coding one.
3. Choose your own scrape interval. The product imposes no cadence on pullers and never pushes.
4. Keep the scraper local. Exposing the endpoint beyond loopback is not a product feature — the backend binds to the loopback interface only.

> **Note:** Scraping is entirely optional. The status bar, the on-demand `GET /api/system/health` snapshot, and the diagnostics surfaces give you everything needed for troubleshooting without any external tooling.

## Practical troubleshooting workflows

### The backend will not stay up

1. Open the recovery screen (it appears after repeated backend failures).
2. Read the local diagnostic information shown there.
3. Open the diagnostics directory from the screen's pointer; inspect the newest minidump metadata (kind, created-at, size) and the local logs around the crash timestamps.
4. Use the retry action. On restart, any audit that was running is marked *interrupted* and offers resume from its checkpoint.
5. If you purged artifacts while investigating, remember the bounded retention reclaims oldest-first — capture what you need before purging.

### An audit was interrupted

1. Open the audit; its state is *interrupted* after a backend crash.
2. Resume from the checkpoint — crawl state is checkpointed, and no scheduling state exists only in memory.
3. If the interruption repeats, check the local logs for background-service failures (each is logged and restarted with bounded backoff without taking down the API or the audit).

### Dashboards feel slow

1. Confirm you are reading aggregates: dashboard reads are constant-cost lookups from precomputed, indexed read models, targeted at under one second at target scale.
2. Scrape or inspect the database read/write latency family on `GET /metrics` to see whether the configured provider is the bottleneck.
3. On the embedded provider, check the WAL size in the status bar; a large WAL during heavy ingestion can be checkpointed with Compact / Trim.
4. On an external provider, verify the server connection with the test-connection action in settings (reachability, authentication, migration compatibility).

### Memory or WAL advisory in the status bar

1. Read the numbers against the advisories (defaults: working-set 2 GiB, WAL 256 MiB; locally configurable).
2. Run Compact / Trim Working Set — managed GC compaction, WAL checkpoint in truncate mode (embedded provider), and OS working-set trim where supported.
3. Compare the returned before/after snapshots. Advisories never trigger automatic actions; the decision is always yours.
4. See [Memory telemetry and Compact / Trim](memory-telemetry-and-compact.md) for the full interpretation guide.

### A plugin's request was blocked

1. Check the local logs — non-granted plugin requests are blocked and logged locally.
2. Review the plugin's grants in settings; grants reach only your destination rules and are revocable at any time with immediate effect.
3. Remember plugins have zero network access by default; a blocked request is the boundary working as designed, not a fault.

### You suspect a database-provider problem

1. Run the test-connection action (settings → database provider): reachability, authentication, and migration-compatibility verdicts.
2. Check the local logs for migration failures — migrations apply at backend startup before the API serves, and the `schema_migrations` ledger records each applied version per provider dialect.
3. See [Database configuration](../01-getting-started/database-configuration.md) for the archive export/import workflow if you need to move or revert providers.

## The automation action log

If you enable the local MCP automation interface, every agent invocation is recorded in a local **automation action log** — the fourth local diagnostic surface:

| Field | Contents |
|---|---|
| Timestamp | When the invocation occurred (UTC) |
| Transport | stdio or loopback SSE/streamable HTTP |
| Tool/resource name | Which tool or resource the agent invoked |
| Argument summary | A summary of the invocation arguments |
| Outcome | The result of the invocation |

The action log is user-viewable and purgeable, like logs and minidumps, and it is pruned by project retention. It carries no identity dimensions. With the automation interface disabled (the first-run default), no action-log entries are produced. See [Local MCP server setup](../06-automation-and-ai-agents/local-mcp-server-setup.md).

## Which surface when

| Surface | Best for | Cadence / access |
|---|---|---|
| Status bar (`system-health`) | Glanceable live memory and WAL while you work | Every 2 seconds, always on |
| `GET /api/system/health` | One-off snapshot identical to the heartbeat | On demand |
| `GET /metrics` | Your own local scraper or monitoring tooling | Pull-only, loopback-only |
| Local logs | Operational events: startup, migrations, service failures, compaction, blocked plugin requests | Written as events occur; bounded retention |
| Minidumps | Crash investigation after abnormal backend termination | On crash only; bounded count/size, oldest reclaimed first |
| `GET /api/diagnostics/artifacts` | Programmatic listing (kind, created-at, size) and purge | On demand |
| Settings → diagnostics management | Open, inspect, purge without touching the API | Manual |
| Recovery screen | Backend will not stay up | After repeated failures; includes retry |
| Automation action log | Auditing what local agents did | While the automation interface is enabled |

## What is never recorded

| Never recorded | Why it matters |
|---|---|
| User, machine, or account dimensions | No identity enrichment exists in any log, metric, minidump metadata, or action-log entry |
| OS-level machine identifiers | No hardware serials, registration stores, or management instrumentation are read — there is nothing to leak |
| Secrets in clear | Credentials (site, destination, external-database, proxy) and local tokens are encrypted at rest; the settings API never returns them in clear |
| Remote copies | No artifact is transmitted automatically; there is no remote crash reporting and no push gateway |

## Diagnostics directory and storage reporting

The diagnostics directory lives inside the per-user application data directory, beside the database and the content store. Its disk usage is included in the user-visible storage reporting, so diagnostic growth is never hidden from you.

Retention boundaries are separate by design:

| Data class | Retention control |
|---|---|
| Audit data (URLs, findings, links, content artifacts, embeddings) | Per-project retention: keep the last N audits, or an age limit; transactional pruning |
| Logs and minidumps | Bounded local retention; minidump count and total size bounded, oldest reclaimed first |
| Automation action-log entries | Pruned with project retention; purgeable on demand |

Purging diagnostics is always safe for your crawl data: it removes local diagnostic artifacts only, never projects, audits, reports, or settings.

## Diagnostics on an air-gapped machine

Every surface in this guide works with no network connectivity:

- Logs and minidumps are written to the local filesystem regardless of connectivity.
- The recovery screen, settings diagnostics management, and the artifacts endpoint operate entirely on local data.
- `GET /metrics` is served over loopback — a scraper on the same machine can pull it even when the machine has no external network at all.
- Crash recovery (minidump capture, bounded-backoff restart, checkpoint resume) is fully local.

The only diagnostic-adjacent action that can ever touch the network is one you explicitly authorize elsewhere in the product (for example, a crawl itself). Diagnostics never initiate outbound traffic.

## Privacy guarantees recap

| Guarantee | Scope |
|---|---|
| No automatic transmission | Logs, minidumps, metrics, and the automation action log never leave the machine automatically |
| No identity dimensions | No user, machine, or account fields in any log, metric, or diagnostic artifact |
| No remote crash reporting | Minidumps exist solely for user-initiated troubleshooting |
| No push | Metrics are pull-only; there is no push gateway or remote export |
| User control | Every artifact is viewable and purgeable from settings, the recovery screen, or the artifacts endpoint |
| Bounded retention | Diagnostics have their own bounded local retention; minidump count and size are bounded, oldest reclaimed first |

## Related guides

- [Memory telemetry and Compact / Trim](memory-telemetry-and-compact.md)
- [Installation and first launch](../01-getting-started/installation-and-first-launch.md)
- [Database configuration](../01-getting-started/database-configuration.md)
- [Lifecycle and checkpointing](../03-running-and-scheduling-audits/lifecycle-and-checkpointing.md)
- [Local MCP server setup](../06-automation-and-ai-agents/local-mcp-server-setup.md)
- [Creating projects and scopes](../02-managing-projects-and-crawls/creating-projects-and-scopes.md)

[← Documentation portal](../README.md)
