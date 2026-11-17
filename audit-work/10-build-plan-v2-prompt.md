# Task: Generate the CrawlForge build plan v2.0 from the architecture and the measured baseline

You are planning, not building. Produce the build plan that takes the current CrawlForge app to the v2.0 specification. Do not change application code or the architecture. The plan is reviewed by the owner before any building starts.

## Why this plan is different

The v1.1 plan declared 43/43 workpieces and 48/48 acceptance criteria complete while no audit could run from the UI and 16 of 24 screens were unreachable. Workpieces were checked in isolation by unit tests that supplied their own inputs. The v2.0 plan must make that impossible: every workpiece is organised around an end-to-end flow, cites what it delivers, and is complete only when a real audit against the fixture site proves it through the UI or MCP.

## Inputs (read in this order)

1. `E:\Crawlforge\audit-work\SOURCE-OF-TRUTH-LOOP.md`: operating rules, especially section 7 (build plan rules) and section 8 (discoveries during build).
2. `E:\Crawlforge\architecture\README.md` (v2.0), `architecture\changes\INDEX.md`, and the domain files. The architecture is the **only** definition of done.
3. `E:\Crawlforge\architecture\06-frontend-shell-and-ui\04-ui-surface-map.md`: surfaces UI-01 to UI-24 and the configure/trigger/view binding for every in-scope capability.
4. `E:\Crawlforge\architecture\TRACEABILITY.md` and `E:\Crawlforge\audit-work\register\capabilities.yaml`, `untagged.yaml`: 275 capabilities; 251 in scope; 24 deferred, out of scope or via MCP (not planned).
5. `E:\Crawlforge\architecture\07-quality-and-verification\` (acceptance criteria #1 to #58, fixture site and acceptance rule) and `E:\Crawlforge\app\tests\fixture-site\expected-results.json` (31 traps).
6. **Measured state, strongest evidence:** `E:\Crawlforge\audit-work\runtime-probe-2\` (clean normal-entry-point probe). If it does not exist, stop and report that probe-2 must run first. Also `audit-work\hotfix-01-report.md` and `audit-work\runtime-probe\RUNTIME-PROBE.md` (first probe, diagnostic workaround runs, use as secondary).
7. Code-level gap evidence: `audit-work\UNDERBUILD-AUDIT.md` (gap IDs G01 to G29), `audit-work\05-functionality-audit-results.md`, `audit-work\ux-api-gap-audit.md`.
8. The v1.1 plan: `E:\Crawlforge\buildplan\index.yaml`, `buildplan\phases\*.yaml`, `buildplan\AGENT-INSTRUCTIONS.md`, `E:\Crawlforge\build_plan_orchestration_prompt.md`. Keep its YAML format, orchestration model, file-ownership and wave rules so the existing orchestration approach still works.
9. `E:\Crawlforge\audit-work\decisions-log.md` (D-001 to D-018).

## Step 1: Freeze v1.1

- `git mv` `buildplan\index.yaml`, `buildplan\phases\` and `buildplan\AGENT-INSTRUCTIONS.md` into `buildplan\v1.1\` unchanged, and add `buildplan\v1.1\README.md`: "Historical. Superseded by build plan v2.0 (2026-11-17). Its completion statuses were not verified against the running product; see audit-work/runtime-probe."
- Commit: `Build plan: freeze v1.1 as history`.

## Step 2: Capability state baseline

Write `buildplan\capability-baseline.yaml`: for every in-scope capability (251), its **as-built state** with an evidence level:

- `state`: `works` | `partial` | `unwired` | `missing_ui` | `missing_engine` | `defect`
- `evidence_level`: `runtime` (probe-2, strongest) | `code` (verified by reading the code now) | `claim` (only an audit document says so)
- `evidence`: one line with the source (probe-2 trap/surface result, file and symbol, or audit gap ID)

Rules: runtime evidence beats code, code beats claims. Where only claims exist for a capability that plan scope depends on, check the code yourself and upgrade to `code`. A capability counts as `works` only with runtime evidence that it works through its UI binding (or MCP for exempt ones).

## Step 3: The plan

Create `buildplan\index.yaml` (version 2.0) and `buildplan\phases\phase-08-*.yaml` onward, continuing the v1.1 numbering. Record HOTFIX-01 (commit `db53378`) and HOTFIX-01b as completed workpieces in `phase-08` with their evidence.

**Organise phases as end-to-end flows**, each crossing engine, wiring, API and UI. Suggested order (adjust with stated reasons):

1. **Foundations:** contract-conformance test harness for every cross-layer payload (REST, hub, IPC, MCP, storage-to-engine), including the known REST counter-key mismatch; an automated **fixture acceptance harness** that starts the fixture site, runs an audit through the normal entry points, compares results with `expected-results.json`, checks `/__fixture/requests`, and walks UI navigation paths over CDP.
2. **Flow 1, HTTP audit end to end:** project overview (UI-02), configurations and configuration editor (UI-03, UI-04), audit run view fixes, report window with dashboard, issue explorer, URL explorer and export (UI-11 to UI-13, UI-20), plus the crawl-side gaps the HTTP traps expose (sitemap ingestion, redirect-hop persistence, robots findings, cookie jar, crawler-trap detection, structured-data findings in audits).
3. **Flow 2, rendering:** direct CDP layer, capture modes, pinned bundled browser, rendered document wired into full audits, raw-vs-rendered comparison.
4. **Flow 3, site types and SPA:** site-type rules, click discovery with its safety rules, consent states, websocket-tolerant wait rule (betting-style traps).
5. **Flow 4, remaining report surfaces:** specialist surfaces, visualizations, comparisons, templates, segments, clients, schedules, logs, plugins.
6. **Flow 5, MCP steering and agent data contract.**
7. **Flow 6, Google integrations** (test double with recorded responses).
8. **Re-baselining:** criteria #1 to #48 re-verified against the fixture site, then #49 to #58.

Distribution (`09-distribution`) is release-time work: list it as a final, unscheduled phase only.

**Every workpiece must have, in addition to the v1.1 fields** (`context_files`, `owned_paths`, `deliverables`, `depends_on_workpieces`, `verification_commands`, `acceptance_criteria_refs`):

- `architecture_sections`: files and section numbers it implements;
- `capabilities`: capability IDs it delivers, each with its baseline state;
- `ui_surfaces`: surface IDs it must make reachable and functional (from the binding table);
- `fixture_traps`: trap IDs that prove it;
- `contract_tests`: cross-layer payloads it adds or changes, each with a conformance test;
- `reopens`: v1.1 workpiece IDs whose delivery it completes or corrects, with the evidence;
- `done_when`: the fixture-harness run and UI path walk that must pass. Unit tests alone never complete a workpiece.

Size rules: each workpiece is a job one builder agent can finish and verify in one session; one owner per file path per wave; shared wiring (DI registration, routing, navigation, README) goes in a final serial workpiece per phase. Builders never edit the spec or the plan; discoveries follow SOURCE-OF-TRUTH-LOOP section 8.

**Gates** (`orchestrator_gates`): build via `launch-crawlforge.ps1`'s build steps with the pinned SDK, all test suites, the contract-conformance suite, `python audit-work\register\check_traceability.py`, and the fixture acceptance harness for the flow's traps.

Write `buildplan\AGENT-INSTRUCTIONS.md` v2.0: carry over the v1.1 manual, then update master-document references (now `architecture\baseline\...` and the living `architecture\`), and add the acceptance rule, contract-test rule, UI reachability rule, and discovery protocol.

## Step 4: Coverage check

Write `audit-work\register\check_buildplan.py` (Python 3, PyYAML) and run it until it passes. It fails when: an in-scope capability whose baseline state is not `works` is covered by no workpiece; a workpiece cites no capabilities, architecture sections, or fixture traps (UI workpieces also need surfaces); a cited capability, surface, trap, criterion or section does not exist; a surface bound in the UI map is made reachable by no workpiece (unless already `works`); file ownership overlaps within a wave.

## Output and commits

Commit after Step 3 (`Build plan v2.0: flows, workpieces, gates`) and after Step 4 (`Build plan v2.0: coverage check`), each ending with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Final reply

Reply with: capability baseline counts by state and evidence level; the phases with workpiece counts and the flow each proves; the five riskiest workpieces and why; any capability you could not place; and any place where the architecture is ambiguous enough that a builder would have to guess (these become change records before building).
