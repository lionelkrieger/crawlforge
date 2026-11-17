# Local UI Plugin System

> **CrawlForge modular architecture — area of concern: `06-frontend-shell-and-ui`.**
> Every source section below is reproduced **verbatim and unabridged** from the immutable master documents. No summarization, paraphrase, or omission has been applied.

| Source document | Version | Sections reproduced in this file |
|---|---|---|
| FS-CRAWL-002 — `baseline/crawlforge-extensions-and-ui-stack.md` | v1.1 | §6 (complete section) |

**Notation.** Cross-references inside the reproduced text retain the source convention: a bare `§n` refers to **FS-CRAWL-001**; `FS-CRAWL-002 §n` refers to the extension specification. The [README traceability matrix](../README.md) maps every source section to its file(s) in this hierarchy.

---

> **Verbatim source — FS-CRAWL-002 v1.1: 6. Local UI Plugin Architecture and Lifecycle Hooks — complete section, verbatim and unabridged**

## 6. Local UI Plugin Architecture and Lifecycle Hooks
> Capabilities: CAP-L-X04

**Extends FS-CRAWL-001 §8.2, §12.** Additional hooks: §2.3, §2.6, §3, §8.7, §9.2, §10.2, §10.3, §10.5.

### 6.1 Principles

Plugins extend the **frontend UI only**: custom report tabs, custom column formatters, and custom filter presets. They contain no backend domain logic, never touch the crawl pipeline, and never mutate backend state (FS-CRAWL-002 §1.6 E5). The system is local-only and sandboxed, with **zero external network access unless explicitly permitted by user destination rules**. There is no marketplace, registry, or remote code channel (FS-CRAWL-002 §1.6 E1).

### 6.2 Plugin directories and discovery

- **Per-user directory:** `%APPDATA%\CrawlForge\plugins\` on Windows; platform equivalents per §13 portability: `~/Library/Application Support/CrawlForge/plugins` (macOS), `~/.config/CrawlForge/plugins` (Linux).
- **Workspace-local directory:** `plugins\` at the workspace root; loaded only while that workspace/project is active.
- **Discovery:** scanned at shell startup. Insertion into the §2.3 startup sequence: after step 5 (main window loads, UI hydrates), the shell scans both directories, validates manifests, loads enabled plugins, and invokes `onLoad`. Plugin failures never block the main window or the backend. A manual **rescan** action exists in settings.
- **Structure:** each plugin is a directory containing a `plugin.json` manifest plus local module files (JS entry, optional HTML/CSS/assets). All code is local; remote code loading is prohibited.
- **Precedence and trust:** on plugin-id collision, the workspace-local plugin overrides the per-user one, with a visible UI warning. Workspace plugins require a one-time local trust prompt per workspace before first load.

### 6.3 Manifest contract (`plugin.json`)

| Field | Required | Semantics |
|---|---|---|
| `id` | Yes | Unique, lowercase, reverse-DNS-style identifier (new design artifact) |
| `name`, `version`, `description` | Yes | Display name; semantic version; short description |
| `author` | No | Free text; no identity verification of any kind |
| `engine.minAppVersion` | Yes | Minimum product version; incompatible plugins are disabled with a diagnostic |
| `entry` | Yes | Relative path to the local JS module |
| `styles` | No | Relative path to local CSS |
| `permissions.network` | No | `{ mode: "none" \| "destinations", destinationRefs: [...] }` — default `none`. `destinations` references user-configured export destinations (§8.5) or explicit user host-allowlist entries; every reference requires a user grant (FS-CRAWL-002 §6.4) |
| `contributes.tabs` | No | Declared report tabs: id, label, optional icon key |
| `contributes.columnFormatters` | No | Declared formatters: target explorer, column key (including dynamic extraction columns, §7.9) |
| `contributes.filterPresets` | No | Declared presets: name, query-builder rule tree (§8.7) |
| `localization` | No | Label map per UI language (§13 localization) |

Invalid manifests disable the plugin with a local diagnostic; they never fail shell startup. Manifests are validated against this contract before any module executes.

### 6.4 Sandbox and capability contract

- **Isolation:** plugin modules execute in an isolated renderer context (sandboxed frame with no same-origin access to the host UI, no Node integration, no filesystem access, no backend credentials). Communication with the host crosses a message bridge with schema-validated payloads only.
- **Host-exposed context API** (the entire capability surface):

| Capability | Contract |
|---|---|
| `app.getInfo()` | Product version, plugin id, UI language |
| `api.get(path, params)` | Read-only GET access to the loopback API (§10), restricted to a data-endpoint allowlist; no POST/PUT/DELETE; no lifecycle or configuration mutation (FS-CRAWL-002 §1.6 E5) |
| `hub.subscribe(eventKinds, handler)` | Host-relayed hub events (§10.3), coalesced per FS-CRAWL-002 §6.5; the plugin never opens its own socket |
| `ui.registerTab / registerColumnFormatter / registerFilterPreset` | Contribution registration per FS-CRAWL-002 §6.6 |
| `storage.get / storage.set` | Plugin-local key-value storage, bounded (1 MiB per plugin), persisted locally in a per-user plugin-state directory |
| `log.info / warn / error` | Local diagnostics only (§10.5 pattern); never transmitted |

- **Column formatters** are pure functions returning sanitized display descriptors (text, tooltip, badge/severity token, in-app link) — never raw HTML; the host renders descriptors, preventing injection.
- **Network enforcement:** the sandbox CSP blocks all connections by default. When the user grants declared destinations, plugin requests route through a host-enforced fetch allowlist matching exactly the granted destination rules (§8.5 destinations or explicit host entries). Non-granted requests are blocked and logged locally. Grants are persisted per plugin and revocable at any time; revocation takes effect immediately.
- **Integrity:** manifest and module file hashes are recorded at first enable; changed files trigger a re-consent prompt before the next load. No remote signing, no remote verification service.

### 6.5 Lifecycle hook contract (deterministic, exposed by the frontend state store)

| Hook | Trigger | Payload | Timeout | Failure behavior |
|---|---|---|---|---|
| `onLoad(context)` | Once at activation (shell startup or user enable) | The FS-CRAWL-002 §6.4 context object; registers tabs, column formatters, filter presets | 5,000 ms | Plugin quarantined with a local diagnostic; shell unaffected |
| `onTabActivate(auditId, filterState)` | User opens the plugin's registered tab | Current audit identifier; active filter state (query-builder-compatible, §8.7) | 3,000 ms | Tab shows a local error state; plugin remains loaded |
| `onDataUpdate(progressBatch)` | Throttled real-time stream during a crawl | Coalesced batch of `crawl-progress`, `url-batch`, `findings-updated` payloads (§10.3) | 1,000 ms budget | Batch dropped; never blocks the hub |
| `onCrawlComplete(auditSummary)` | Audit reaches `completed` (§5.7; `audit-completed` event, §10.3) | Audit summary — the dashboard-summary payload shape (GET `/api/audits/{id}/summary`) | 5,000 ms | Logged locally; never fails the audit |
| `onTabDeactivate` / `onUnload` (optional) | Tab closed / plugin disabled or shell shutdown | — | 2,000 ms | Cleanup skipped; host reclaims resources |

Determinism and isolation rules:

- Discovery order is sorted by (source precedence, plugin id); hooks execute in registration order; `onLoad` registrations complete before the tab strip is finalized. Identical inputs produce identical registration outcomes.
- `onDataUpdate` is delivered **at most once per second per plugin** (parent cadence discipline, §5.7), only while the plugin tab is visible or the plugin declared background interest at load; backpressure uses drop-oldest coalescing.
- `onCrawlComplete` fires exactly once per audit per plugin.
- An unhandled hook error disables the plugin for the session with a diagnostic entry; three consecutive failures quarantine it until the user re-enables. A plugin failure never crashes the shell, never affects a crawl, and never corrupts host state.

### 6.6 UI contribution contracts

- **Report tab:** appears in the report-window tab strip (§8.2, §12) with the manifest label; lazy-loaded on first activation; tabular content obeys the virtualization mandate (FS-CRAWL-002 §7).
- **Column formatter:** targets an explorer column (including dynamic custom-extraction columns, §7.9); the host invokes it per visible row only (virtualization-safe); descriptors are sanitized (FS-CRAWL-002 §6.4).
- **Filter preset:** a named query-builder rule tree (§8.7); applying it sets the explorer filter state; saving it persists through the existing `/api/queries` routes on explicit user action.

### 6.7 Registry, management, and REST (extends §9.2, §10.2, §12)

| Object | Kind | Key fields (abstract) | Notes |
|---|---|---|---|
| `ui_plugins` | New table | Plugin id; name; version; source kind (`user` \| `workspace`); directory path; enabled flag; permissions JSON (grants); manifest hash; installed-at; last-loaded-at | Local registry; configuration data, not pruned by audit retention |

- **Settings surface (§12):** plugin list with id, name, version, source directory, enabled toggle, granted permissions, diagnostics, open-folder, rescan, and remove actions; a per-plugin permission consent dialog enumerating declared destinations before any grant.
- **REST:** GET `/api/plugins` (registry + load state); PUT `/api/plugins/{id}` (enabled flag, permission grants/revocations); POST `/api/plugins/rescan`; DELETE `/api/plugins/{id}` (registry removal; files remain on disk for user deletion via open-folder).
- Plugin errors and hook failures are recorded in local diagnostics (§10.5 pattern), user-viewable and purgeable.

### 6.8 Security and privacy invariants

- No telemetry, no remote code, no identity dimensions (§3 rows 1, 6; FS-CRAWL-002 §1.6).
- Zero plugin network traffic without an explicit user grant; grants reach only user destination rules (§2.6 boundary preserved).
- Plugins cannot read host storage, backend credentials, or other plugins' state; the bridge is the only channel.
- Disabling a plugin unloads its module, revokes its hub relays, and removes its contributions immediately.

**Acceptance hook:** FS-CRAWL-002 §9, criterion #39.

---
