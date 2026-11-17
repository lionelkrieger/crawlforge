# Scheduled Audits

> **CrawlForge modular architecture — area of concern: `03-crawl-engine`.**
> Every source section below is reproduced **verbatim and unabridged** from the immutable master documents. No summarization, paraphrase, or omission has been applied.

| Source document | Version | Sections reproduced in this file |
|---|---|---|
| FS-CRAWL-001 — `baseline/functional-specification.md` | v1.4 | §6 (complete section) |

**Notation.** Cross-references inside the reproduced text retain the source convention: a bare `§n` refers to **FS-CRAWL-001**; `FS-CRAWL-002 §n` refers to the extension specification. The [README traceability matrix](../README.md) maps every source section to its file(s) in this hierarchy.

---

> **Verbatim source — FS-CRAWL-001 v1.4: 6. Scheduled Audits — complete section, verbatim and unabridged**

## 6. Scheduled Audits
> Capabilities: CAP-H-188

### 6.1 Schedule model

- A **schedule** binds a reusable audit configuration (section 5.1) to a recurrence rule: either a fixed interval or a calendar-style rule (time of day plus day-of-week/month selectors), evaluated in the user's local timezone.
- Fields: name; project and configuration references; recurrence rule; enabled flag; optional limit overrides; optional completion actions (section 6.5); next-due timestamp; last-run reference.
- Schedules are created, edited, enabled, disabled, and deleted locally; no remote service participates in scheduling.

### 6.2 Execution rules

- The scheduler (section 2.7) computes and persists the next due time after each run; scheduling state survives restarts.
- **Catch-up policy:** if the machine was off or asleep at a due time, the schedule runs **once** at the next opportunity — no backlog of missed runs is replayed — and the run history records it as a catch-up run.
- **No overlap:** two audits of the same schedule never run simultaneously; a due time arriving while the previous run is still active is skipped and recorded as skipped-overlap.
- A scheduled run is an ordinary audit: the same limits resolution, lifecycle, checkpoints, and hub events apply; it is distinguishable in history by its trigger type (manual, scheduled, or catch-up).
- Global concurrency: at most a small fixed number of audits (scheduled or manual) run at once; excess due runs queue in due-time order.

### 6.3 Run history

- Each firing records: schedule reference; resulting audit reference (or failure reason); trigger type; started/finished timestamps; outcome, including completion-action results.
- History is viewable per schedule and pruned by retention (section 9.5).

### 6.4 UI and contract

- The main hub window gains a schedules surface: list with next-due and last-outcome, create/edit forms, enable toggle, a run-now action, and run history.
- REST resources and hub events are defined in section 10.

### 6.5 Completion actions (optional, user-authorized)

- Per schedule, the user may pre-authorize: a local OS notification; an export to one of their configured destinations (section 8.5); a completion message to a notification endpoint they configured themselves.
- Completion actions never run without prior explicit configuration; failure of an action is logged and reported in run history but **never fails the audit**.

---
