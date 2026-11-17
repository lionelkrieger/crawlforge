# Agent Tools and Workflows

Once the optional local MCP server is enabled, a local AI agent can steer CrawlForge end to end: start audits, watch progress, trigger follow-up passes, and read findings and analysis data — all over strictly local transports, with every invocation recorded in a local action log. The agent surface is a **semantic projection of the local API**: identical validation, limits resolution, lifecycle rules, and concurrency caps apply, and agents get **no elevated capability**.

This guide lists the exact tools, resources, and prompts the server exposes, explains the delegated control flow and the action log, spells out what agents can and cannot do, and walks through three practical SEO workflows. To enable the interface and configure a client first, see [Local MCP server setup](local-mcp-server-setup.md).

> **Note:** Everything here runs on your machine. There is zero telemetry, no cloud relay, and no outbound traffic added by the MCP interface. Outbound requests happen only where you have explicitly authorized them — crawl targets you configured, destinations you configured and initiated or pre-authorized, and an external database you configured.

## Before you start

1. Enable the MCP automation interface in settings and select a transport — see [Local MCP server setup](local-mcp-server-setup.md).
2. Keep CrawlForge **running**: the interface provides programmatic control while the application is running. Headless command-line execution without a window is not part of this version.
3. Connect your local MCP client. After the standard protocol handshake, the server advertises its **tools, resources, and prompts** — the agent discovers the surface below automatically.

The surface consists of **8 lifecycle tools**, **15 read-only resources**, and **5 prompts** (two of them optional). Nothing else is exposed.

## Tools — lifecycle control

Tools are the protocol's invocable actions. Each maps one-to-one onto the corresponding local API operation with identical semantics.

| Tool | Input fields | Behavior | Output |
|---|---|---|---|
| `start-audit` | Configuration reference; optional limit overrides | Starts an audit exactly as the REST start operation | Audit identifier and initial state |
| `pause-audit` | Audit identifier | Checkpoints and pauses | New state |
| `resume-audit` | Audit identifier | Rehydrates and resumes | New state |
| `cancel-audit` | Audit identifier | Drains workers, finalizes partial data | New state |
| `re-audit` | Audit identifier; optional refresh flag | Triggers a follow-up analysis pass over the same dataset | New audit identifier |
| `analyze-single-page` | Target URL; optional rendering flag; optional rule filters | Ad-hoc single-page audit | Findings and element inventory |
| `run-schedule-now` | Schedule identifier | Manual trigger of a schedule | Audit identifier |
| `get-audit-progress` | Audit identifier | Read-only progress snapshot (the polling contract) | State, counters, throughput, elapsed, estimated remainder, phase |

### The REST operations behind the tools

Each lifecycle tool wraps the same loopback REST operation the UI uses:

| Tool | REST operation |
|---|---|
| `start-audit` | `POST /api/audits` |
| `pause-audit` | `POST /api/audits/{id}/pause` |
| `resume-audit` | `POST /api/audits/{id}/resume` |
| `cancel-audit` | `POST /api/audits/{id}/cancel` |
| `re-audit` | `POST /api/audits/{id}/reaudit` |
| `analyze-single-page` | `POST /api/analysis/single-page` |
| `run-schedule-now` | `POST /api/schedules/{id}/run-now` |

`get-audit-progress` is a read-only snapshot: it returns the same progress payload as the `crawl-progress` resource and the real-time hub's `crawl-progress` event — state, queued/fetched/error/blocked counters, throughput, elapsed time, estimated remainder, and current phase.

### Immediate returns, never blocking

Long-running work **never blocks the protocol channel**. All tools return immediately with identifiers; progress is obtained through the polling/notification contract described in [The delegated flow](#the-delegated-flow). A `start-audit` call returns the audit identifier and initial state right away — the crawl itself proceeds in the backend.

### Error behavior and conflicting commands

- **Invalid lifecycle transitions** are rejected with protocol error objects mirroring the API error envelope: a machine-readable code plus a human-readable message. Validation errors carry field-level reasons.
- **Duplicate starts** are rejected idempotently with the current state — starting an audit that is already running returns the current state rather than launching a second run.
- **Conflicting lifecycle commands** from concurrent local clients are resolved by the audit state machine: the **first valid transition wins**; later commands are rejected idempotently.

### Audit states the tools move between

The audit lifecycle is:

`configuring → running → (paused ⇄ running) → completed | cancelled | failed | interrupted`

- **Pause** checkpoints the frontier, seen-set, and counters; **resume** rehydrates them.
- **Cancel** drains workers and finalizes partial data — a cancelled audit keeps its partial results.
- **Interrupted** (process loss) is recoverable from the last checkpoint via `resume-audit`.
- The terminal states an agent watches for are **`completed`** and **`failed`**.

### Limits and concurrency apply unchanged

Agent-invoked audits resolve limits exactly like UI-invoked audits:

- The limits resolver merges, in priority order: **per-audit override → project default → application default → built-in unlimited profile**. Every ceiling defaults to Unlimited in the built-in profile.
- The optional limit overrides on `start-audit` accept any subset of the crawl limit parameters (page ceilings, throughput, worker concurrency, depth, timeouts, retry budget, redirect hops, response size, duration). Invalid overrides are rejected with a descriptive local error before the audit starts.
- A running audit keeps the limit snapshot it started with; changes apply to subsequent audits.
- The **global concurrency cap** applies: at most a small fixed number of audits (scheduled or manual) run at once; excess runs queue. Agent follow-ups are ordinary audits under the same cap.

> **Warning:** Agents cannot raise ceilings beyond what local validation allows, cannot bypass politeness or robots rules, and cannot change application defaults. Limit overrides are per-audit inputs, validated like any other.

## Resources — read-only data

Resources are **read-only, addressable data views**. Each is identified by a structured address combining **resource kind + audit (or schedule) reference + optional filters**. Reading a resource executes no side effects.

Resource addresses follow the local API conventions: list-style views accept offset/limit pagination, filtering, and sorting, and responses use the uniform envelope — a data payload, optional pagination metadata, and an optional error object (machine-readable code plus human-readable message).

### Baseline resources

| Resource | Addressed by | Content |
|---|---|---|
| `audit-summary` | Audit reference | Metric cards: counts by status/severity/category, crawl health (same payload as the dashboard summary) |
| `high-severity-findings` | Audit reference; severity floor; pagination | Finding rows at or above the severity floor, with evidence and recommendations |
| `url-detail` | Audit reference; URL reference | Full URL record, element inventory, findings, links, redirect chain |
| `audit-list` | Optional project filter; pagination | Audit summaries with counters |
| `schedule-status` | Schedule reference | Enabled flag, next-due time, last outcome |
| `crawl-progress` | Audit reference | The same snapshot as the progress tool, in resource form |
| `extracted-content` | Audit reference; rule filter; pagination | Custom extraction values in dynamic-column form |

Finding severities use the product vocabulary — **Error, Warning, Notice, Pass** — so a severity floor on `high-severity-findings` selects rows at or above the chosen level (for example, Error only, or Error and Warning).

### Analysis resources

These read-only resources project the advanced analysis surfaces. They follow the same address pattern and add no tools — the lifecycle tool list above is unchanged.

| Resource | Addressed by | Content |
|---|---|---|
| `ngram-analysis` | Audit reference; `scope`, `zone`, `order`, `term`, `urlId`, `minDf` filters; pagination | Site summaries, per-URL frequencies, top cannibalization clusters (bounded) |
| `waf-blocks` | Audit reference; provider/class filters; pagination | Detection rows with evidence + summary counts |
| `host-throttle` | Audit reference; optional host filter | Current per-host controller state + recent adjustments |
| `redirect-matrix-results` | Audit reference; verdict filter; pagination | Migration verdicts, hop chains, canonical evidence |
| `log-correlation` | Audit reference; import reference; view; pagination | True orphans, budget waste, uncrawled indexable rows |
| `semantic-link-opportunities` | Audit reference; source filter; similarity band; pagination | Source, target, similarity, anchor candidates |
| `geo-readiness` | Audit reference; agent filter | `llms.txt` validation results + governance summary |
| `analytics-coverage` | Audit reference; vendor filter | Per-container coverage across indexable pages |

Notes on filters and content:

- **`ngram-analysis`** — `scope` selects site-summary rows (`site`) or per-URL rows (`url`); `zone` and `order` narrow the n-gram zone and length (1/2/3); `term` searches terms; `urlId` filters to one URL; `minDf` sets a minimum document frequency. Cannibalization clusters are bounded.
- **`waf-blocks`** — a read-only projection of the WAF detection list and summary. Signature-catalogue management is **not** exposed via MCP.
- **`host-throttle`** — read-only current AIMD controller state and recent adjustments per host. Manual rate pinning stays a UI/REST action, not an agent tool.
- **`redirect-matrix-results`** — per-row migration verdicts with full hop chains and canonical evidence. Verdicts follow the matrix rule keys: `redirect-matrix.chain-exceeded`, `redirect-matrix.final-non-200`, `redirect-matrix.target-mismatch`, `redirect-matrix.canonical-mismatch`, and `redirect-matrix.pass`. Matrix upload and management are not exposed via MCP.
- **`log-correlation`** — projects the crawl-vs-log correlation endpoint; the `view` filter mirrors that endpoint's views (`summary`, `orphans`, `waste`, `uncrawled`). Log import management is not exposed via MCP.
- **`semantic-link-opportunities`** — requires the local semantic pass to have produced data. If the embedding provider is unavailable, semantic passes are marked unavailable with a local diagnostic; audits never fail because of embeddings. Provider configuration is not exposed via MCP.
- **`geo-readiness`** — filter by crawler agent to inspect `llms.txt` validation and the bot-governance summary (search indexing, AI RAG citation, LLM training purposes). GEO tool invocation and configuration are not exposed via MCP.
- **`analytics-coverage`** — per-container coverage across indexable pages, filterable by vendor. Analytics configuration is not exposed via MCP.

### Additive fields on baseline resources

The analysis modules extend three baseline resources with additional payload fields:

- **`audit-summary`** gains WAF counts.
- **`crawl-progress`** gains the throttle summary and matrix counters (validated/total while a migration matrix is attached).
- **`url-detail`** gains WAF detection, `content_scope` diagnostics, top primary n-grams, `serp_metrics`, header-conflict detail, and semantic relations.

> **Tip:** Prefer resources and prompts over raw tool polling for analysis. Resources are read-only and composable; prompts package them into agent-ready context with no side effects.

### Choosing the right resource

| Question | Resource |
|---|---|
| How healthy is this audit overall? | `audit-summary` |
| What must be fixed first? | `high-severity-findings` |
| What exactly is on this page? | `url-detail` |
| Which audits exist for this project? | `audit-list` |
| Is the schedule enabled, and when is it next due? | `schedule-status` |
| How far along is the running audit? | `crawl-progress` |
| What did my custom extraction rules capture? | `extracted-content` |
| Which terms dominate, and where do pages cannibalize? | `ngram-analysis` |
| Was the crawler challenged by a WAF? | `waf-blocks` |
| How is per-host politeness behaving? | `host-throttle` |
| Did the migration redirects land correctly? | `redirect-matrix-results` |
| What do server logs say the crawl missed? | `log-correlation` |
| Which internal links should exist? | `semantic-link-opportunities` |
| Are AI crawlers governed correctly? | `geo-readiness` |
| Is analytics container coverage complete? | `analytics-coverage` |

## Prompts — agent-ready briefings

Prompts are reusable, parameterized templates that compose resources into agent-ready context. They **execute no side effects**.

| Prompt | Parameters | Composition |
|---|---|---|
| `audit-health-briefing` | Audit reference | Summary metrics plus top issues, structured as a briefing for an agent to narrate |
| `high-severity-triage` | Audit reference | High-severity findings grouped by category with URL evidence, ordered for remediation planning |
| `audit-delta-briefing` | Two audit references | New/resolved findings and metric deltas, structured for change reporting |
| `ngram-topical-briefing` (optional) | Audit reference | Composes `ngram-analysis` + `audit-summary` into an agent-ready topical briefing; no side effects |
| `semantic-topical-briefing` (optional) | Audit reference | Composes `semantic-link-opportunities` + `audit-summary`; no side effects |

The last two prompts are optional additions introduced with the N-gram and semantic analysis modules. Use `audit-delta-briefing` for before/after reporting — it takes two audit references, such as a scheduled run and its predecessor.

## The action log

Every invocation — tool calls and resource reads alike — is recorded in the local **`automation_action_log`**:

| Field | Content |
|---|---|
| Timestamp | When the invocation occurred |
| Transport | Which local transport carried it (stdio or SSE / streamable HTTP) |
| Tool/resource name | What was invoked or read |
| Argument summary | A summary of the arguments supplied |
| Outcome | The result of the invocation |

The log is **user-viewable and purgeable**, and it is pruned by the product's retention rules. It exists for transparency: you can always see exactly what an agent did.

> **Tip:** After any delegated run, review the action log to confirm the agent performed only the steps you expected. Purge it whenever you like — purging never affects audit data.

## The delegated flow

The interface supports a standard pattern for an external **local** agent negotiating multi-step actions:

1. **Capability discovery** — the agent performs the standard protocol handshake; the server advertises its tools, resources, and prompts.
2. **Intent mapping** — the agent reads configuration and `audit-list` resources, then invokes `start-audit` with the chosen configuration.
3. **Progress negotiation** — the agent polls `get-audit-progress` (or reads the `crawl-progress` resource) at a bounded cadence. The server enforces a **minimum polling interval** and returns **retry hints** when polled too fast. On push-capable transports the server may additionally emit progress and completion notifications, so polling is optional.
4. **Completion detection** — the agent observes the terminal state (`completed` or `failed`) via the progress snapshot or a completion notification.
5. **Follow-up triggering** — upon completion, the agent may automatically invoke `re-audit` (optionally with the refresh flag) or `analyze-single-page` for targeted verification. The follow-up is an ordinary audit: same limits, same lifecycle, same concurrency caps.
6. **Consumption** — the agent reads `audit-summary` and `high-severity-findings` resources, or invokes the briefing prompts, to produce its analysis.

### Guardrails

- Agent-invoked actions grant **no elevated capability**: everything remains subject to local validation, limits resolution, and the global concurrency cap.
- Long-running work never blocks the protocol channel; all tools return immediately with identifiers.
- Conflicting lifecycle commands from concurrent local clients are resolved by the audit state machine: the first valid transition wins; later commands are rejected idempotently.
- All invocations are recorded in the local, user-viewable, purgeable action log for transparency.

> **Note:** Respect the retry hints. The underlying progress data updates at most once per second while an audit runs, and the server enforces a minimum polling interval — an agent that polls faster gets retry hints, not fresher data.

### Multiple local clients

Several local clients may be connected at once — for example a stdio-spawned agent and a resident SSE agent. The surface stays consistent:

- Conflicting lifecycle commands are resolved by the audit state machine: the first valid transition wins; later commands are rejected idempotently.
- The global concurrency cap applies across all audits, scheduled or manual, whichever client started them.
- The action log records the transport of every invocation, so you can tell which client did what.

## What agents can and cannot do

### Agents can

- Start audits from existing configurations, with validated per-audit limit overrides.
- Pause, resume, and cancel audits; recover interrupted audits from checkpoints.
- Trigger follow-up analysis passes (`re-audit`) and ad-hoc single-page audits (`analyze-single-page`).
- Manually trigger an existing schedule (`run-schedule-now`).
- Read progress, summaries, findings, URL detail, extraction values, schedule status, and all analysis resources.
- Compose briefings via the prompts and narrate results.

### Agents cannot

The MCP boundary excludes all management surfaces. There is **no** management via MCP of:

- Settings and application defaults
- WAF signatures and stop-words
- Plugins
- Export destinations and credentials
- Proxy configuration
- Database providers and embedding providers
- Log imports
- Migration matrices
- GEO tools
- Analytics configuration

Concretely, this also means:

- **No schedule management** — agents can read `schedule-status` and trigger `run-schedule-now`, but creating, editing, enabling, disabling, or deleting schedules stays in the app.
- **No configuration editing** — `start-audit` consumes a configuration reference; creating or modifying projects and configurations is a user action.
- **No export or delivery tools** — the tool surface is exactly the eight tools listed above; exports and destination delivery remain user-initiated or pre-authorized in the app.
- **No credential access** — tokens, site credentials, destination credentials, and proxy credentials are never exposed through the surface.

### Authorization and network boundaries

- Outbound traffic occurs **only with explicit user authorization**: crawl targets you configured (optionally proxied), transfers to destinations you configured and initiated or pre-authorized on a schedule, and connections to an external database you configured. No destinations or external databases exist by default.
- The MCP interface itself adds **no outbound traffic**, and nothing consults any vendor service.
- Revocation is local and immediate: **rotate the token** to invalidate clients, or **disable the interface** to close all listeners and terminate any running stdio adapter.

> **Warning:** An agent is a convenience, not an authority. Treat its outputs as drafts: findings, briefings, and recommendations are analysis products for you to review — the agent cannot publish, deliver, or reconfigure anything on your behalf.

## Practical workflows

Three end-to-end patterns, written as numbered steps. Each assumes the interface is enabled and your local client is connected (see [Local MCP server setup](local-mcp-server-setup.md)).

### Workflow 1 — Delegated site audit with triage and re-audit verification

The canonical delegated flow: an agent runs a full audit, waits for completion, verifies with a follow-up pass, and delivers a triage briefing.

1. **Discover the surface.** The agent completes the standard protocol handshake; the server advertises tools, resources, and prompts.
2. **Map intent.** The agent reads configuration information and the `audit-list` resource (optionally filtered by project) and selects the configuration reference for the site you want audited.
3. **Start the audit.** The agent invokes `start-audit` with the configuration reference and any optional limit overrides (for example, a total page ceiling for a bounded run). It receives the audit identifier and initial state immediately.
4. **Negotiate progress.** The agent polls `get-audit-progress` at a bounded cadence — or reads `crawl-progress` — honoring retry hints. On a push-capable transport it may instead receive progress and completion notifications.
5. **Detect completion.** The agent observes the terminal state `completed` (or handles `failed` using the error code and message).
6. **Trigger a follow-up pass.** The agent invokes `re-audit` on the completed audit (optionally with the refresh flag). The follow-up runs over the same dataset as an ordinary audit under the same limits and concurrency caps, and returns a new audit identifier.
7. **Consume results.** The agent reads `audit-summary` for metric cards and crawl health, then `high-severity-findings` with a severity floor of Error — or invokes the `high-severity-triage` prompt for findings grouped by category with URL evidence, ordered for remediation planning.
8. **Narrate.** The agent produces a remediation briefing from the triage context, optionally opening with the `audit-health-briefing` prompt for the executive summary.
9. **Review the trail.** You review the agent's output and inspect `automation_action_log` to confirm every invocation — start, polls, re-audit, and reads — is recorded with transport, arguments, and outcome.

> **Tip:** For a quick bounded run, pass limit overrides on `start-audit` (such as a page ceiling or depth ceiling) instead of editing the configuration — overrides are validated per audit and never mutate the saved configuration.

Variations on the same flow:

- **Pause and resume:** invoke `pause-audit` to free the machine mid-run — the frontier, seen-set, and counters are checkpointed — then `resume-audit` to rehydrate and continue.
- **Failure and interruption:** a `failed` audit is terminal; an `interrupted` audit (process loss) is recoverable from its last checkpoint with `resume-audit`.
- **Cancellation with partial data:** `cancel-audit` drains workers and finalizes partial data, so the agent can still consume what was crawled.

### Workflow 2 — Manual schedule trigger with delta reporting

You already run a recurring schedule; the agent triggers an on-demand run and reports what changed since the previous run.

1. **Prepare the schedule yourself.** In the app, confirm the schedule exists and is enabled. Schedules are created and managed by you — agents cannot create or edit them.
2. **Check status.** The agent reads the `schedule-status` resource for the schedule reference: enabled flag, next-due time, and last outcome.
3. **Trigger the run.** The agent invokes `run-schedule-now` with the schedule identifier and receives the new audit identifier. The run is an ordinary audit — same limits resolution, lifecycle, checkpoints, and hub events — distinguishable in history by its manual trigger type.
4. **Wait for completion.** The agent polls `get-audit-progress` at a bounded cadence (or consumes notifications) until the terminal state `completed` or `failed`.
5. **Identify the predecessor.** The agent reads `audit-list` (project-filtered) to pick the previous run's audit reference for comparison.
6. **Compose the delta.** The agent invokes the `audit-delta-briefing` prompt with the two audit references — the new run and the previous run — receiving new/resolved findings and metric deltas structured for change reporting.
7. **Report.** The agent narrates the change report: what regressed, what was fixed, and how the metric cards moved.
8. **Verify transparency.** Every step — status read, trigger, polls, list read, prompt — appears in `automation_action_log`.

> **Note:** The scheduler's own rules still govern recurring runs: no overlap between runs of the same schedule, catch-up runs fire once at the next opportunity, and the global concurrency cap queues excess runs. The manually triggered run is an ordinary audit under the same limits resolution, lifecycle, and concurrency cap.

### Workflow 3 — Single-page fix verification with topical and linking briefings

After you remediate specific pages, the agent verifies the fixes and packages topical and internal-linking recommendations.

1. **Verify the fixed page.** The agent invokes `analyze-single-page` with the target URL, the optional rendering flag (for JavaScript-rendered pages), and optional rule filters to focus on the rules you fixed. It receives findings and the element inventory for that page.
2. **Inspect the full record.** For the latest audit containing the page, the agent reads the `url-detail` resource: full URL record, element inventory, findings, links, and redirect chain — including the additive WAF detection, `content_scope` diagnostics, top primary n-grams, `serp_metrics`, header-conflict detail, and semantic relations where present.
3. **Assess topical coverage.** The agent reads `ngram-analysis` with `scope=site` and suitable `zone`/`order`/`minDf` filters — or invokes the optional `ngram-topical-briefing` prompt, which composes `ngram-analysis` with `audit-summary` into an agent-ready topical briefing.
4. **Find internal-link opportunities.** The agent reads `semantic-link-opportunities` with a source filter and similarity band — or invokes the optional `semantic-topical-briefing` prompt, which composes the opportunities with `audit-summary`.
5. **Check crawl health signals.** If the site sits behind a WAF or rate limits, the agent reads `waf-blocks` (provider/class filters) and `host-throttle` to confirm the crawl was not silently blocked or throttled while verifying.
6. **Deliver recommendations.** The agent compiles a short report: verification verdict per fixed page, topical gaps and cannibalization clusters worth addressing, and concrete source→target link suggestions with anchor candidates.
7. **You decide.** All recommendations are analysis output. Applying changes to your site, creating exports, or adjusting configurations remains your action in the app.

> **Note:** Semantic resources depend on the local embedding pass. If the embedding provider is unavailable, semantic passes are marked unavailable with a local diagnostic and the audit still completes — the agent should report the gap rather than treat it as a failure.

## Related guides

- [Local MCP server setup](local-mcp-server-setup.md) — enabling the interface, transports, token authentication and rotation, client configuration, and security boundaries.
- [Lifecycle and checkpointing](../03-running-and-scheduling-audits/lifecycle-and-checkpointing.md) — the audit states, checkpointing, and recovery behavior behind the lifecycle tools.
- [Crawl limits and AIMD politeness](../03-running-and-scheduling-audits/crawl-limits-and-aimd-politeness.md) — limits resolution, override rules, and the adaptive politeness controller behind `host-throttle`.
- [N-grams and topical density](../05-advanced-seo-and-intelligence-modules/n-grams-and-topical-density.md) — the analysis behind `ngram-analysis` and `ngram-topical-briefing`.
- [Semantic internal linking](../05-advanced-seo-and-intelligence-modules/semantic-internal-linking.md) — the local embedding pipeline behind `semantic-link-opportunities` and `semantic-topical-briefing`.
- [List mode and migration matrix](../02-managing-projects-and-crawls/list-mode-and-migration-matrix.md) — the migration validation behind `redirect-matrix-results`.

[← Documentation portal](../README.md)
