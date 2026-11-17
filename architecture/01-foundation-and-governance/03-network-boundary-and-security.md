# Network Boundary & Security

> **CrawlForge modular architecture — area of concern: `01-foundation-and-governance`.**
> Every source section below is reproduced **verbatim and unabridged** from the immutable master documents. No summarization, paraphrase, or omission has been applied.

| Source document | Version | Sections reproduced in this file |
|---|---|---|
| FS-CRAWL-001 — `baseline/functional-specification.md` | v1.4 | §2.6, §9.1 |
| FS-CRAWL-002 — `baseline/crawlforge-extensions-and-ui-stack.md` | v1.1 | §1.4 rule 9, §6.4, §6.8, §11.1, §12.2 |

**Notation.** Cross-references inside the reproduced text retain the source convention: a bare `§n` refers to **FS-CRAWL-001**; `FS-CRAWL-002 §n` refers to the extension specification. The [README traceability matrix](../README.md) maps every source section to its file(s) in this hierarchy.

---

> **Verbatim source — FS-CRAWL-001 v1.4: 2.6 Network boundary — verbatim and unabridged**

### 2.6 Network boundary
> Capabilities: CAP-H-181, CAP-H-182, CAP-H-183 · Decisions: D-005, D-012, D-010 · Change records: CR-003

Outbound traffic is limited to:

- HTTP(S) requests to crawl targets, including their robots files, sitemaps, and page resources — optionally routed through a user-configured proxy (section 5.9).
- File writes for user-initiated exports (local filesystem).
- Transfers to **export destinations explicitly configured by the user** (section 8.5), occurring only when the user initiates them or pre-authorizes them on a schedule. No destinations exist by default.
- Connections to a **user-configured external database server** (section 2.4), only when that provider is selected. None is configured by default.
- Connections to **user-connected search and analytics services** (Google Search Console, Google Analytics 4, PageSpeed Insights; `08-integrations`), using the user's own OAuth client or API key, only for properties the user has explicitly connected to a project, and only when the user initiates a sync or has scheduled one. None is connected by default. **[Added by CR-003, D-005, D-012]**
- A **user-initiated release check** against the project's public release channel (GitHub Releases; `09-distribution`). It runs only when the user requests it and transmits no identifier, usage data or diagnostic data. Specified now; built once public releases exist. **[Added by CR-003, D-010]**

There is no telemetry, no remote crash reporting, no automatic update checking, no account service, and no vendor-operated cloud service of any kind. User-configured destinations, database servers and user-connected search and analytics services are the user's own services, not vendor services. Data synced from them is cached locally so audits and reports remain fully functional offline after a sync. Disconnecting all such services and deleting all destinations restores the guarantee of zero non-crawl outbound traffic, except a release check the user explicitly starts. **[Amended by CR-003, D-005, D-012, D-010]**

```
┌──────────────────────────────────────────────────────────┐
│ Desktop shell (window host)                              │
│  splash · main hub · report · visualization · analyzer   │
│  settings · recovery                                     │
└───────────────┬──────────────────────────────────────────┘
                │  HTTP (JSON)  +  WebSocket hub (events)
                │  loopback only
┌───────────────▼──────────────────────────────────────────┐
│ Local backend service                                    │
│  crawl engine (proxy routing) · audit engine             │
│  reporting aggregation · scheduler · export delivery     │
│  content store · local MCP server · metrics · hub        │
└───────┬──────────────────────────────────┬───────────────┘
        │ transactions · migrations        │ stdio / loopback SSE
        │                                  │ (optional, off by default)
┌───────▼───────────────────────────┐  ┌───▼───────────────────────┐
│ Configured database               │  │ Local AI / automation     │
│ default: local file-based         │  │ clients (user-owned)      │
│ optional: external client-server  │  └───────────────────────────┘
│ (user-configured)                 │
└───────────────────────────────────┘
```

---

> **Verbatim source — FS-CRAWL-001 v1.4: 9.1 Conventions — verbatim and unabridged**

### 9.1 Conventions
> Capabilities: CAP-L-X01

- One logical installation database, served by the configured provider (section 2.4); all crawl data is project-scoped.
- Surrogate integer keys; UTC timestamps; JSON text columns for flexible payloads.
- Projects and audits support soft delete with transactional pruning.
- **Secret protection (mandatory):** stored site credentials, destination credentials, external-database credentials, proxy credentials, and any local token or signing secrets are encrypted at rest using app-generated entropy persisted locally; key material is never derived from hardware or OS identity (section 3, row 1).

---

> **Verbatim source — FS-CRAWL-002 v1.1: §1.4 rule 9 — verbatim excerpt (the complete §1.4 invariant set lives in 01-foundation-and-governance/01-clean-room-and-exclusions.md)**

9. **Loopback-local services and offline ingestion (v1.1).** The v1.1 modules add no off-machine traffic. Log ingestion reads local disk only (FS-CRAWL-002 §11); embedding providers are a bundled offline model or a **user-configured loopback-only daemon** whose address validation rejects any non-loopback endpoint (FS-CRAWL-002 §12) — loopback connections stay inside the machine boundary and are user-owned local infrastructure, analogous to the user-configured external database pattern (§2.4) but never off-machine. The single optional exception is the **user-triggered, off-by-default DNS verification** of log bot hits (FS-CRAWL-002 §11.3): DNS queries only, explicit per-import consent, never automatic, locally logged; with it disabled the log module generates zero outbound traffic. Bundled bot CIDR ranges, agent profiles, and font-metric tables are packaged assets updated only with product updates (§13).

---

> **Verbatim source — FS-CRAWL-002 v1.1: 6.4 Sandbox and capability contract — verbatim and unabridged**

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

---

> **Verbatim source — FS-CRAWL-002 v1.1: 6.8 Security and privacy invariants — verbatim and unabridged**

### 6.8 Security and privacy invariants

- No telemetry, no remote code, no identity dimensions (§3 rows 1, 6; FS-CRAWL-002 §1.6).
- Zero plugin network traffic without an explicit user grant; grants reach only user destination rules (§2.6 boundary preserved).
- Plugins cannot read host storage, backend credentials, or other plugins' state; the bridge is the only channel.
- Disabling a plugin unloads its module, revokes its hub relays, and removes its contributions immediately.

**Acceptance hook:** FS-CRAWL-002 §9, criterion #39.

---

---

> **Verbatim source — FS-CRAWL-002 v1.1: 11.1 Purpose and network boundary — verbatim and unabridged**

### 11.1 Purpose and network boundary

Import server access logs from local disk and correlate bot traffic against crawl results to reveal **true orphans**, **crawl-budget waste**, and **uncrawled indexable pages**. Ingestion, parsing, classification, and correlation are **100% offline — zero network calls**. The sole optional exception is user-triggered DNS verification (FS-CRAWL-002 §11.3), off by default, disclosed and consented per import (FS-CRAWL-002 §1.4 rule 9).

---

> **Verbatim source — FS-CRAWL-002 v1.1: 12.2 Embedding providers (strictly local) — verbatim and unabridged**

### 12.2 Embedding providers (strictly local)

| Provider class | Contract |
|---|---|
| **Bundled local ONNX runtime model** | Packaged model asset (§13 packaging manifest); offline CPU/GPU execution; model tag and vector dimension recorded with every embedding; no network of any kind |
| **Loopback local LLM/embedding daemon** | Ollama / LM Studio-class daemon at a **user-configured loopback endpoint** (e.g. `http://127.0.0.1:11434` or any configurable loopback port). Configuration validation **rejects any non-loopback address** (§4.3 pattern). Connection-test action mirrors the §8.5 / §2.4 test-connection pattern. The daemon is user-owned local infrastructure; no API keys to remote services, no cloud fallback |

- Provider selection lives in settings (`application_settings`); the active provider and model tag are snapshotted per audit.
- If the provider is unavailable, semantic passes are marked **unavailable** with a local diagnostic; audits never fail because of embeddings.
- No remote embedding API, remote vector store, or hosted LLM service is permitted (FS-CRAWL-002 §1.6 E7; §2.6 boundary preserved — loopback stays on-machine, FS-CRAWL-002 §1.4 rule 9).

---

## Amendment history

### CR-003 (2026-11-17; D-005, D-012, D-010)

Replaced text in §2.6 (closing paragraph):

> There is no telemetry, no remote crash reporting, no update checking, no account service, and no vendor-operated cloud service of any kind. User-configured destinations and database servers are the user's own services, not vendor services. Diagnostic artifacts (logs, minidumps) and metrics never leave the machine (section 10.5).
