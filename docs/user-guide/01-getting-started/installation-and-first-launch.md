# Installation and First Launch

CrawlForge is a standalone desktop website crawler and auditor. It runs entirely on your own machine: the interface, the crawl engine, the audit engine, your reports, and all diagnostics are local. There are no accounts, no sign-in, no subscriptions, and no license checks — every feature is enabled by default, and the product is fully usable on an air-gapped machine.

This guide covers what ships in the package, how the two local processes cooperate, what happens the first time you launch the application, where your data lives, and the privacy guarantees that hold from the very first second.

> **Note:** CrawlForge never checks for updates, never phones home, and never transmits diagnostic data. The only outbound traffic the product can ever generate is traffic you explicitly authorize yourself — crawling the websites you target, exports to destinations you configure, and connections to a database server you configure. All of those are off by default.

## What ships in the package

CrawlForge consists of two cooperating local processes with a strict separation of concerns:

| Process | Role | What it does |
|---|---|---|
| Desktop shell | Window host (Electron-class) | Opens windows, integrates with the operating system, loads the local UI bundle, spawns and supervises the backend. Contains no crawl or audit logic. |
| Local backend | Self-contained local HTTP service (ASP.NET Core-class) | All domain logic: crawl engine, audit engine, persistence, reporting, the real-time event hub, the local automation interface, and metrics. Ships with its own runtime. |

The shell spawns the backend as a child process, monitors its health, and restarts it automatically if it fails. The interface you see is a local web application loaded from the packaged bundle — it never loads remote content.

### Self-contained runtimes — no prerequisite installs

The backend ships with its own runtime. You do not need to install a separate runtime, framework, or database engine before using CrawlForge. The package is a self-contained asset manifest that includes:

- The backend runtime and the local UI bundle.
- Native database extensions for the embedded file-based provider, per operating system and architecture.
- Engine locale packs and per-locale spelling dictionaries.
- The stdio adapter for the local automation interface.
- Media codecs for the embedded page preview.
- A local icon set and a local open-source attribution file.
- Bundled analysis catalogs: the WAF signature catalogue, per-language stop-word lists, technology fingerprints, font-metric tables, bot CIDR ranges, and AI agent profiles.

All libraries and assets are bundled locally. There is no CDN, no remote font, and no remote icon fetch. Bundled catalogs update only with product updates — there is no runtime fetch of signatures, dictionaries, or fingerprints.

## Platform support

| Platform | Status |
|---|---|
| Windows | Primary |
| macOS | Secondary |
| Linux | Secondary |

The product is portable across these platforms with a self-contained runtime and no prerequisite installs. Platform-specific native assets (database extensions, codecs) ship inside the package for each supported operating system and architecture.

## Installing the application

1. Obtain the CrawlForge package for your platform.
2. Place the application where you want it to live. The package is self-contained; nothing else needs to be installed first.
3. Launch the desktop shell.

There is no activation step, no registration, no sign-in, and no license entry. The first-run experience proceeds directly to project creation.

> **Tip:** Because everything is local, you can install and run CrawlForge on a machine with no network connectivity at all. Crawling target websites naturally requires reaching those websites, but every other feature — including re-auditing stored content, reports, comparisons, and standalone tools operating on stored artifacts or local input — works offline. Tools that take a live target URL (URL check, render test, robots test by URL, structured-data check by URL) must reach that website.

## The runtime performance profile (Server GC)

The backend runs under a server-grade memory-management profile, fixed at packaging time and active by default:

| Setting | Value | Notes |
|---|---|---|
| Garbage collection mode | `System.GC.Server` | Server garbage collection, sized for high-throughput concurrent crawling and large read-model working sets |
| Concurrent collection | Enabled | Collections run concurrently with application work |
| Configurability | Fixed at packaging | Not a user tuning knob; the stated scale target is validated against this profile |
| Reporting | Version/diagnostics surface | `GET /api/version` reports the active runtime profile |

You do not need to configure anything here. The profile is why CrawlForge can hold audits of several hundred thousand URLs with bounded memory on commodity desktop hardware. The live memory numbers you see in the status bar are produced under this profile — see [Memory telemetry and Compact / Trim](../07-diagnostics-and-troubleshooting/memory-telemetry-and-compact.md) for how to read them.

## What happens on first launch

The startup sequence is deterministic and identical on every platform:

1. The shell launches and displays a splash window.
2. The shell spawns the backend process under the Server GC performance profile.
3. The backend applies schema migrations and first-run seeding against the configured database provider, then opens the API and the real-time hub.
4. The backend signals readiness; the shell confirms via a health probe.
5. The shell loads the main window; the UI connects to the hub and hydrates from the API.
6. The shell scans the local plugin directories, validates manifests, loads enabled plugins, and invokes their `onLoad` hook. Plugin failures never block the main window or the backend.

### First-run initialization

On the default provider, initialization completes with **zero network calls**. Migrations create all tables, then seed exactly one `application_settings` row with deterministic defaults:

| Setting | First-run default |
|---|---|
| Database provider | Local file-based embedded database |
| Crawl ceilings | Unlimited |
| Audit categories | All enabled |
| Local-only feature toggles | On |
| Default politeness | On |
| Schedules | None |
| Export destinations | None |
| External database | Not configured |
| User dictionary | Empty |
| Local MCP automation interface | Disabled |

The product is fully usable immediately after first launch, including on an air-gapped machine. There is no entitlement-driven screen and no setup wizard gating functionality — you land in the main hub and can create your first project right away.

> **Note:** Background services (the audit scheduler, retention pruning and integrity maintenance, and export-delivery retries) start only after migrations and seeding complete, and before the readiness signal is emitted. No scheduling state exists only in memory — every service re-derives its state from persisted data after a restart.

## Where your data lives

All application data remains on the local machine by default. CrawlForge uses a per-user application data directory:

| Platform | Per-user application data directory |
|---|---|
| Windows | `%APPDATA%\CrawlForge` |
| macOS | `~/Library/Application Support/CrawlForge` |
| Linux | `~/.config/CrawlForge` |

Inside this directory you will find:

- **The database** — on the default provider, a local file-based embedded database (SQLite-class). Zero configuration, zero network, fully offline. If you later configure your own external database server, the logical schema is identical; see [Database configuration](database-configuration.md).
- **The content store** — a dedicated directory beside the database holding compressed response bodies and rendered artifacts (screenshots, rendered DOM). It is independent of the chosen database provider and enables re-audit without re-fetching and cross-audit content comparison.
- **The diagnostics directory** — local structured logs and native crash minidumps. Bounded, user-viewable, and purgeable. See [Logs, minidumps, and metrics](../07-diagnostics-and-troubleshooting/logs-minidumps-and-metrics.md).
- **The local port handshake** — a port descriptor the shell reads to discover the backend's actual loopback port (below).
- **Window state and UI preferences** — persisted locally so windows reopen the way you left them.

Disk usage is user-visible: storage reporting includes the content store and the diagnostics directory, and per-project retention keeps growth under your control.

> **Tip:** Plugins live in their own local directories: `%APPDATA%\CrawlForge\plugins\` on Windows, `~/Library/Application Support/CrawlForge/plugins` on macOS, and `~/.config/CrawlForge/plugins` on Linux, plus an optional workspace-local `plugins\` directory. Plugins are local user-authored artifacts only — there is no remote plugin registry or marketplace.

## Loopback-only networking and the local handshake

The backend binds to the **loopback interface only**. It is never reachable from other machines. Every listener the product opens is loopback-only.

### Port strategy

1. A configured default loopback port is attempted first.
2. If that port is occupied, the backend falls back to an operating-system-assigned ephemeral port.
3. The shell discovers the actual port through a local handshake — a port descriptor written to the per-user application data directory at spawn time.

You never need to know or configure the port number for normal use; the shell handles discovery automatically.

### The per-launch session token

As optional local hardening, a random session token is generated at spawn and shared between the shell and the backend. At the user level, three properties matter:

- It is **random per launch** — it is not derived from your hardware, operating system stores, or any persistent machine identity.
- It **never leaves the machine**.
- It is a local credential only. The shell-facing API requires no authentication headers from you; there is no sign-in anywhere in the product.

> **Note:** CrawlForge reads no OS registration stores, management instrumentation, hardware serials, or any OS-level machine identifier. No persistent hardware-derived identity of any kind is generated, stored, or transmitted.

## The zero-telemetry assurance

These guarantees are structural, not settings you have to find and switch off:

| Guarantee | What it means for you |
|---|---|
| No telemetry | No usage analytics, no remote crash reporting, no identity-dimension log enrichment, and no automatic transmission of any diagnostic data |
| No update checking | No account-bound update feeds and no update checking of any kind exists in this version |
| No accounts or licenses | No sign-in, registration, subscription, trial, seat, or entitlement concepts; no feature, crawl limit, or report varies with any remote input |
| No remote control plane | No installation enrollment, no cloud relay, no heartbeat or liveness reporting to any vendor, no remote credential issuance or revocation |
| No remote feature flags | Every feature is enabled locally by default; optional toggles are local preferences stored on your machine |
| Local diagnostics only | Logs, minidumps, and the automation action log are produced and stored only on the local filesystem; they are user-viewable and purgeable |

Outbound traffic is limited to exactly these classes, all under your control:

1. HTTP(S) requests to crawl targets — including their robots files, sitemaps, and page resources — optionally routed through a proxy you configure.
2. File writes for exports you initiate (local filesystem).
3. Transfers to export destinations you explicitly configure, occurring only when you initiate them or pre-authorize them on a schedule. No destinations exist by default.
4. Connections to an external database server you configure, only when that provider is selected. None is configured by default.

There is no vendor-operated cloud service of any kind. Destinations and database servers you configure are your own services, not vendor services.

## Shutdown, restart, and crash recovery

### Graceful shutdown

When you close the application, the shell requests a graceful stop. The backend checkpoints any running crawl, drains workers and background services, closes the database, and exits; the shell then exits. Your crawl state is preserved.

### Automatic crash recovery

If the backend exits unexpectedly:

1. A local native diagnostic minidump is captured in the diagnostics directory.
2. The shell restarts the backend with bounded backoff.
3. On restart, the backend marks any audit that was running as *interrupted* and offers resume from its checkpoint.
4. After repeated failures, the shell shows a local recovery screen with diagnostic information, a pointer to the local crash artifacts, and a retry action.

A failing background service is logged locally and restarted with bounded backoff; it never takes down the API or an in-flight audit.

> **Tip:** If you ever land on the recovery screen, everything you need is local: the diagnostic information on screen, the logs and minidumps in the diagnostics directory, and the retry action. See [Logs, minidumps, and metrics](../07-diagnostics-and-troubleshooting/logs-minidumps-and-metrics.md) for a practical workflow.

## Verifying a healthy install

You can confirm the backend is healthy without any network access beyond loopback:

| Check | Surface | What you get |
|---|---|---|
| Readiness | `GET /api/health` | Readiness flag and uptime |
| Version and profile | `GET /api/version` | Product version, schema version, and the active runtime profile (Server GC) |
| Live memory telemetry | Status bar (`system-health` heartbeat) | Backend working set, managed heap, GC generation counts, WAL size, frontend JS heap — refreshed every 2 seconds |
| Settings defaults | `GET /api/settings` | The full settings object with defaults guaranteed; secrets are never returned in clear |

In normal use you never call these endpoints yourself — the shell and the status bar use them for you. They are listed here so you know what "healthy" means and where the numbers come from.

## Windows and surfaces available from first launch

Every window and surface is local and available immediately — nothing is gated:

| Window | Purpose |
|---|---|
| Splash | Shown while the backend starts |
| Main hub | Projects, dashboards, and schedules |
| Report window | Audit reports and explorers |
| Visualization window | Link graphs, depth maps, and charts |
| Single-page analyzer | Analyze one page without creating a project |
| Standalone tools | Robots test, URL check, structured-data check, render test, sitemap generator, query builder, and more |
| Settings | All local preferences (below) |
| Recovery screen | Local diagnostics and retry after repeated backend failures |

The settings surfaces include:

- Database provider selection with connection-test feedback.
- Export-destination management with test-connection feedback.
- Proxy configuration.
- User-dictionary management.
- The MCP automation-interface toggle with transport selection, token display, and rotation.
- Diagnostics management (open, inspect, and purge local logs and crash artifacts).
- UI language selection.

> **Note:** The local MCP automation interface is **disabled by default** on first run. If you want local AI agents or scripts to drive audits, enable it in settings — it remains loopback-only (or stdio for locally spawned clients), with locally generated tokens. See [Local MCP server setup](../06-automation-and-ai-agents/local-mcp-server-setup.md).

## Air-gapped operation

CrawlForge is fully usable on a machine with no network connectivity. The split is simple:

| Works with zero network | Requires network you authorize |
|---|---|
| First-launch initialization and migrations (default provider) | Crawling target websites (including their robots files, sitemaps, and page resources) |
| Re-auditing stored content without re-fetching | Transfers to export destinations you configure (off by default) |
| Cross-audit content comparison | Connections to your own external database server (only when selected) |
| All reports, dashboards, explorers, and visualizations | Proxied crawls through a proxy you configure |
| Standalone tools operating on stored artifacts or local input | |
| Local logs, minidumps, metrics, and the status bar | |

> **Note:** Scheduled audits run unattended once configured, but only the scheduling and trigger mechanism is local — the audit itself still crawls target websites over the network you authorize. Unattended execution is not zero-network execution.

The content store is what makes offline re-analysis possible: response bodies and rendered artifacts are persisted as compressed, managed files beside the database, addressable across audits of the same project.

## Localization and accessibility

- The UI is keyboard-navigable, including the Ctrl+K command palette and the data grids.
- The product ships with UI localization and a user-selectable language, with locale-aware formatting.
- Engine locale assets are bundled; spelling dictionaries ship per locale.
- The initial language set is a product decision, but the localization framework and asset pipeline are part of the package.

Select your language in Settings → UI language selection. The choice is a local preference, stored on your machine like every other setting.

## Quick troubleshooting pointers

| Symptom | First place to look |
|---|---|
| Backend restarts repeatedly | Recovery screen → diagnostics directory (logs + minidumps) |
| Memory or WAL advisory color in the status bar | [Memory telemetry and Compact / Trim](../07-diagnostics-and-troubleshooting/memory-telemetry-and-compact.md) |
| Want to inspect or purge logs and crash artifacts | Settings → diagnostics management; [Logs, minidumps, and metrics](../07-diagnostics-and-troubleshooting/logs-minidumps-and-metrics.md) |
| Considering your own database server | [Database configuration](database-configuration.md) |

## Related guides

- [Database configuration](database-configuration.md)
- [Memory telemetry and Compact / Trim](../07-diagnostics-and-troubleshooting/memory-telemetry-and-compact.md)
- [Logs, minidumps, and metrics](../07-diagnostics-and-troubleshooting/logs-minidumps-and-metrics.md)
- [Creating projects and scopes](../02-managing-projects-and-crawls/creating-projects-and-scopes.md)
- [Lifecycle and checkpointing](../03-running-and-scheduling-audits/lifecycle-and-checkpointing.md)
- [Local MCP server setup](../06-automation-and-ai-agents/local-mcp-server-setup.md)

[← Documentation portal](../README.md)
