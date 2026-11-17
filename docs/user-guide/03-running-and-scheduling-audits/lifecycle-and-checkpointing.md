# Audit Lifecycle and Checkpointing

Every CrawlForge audit is a long-running, **resumable** local job. You can
pause it, resume it, cancel it, or lose the whole process to a crash — and in
every case the work already done is preserved on disk. Nothing about an
audit's progress lives only in memory.

This guide covers the audit state machine, what each control action does,
how checkpointing works, exactly what survives a restart, and practical
recovery workflows for when things go wrong.

> **Note:** All lifecycle control happens over the loopback-only local API
> and the real-time hub. There is no remote orchestration, no telemetry, and
> no cloud dependency — an audit runs identically on an air-gapped machine.

## The audit lifecycle at a glance

The specification's state flow is:

```
configuring → running → (paused ⇄ running) → completed | cancelled | failed | interrupted
```

- **configuring** — the audit is being set up (configuration chosen, limits
  resolved) but no work has started.
- **running** — actively crawling and/or auditing.
- **paused** — suspended by you; resumable from the latest checkpoint.
- **completed** — finished successfully. Terminal.
- **cancelled** — stopped by you; partial results are retained. Terminal.
- **failed** — terminated by an unrecoverable error. Terminal.
- **interrupted** — the process was lost while the audit was running.
  Recoverable from the last checkpoint (see [Crash recovery](#crash-recovery)).

The audit list and detail views report these wire statuses:

| Status | Meaning | Resumable? |
|---|---|---|
| `queued` | Created but not yet started. | Starts normally; after a restart it is offered for resume like any unfinished run. |
| `running` | Actively crawling and/or auditing. | — |
| `paused` | Suspended; resumable from the latest checkpoint. | Yes — `resume`. |
| `completed` | Finished successfully. | No (terminal). Use re-audit for a fresh pass. |
| `cancelled` | Stopped by the user; partial results retained. | No (terminal). |
| `failed` | Terminated by an unrecoverable error. | No (terminal). Inspect diagnostics, then start a new audit. |

> **Note:** `interrupted` is a recovery condition rather than a resting
> status. When the backend restarts after an unexpected exit, audits that
> were left running are marked interrupted and surfaced as **paused,
> resumable from their last checkpoint** — never silently restarted, never
> lost.

## Processing phases

Within a run, the audit advances through coarse phases, reported in the audit
detail (`GET /api/audits/{id}`) and in the progress stream:

1. **Crawling** — fetching URLs and building the link graph.
2. **Analyzing** — running the audit analyzers over collected pages.
3. **Scoring** — computing category and overall scores.
4. **Finalizing** — persisting aggregates and finalizing artifacts.
5. **Finished** — all phases complete.

Checkpoints are written during the crawl phase; the later phases work over
data that is already durably persisted, so they do not need frontier
checkpoints of their own.

## Starting an audit

1. Choose a saved crawl configuration (seeds, scope, rendering mode,
   politeness options, and the rest of the audit inputs).
2. Optionally apply per-audit limit overrides.
3. Start the audit — in the UI, or via `POST /api/audits` with the
   configuration reference and optional overrides.

The call **returns immediately** with the audit identifier and initial
state. All progress flows over the real-time hub; you never hold a request
open waiting for a crawl.

Two edge behaviors are guaranteed:

- **Invalid limits are rejected up front** with a descriptive local error —
  the audit never starts with a bad configuration.
- **Starting an audit that is already running is rejected idempotently**
  with the current state. Double-clicking "start" cannot spawn a duplicate
  run.

> **Tip:** A running audit keeps the limit snapshot it started with. If you
> change defaults mid-run, the change applies to the next audit — finish or
> cancel the current one first if you want the new values to take effect.

## Pausing and resuming

**Pause** (`POST /api/audits/{id}/pause`) suspends the audit and writes a
checkpoint. **Resume** (`POST /api/audits/{id}/resume`) rehydrates that
checkpoint and continues. Both return the new state. You can pause and
resume as often as you like — the `paused ⇄ running` loop is a first-class
part of the lifecycle, not an emergency measure.

What a pause checkpoint captures:

- **The frontier** — the prioritized queue of URLs still to fetch, with
  their depths and ordering.
- **The seen-set** — which canonical URLs have already been handled, so
  nothing is fetched twice on resume. (Every URL state transition is
  persisted as it happens, so the seen-set is restored from the persisted
  URL records.)
- **The counters** — discovered/fetched/failed totals and friends, so
  progress reporting continues seamlessly.
- **The AIMD controller state** — per-host rate, concurrency, TTFB baseline,
  success streak, and cooldown. Resume rehydrates it **exactly**: the crawl
  continues with the same politeness posture it had when you paused.

What resume does *not* do: re-fetch completed URLs, reset counters, or
re-resolve limits. The audit continues the same run.

> **Note:** Pause is cooperative and quick, but a fetch already in flight
> completes or times out first. The checkpoint reflects a consistent frontier
> state, not a torn one.

## Canceling

**Cancel** (`POST /api/audits/{id}/cancel`) stops the audit permanently:

1. Workers are drained — in-flight requests are wound down, not abandoned
   mid-write.
2. Partial data is **finalized**: everything fetched up to the cancellation
   point remains queryable — URL records, findings, scores computed so far,
   and exports all work against the partial dataset.
3. The audit enters the terminal `cancelled` state.

Cancel is the right tool when you started the wrong scope, the site is
clearly not what you expected, or you simply want the partial results now.
It is not a pause — a cancelled audit cannot be resumed. Start a new audit
(or a re-audit) to continue the work.

> **Warning:** `completed`, `cancelled`, and `failed` are terminal. There is
> no "un-cancel". If you might want to continue later, **pause** instead.

## Checkpoints: what is saved, and when

Checkpoint rows live in the local database (`crawl_checkpoints`), one
snapshot per save, containing the serialized frontier, the counters, and the
serialized per-host AIMD state. They are written:

| Moment | What happens |
|---|---|
| Every 25 fetches during the crawl | A rolling checkpoint bounds crash loss to at most the last few fetches. |
| On pause | A full checkpoint before the run suspends. |
| At the end of the crawl phase | A final checkpoint before analysis begins. |
| On graceful shutdown | Every active run is checkpointed before the backend exits. |

Because URL records, link edges, and extracted elements are **persisted
incrementally in transactional batches** throughout the crawl, a checkpoint
is a coordination point, not the only durable copy of your data. The engine
never requires the full dataset in memory, and writes are crash-safe
(provider-appropriate durability plus transactions).

> **Note:** Checkpoints and throttle history are subject to the standard
> retention policy. Old audit data — including its checkpoints — is pruned
> over time; recent and active audits are always fully recoverable.

## What survives a restart

| Artifact | Survives restart? | How |
|---|---|---|
| URL records, link edges, extracted elements | Yes | Persisted incrementally in transactional batches as the crawl runs. |
| Per-URL states (`queued → fetching → fetched \| redirected \| error \| blocked \| skipped`) | Yes | Every transition is persisted when it happens. |
| Frontier, seen-set, counters | Yes | Restored from the latest checkpoint and the persisted URL records. |
| AIMD per-host state (pause/resume) | Yes, exactly | Serialized into the checkpoint payload; rehydrated on resume. |
| AIMD per-host state (crash) | Re-derived conservatively | Hosts restart at the initial rate (1 req/s) with a fresh warm-up — speed is re-earned, never assumed. |
| Audit status, phase, counters | Yes | Persisted; unfinished runs are marked for resume on startup. |
| Schedules and next-due times | Yes | The scheduler persists state after every run; no scheduling state exists only in memory. |
| Schedule run history | Yes | Persisted rows, pruned by retention. |
| Findings, scores, aggregates | Yes | Persisted as they are produced; dashboard reads come from aggregates. |
| In-flight fetches at the moment of loss | Re-queued | The checkpoint frontier determines what is still outstanding. |
| Crash diagnostics (minidumps, logs) | Yes | Captured to the local diagnostics directory; never transmitted. |

## Graceful shutdown

When you quit the application normally:

1. The shell requests a graceful stop.
2. The backend **checkpoints any running crawl**, drains workers and
   background services (scheduler, retention, export retries), closes the
   database, and exits.
3. The shell exits.

On the next launch, your unfinished audits are waiting in `paused` state,
resumable from exactly where they stopped — including their AIMD posture.
Scheduled audits pick up from their persisted next-due times; a due time
missed while the machine was off is handled by the catch-up policy (see
[Scheduled audits](./scheduled-audits.md)).

## Crash recovery

If the backend exits unexpectedly:

1. A **local native diagnostic minidump** is captured, alongside the
   structured local logs.
2. The shell **restarts the backend with bounded backoff** — automatically,
   without asking you.
3. On restart, the backend applies any pending schema migrations, then
   marks every audit that was running as **interrupted** and offers resume
   from its checkpoint. In the audit list these appear as paused runs with
   their progress intact.
4. Background services re-derive their state from persisted data. The
   scheduler, retention, and export retries come back without manual
   intervention.
5. AIMD starts conservatively: each host re-warms at 1 req/s. Expect a brief
   ramp-up period after a crash — this is deliberate politeness, not a
   malfunction.

**After repeated failures**, the shell stops retrying blindly and shows a
**local recovery screen** with:

- diagnostic information about the failure,
- a pointer to the local crash artifacts (minidumps and logs),
- a retry action.

Everything on that screen is local. You can inspect or purge diagnostic
artifacts at any time (`GET /api/diagnostics/artifacts`, kind-filtered;
`DELETE /api/diagnostics/artifacts` to purge). Logs, minidumps, and the
automation action log are user-viewable and purgeable — and are **never
transmitted automatically**.

> **Tip:** If a crash recurs at the same point in a crawl, the usual suspects
> are environmental (disk full, database connection lost for the optional
> external provider, an oversized response). The recovery screen's artifact
> pointer is the fastest route to the actual error text.

## Practical recovery workflows

### Resume after a normal quit

1. Launch CrawlForge.
2. Open the audit list; unfinished audits show as `paused`.
3. Open the audit and choose **Resume** (`POST /api/audits/{id}/resume`).
4. Watch the progress stream pick up from the persisted counters. AIMD
   continues with its checkpointed per-host state.

### Resume after a crash or power loss

1. Relaunch the application (the shell may already have restarted the
   backend automatically).
2. The previously running audit is marked interrupted and offered for
   resume — it appears as `paused` with its checkpoint intact.
3. Resume it. URLs completed after the last rolling checkpoint are not
   re-fetched — the seen-set restored from the persisted URL records
   deduplicates them — and the frontier continues from the checkpoint.
   Expect a short AIMD warm-up as hosts re-earn their rates.
4. If the backend keeps failing, use the recovery screen: read the
   diagnostics, check the pointed-to crash artifacts, fix the environmental
   cause, then retry.

### Stop now, keep the partial results

1. Choose **Cancel** on the running audit.
2. Wait for the drain to finalize (the state becomes `cancelled`).
3. Work with the partial dataset: explore URLs and findings, run exports,
   or compare against another audit. Nothing fetched is discarded.

### Re-run the analysis without re-crawling

1. On a finished audit, choose **Re-audit**
   (`POST /api/audits/{id}/reaudit`, optional refresh flag).
2. A new audit identifier is created, linked to the same dataset. The
   analysis passes re-run over stored content — fully offline where the
   content store already holds the bodies.
3. Use the audit comparison view to see new/resolved findings and metric
   deltas between the two runs.

> **Note:** WAF detection catalogues are snapshotted per audit, so a re-audit
> is reproducible. Re-auditing under an updated catalogue may reclassify
> previously fetched URLs; those surface as ordinary finding deltas in the
> comparison.

### A scheduled run was missed overnight

That is the scheduler's catch-up policy, not a lifecycle fault: the schedule
fires **once** at the next opportunity and records the run as a catch-up
run. See [Scheduled audits](./scheduled-audits.md) for the full behavior.

## Progress and counters

While an audit runs, the `crawl-progress` hub event streams updates
**throttled to at most one message per second**, carrying:

- counters: queued, fetched, errors, blocked (plus the `wafBlocked`
  sub-counter for WAF challenge blocks),
- throughput and elapsed time,
- estimated remainder,
- the current phase,
- the throttled-host count and mean effective rate (AIMD summary).

The same counters are readable at any time via `GET /api/audits/{id}`
(detail, counters, phase) and `GET /api/audits` (summaries with counters,
filterable by project and status). Because progress is persisted, the
numbers you see after a resume are continuous with the numbers before the
pause.

## Local-only guarantees

- The backend binds to the **loopback interface only**; lifecycle commands
  never arrive from, or report to, any other machine.
- **Zero telemetry**: no progress, crash, or usage data leaves the machine.
  Outbound traffic exists only for your crawl targets (optionally proxied),
  destinations you explicitly configured, and a user-configured external
  database server — and only when that provider is selected (none is
  configured by default).
- Crash artifacts stay on the local filesystem, viewable and purgeable by
  you.
- Every feature is enabled locally by default; there are no remote feature
  flags, accounts, licenses, or entitlements gating any lifecycle action.

## Related guides

- [Crawl limits and AIMD politeness](./crawl-limits-and-aimd-politeness.md) —
  the rate controller whose state rides along in every checkpoint.
- [Scheduled audits](./scheduled-audits.md) — recurrence, catch-up runs, and
  completion actions for unattended runs.
- [Creating projects and scopes](../02-managing-projects-and-crawls/creating-projects-and-scopes.md)
  — the configurations that audits are started from.

[← Documentation portal](../README.md)
