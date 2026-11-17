# Task: Orchestrate and build phase 08 of the CrawlForge build plan (Foundations)

You are the **orchestrator** for phase 08. You spawn **two builder sub-agents**, give each one workpiece, run them **in parallel**, coordinate and review their work, then do the final serial workpiece yourself. Phase 08 builds the two harnesses that every later phase is judged by, so correctness matters more than speed.

## Read first

1. `E:\Crawlforge\CLAUDE.md`: workspace rules, build/run, ports, data safety, gotchas. They apply to you and to both sub-agents, whatever tool you run in.
2. `E:\Crawlforge\app\CODEMAP.md` if it exists: where code is registered, wired and mounted.
3. `E:\Crawlforge\buildplan\AGENT-INSTRUCTIONS.md` (v2.0) and `E:\Crawlforge\buildplan\index.yaml`.
4. `E:\Crawlforge\buildplan\phases\phase-08-hotfix-record-and-foundations.yaml`: the workpieces. WP-08-01 and WP-08-02 are completed records (HOTFIX-01, HOTFIX-01b). Your work is **WP-08-03**, **WP-08-04** (wave 8B, parallel, disjoint `owned_paths`) and **WP-08-05** (wave 8C, serial, after both).
5. `E:\Crawlforge\audit-work\SOURCE-OF-TRUTH-LOOP.md` sections 7 and 8.
6. The architecture files each workpiece cites in `context_files` and `architecture_sections`, plus `E:\Crawlforge\app\tests\fixture-site\README.md` and `expected-results.json`, and `E:\Crawlforge\audit-work\reports\runtime-probe-2\` (the verdicts WP-08-04 must reproduce).

## Hard rules for you and every sub-agent

- **Never edit:** `architecture\`, `audit-work\register\`, `audit-work\decisions-log.md`, `audit-work\SOURCE-OF-TRUTH-LOOP.md`, `app\tests\fixture-site\expected-results.json`. If the spec looks wrong or ambiguous, stop that item and report it (loop section 8).
- **Build plan:** only you, the orchestrator, edit `buildplan\`, and only the status fields of phase-08 workpieces (`status`, `assigned_agent`, `started_at`, `completed_at`, `blocker_reason`, `blocked_at`) and `index.yaml` status counts. Sub-agents never edit `buildplan\`.
- **File ownership:** a sub-agent writes only inside its workpiece's `owned_paths`. WP-08-03 and WP-08-04 must never touch each other's paths or any shared file (solution file, DI registration, shared configs). Shared wiring is WP-08-05, done by you after both finish.
- **No product behaviour changes in phase 08.** These workpieces build test infrastructure. If a harness exposes a product defect, record it; do not fix product code.
- **Data safety:** before the first audit run, copy `%APPDATA%\CrawlForge` to `%APPDATA%\CrawlForge-backup-<date>`. Use only clearly named test projects ("Fixture harness ..."). Never modify or delete the owner's real projects (for example "Airpower").
- **Git:** commit only your own work, one commit per workpiece, message starting with the workpiece ID, ending with `Co-Authored-By: <your model name>`. Sub-agents may commit inside their own `owned_paths` only. Never push, force, reset or rebase. If git reports a stale `index.lock` from a crashed process, stop and report it rather than deleting it while another git process may be running.
- Exclude `bin`, `obj`, `node_modules`, `dist`, `tools-env` from searches. In Git Bash prefix commands that contain `/api/...` style arguments with `MSYS_NO_PATHCONV=1`.

## How to run it

### Step 1: Prepare
Confirm the working tree is clean and `dotnet --version` inside `E:\Crawlforge\app` prints a 10.0 SDK. Stop any stale CrawlForge, Electron or fixture-server processes holding ports 47311, 9222 or 9223. Back up the app data folder (data safety rule). Set WP-08-03 and WP-08-04 to `in_progress` with `started_at`.

### Step 2: Spawn two builder sub-agents in parallel
Give each sub-agent one **complete, standalone** brief: the full text of its workpiece from the phase-08 YAML (title, context_files, architecture_sections, capabilities, ui_surfaces, fixture_traps, contract_tests, reopens, owned_paths, deliverables, verification_commands, done_when), plus all of the hard rules above, plus this instruction: "Work only inside your owned_paths. Do not edit the build plan, the architecture or the register. When done, report: files created, how each deliverable is met, the exact verification commands you ran and their output, and anything you could not do."

- **Sub-agent A: WP-08-03**, contract-conformance harness for every cross-layer payload (REST, hub, IPC, MCP, storage-to-engine), including the known REST counter-key mismatch recorded as a failing KnownGap test, not fixed.
- **Sub-agent B: WP-08-04**, fixture acceptance harness: starts the fixture site, runs audits through normal entry points, compares against `expected-results.json`, checks `/__fixture/requests`, walks UI paths over CDP, writes verdict JSON and screenshots. Its done_when requires reproducing probe-2's verdicts on the current build (4 pass, 5 partial, 10 fail, 12 not testable in http and js modes) before anything else.

Tell sub-agent B that it needs the running app and fixture site, and sub-agent A mostly does not; they must not both start or stop the app at the same time. Agree who owns the running app instance (sub-agent B), and have A use its own test process or ask you before needing the app.

If your environment cannot run two sub-agents in parallel, run A then B, and say so in your report.

### Step 3: Coordinate and review
While they work, answer their questions from the spec, never by inventing requirements. When each reports done, **review before accepting**: check every deliverable exists inside its owned_paths, re-run its verification commands yourself, confirm `done_when` is actually met with evidence, and confirm no file outside owned_paths changed (`git status`, `git diff --stat`). Send work back with specific findings if anything is missing. Only then set the workpiece to `completed` with `completed_at`, and commit the ledger update.

### Step 4: WP-08-05 yourself (serial)
After both are completed: add the conformance test project to `CrawlForge.sln`, write `app\tests\fixture-acceptance\README.md`, and create `app\tools\gates\run-phase-gates.ps1` that executes each phase's `orchestrator_gates` (build with the pinned SDK, all test suites, the conformance suite, `python audit-work\register\check_traceability.py`, `python audit-work\register\check_buildplan.py`, and the fixture harness for the phase's traps). Meet WP-08-05's done_when, run its verification commands, commit, update the ledger.

### Step 5: Phase gate
Run `app\tools\gates\run-phase-gates.ps1` for phase 08. If it passes, set phase-08 to completed in `index.yaml` and commit.

## Final report

Write `E:\Crawlforge\audit-work\reports\phase-08\PHASE-08-REPORT.md` and reply with its summary:
- each workpiece: status, commit, how done_when was proven;
- the harness verdicts on the current build compared with probe-2 (must match);
- the conformance suite results, including KnownGap failures;
- product defects the harnesses exposed (recorded, not fixed);
- any spec ambiguity you stopped on;
- whether the two sub-agents ran in parallel, and any coordination problems.
