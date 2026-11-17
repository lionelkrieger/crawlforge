# Scheduled Audits

CrawlForge can run audits for you on a recurrence — every six hours, every
weekday morning, on the 1st and 15th of each month — without anyone clicking
a button. A **schedule** binds one of your saved crawl configurations to a
recurrence rule, evaluated in your **local timezone**, entirely on your
machine. No remote service participates in scheduling, and nothing about a
schedule leaves the computer it was created on.

This guide documents the exact recurrence grammar, how the scheduler handles
asleep machines and overlapping runs, what the run history records, and the
optional completion actions you can pre-authorize.

> **Note:** Scheduled runs are ordinary audits. The same limits resolution,
> lifecycle, checkpoints, and live progress events apply — a scheduled run
> is distinguishable only by its trigger type in history. Everything in
> [Lifecycle and checkpointing](./lifecycle-and-checkpointing.md) applies to
> scheduled runs unchanged.

## The schedule model

A schedule carries these fields:

| Field | Default | Bounds | Notes |
|---|---|---|---|
| Name | — | Non-empty text | Display name for the schedule. |
| Project reference | — | Existing project | The owning project. |
| Configuration reference | — | Existing configuration | The reusable audit configuration the schedule runs. |
| Recurrence rule (`cron`) | Empty | The grammar below | Empty means manual-only: the schedule exists but never fires on its own. |
| Enabled flag | Enabled | On/Off | Disabled schedules keep their definition but do not fire. |
| Limit overrides | None | Per-audit override fields | Optional overrides applied to each scheduled run. |
| Completion actions | None | See below | Optional, pre-authorized actions run after each firing completes. |
| Next-due timestamp | Computed | — | Persisted; recomputed after every run. |
| Last-run reference | None | — | The most recent firing. |

Schedules are created, edited, enabled, disabled, and deleted locally. The
schedules surface in the main window shows the list with next-due and
last-outcome, create/edit forms, an enable toggle, a **run-now** action, and
the run history.

## The recurrence grammar

Recurrence rules use a small, locked local grammar with exactly two families:
`interval:` and `calendar:`. The rule is carried in the schedule's `cron`
field (the wire name is historical — **this is not cron syntax**; standard
cron expressions are not accepted).

### Family 1: `interval:<timespan>` — fixed intervals

A constant-format TimeSpan: `hh:mm:ss`, optionally prefixed with a day count
and a dot. The interval must be **positive and at most 366 days**.

| Example | Meaning |
|---|---|
| `interval:00:30:00` | Every 30 minutes. |
| `interval:06:00:00` | Every 6 hours. |
| `interval:1.00:00:00` | Every 24 hours. |
| `interval:7.00:00:00` | Every 7 days. |

Interval rules fire a fixed duration after the previous firing's reference
instant — they do not anchor to a time of day. If you need "every day at
09:00", use a calendar rule instead.

### Family 2: `calendar:HH:mm` — time of day, with optional day selectors

A 24-hour local time, optionally followed by `;days=...`:

| Example | Meaning |
|---|---|
| `calendar:09:00` | Every day at 09:00 local time. |
| `calendar:09:00;days=Mon,Wed,Fri` | Monday, Wednesday, Friday at 09:00. |
| `calendar:02:30;days=1,15` | The 1st and 15th of every month at 02:30. |

Selector rules:

- Weekday names are the **three-letter English names**: `Mon`, `Tue`, `Wed`,
  `Thu`, `Fri`, `Sat`, `Sun` (case-insensitive).
- Month days are integers **1–31**. A month that lacks a selected day (for
  example the 31st in a 30-day month) simply has no firing that month.
- **Weekday and day-of-month selectors must not be mixed** in one rule.
- Duplicate days are rejected; the selector must list at least one day.
- Without a `days=` selector, the rule fires daily.

### Validation

Rules are validated when the schedule is saved, with descriptive errors —
for example an interval that is zero or negative, a time that is not `HH:mm`,
an unknown day name, or a mixed weekday/month-day selector. A malformed rule
never reaches the scheduler.

> **Warning:** Only `interval:` and `calendar:` rules exist. Five-field cron
> expressions (`0 9 * * 1`), natural-language recurrences, and other schemes
> are rejected at save time.

## Local timezone evaluation

Calendar rules are evaluated in **your local timezone**, not UTC:

- The next occurrence is always **strictly after** the current instant.
- **Fall-back (ambiguous local time):** when clocks move back and a local
  time occurs twice, the firing resolves deterministically to the
  **standard-time offset** — the second of the two occurrences (the one at
  the standard-time offset).
- **Spring-forward (nonexistent local time):** if the scheduled local time
  does not exist on a given date (for example 02:30 on a spring-forward
  night), that date is **skipped** and the rule fires on the next matching
  date. No duplicate or phantom run is produced.

Interval rules are timezone-independent: they add a fixed duration.

> **Tip:** If you travel with your laptop, calendar rules follow the
> machine's current local timezone. A "09:00 daily" audit fires at 09:00
> wherever the machine is — which is usually what you want for reports tied
> to your working day, but worth knowing for overnight crawls of sites in
> other regions.

## How the scheduler executes

The scheduler is a local background service. It wakes on a short tick (30
seconds by default), processes due schedules, and **computes and persists the
next due time after each run**. Scheduling state survives restarts — no
scheduling state exists only in memory.

### Catch-up: the machine was off or asleep

If the machine was off or asleep at a due time, the schedule runs **once**
at the next opportunity — the first scheduler tick after the machine is
back. Two guarantees matter:

- **No backlog replay.** Ten missed nightly runs do not produce ten audits.
  Exactly one catch-up run fires, and the next-due time moves strictly
  forward.
- **Honest labeling.** The run history records the firing with trigger type
  `catch-up`, so you can always tell a catch-up run from a on-time
  `scheduled` run.

A firing delayed beyond the catch-up threshold (one scheduler tick by
default, configurable) is classified as catch-up; a healthy running
scheduler processes due times within a single tick and labels them
`scheduled`.

### No overlap

Two audits of the **same schedule** never run simultaneously. If a due time
arrives while the previous run of that schedule is still active, the firing
is **skipped** and recorded in history as **skipped-overlap** — with no audit
created. The next due time is still computed, so the schedule continues
normally after the long run finishes.

### Global concurrency and queueing

At most a small fixed number of audits — scheduled or manual — run at once
(**2 by default**, locally configurable, minimum 1). Excess due runs **queue
in due-time order** and start as slots free up. A busy machine therefore
delays runs; it never drops them silently and never stampedes the CPU with
parallel crawls.

### Trigger types

Every firing records what caused it:

| Trigger | Meaning |
|---|---|
| `scheduled` | An on-time firing of the recurrence rule. |
| `manual` | You pressed **run-now** (or started the schedule's audit yourself). |
| `catch-up` | A due time missed while the machine was off/asleep, fired once at the next opportunity. |

## Run history

Each firing records a history row:

- the schedule reference,
- the resulting audit reference — or the failure reason if no audit could
  start,
- the trigger type (`scheduled`, `manual`, `catch-up`),
- started and finished timestamps,
- the outcome, **including completion-action results** (each action's kind,
  whether it succeeded, and detail).

Skipped-overlap firings appear as skipped rows with the reason, so a gap in
audits is always explained. History is viewable per schedule
(`GET /api/schedules/{id}/runs`, paginated) and is pruned by the retention
policy (the shipped local default keeps 90 days of run history).

> **Tip:** When a morning report is missing, check the history first:
> skipped-overlap means the previous crawl ran long; a catch-up row means
> the machine was asleep; a failure reason means the audit itself could not
> start (for example, a deleted configuration).

## Completion actions (optional, user-authorized)

Per schedule, you may **pre-authorize** actions that run when a scheduled
audit completes. They never run without prior explicit configuration, and
they only ever target things you configured yourself:

| Kind | What it does | Required configuration |
|---|---|---|
| `local-notification` | A local OS notification surfaced through the shell. | None; optional `title` and `message`. |
| `export-delivery` | Generates an export of the finished audit and delivers it to one of your configured destinations. | A configured destination (`destinationId`) and an export `format` (`xlsx`, `csv`, `html`, or `pdf`); optional `datasets` selection. |
| `notification-endpoint` | Posts a completion message to a notification endpoint **you configured yourself**. | An absolute `http(s)` endpoint URL; optional `title` and `message`. |

Ground rules:

- **Explicit authorization only.** No action exists by default; each one is
  something you added to the schedule.
- **Failures never fail the audit.** If an action fails (destination
  unreachable, endpoint down), the failure is logged and reported in the run
  history — the audit's own outcome is untouched.
- **Validation at save time.** Misconfigured actions (missing destination,
  missing format, non-http(s) endpoint) are rejected when you save the
  schedule, never discovered mid-run.
- **Bounded waits.** The export action waits for the export job to finish
  generating, up to a local timeout (5 minutes by default), then reports the
  outcome either way.

> **Warning:** `export-delivery` and `notification-endpoint` are the only
> scheduled features that touch anything beyond your machine, and only
> because you explicitly configured the destination or endpoint.
> Credentials for destinations are encrypted at rest and never returned in
> clear by the API. There is no vendor or telemetry destination of any kind.

### Setting up completion actions

1. Open the schedule's edit form (or build the `completionActions` array in
   the API request).
2. Add one action per outcome you want — for example a
   `local-notification` for yourself plus an `export-delivery` of the
   findings spreadsheet to your configured destination.
3. Save. The schedule validates every action immediately.
4. After the next firing, read the run history row: each action's success
   or failure is recorded alongside the audit outcome.

Example — create a weekly schedule with a local notification via the API:

```http
POST /api/projects/{projectId}/schedules
Content-Type: application/json

{
  "configurationId": "{configurationId}",
  "name": "Weekly technical audit",
  "enabled": true,
  "cron": "calendar:09:00;days=Mon",
  "completionActions": [
    { "kind": "local-notification", "parameters": { "title": "CrawlForge", "message": "Weekly audit finished" } }
  ]
}
```

## Managing schedules via the API

All routes are loopback-only JSON, with the standard response envelope and
offset/limit pagination:

| Operation | Method & path |
|---|---|
| List a project's schedules | `GET /api/projects/{id}/schedules` |
| Create a schedule | `POST /api/projects/{id}/schedules` |
| List schedules across projects | `GET /api/schedules` |
| Read / update / delete a schedule | `GET`, `PUT`, `DELETE /api/schedules/{id}` |
| Enable / disable | `POST /api/schedules/{id}/enable`, `/disable` — returns the new state and next-due time |
| Run now | `POST /api/schedules/{id}/run-now` — returns the started audit identifier (a `manual` trigger) |
| Run history | `GET /api/schedules/{id}/runs` |

Create/update fields: `configurationId`, `name`, `enabled` (defaults to
true), `cron` (the recurrence expression; empty clears it to manual-only),
and `completionActions`. Schedule records additionally carry `id`,
`projectId`, `nextDueAt`, `lastRunAt`, `createdAt`, and `updatedAt`.

Live hub events accompany the lifecycle: `schedule-fired` when a run starts,
`schedule-run-completed` when it finishes (with outcome and action results),
and `local-notification` for the OS-notification action.

> **Note:** Enabling or disabling a schedule recomputes and returns the
> next-due time immediately — the list view always shows when the schedule
> will actually fire next.

## Practical workflows

### Weekly client report, every Monday at 09:00

1. Save a crawl configuration for the client's site (scope, limits,
   politeness).
2. Create a schedule on the client's project with
   `calendar:09:00;days=Mon`.
3. Add an `export-delivery` completion action: the findings dataset as
   `xlsx` to your configured delivery destination.
4. Leave it enabled. Each Monday the audit runs unattended; the history row
   tells you whether the export delivered.

### Overnight crawl of a large site, twice a month

1. Use `calendar:02:30;days=1,15`.
2. Remember the spring-forward rule: if 02:30 does not exist on a firing
   date, that date is skipped and the next matching date fires.
3. If the machine sleeps through a due time, exactly one `catch-up` run
   fires when it wakes — check the trigger type in history.

### Health check every six hours

1. Use `interval:06:00:00` with a sample-crawl configuration (bounded
   depth/pages) so each run is quick.
2. Add a `local-notification` action so completions surface on your desktop.
3. If a run ever exceeds six hours, the next due time is recorded as
   skipped-overlap rather than piling up concurrent crawls of the same site.

### Pause a schedule without deleting it

1. Disable it (`POST /api/schedules/{id}/disable` or the enable toggle).
2. The definition, history, and completion actions are preserved; no
   firings occur while disabled.
3. Re-enable when ready — the next-due time is recomputed from now, not
   replayed from the past.

## What scheduled audits are not

- **Not a headless command line.** Starting audits from a CLI without any
  window is an open scope decision and is not part of this version.
  Schedules do run fully unattended once configured, and the local MCP
  interface allows programmatic control while the application is running.
- **Not a cloud service.** The scheduler lives in the local backend; if the
  application is not running (or the machine is off), nothing fires until
  the next launch — then the catch-up policy applies.
- **Not an update channel.** No schedule, action, or background service
  checks for product updates; no update checking of any kind exists in this
  version.

## Related guides

- [Lifecycle and checkpointing](./lifecycle-and-checkpointing.md) — what
  happens inside each scheduled run, including crash recovery.
- [Crawl limits and AIMD politeness](./crawl-limits-and-aimd-politeness.md) —
  the limits and per-host rate control every scheduled run inherits.
- [Creating projects and scopes](../02-managing-projects-and-crawls/creating-projects-and-scopes.md)
  — the configurations schedules bind to.

[← Documentation portal](../README.md)
