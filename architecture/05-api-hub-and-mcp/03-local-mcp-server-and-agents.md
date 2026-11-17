# Local MCP Server & Delegated Agents

> **CrawlForge modular architecture — area of concern: `05-api-hub-and-mcp`.**
> Every source section below is reproduced **verbatim and unabridged** from the immutable master documents. No summarization, paraphrase, or omission has been applied.

| Source document | Version | Sections reproduced in this file |
|---|---|---|
| FS-CRAWL-001 — `baseline/functional-specification.md` | v1.4 | §11 (complete section) |
| FS-CRAWL-002 — `baseline/crawlforge-extensions-and-ui-stack.md` | v1.1 | Appendix D (complete) |

**Notation.** Cross-references inside the reproduced text retain the source convention: a bare `§n` refers to **FS-CRAWL-001**; `FS-CRAWL-002 §n` refers to the extension specification. The [README traceability matrix](../README.md) maps every source section to its file(s) in this hierarchy.

---

> **Verbatim source — FS-CRAWL-001 v1.4: 11. Local Automation Interface — Local MCP Server (Optional) — complete section, verbatim and unabridged**

## 11. Local Automation Interface — Local MCP Server (Optional)
> Capabilities: CAP-H-185

### 11.1 Mandate and boundary

- The local automation interface **shall implement a standardized Model Context Protocol (MCP) server**, conforming to the published protocol's capability negotiation, tool invocation, resource reading, and prompt semantics. It is a protocol-conformant surface, not an ad-hoc remote-procedure layer.
- The interface is **optional and off by default**, enabled in settings. When enabled, it is reachable only through the local transports below; it is never bridged, relayed, or exposed remotely (section 3, row 4).
- The MCP surface is a **semantic projection of the local API** (section 10): identical validation, limits resolution (section 4), lifecycle rules (section 5.7), and concurrency caps (section 6.2) apply. It exposes **no** settings, destination, credential, proxy, or database-provider management.
- Enabling the interface adds **no outbound traffic**.

### 11.2 Transport layers

| Transport | Binding | Client class | Authentication |
|---|---|---|---|
| stdio | Local process pipes | An agent spawns the packaged **stdio adapter** as a child process; the adapter bridges protocol messages to the backend's loopback API | Local process-spawn trust; the local token may optionally be required |
| SSE / streamable HTTP | Loopback HTTP endpoint | Resident local agents connecting over the loopback interface | Local token (required) |

- The stdio adapter is a thin, packaged local bridge process: it holds no domain logic, opens no non-loopback sockets, and terminates when its parent client or the backend exits.
- Both transports are strictly local. **No remote or cloud bridging, enrollment, heartbeat, or remote credential issuance exists** (section 3, row 4).

### 11.3 Tools — lifecycle control

Tools are the protocol's invocable actions. Names below are new design artifacts; each maps one-to-one onto the corresponding local API operation with identical semantics.

| Tool | Input fields | Behavior | Output |
|---|---|---|---|
| start-audit | Configuration reference; optional limit overrides | Starts an audit exactly as the REST start operation | Audit identifier and initial state |
| pause-audit | Audit identifier | Checkpoints and pauses | New state |
| resume-audit | Audit identifier | Rehydrates and resumes | New state |
| cancel-audit | Audit identifier | Drains workers, finalizes partial data | New state |
| re-audit | Audit identifier; optional refresh flag | Triggers a follow-up analysis pass over the same dataset | New audit identifier |
| analyze-single-page | Target URL; optional rendering flag; optional rule filters | Ad-hoc single-page audit | Findings and element inventory |
| run-schedule-now | Schedule identifier | Manual trigger of a schedule | Audit identifier |
| get-audit-progress | Audit identifier | Read-only progress snapshot (the polling contract, section 11.5) | State, counters, throughput, elapsed, estimated remainder, phase |

- Invalid lifecycle transitions are rejected with protocol error objects mirroring the API error envelope; duplicate starts are rejected idempotently with the current state.
- Every invocation is recorded in the local automation action log (section 9.2).

### 11.4 Resources and prompts — data consumption
> Capabilities: CAP-H-186, CAP-J-203, CAP-J-204, CAP-J-208, CAP-J-210, CAP-J-212

**Resources** are read-only, addressable data views. Each is identified by a structured address combining resource kind, audit (or schedule) reference, and optional filters; the address templates are new design artifacts.

| Resource | Addressed by | Content |
|---|---|---|
| audit-summary | Audit reference | Metric cards: counts by status/severity/category, crawl health (same payload as the dashboard summary) |
| high-severity-findings | Audit reference; severity floor; pagination | Finding rows at or above the severity floor, with evidence and recommendations |
| url-detail | Audit reference; URL reference | Full URL record, element inventory, findings, links, redirect chain |
| audit-list | Optional project filter; pagination | Audit summaries with counters |
| schedule-status | Schedule reference | Enabled flag, next-due time, last outcome |
| crawl-progress | Audit reference | The same snapshot as the progress tool, in resource form |
| extracted-content | Audit reference; rule filter; pagination | Custom extraction values in dynamic-column form (section 7.9) |

**Prompts** are reusable, parameterized templates that compose resources into agent-ready context. They execute no side effects.

| Prompt | Parameters | Composition |
|---|---|---|
| audit-health-briefing | Audit reference | Summary metrics plus top issues, structured as a briefing for an agent to narrate |
| high-severity-triage | Audit reference | High-severity findings grouped by category with URL evidence, ordered for remediation planning |
| audit-delta-briefing | Two audit references | New/resolved findings and metric deltas, structured for change reporting |

### 11.5 Delegated AI control flows
> Capabilities: CAP-H-X04, CAP-J-209, CAP-J-213 · Decisions: D-008, D-013 · Change records: CR-011

The interface shall support the following architectural pattern for an external **local** agent negotiating multi-step actions:

1. **Capability discovery** — the agent performs the standard protocol handshake; the server advertises its tools, resources, and prompts.
2. **Intent mapping** — the agent reads configuration and audit-list resources, then invokes start-audit with the chosen configuration.
3. **Progress negotiation** — the agent polls get-audit-progress (or reads the crawl-progress resource) at a bounded cadence. The server enforces a minimum polling interval and returns retry hints when polled too fast; on push-capable transports the server may additionally emit progress and completion notifications so polling is optional.
4. **Completion detection** — the agent observes the terminal state (completed or failed) via the progress snapshot or a completion notification.
5. **Follow-up triggering** — upon completion, the agent may automatically invoke re-audit (optionally with refresh) or analyze-single-page for targeted verification. The follow-up is an ordinary audit: same limits, same lifecycle, same concurrency caps.
6. **Consumption** — the agent reads audit-summary and high-severity-findings resources, or invokes the briefing prompts, to produce its analysis.

**Guardrails:**

- Agent-invoked actions grant **no elevated capability**: everything remains subject to local validation, limits resolution, and the global concurrency cap.
- Long-running work never blocks the protocol channel; all tools return immediately with identifiers, and progress is obtained through the polling/notification contract.
- Conflicting lifecycle commands from concurrent local clients are resolved by the audit state machine: the first valid transition wins; later commands are rejected idempotently.
- All invocations are recorded in the local, user-viewable, purgeable action log for transparency.
- AI clients interpret only data CrawlForge has collected. The interface exposes no operation that fabricates or edits findings; every finding returned carries its evidence and source rule. **[Added by CR-011, D-008]**

### 11.6 Credentials and lifecycle

- Authentication uses a **locally generated random token**, viewable and rotatable by the user in settings; rotation invalidates existing clients immediately. The token is stored encrypted at rest (section 9.1) and never leaves the machine. It is never derived from hardware or OS identity (section 3, row 1).
- No enrollment, heartbeat, liveness reporting, or remote credential issuance exists.
- Disabling the interface closes all of its listeners and terminates any running stdio adapter.

### 11.3.1 Steering and query tools
> Capabilities: CAP-H-185 · Decisions: D-008, D-013, D-014 · Change records: CR-011

**[Added by CR-011]** Each maps one-to-one onto a REST operation with identical semantics; the rules of section 11.3 apply.

| Tool | Input fields | Behavior | Output |
|---|---|---|---|
| list-projects / create-project / update-project | Project fields | Project management | Project record(s) |
| list-configurations / get-configuration / create-configuration / update-configuration | Project reference; configuration fields | Configuration management, including capture mode and every section 5.1 input | Configuration record |
| query-findings | Audit reference; filters (severity, category, rule, URL pattern, template); cursor | Paged finding rows with evidence | Rows, total count, next cursor |
| query-urls | Audit reference; filters; selected columns; cursor | Paged URL rows | Rows, total count, next cursor |
| export-agent-dataset | Audit reference; dataset; format | Writes a bulk dataset file (section 11.8) to a local path | File path, row count |

- Project- and configuration-changing tools are permitted only while the Settings switch **"Allow MCP clients to change projects and configurations"** is on. It is **on by default**. Every invocation is recorded in the action log.

### 11.7 Summary-first responses
> Capabilities: CAP-H-186 · Decisions: D-014 · Change records: CR-011

**[Added by CR-011]** Every data-returning tool and resource leads with counts and the highest-priority items, returns at most one page of rows (default 50, maximum 500), and carries stable identifiers (audit, URL, finding, rule) and a cursor. No tool returns an unbounded result set.

### 11.8 Agent data contract
> Capabilities: CAP-H-175, CAP-G-165 · Decisions: D-014 · Change records: CR-011

**[Added by CR-011]**

- Over MCP: compact JSON, summary-first and paginated (section 11.7).
- Bulk files for agents working in code: **CSV** for flat tables (URLs, links, findings), **JSON Lines** for nested records (full URL records, structured-data entities, redirect chains), **Markdown** for page content.
- A **read-only SQLite snapshot** of an audit, with a documented schema, may be exported for query by an agent. It contains no secrets or credentials.
- Every exported row carries the same stable identifiers as the API.
- Transports are unchanged (section 11.2): stdio, used by VS Code and Claude, and the loopback transport.

---

---

> **Verbatim source — FS-CRAWL-002 v1.1: Appendix D — Additive MCP Surface (extends FS-CRAWL-001 §11.4) — complete section, verbatim and unabridged**

## Appendix D — Additive MCP Surface (extends FS-CRAWL-001 §11.4)

Read-only resources following the parent address pattern (resource kind + audit reference + optional filters). No new tools; lifecycle tools (§11.3) are unchanged. The §11.1 boundary holds: no settings, signature, stop-word, plugin, destination, credential, proxy, provider, log-import, embedding-provider, migration-matrix, GEO-tool, or analytics-configuration management via MCP.

| Resource / prompt | Addressed by | Content |
|---|---|---|
| `ngram-analysis` (resource) | Audit reference; scope, zone, order, term, URL, min-DF filters; pagination | Site summaries, per-URL frequencies, top cannibalization clusters (bounded) |
| `waf-blocks` (resource) | Audit reference; provider/class filters; pagination | Detection rows with evidence + summary counts |
| `host-throttle` (resource) | Audit reference; optional host filter | Current per-host controller state + recent adjustments |
| `redirect-matrix-results` (resource) | Audit reference; verdict filter; pagination | Migration verdicts, hop chains, canonical evidence (FS-CRAWL-002 §10.8) |
| `log-correlation` (resource) | Audit reference; import reference; view; pagination | True orphans, budget waste, uncrawled indexable rows (FS-CRAWL-002 §11.7) |
| `semantic-link-opportunities` (resource) | Audit reference; source filter; similarity band; pagination | Source, target, similarity, anchor candidates (FS-CRAWL-002 §12.8) |
| `geo-readiness` (resource) | Audit reference; agent filter | llms.txt validation results + governance summary (FS-CRAWL-002 §13.5) |
| `analytics-coverage` (resource) | Audit reference; vendor filter | Per-container coverage across indexable pages (FS-CRAWL-002 §14.5) |
| `ngram-topical-briefing` (prompt, optional) | Audit reference | Composes `ngram-analysis` + `audit-summary` into an agent-ready topical briefing; no side effects |
| `semantic-topical-briefing` (prompt, optional) | Audit reference | Composes `semantic-link-opportunities` + `audit-summary`; no side effects (FS-CRAWL-002 §12.8) |

Additive payload fields on parent resources: `audit-summary` (WAF counts), `crawl-progress` (throttle summary, matrix counters), `url-detail` (WAF detection, `content_scope` diagnostics, top primary n-grams, `serp_metrics`, header-conflict detail, semantic relations). Every invocation remains logged in `automation_action_log` (§9.2, §11.5).
