# Database Configuration

CrawlForge stores every project, audit, finding, and report in one logical installation database. You can run it two ways: the default local file-based embedded database (zero configuration, fully offline), or an optional external client-server database server that you own and configure yourself. Both providers implement the same logical schema, the same read models, and the same migration ledger — your reports look and behave identically on either.

This guide explains both providers, how to test a connection before first use, how to switch providers with the archive export/import workflow, and where your data lives on disk.

> **Note:** No vendor-hosted database exists or is ever contacted. An external database server is your own infrastructure. CrawlForge connects to it only when you select that provider, and the credentials you enter are encrypted at rest.

## The two provider classes

| Provider | Class | When to choose it | Network behavior |
|---|---|---|---|
| Default — embedded | Local file-based (SQLite-class) | Personal desktop use; the out-of-the-box experience | Zero network; the database is a local file in your per-user application data directory |
| Optional — external | Client-server relational (MySQL-class) | High-volume deployments beyond the embedded default | Connections only to the server you configure; none is configured by default |

Persistence is abstracted behind a provider contract, so the application is not restricted to a single storage engine. Provider selection and connection settings live in application settings (`application_settings`).

### What stays the same on both providers

- The **logical schema, read models, and migration ledger are provider-agnostic**. Each provider supplies a dialect-specific migration path to the same logical schema version.
- All crawl data is project-scoped, with surrogate integer keys, UTC timestamps, and JSON text columns for flexible payloads.
- Foreign keys are enforced; all multi-row writes occur inside transactions on either provider.
- Reports, dashboards, and lists are served from precomputed, indexed read models — never full scans over raw crawl data at the target scale.
- The **content store stays on the local filesystem beside the database, independent of the chosen provider** (below).

## Choosing a provider

| Your situation | Recommended provider |
|---|---|
| Personal desktop use, single machine | The embedded default — zero configuration, zero network |
| Audits of several hundred thousand URLs on commodity desktop hardware | The embedded default — the stated scale target is validated against it, with streaming persistence keeping memory bounded |
| High-volume deployments beyond the embedded default | Your own external client-server database |
| Air-gapped machine | The embedded default — first-run initialization and all reporting complete with zero network calls |

The embedded provider is not a "lite" mode: it implements the full logical schema, the same read models, and the same capabilities (including vector-similarity search through bundled native extensions). Choose the external provider when your volume or infrastructure preferences call for it, not because features differ.

## Default: the local file-based database

The embedded provider is what you get on first launch, with no configuration at all:

- The database resides in the per-user application data directory (`%APPDATA%\CrawlForge` on Windows, `~/Library/Application Support/CrawlForge` on macOS, `~/.config/CrawlForge` on Linux).
- It uses **write-ahead logging (WAL)** for crash-safe durability.
- Required native extensions are loaded per platform from the self-contained package — you never install database extensions yourself.
- First-run initialization is deterministic and requires zero network calls.

### Required database capabilities

The configured provider must supply these capabilities natively or through loadable extensions; for the embedded provider, the native extensions ship inside the package per operating system and architecture:

| Capability | Used for |
|---|---|
| Regular-expression predicates in queries | Pattern filtering in explorers and the query builder |
| In-database delimited-text import/export | List ingestion and CSV-class export paths |
| Vector-similarity search over stored embeddings | Local semantic internal-linking analysis |
| Extended mathematical and statistical functions | Distributions, scoring, and aggregate metrics |

You do not manage these capabilities directly — they are part of the provider contract and the self-contained packaging.

## Optional: your own MySQL-class server

For high-volume deployments, you can point CrawlForge at a client-server relational database (MySQL-class) that you operate. The server is your own infrastructure — on your machine, your LAN, or wherever you choose to run it.

### Connection fields

Configure the connection in Settings → database provider selection:

| Field | Default | Bounds | Notes |
|---|---|---|---|
| Provider selection | Local file-based embedded | Embedded or external client-server | Switching providers is an explicit, user-initiated operation using archive export/import — never automatic |
| Host | Not configured | A server address you own | No default server exists; nothing is contacted until you configure it |
| Port | Not configured | The port your server listens on | User-configured |
| Database name | Not configured | A database on your server | User-configured |
| Credentials | Not configured | Your own database username and password | Encrypted at rest; never returned in clear by the settings API |

> **Warning:** Credentials are protected with app-generated entropy persisted locally. Key material is never derived from your hardware or OS identity. The settings surface (`GET /api/settings`) guarantees defaults but never returns secrets in clear.

### What changes when the external provider is active

- Durability uses the server's **native transactional durability** instead of the embedded WAL.
- The `sqliteWalBytes` field in the live status-bar heartbeat becomes null — there is no embedded WAL to report. The [memory telemetry guide](../07-diagnostics-and-troubleshooting/memory-telemetry-and-compact.md) explains the display.
- Migrations run against your server at backend startup, before the API begins serving, exactly as on the embedded provider.
- The content store remains local beside the database directory regardless of provider.

## Testing the connection before first use

Always test the connection before relying on a new provider configuration. The test action reports three verdicts:

| Verdict | What it tells you |
|---|---|
| Reachability | The server address and port accept a connection |
| Authentication | Your credentials are valid |
| Migration compatibility | The provider can run the schema migrations to the current logical schema version |

### Procedure

1. Open Settings → database provider selection.
2. Select the external client-server provider and enter host, port, database name, and credentials.
3. Run the **test connection** action (`POST /api/settings/database/test` under the hood).
4. Read the verdicts. Proceed only when reachability, authentication, and migration compatibility all pass.
5. Save the configuration. The backend applies migrations against the configured provider at its next startup, before the API begins serving.

> **Tip:** The same test-connection pattern is used for export destinations and the optional loopback embedding daemon — configure, test, then use. A failed test never changes your active provider.

## Switching providers: archive export/import

Provider switching is explicit and user-initiated. It uses the per-project **archive export/import** mechanism, which produces a self-contained local bundle and doubles as the provider-migration vehicle. Nothing switches automatically, and no data moves between providers behind your back.

### Moving from the embedded default to your own server

1. **Export an archive** of the project from the current (embedded) provider. The archive is a self-contained local bundle written to the local filesystem.
2. **Configure and test** the external provider connection (procedure above). Do not skip the test.
3. **Select the external provider** in settings. On the next backend startup, migrations create the full logical schema on your server.
4. **Import the archive** into the installation now running on the external provider.
5. **Verify** by opening the project and running an audit — report output is identical across providers because the logical schema and read models are identical.

### Reverting to the default file-based provider

Reverting works the same way in reverse, via archive import:

1. Export the project archive while running on the external provider.
2. Select the default local file-based provider in settings.
3. Import the archive into the embedded installation.
4. Verify your reports and re-audit a stored page to confirm content-store addressing is intact.

> **Note:** The archive is per-project and self-contained. It is a local file you control — there is no cloud staging area and no vendor service involved in the transfer.

## Migrations and the migration ledger

Schema evolution is handled by versioned migrations applied automatically at backend startup, before the API begins serving, on the configured provider:

- Migrations are **idempotent** — restarting the backend never double-applies a version.
- The `schema_migrations` ledger records each applied version, the provider dialect, and the applied-at timestamp, per provider.
- Both providers migrate to the **same logical schema version**; new tables and columns arrive only through versioned, provider-agnostic migrations.
- First-run seeding creates exactly one `application_settings` row with deterministic defaults (embedded provider, unlimited crawl ceilings, all audit categories enabled, local-only toggles on, default politeness on).

You never run migrations manually. If a migration cannot apply, the backend does not begin serving, and the local logs record the failure — see [Logs, minidumps, and metrics](../07-diagnostics-and-troubleshooting/logs-minidumps-and-metrics.md).

## Where your data lives

| Data | Location | Provider-dependent? |
|---|---|---|
| Logical database (projects, audits, findings, read models, settings) | Embedded: local file in the per-user application data directory. External: your configured server | Yes — this is what the provider selection changes |
| Content store (compressed response bodies, screenshots, rendered DOM) | Dedicated local directory beside the database, indexed by the content-artifacts table | No — always local, on either provider |
| Diagnostics (logs, minidumps) | Local diagnostics directory | No |
| Archive bundles | Local filesystem, wherever you export them | No |

Content-store artifacts are keyed by (audit, URL, kind) and are addressable **across audits of the same project**, which is what enables re-audit without re-fetching and side-by-side content comparison. Artifacts are compressed, managed files — never held wholly in memory.

Storage reporting is user-visible and includes the content store and the diagnostics directory. Per-project retention (keep the last N audits, or an age limit) prunes dependent rows transactionally — including content artifacts, embeddings, extracted content, schedule run history, and automation action-log entries — and reclaims orphaned content-store files.

## Durability, integrity, and maintenance

- **Crash-safe writes:** the embedded provider uses write-ahead logging; the external provider uses its native transactional durability. All multi-row writes occur inside transactions on either provider.
- **Periodic integrity checks and compaction** keep the database healthy on either provider.
- **Retention pruning** is transactional and user-configurable per project.
- **Command/query separation:** crawl and audit ingestion flows through a transactional write path; every report, dashboard, and list read is served from precomputed, indexed read models. Dashboard reads from aggregates return in under one second at target scale.

> **Tip:** If the embedded WAL file grows large during heavy ingestion, the manual Compact / Trim Working Set action checkpoints it (truncate mode) alongside managed GC compaction. See [Memory telemetry and Compact / Trim](../07-diagnostics-and-troubleshooting/memory-telemetry-and-compact.md).

## Privacy and network notes

- The external database connection is one of exactly four outbound traffic classes the product permits, and it exists **only when you select that provider**. None is configured by default.
- Database credentials are encrypted at rest using app-generated entropy persisted locally; key material is never derived from hardware or OS identity.
- The `application_settings` table contains no identity, license, or account columns.
- With the default provider, the product generates zero database-related network traffic of any kind.

## One logical database, project-scoped data

There is one logical installation database, served by the configured provider. All crawl data is project-scoped, and the relationships are the same on both providers:

- Projects have many configurations; configurations have many audits.
- Audits have many URL records, link edges, findings, and metric summaries; URL records have many element rows.
- Internal link edges reference URL records on both ends.
- Schedules reference configurations and produce run-history rows linked to audits.
- Content artifacts and embeddings belong to (audit, URL) pairs and are addressable across audits of the same project for comparison.

Projects and audits support **soft delete with transactional pruning** — deleting a project removes its dependent data transactionally rather than leaving orphans behind.

The `application_settings` table is a single row of preferences and defaults: UI preferences, default crawl limits, default audit toggles, storage options, database provider selection and connection settings (credentials encrypted), and theme/locale. It is seeded at first run with the default file-based provider. **No identity, license, or account columns exist.**

## What reports read: precomputed aggregates

During and after an audit, the backend computes summary metrics into dedicated aggregate tables on the configured provider. This is why dashboards stay fast at any scale — reads are constant-cost lookups, not scans:

| Aggregate | Contents |
|---|---|
| URL counts | By status class, content type, depth, and indexability |
| Finding counts | By severity and category |
| Crawl health | Errors, redirects, blocked and skipped counts |
| Distributions | Response time, page weight, word count |
| Link-graph statistics | Orphan pages, hub pages, reachability (standard graph traversal from the seeds); link-equity distribution — inlink counts, percentile ranking, and concentration across pages |
| Audit scoring | Per-category and overall scores derived from finding counts and severities weighted by indexable-URL coverage |

Audit scores are stored per audit so they are **comparable across audits** and drive the score-trend surface. A disabled category contributes no score; suppressed findings are excluded. Aggregates are keyed by audit and metric with a dimension payload — identical behavior on either provider, because aggregate views are materialized as precomputed read models where the provider lacks native view support.

## Retention and storage management

Retention is user-configurable per project: keep the last N audits, or an age limit. Pruning is transactional and removes:

| Pruned with retention | Notes |
|---|---|
| Dependent audit rows | URL records, findings, links, metrics, and other audit-scoped data |
| Content artifacts | Compressed bodies and rendered artifacts; orphaned content-store files are reclaimed |
| Embeddings | Stored vectors for pruned audits |
| Extracted content | Custom extraction values |
| Schedule run history | Run-history rows for pruned audits |
| Automation action-log entries | Local agent invocation history tied to pruned data |

Configuration tables are not pruned by audit retention. Periodic integrity checks and compaction keep the database healthy on either provider. Diagnostic artifacts (logs, minidumps) have their own bounded local retention, separate from project retention.

Storage reporting is user-visible and includes the content store and the diagnostics directory, so you can see exactly where disk is going before you decide to prune.

## Common questions

**Does switching providers change my reports?**
No. The logical schema, read models, and migration ledger are provider-agnostic; both providers migrate to the same logical schema version. A full audit on your own server produces identical report output to the embedded default.

**Is my content store uploaded when I use an external database?**
No. The content store always stays on the local filesystem beside the database directory, independent of the chosen provider. Only the logical database lives on your server.

**Can I run both providers at once?**
There is one logical installation database, served by the configured provider. You switch between providers explicitly with archive export/import; you do not run two active installations against one settings row.

**Do I need to create the schema on my server myself?**
No. Migrations create all tables automatically at backend startup, before the API begins serving. The connection test's migration-compatibility verdict tells you in advance whether the provider can run them.

**Where are my database credentials stored?**
In application settings, encrypted at rest using app-generated entropy persisted locally. Key material is never derived from hardware or OS identity, and the settings API never returns secrets in clear.

## Troubleshooting

| Symptom | What to do |
|---|---|
| Connection test fails on reachability | Verify the server address and port, and that your server accepts connections from this machine |
| Connection test fails on authentication | Re-enter credentials; confirm the account exists on your server with rights to the configured database |
| Connection test fails on migration compatibility | Check the local logs for the migration failure detail; the backend will not serve until migrations apply |
| Status bar shows no WAL size | Expected when the external provider is active — `sqliteWalBytes` is null by design |
| Reports feel slow after a provider switch | Confirm the archive import completed and read models are populated; dashboard reads are constant-cost lookups on both providers |

## Related guides

- [Installation and first launch](installation-and-first-launch.md)
- [Memory telemetry and Compact / Trim](../07-diagnostics-and-troubleshooting/memory-telemetry-and-compact.md)
- [Logs, minidumps, and metrics](../07-diagnostics-and-troubleshooting/logs-minidumps-and-metrics.md)
- [Creating projects and scopes](../02-managing-projects-and-crawls/creating-projects-and-scopes.md)
- [Dashboards and virtualized explorers](../04-exploring-reports-and-visualizations/dashboards-and-virtualized-explorers.md)
- [Exports and secure destinations](../04-exploring-reports-and-visualizations/exports-and-secure-destinations.md)

[← Documentation portal](../README.md)
