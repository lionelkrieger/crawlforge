# Task: Coordinate and build phase 09 of the CrawlForge build plan (Flow 1, HTTP audit end to end)

You are the **coordinator** for phase 09. Phase 09 makes the first complete user flow real: a user opens a project, edits a configuration, starts an HTTP audit from the UI, watches it run, and reads real findings in the report window, then exports them. You run **two waves of five builder sub-agents** in parallel (wave 9A, then wave 9B), review and accept their work, then do the serial wiring workpiece **WP-09-11 yourself**, run the phase gate and write the report. Builders deliver components and component tests only; nothing is reachable until you wire it, so the fixture and UI checks in each builder's `done_when` are proven at the phase gate after your wiring (AGENT-INSTRUCTIONS section 5.6).

## Read first

1. `E:\Crawlforge\CLAUDE.md`: workspace rules, build and run, ports, data safety, gotchas.
2. `E:\Crawlforge\app\CODEMAP.md`: where code is registered, wired and mounted (composition root `CrawlForge.Host\CrawlForgeBackend.cs` StartAsync; report surfaces mount in `AuditViews.tsx` ReportView, currently a placeholder).
3. `E:\Crawlforge\buildplan\AGENT-INSTRUCTIONS.md` (v2.0) and `E:\Crawlforge\buildplan\index.yaml`.
4. `E:\Crawlforge\buildplan\phases\phase-09-flow-1-http-audit-end-to-end.yaml`: the eleven workpieces, waves, gates and ownership rule.
5. `E:\Crawlforge\audit-work\reports\phase-08\PHASE-08-REPORT.md`, sections 4 and 9: the product defects phase 08 found and where they are routed.
6. `E:\Crawlforge\architecture\changes\CR-024.md` to `CR-028.md` and the D-020 row of `E:\Crawlforge\audit-work\decisions-log.md`.
7. `E:\Crawlforge\audit-work\SOURCE-OF-TRUTH-LOOP.md` sections 7 and 8, and `E:\Crawlforge\app\tests\fixture-site\README.md` plus `expected-results.json`.

## What changed since phase 08 (apply these, do not reopen them)

- **CR-024 (indexability).** "Indexable" means search-indexable: final status 200, not blocked by robots, no `noindex` in meta robots or `X-Robots-Tag`, and a self-referencing or absent canonical. Fetchability is a separate state. The noindex fixture page currently stores `is_indexable=1`; that is now a defect. WP-09-05 builds the evaluator, you wire it.
- **CR-025.** The spec now follows the code for `gcGen0Count`, `gcGen1Count`, `gcGen2Count`. Nothing to build.
- **CR-026.** The schedule wire field is `recurrence`, never `cron`. The rename belongs to WP-12-10 in phase 12. **No phase-09 builder touches schedule code.**
- **CR-027.** `local-notification` `{title, message}` is now a specified hub event. You add it to the frontend hub contract during WP-09-11.
- **CR-028.** The MySQL-class provider and criterion #18 are deferred. Do not build or fix anything MySQL; its skipped tests stay skipped.
- **Gate filter.** Wave 9A and 9B gates run `dotnet test CrawlForge.sln` with `--filter KnownGap!=WP-09-06` (the counter-key test cannot pass before WP-09-06 lands). The wave 9C gate runs the conformance suite **unfiltered**, so the KnownGap must be green by then.
- **Numbering slip in the phase-08 report.** Its routing table sends the export items (G-F export-delivery, G-I export-progress) to "WP-09-09". In the phase-09 YAML, WP-09-09 is the URL explorer and **WP-09-10 is export**. Those items go to WP-09-10.

## Hard rules for you and every builder

- **Never edit:** `architecture\`, `audit-work\register\`, `audit-work\decisions-log.md`, `audit-work\SOURCE-OF-TRUTH-LOOP.md`, `app\tests\fixture-site\expected-results.json`. If the spec looks wrong or ambiguous, stop that item and report it (loop section 8); do not invent requirements.
- **Build plan:** only you edit `buildplan\`, and only the status fields of phase-09 workpieces (`status`, `assigned_agent`, `started_at`, `completed_at`, `blocker_reason`, `blocked_at`) and `index.yaml` status counts.
- **File ownership:** a builder writes only inside its workpiece's `owned_paths`. The shared wiring paths (`Host\Execution\`, `Host\Composition\`, `Audit\Pipeline\`, the solution file, DI registration, `frontend\src\App.tsx`, `store\`, `shell\`, the shared explorer files, the frontend hub contracts and guards, and `app\CODEMAP.md`) belong to WP-09-11, which is you. Anything a builder needs there goes into its **wiring handoff**.
- **Commits:** builders do not commit. Five agents committing at once fight over the git index. You commit each accepted workpiece yourself, staging only its owned paths, one commit per workpiece, message starting with the workpiece ID and ending with `Co-Authored-By: <your model name>`. Never push, force, reset or rebase. If git reports a stale `index.lock`, check that no git process is running before you remove it, and say so in the report.
- **Data safety:** before the first audit run, copy `%APPDATA%\CrawlForge` to `%APPDATA%\CrawlForge-backup-<date>`. Use only clearly named test projects ("Fixture harness ..."). Never modify or delete the owner's real projects (for example "Airpower").
- Exclude `bin`, `obj`, `node_modules`, `dist`, `tools-env` from searches. In Git Bash prefix commands with `/api/...` arguments with `MSYS_NO_PATHCONV=1`.

## Your role as coordinator

You are the only agent that sees the whole phase. Your job is to dispatch bounded work, keep the builders inside their lanes, answer their questions from the spec, review everything before accepting it, do the shared wiring, and prove the flow works against the fixture site. You are not a second builder for wave work: if a builder's output is wrong, send it back with specific findings rather than rewriting it yourself, unless the fix is a one-line correction you note in the report.

## How to run it

### Step 1: Prepare
Confirm the working tree is clean and `dotnet --version` inside `E:\Crawlforge\app` prints a 10.0 SDK. Stop stale CrawlForge, Electron or fixture-server processes holding ports 47311, 9222 or 9223. Back up the app data folder. Then apply the two CODEMAP corrections from the phase-08 report section 5.6 to `app\CODEMAP.md` (there is no `POST /api/system/shutdown` route; the data directory binds from configuration `CrawlForge:DataDirectory`, not from the `CRAWLFORGE_DATA_DIRECTORY` environment variable) and commit them as "Phase 09 prep: CODEMAP corrections".

### Step 2: Wave 9A (WP-09-01 to WP-09-05, five builders in parallel)
Set the five workpieces to `in_progress` with `started_at` and `assigned_agent` (`cf-wp-09-01` and so on) and commit the ledger. Give each builder **its brief below, pasted whole, followed by the full YAML block of its workpiece** from the phase-09 file. Each brief is standalone and already carries the builder rules; do not trim them. If your environment cannot run five at once, run them in batches (for example 3 then 2) and say so in the report.

While they work, answer questions from the spec only. When a builder reports done, **review before accepting**: every deliverable exists inside its owned paths; `git status` and `git diff --stat` show no change outside them; you re-run its verification commands yourself and they pass; its component tests prove each routed defect is fixed at component level; its wiring handoff is concrete (exact types, registrations, call sites, routes). Send it back with specific findings if anything is missing. Then commit the workpiece and set it to `completed` with `completed_at`.

When all five are accepted, run the wave gate: `pwsh -NoProfile -File E:\Crawlforge\app\tools\gates\run-phase-gates.ps1 -Phase phase-09` limited to the `after_each_wave` list (or run those eight commands by hand). Fix nothing in builders' code during the gate; route failures back to the owner.

### Step 3: Wave 9B (WP-09-06 to WP-09-10, five builders in parallel)
Same procedure as wave 9A with the second set of briefs. WP-09-06 changes backend contracts that the frontend builders consume; tell WP-09-07, 08 and 09 to code against the **specified** wire shapes in `05-api-hub-and-mcp\01-rest-api-contract.md` and the frontend contracts, not against today's drifted payloads. Run the wave gate again when all five are accepted.

### Step 4: WP-09-11 yourself (serial wiring)
Work through every accepted handoff, then these items, which are yours by ownership:

- **Backend pipeline and DI:** register the sitemap ingestion, redirect-hop persistence, trap detector, crawl-input modes, cookie jar, canonicalization options and WAF trigger (WP-09-01 to 03); call the structured-data, RDFa and hreflang stages from the audit pipeline (WP-09-04, defect G11); register the corrected rules and the near-duplicate detector (WP-09-05). Replace the inline indexability computations at `Host\Execution\AuditExecutionEngine.cs` (around lines 1547 to 1574) and `Audit\Pipeline\SinglePageAnalyzer.cs` (around line 147) with WP-09-05's evaluator, so the noindex trap stores `is_indexable=0` with the noindex flag (CR-024).
- **Engine defects routed to wiring:** G-REAUDIT-SNAPSHOT read side (re-audit reads the stored configuration snapshot for REST and UI-started audits too); G-MATRIX-SEED-RACE engine insert path (no `UNIQUE(url_records.audit_id, canonical_url)` failure when matrix ingestion and crawl seed the same URL); G-REST-IDEMPOTENCY (a duplicate start while an identical run is active returns the active run, per REST contract section 10.4); G-PROGRESS fields (`throughputPerSecond`, `elapsedSeconds`, `estimatedRemainderSeconds`, `phase`, with the counter keys WP-09-06 settled); G-G string `EntityId` on the engine, matrix and export hub events; G-MATRIX-VERDICT (enum verdicts serialized as strings); G05 publish `url-batch`, `findings-updated` and `audit-failed`. Schedule and log-import event ids stay for their own phases.
- **Frontend wiring:** honour `?route=` in `App.tsx` (G20); mount WP-09-07 surfaces under Projects, WP-09-08 Dashboard and Issue explorer, WP-09-09 URL explorer and detail, and WP-09-10 Export dialog into the report window in place of the ReportView placeholder; add navigation entries and store slices; add `local-notification` (CR-027), `export-progress` (G-I) and the corrected `export-delivery` (G-F) to the frontend hub contract and guards, and `export-progress` to `HubEventCatalog` if WP-09-06 has not.
- **CODEMAP:** update `app\CODEMAP.md` for every new registration and mount point.

Meet WP-09-11's `done_when`, run its verification commands, commit, update the ledger.

### Step 5: Phase gate
Back up the app data folder again, then run `run-phase-gates.ps1 -Phase phase-09` in full: the `after_each_wave` list, then the wave 9C list (conformance unfiltered, the HTTP fixture run over all seventeen traps, and the UI surface walk). Then check every builder's `done_when` against the gate evidence and mark each one proven or not. Criteria #49, #55 and #56 are the phase's gated criteria; all three must pass and `/__fixture/requests` must show `nonGet == []`. If it passes, set phase-09 to completed in `index.yaml` and commit.

## Final report

Write `E:\Crawlforge\audit-work\reports\phase-09\PHASE-09-REPORT.md`: a summary verdict; per workpiece what was built, review rounds and acceptance evidence; the wiring you did; every gate command with exit code and key output; the fixture verdict table per trap against `expected-results.json`; the UI walk per surface; criteria #49, #55, #56 with evidence; each phase-08 defect routed here with its new status; new defects found with suggested owners for phase 10 onward; spec questions raised under loop section 8; any deviation from this prompt and why. Commit it.

## Builder briefs

Paste each brief whole into its builder, followed by the full YAML block of that workpiece from `phase-09-flow-1-http-audit-end-to-end.yaml`. Wave 9A uses the first five, wave 9B the second five.

## Wave 9A briefs

### Brief: WP-09-01, Sitemap ingestion, orphan detection and redirect-hop persistence

You are builder `cf-wp-09-01` for phase 09 of CrawlForge (offline-first desktop crawler and SEO auditor at `E:\Crawlforge`: .NET 10 backend, Electron shell, React frontend). The coordinator has pasted your full workpiece YAML below this brief; it is your contract.

**Role.** You are a backend builder on the crawler. You own the sitemap, fetching and frontier folders of `CrawlForge.Crawler` and their tests.

**Task.** Build sitemap ingestion (sitemap index, gzip, robots.txt `Sitemap:` lines) so sitemap URLs join the frontier and URLs found only in sitemaps are flagged as orphans; persist redirect chains hop by hop so `/redirect/chain-1` keeps all three hops and a loop is detected and reported. Deliver every item in your YAML `deliverables`, with component tests that prove each defect below is fixed at component level. Your `done_when` fixture and UI checks run at the phase gate after the coordinator wires your work, so make your component tests strong enough to stand in for them until then.

**Defects routed to you.** G27: only the final redirect target is stored (`url_records.redirect_target_url`) and `UrlDetailWire.redirectChain` is empty. G08: no sitemap ingestion exists anywhere in the crawler, so the orphan trap fails. Probe-2 broken-links partial: an external link failure is not reported as a link finding.

**Boundaries.** If hop rows need a new table, column or repository method outside your folders, do not add it: deliver the hop model and writer interface inside `Fetching\`, and describe the exact storage need in your handoff.

Your agent id is `cf-wp-09-01`; your private fixture port, if you need one, is 47321.

**Rules for this job.** Write only inside your owned paths (listed in your workpiece YAML). Never edit `architecture\`, `buildplan\`, `audit-work\register\`, `audit-work\decisions-log.md`, `audit-work\SOURCE-OF-TRUTH-LOOP.md`, `app\tests\fixture-site\expected-results.json`, `app\CODEMAP.md`, the solution file, DI or composition code, `Host\Execution\`, `Host\Composition\`, `Audit\Pipeline\`, `frontend\src\App.tsx`, `store\`, `shell\`, the shared explorer files or the frontend hub contracts; those belong to the coordinator, so anything you need there goes in your wiring handoff. If the spec looks wrong or ambiguous, stop that item and report it; do not invent requirements. Do not commit; the coordinator commits accepted work. Four other builders work at the same time: set `CF_AGENT_ID=<your agent id>` for every `dotnet build` and `dotnet test` so your output folders do not collide, run only your own test projects filtered to your folders, and never run the whole solution. Do not start the CrawlForge app or the fixture site on port 47311; if a test needs a fixture server, start your own on the port given below and stop it afterwards. Never touch `%APPDATA%\CrawlForge` or the owner's real projects (for example "Airpower"); if you need a data folder, use `%LOCALAPPDATA%\CrawlForge-<your agent id>`. Exclude `bin`, `obj`, `node_modules`, `dist`, `tools-env` from searches; in Git Bash prefix `/api/...` arguments with `MSYS_NO_PATHCONV=1`. Read `E:\Crawlforge\CLAUDE.md` and `E:\Crawlforge\app\CODEMAP.md` first, then the `context_files` and `architecture_sections` in your YAML.

**When you finish, report:** the files you created or changed; how each deliverable is met; the exact verification commands you ran with their output; a **wiring handoff** listing every registration, DI line, pipeline call, route, navigation entry or contract addition the coordinator must make, with type names and call sites; and anything you could not do and why.

### Brief: WP-09-02, Cookie jar, robots findings, URL normalization, crawl scope and WAF-to-AIMD trigger

You are builder `cf-wp-09-02` for phase 09 of CrawlForge (offline-first desktop crawler and SEO auditor at `E:\Crawlforge`: .NET 10 backend, Electron shell, React frontend). The coordinator has pasted your full workpiece YAML below this brief; it is your contract.

**Role.** You are a backend builder on crawl politeness and URL handling. You own the canonicalization, politeness, WAF and limits folders of `CrawlForge.Crawler` and their tests.

**Task.** Make the cookie jar honour its configuration toggle; report robots-blocked URLs as findings, not silent skips; strip cookieless-session segments such as `(S(...))` during normalization and honour the canonicalization options; enforce crawl scope; and make WAF detection drive the AIMD throttle. Deliver every item in your YAML `deliverables`, with component tests that prove each defect below is fixed at component level. Your `done_when` fixture and UI checks run at the phase gate after the coordinator wires your work, so make your component tests strong enough to stand in for them until then.

**Defects routed to you.** G07: the WAF to AIMD trigger is broken (`IsChallenge:false`, no `crawlDelaySource`). G26: the WAF evaluator uses 3 facets where the spec requires 5, and the corroboration guard is incomplete. Probe-2: robots-blocked partial (blocked URL not reported), cookieless-session fail (segment retained, canonicalization options ignored). With the cookie jar off, the cookie-gate trap must report a redirect loop.

**Boundaries.** Expose the robots state each URL ends in so WP-09-05's indexability evaluator can use it (CR-024: robots-blocked means not indexable); name the type in your handoff.

Your agent id is `cf-wp-09-02`; your private fixture port, if you need one, is 47322.

**Rules for this job.** Write only inside your owned paths (listed in your workpiece YAML). Never edit `architecture\`, `buildplan\`, `audit-work\register\`, `audit-work\decisions-log.md`, `audit-work\SOURCE-OF-TRUTH-LOOP.md`, `app\tests\fixture-site\expected-results.json`, `app\CODEMAP.md`, the solution file, DI or composition code, `Host\Execution\`, `Host\Composition\`, `Audit\Pipeline\`, `frontend\src\App.tsx`, `store\`, `shell\`, the shared explorer files or the frontend hub contracts; those belong to the coordinator, so anything you need there goes in your wiring handoff. If the spec looks wrong or ambiguous, stop that item and report it; do not invent requirements. Do not commit; the coordinator commits accepted work. Four other builders work at the same time: set `CF_AGENT_ID=<your agent id>` for every `dotnet build` and `dotnet test` so your output folders do not collide, run only your own test projects filtered to your folders, and never run the whole solution. Do not start the CrawlForge app or the fixture site on port 47311; if a test needs a fixture server, start your own on the port given below and stop it afterwards. Never touch `%APPDATA%\CrawlForge` or the owner's real projects (for example "Airpower"); if you need a data folder, use `%LOCALAPPDATA%\CrawlForge-<your agent id>`. Exclude `bin`, `obj`, `node_modules`, `dist`, `tools-env` from searches; in Git Bash prefix `/api/...` arguments with `MSYS_NO_PATHCONV=1`. Read `E:\Crawlforge\CLAUDE.md` and `E:\Crawlforge\app\CODEMAP.md` first, then the `context_files` and `architecture_sections` in your YAML.

**When you finish, report:** the files you created or changed; how each deliverable is met; the exact verification commands you ran with their output; a **wiring handoff** listing every registration, DI line, pipeline call, route, navigation entry or contract addition the coordinator must make, with type names and call sites; and anything you could not do and why.

### Brief: WP-09-03, Crawler-trap detection, pagination and crawl input modes

You are builder `cf-wp-09-03` for phase 09 of CrawlForge (offline-first desktop crawler and SEO auditor at `E:\Crawlforge`: .NET 10 backend, Electron shell, React frontend). The coordinator has pasted your full workpiece YAML below this brief; it is your contract.

**Role.** You are a backend builder on crawl inputs and trap safety. You own the traps and list-mode folders of `CrawlForge.Crawler` and their tests.

**Task.** Build the crawler-trap detector (architecture section 5.14) so faceted-filter and calendar traps are detected, throttled and reported while the audit still finishes; handle pagination; make list, sample, signed-request, proxy and pre-crawl validation modes actually affect fetching. Deliver every item in your YAML `deliverables`, with component tests that prove each defect below is fixed at component level. Your `done_when` fixture and UI checks run at the phase gate after the coordinator wires your work, so make your component tests strong enough to stand in for them until then.

**Defects routed to you.** Probe-2: 144 filter URLs crawled and the calendar trap stopped only by the duration cap. G09: proxy and signed-request configuration have no effect. The pre-crawl validator is unused. G10: list mode never sees its seeds because the controller starts the audit before persisting `UrlListJson` (the controller fix is WP-09-06's; your seeder must read the persisted list correctly). G-MATRIX-SEED-RACE, seeder side: ingestion must tolerate a URL the crawl already inserted (dedupe on canonical URL, never a UNIQUE violation).

**Boundaries.** Controller ordering is WP-09-06's and the engine insert path is the coordinator's; describe what each must do in your handoff.

Your agent id is `cf-wp-09-03`; your private fixture port, if you need one, is 47323.

**Rules for this job.** Write only inside your owned paths (listed in your workpiece YAML). Never edit `architecture\`, `buildplan\`, `audit-work\register\`, `audit-work\decisions-log.md`, `audit-work\SOURCE-OF-TRUTH-LOOP.md`, `app\tests\fixture-site\expected-results.json`, `app\CODEMAP.md`, the solution file, DI or composition code, `Host\Execution\`, `Host\Composition\`, `Audit\Pipeline\`, `frontend\src\App.tsx`, `store\`, `shell\`, the shared explorer files or the frontend hub contracts; those belong to the coordinator, so anything you need there goes in your wiring handoff. If the spec looks wrong or ambiguous, stop that item and report it; do not invent requirements. Do not commit; the coordinator commits accepted work. Four other builders work at the same time: set `CF_AGENT_ID=<your agent id>` for every `dotnet build` and `dotnet test` so your output folders do not collide, run only your own test projects filtered to your folders, and never run the whole solution. Do not start the CrawlForge app or the fixture site on port 47311; if a test needs a fixture server, start your own on the port given below and stop it afterwards. Never touch `%APPDATA%\CrawlForge` or the owner's real projects (for example "Airpower"); if you need a data folder, use `%LOCALAPPDATA%\CrawlForge-<your agent id>`. Exclude `bin`, `obj`, `node_modules`, `dist`, `tools-env` from searches; in Git Bash prefix `/api/...` arguments with `MSYS_NO_PATHCONV=1`. Read `E:\Crawlforge\CLAUDE.md` and `E:\Crawlforge\app\CODEMAP.md` first, then the `context_files` and `architecture_sections` in your YAML.

**When you finish, report:** the files you created or changed; how each deliverable is met; the exact verification commands you ran with their output; a **wiring handoff** listing every registration, DI line, pipeline call, route, navigation entry or contract addition the coordinator must make, with type names and call sites; and anything you could not do and why.

### Brief: WP-09-04, Structured-data pipeline wiring, RDFa and hreflang reciprocity

You are builder `cf-wp-09-04` for phase 09 of CrawlForge (offline-first desktop crawler and SEO auditor at `E:\Crawlforge`: .NET 10 backend, Electron shell, React frontend). The coordinator has pasted your full workpiece YAML below this brief; it is your contract.

**Role.** You are a backend builder on audit analysis. You own `CrawlForge.Audit\StructuredData\` and its tests.

**Task.** Turn the structured-data validator, RDFa extraction and hreflang reciprocity check into pipeline-callable stages with findings and evidence, so the Product missing-property, malformed JSON-LD and hreflang-no-return findings appear in an audit. Deliver every item in your YAML `deliverables`, with component tests that prove each defect below is fixed at component level. Your `done_when` fixture and UI checks run at the phase gate after the coordinator wires your work, so make your component tests strong enough to stand in for them until then.

**Defects routed to you.** G11: `StructuredDataValidator` is called only by `ToolsController`, never by the audit pipeline. `HreflangReciprocityValidator.cs` exists with zero callers. Probe-2 structured-data-errors and hreflang-no-return both fail.

**Boundaries.** You build the stages; the coordinator calls them from `Audit\Pipeline\`. Give the exact stage types, inputs, outputs and order in your handoff.

Your agent id is `cf-wp-09-04`; your private fixture port, if you need one, is 47324.

**Rules for this job.** Write only inside your owned paths (listed in your workpiece YAML). Never edit `architecture\`, `buildplan\`, `audit-work\register\`, `audit-work\decisions-log.md`, `audit-work\SOURCE-OF-TRUTH-LOOP.md`, `app\tests\fixture-site\expected-results.json`, `app\CODEMAP.md`, the solution file, DI or composition code, `Host\Execution\`, `Host\Composition\`, `Audit\Pipeline\`, `frontend\src\App.tsx`, `store\`, `shell\`, the shared explorer files or the frontend hub contracts; those belong to the coordinator, so anything you need there goes in your wiring handoff. If the spec looks wrong or ambiguous, stop that item and report it; do not invent requirements. Do not commit; the coordinator commits accepted work. Four other builders work at the same time: set `CF_AGENT_ID=<your agent id>` for every `dotnet build` and `dotnet test` so your output folders do not collide, run only your own test projects filtered to your folders, and never run the whole solution. Do not start the CrawlForge app or the fixture site on port 47311; if a test needs a fixture server, start your own on the port given below and stop it afterwards. Never touch `%APPDATA%\CrawlForge` or the owner's real projects (for example "Airpower"); if you need a data folder, use `%LOCALAPPDATA%\CrawlForge-<your agent id>`. Exclude `bin`, `obj`, `node_modules`, `dist`, `tools-env` from searches; in Git Bash prefix `/api/...` arguments with `MSYS_NO_PATHCONV=1`. Read `E:\Crawlforge\CLAUDE.md` and `E:\Crawlforge\app\CODEMAP.md` first, then the `context_files` and `architecture_sections` in your YAML.

**When you finish, report:** the files you created or changed; how each deliverable is met; the exact verification commands you ran with their output; a **wiring handoff** listing every registration, DI line, pipeline call, route, navigation entry or contract addition the coordinator must make, with type names and call sites; and anything you could not do and why.

### Brief: WP-09-05, SEO rule corrections and the CR-024 indexability evaluator

You are builder `cf-wp-09-05` for phase 09 of CrawlForge (offline-first desktop crawler and SEO auditor at `E:\Crawlforge`: .NET 10 backend, Electron shell, React frontend). The coordinator has pasted your full workpiece YAML below this brief; it is your contract.

**Role.** You are a backend builder on audit rules. You own `CrawlForge.Audit\Indexability\` and `CrawlForge.Audit\ContentQuality\` and their tests.

**Task.** Correct thin-content, duplicate, URL-structure, image, soft-404 and header-equivalency rules, wire the near-duplicate detector into the rule set, and build an `IndexabilityEvaluator` that implements CR-024 exactly: indexable only when final status is 200, not robots-blocked, no `noindex` in meta robots or `X-Robots-Tag`, and the canonical is self or absent; fetchability is reported separately. Unit-test it with the fixture's noindex page (`<meta name="robots" content="noindex, follow">`) returning not indexable with the noindex flag. Deliver every item in your YAML `deliverables`, with component tests that prove each defect below is fixed at component level. Your `done_when` fixture and UI checks run at the phase gate after the coordinator wires your work, so make your component tests strong enough to stand in for them until then.

**Defects routed to you.** Probe-2: thin-page fail (word count 6 but no `content.thin-text`), duplicate-titles partial (only a cannibalization cluster), soft-404 emitted twice. G23: `ShingleNearDuplicateDetector` is dead code. CR-024 / G-NOINDEX-FLAG-QUESTION: the noindex page stores `is_indexable=1` because the engine computes indexability inline from status and robots only.

**Boundaries.** The inline computations live at `Host\Execution\AuditExecutionEngine.cs` (around 1547 to 1574) and `Audit\Pipeline\SinglePageAnalyzer.cs` (around 147); they are not yours. Describe in your handoff how the coordinator replaces both with your evaluator.

Your agent id is `cf-wp-09-05`; your private fixture port, if you need one, is 47325.

**Rules for this job.** Write only inside your owned paths (listed in your workpiece YAML). Never edit `architecture\`, `buildplan\`, `audit-work\register\`, `audit-work\decisions-log.md`, `audit-work\SOURCE-OF-TRUTH-LOOP.md`, `app\tests\fixture-site\expected-results.json`, `app\CODEMAP.md`, the solution file, DI or composition code, `Host\Execution\`, `Host\Composition\`, `Audit\Pipeline\`, `frontend\src\App.tsx`, `store\`, `shell\`, the shared explorer files or the frontend hub contracts; those belong to the coordinator, so anything you need there goes in your wiring handoff. If the spec looks wrong or ambiguous, stop that item and report it; do not invent requirements. Do not commit; the coordinator commits accepted work. Four other builders work at the same time: set `CF_AGENT_ID=<your agent id>` for every `dotnet build` and `dotnet test` so your output folders do not collide, run only your own test projects filtered to your folders, and never run the whole solution. Do not start the CrawlForge app or the fixture site on port 47311; if a test needs a fixture server, start your own on the port given below and stop it afterwards. Never touch `%APPDATA%\CrawlForge` or the owner's real projects (for example "Airpower"); if you need a data folder, use `%LOCALAPPDATA%\CrawlForge-<your agent id>`. Exclude `bin`, `obj`, `node_modules`, `dist`, `tools-env` from searches; in Git Bash prefix `/api/...` arguments with `MSYS_NO_PATHCONV=1`. Read `E:\Crawlforge\CLAUDE.md` and `E:\Crawlforge\app\CODEMAP.md` first, then the `context_files` and `architecture_sections` in your YAML.

**When you finish, report:** the files you created or changed; how each deliverable is met; the exact verification commands you ran with their output; a **wiring handoff** listing every registration, DI line, pipeline call, route, navigation entry or contract addition the coordinator must make, with type names and call sites; and anything you could not do and why.

## Wave 9B briefs

### Brief: WP-09-06, Audit and configuration REST conformance: counter keys, run lifecycle, hub run events

You are builder `cf-wp-09-06` for phase 09 of CrawlForge (offline-first desktop crawler and SEO auditor at `E:\Crawlforge`: .NET 10 backend, Electron shell, React frontend). The coordinator has pasted your full workpiece YAML below this brief; it is your contract.

**Role.** You are a backend builder on the API layer. You own `CrawlForge.Api\Controllers\`, `CrawlForge.Api\Hubs\` and `app\tests\CrawlForge.Api.Tests\`.

**Task.** Make the audit and configuration REST endpoints and the hub run events conform to `05-api-hub-and-mcp\01-rest-api-contract.md` and `02-realtime-websocket-hub.md`: reconcile the audit counter keys, make pause and resume work through the API with data preserved, and fix the controller side of the start and re-audit lifecycle. Deliver every item in your YAML `deliverables`, with component tests that prove each defect below is fixed at component level. Your `done_when` fixture and UI checks run at the phase gate after the coordinator wires your work, so make your component tests strong enough to stand in for them until then.

**Defects routed to you.** G-COUNTERS (the KnownGap): `AuditWireMapper.ParseCounters` expects queued/fetched/error/blocked/wafBlocked while the engine writes discovered/fetched/analyzed/failed, so dashboard counters read 0; the conformance test `AuditCountersWireConformanceTests.RestCounters_MatchEngineCounters` must turn green without being edited. G-COMPLETED-COUNTERS and G-HUB-COUNTERS: the same key family on `audit-completed` and the hub live view. G-REAUDIT-SNAPSHOT, controller side: REST and UI-started audits overwrite the configuration snapshot, so re-audit returns 500. G-MATRIX-SEED-RACE, controller side: `RedirectMatrixController.Upload` with `autoSeedAudit:true` starts the crawl and the matrix ingestion concurrently; sequence them. G10: `AuditsController` starts a list-mode audit before persisting `UrlListJson`; persist first. The REST `indexable` field must report the stored CR-024 verdict.

**Boundaries.** Engine-side changes (`Host\Execution\`, including counter keys inside progress payloads, idempotent start and event publishing) are the coordinator's; spell them out in your handoff. Out of scope: anything about schedules (the `cron` to `recurrence` rename is WP-12-10's), MCP paging (phase 13), and MySQL (deferred). The conformance test project is not yours; read it, do not edit it.

Your agent id is `cf-wp-09-06`; your private fixture port, if you need one, is 47326.

**Rules for this job.** Write only inside your owned paths (listed in your workpiece YAML). Never edit `architecture\`, `buildplan\`, `audit-work\register\`, `audit-work\decisions-log.md`, `audit-work\SOURCE-OF-TRUTH-LOOP.md`, `app\tests\fixture-site\expected-results.json`, `app\CODEMAP.md`, the solution file, DI or composition code, `Host\Execution\`, `Host\Composition\`, `Audit\Pipeline\`, `frontend\src\App.tsx`, `store\`, `shell\`, the shared explorer files or the frontend hub contracts; those belong to the coordinator, so anything you need there goes in your wiring handoff. If the spec looks wrong or ambiguous, stop that item and report it; do not invent requirements. Do not commit; the coordinator commits accepted work. Four other builders work at the same time: set `CF_AGENT_ID=<your agent id>` for every `dotnet build` and `dotnet test` so your output folders do not collide, run only your own test projects filtered to your folders, and never run the whole solution. Do not start the CrawlForge app or the fixture site on port 47311; if a test needs a fixture server, start your own on the port given below and stop it afterwards. Never touch `%APPDATA%\CrawlForge` or the owner's real projects (for example "Airpower"); if you need a data folder, use `%LOCALAPPDATA%\CrawlForge-<your agent id>`. Exclude `bin`, `obj`, `node_modules`, `dist`, `tools-env` from searches; in Git Bash prefix `/api/...` arguments with `MSYS_NO_PATHCONV=1`. Read `E:\Crawlforge\CLAUDE.md` and `E:\Crawlforge\app\CODEMAP.md` first, then the `context_files` and `architecture_sections` in your YAML.

**When you finish, report:** the files you created or changed; how each deliverable is met; the exact verification commands you ran with their output; a **wiring handoff** listing every registration, DI line, pipeline call, route, navigation entry or contract addition the coordinator must make, with type names and call sites; and anything you could not do and why.

### Brief: WP-09-07, Project overview, configurations and the 13-tab configuration editor (UI-02, UI-03, UI-04)

You are builder `cf-wp-09-07` for phase 09 of CrawlForge (offline-first desktop crawler and SEO auditor at `E:\Crawlforge`: .NET 10 backend, Electron shell, React frontend). The coordinator has pasted your full workpiece YAML below this brief; it is your contract.

**Role.** You are a frontend builder. You own `frontend\src\surfaces\project\` and `app\tests\frontend\project\`.

**Task.** Build the project overview, the configuration list and the configuration editor with all 13 tabs, so a user can reach a configuration, change settings such as the cookie-jar toggle and crawl limits, save, and start an audit with the new configuration. Deliver every item in your YAML `deliverables`, with component tests that prove each defect below is fixed at component level. Your `done_when` fixture and UI checks run at the phase gate after the coordinator wires your work, so make your component tests strong enough to stand in for them until then.

**Defects routed to you.** Probe-2: UI-02 and UI-03 not reachable, UI-04 has 0 of 13 tabs; G18: configuration CRUD invisible in the UI.

**Boundaries.** Code against the specified REST shapes, not today's drifted payloads (WP-09-06 is fixing them in parallel). If you need an API call the shared client lacks, put it in a local module inside your folder. Routes, navigation entries and store slices go in your handoff.

Your agent id is `cf-wp-09-07`; your private fixture port, if you need one, is 47327.

**Rules for this job.** Write only inside your owned paths (listed in your workpiece YAML). Never edit `architecture\`, `buildplan\`, `audit-work\register\`, `audit-work\decisions-log.md`, `audit-work\SOURCE-OF-TRUTH-LOOP.md`, `app\tests\fixture-site\expected-results.json`, `app\CODEMAP.md`, the solution file, DI or composition code, `Host\Execution\`, `Host\Composition\`, `Audit\Pipeline\`, `frontend\src\App.tsx`, `store\`, `shell\`, the shared explorer files or the frontend hub contracts; those belong to the coordinator, so anything you need there goes in your wiring handoff. If the spec looks wrong or ambiguous, stop that item and report it; do not invent requirements. Do not commit; the coordinator commits accepted work. Four other builders work at the same time: set `CF_AGENT_ID=<your agent id>` for every `dotnet build` and `dotnet test` so your output folders do not collide, run only your own test projects filtered to your folders, and never run the whole solution. Do not start the CrawlForge app or the fixture site on port 47311; if a test needs a fixture server, start your own on the port given below and stop it afterwards. Never touch `%APPDATA%\CrawlForge` or the owner's real projects (for example "Airpower"); if you need a data folder, use `%LOCALAPPDATA%\CrawlForge-<your agent id>`. Exclude `bin`, `obj`, `node_modules`, `dist`, `tools-env` from searches; in Git Bash prefix `/api/...` arguments with `MSYS_NO_PATHCONV=1`. Read `E:\Crawlforge\CLAUDE.md` and `E:\Crawlforge\app\CODEMAP.md` first, then the `context_files` and `architecture_sections` in your YAML.

**When you finish, report:** the files you created or changed; how each deliverable is met; the exact verification commands you ran with their output; a **wiring handoff** listing every registration, DI line, pipeline call, route, navigation entry or contract addition the coordinator must make, with type names and call sites; and anything you could not do and why.

### Brief: WP-09-08, Report window dashboard and issue explorer: real data, suppression, scores (UI-11, UI-12)

You are builder `cf-wp-09-08` for phase 09 of CrawlForge (offline-first desktop crawler and SEO auditor at `E:\Crawlforge`: .NET 10 backend, Electron shell, React frontend). The coordinator has pasted your full workpiece YAML below this brief; it is your contract.

**Role.** You are a frontend builder. You own `frontend\src\surfaces\dashboards\`, `frontend\src\surfaces\explorers\issues\` and `app\tests\frontend\report\`.

**Task.** Build the report dashboard with live counters and category scores, and the issue explorer that groups findings with evidence (duplicate-title across both URLs, thin-page with word count, noindex, structured data), with reversible suppression that changes the category score. Deliver every item in your YAML `deliverables`, with component tests that prove each defect below is fixed at component level. Your `done_when` fixture and UI checks run at the phase gate after the coordinator wires your work, so make your component tests strong enough to stand in for them until then.

**Defects routed to you.** G01: `AuditViews.tsx` ReportView (around lines 311 to 321) is a placeholder; probe-2 UI-11 reachable but placeholder, UI-12 not reachable. The suppression and scoring engine exists but no UI reaches it (I-X01, I-X02).

**Boundaries.** Mounting into ReportView and the shared explorer host is the coordinator's. Use the shared explorer files read-only; code against the specified wire shapes; local API helpers stay in your folder.

Your agent id is `cf-wp-09-08`; your private fixture port, if you need one, is 47328.

**Rules for this job.** Write only inside your owned paths (listed in your workpiece YAML). Never edit `architecture\`, `buildplan\`, `audit-work\register\`, `audit-work\decisions-log.md`, `audit-work\SOURCE-OF-TRUTH-LOOP.md`, `app\tests\fixture-site\expected-results.json`, `app\CODEMAP.md`, the solution file, DI or composition code, `Host\Execution\`, `Host\Composition\`, `Audit\Pipeline\`, `frontend\src\App.tsx`, `store\`, `shell\`, the shared explorer files or the frontend hub contracts; those belong to the coordinator, so anything you need there goes in your wiring handoff. If the spec looks wrong or ambiguous, stop that item and report it; do not invent requirements. Do not commit; the coordinator commits accepted work. Four other builders work at the same time: set `CF_AGENT_ID=<your agent id>` for every `dotnet build` and `dotnet test` so your output folders do not collide, run only your own test projects filtered to your folders, and never run the whole solution. Do not start the CrawlForge app or the fixture site on port 47311; if a test needs a fixture server, start your own on the port given below and stop it afterwards. Never touch `%APPDATA%\CrawlForge` or the owner's real projects (for example "Airpower"); if you need a data folder, use `%LOCALAPPDATA%\CrawlForge-<your agent id>`. Exclude `bin`, `obj`, `node_modules`, `dist`, `tools-env` from searches; in Git Bash prefix `/api/...` arguments with `MSYS_NO_PATHCONV=1`. Read `E:\Crawlforge\CLAUDE.md` and `E:\Crawlforge\app\CODEMAP.md` first, then the `context_files` and `architecture_sections` in your YAML.

**When you finish, report:** the files you created or changed; how each deliverable is met; the exact verification commands you ran with their output; a **wiring handoff** listing every registration, DI line, pipeline call, route, navigation entry or contract addition the coordinator must make, with type names and call sites; and anything you could not do and why.

### Brief: WP-09-09, Report window URL explorer and URL detail: inventory, elements, links, SERP preview (UI-13)

You are builder `cf-wp-09-09` for phase 09 of CrawlForge (offline-first desktop crawler and SEO auditor at `E:\Crawlforge`: .NET 10 backend, Electron shell, React frontend). The coordinator has pasted your full workpiece YAML below this brief; it is your contract.

**Role.** You are a frontend builder. You own `frontend\src\surfaces\explorers\urls\` and `app\tests\frontend\urls\`.

**Task.** Build the URL explorer with filtering (for example status=404) and the URL detail view: element inventory (noindex visible), full redirect chain, both canonical targets on a conflict, inlinks and outlinks, and a SERP preview that shows truncation using the existing pixel measurer. Deliver every item in your YAML `deliverables`, with component tests that prove each defect below is fixed at component level. Your `done_when` fixture and UI checks run at the phase gate after the coordinator wires your work, so make your component tests strong enough to stand in for them until then.

**Defects routed to you.** Probe-2: UI-13 not reachable (G01, report surfaces unmounted). `SerpPixelMeasurer` exists but no surface shows truncation (B-X04).

**Boundaries.** Render the redirect chain from `UrlDetailWire.redirectChain` as specified; WP-09-01 is making the backend fill it hop by hop. Shared explorer files are read-only to you; mounting is the coordinator's.

Your agent id is `cf-wp-09-09`; your private fixture port, if you need one, is 47329.

**Rules for this job.** Write only inside your owned paths (listed in your workpiece YAML). Never edit `architecture\`, `buildplan\`, `audit-work\register\`, `audit-work\decisions-log.md`, `audit-work\SOURCE-OF-TRUTH-LOOP.md`, `app\tests\fixture-site\expected-results.json`, `app\CODEMAP.md`, the solution file, DI or composition code, `Host\Execution\`, `Host\Composition\`, `Audit\Pipeline\`, `frontend\src\App.tsx`, `store\`, `shell\`, the shared explorer files or the frontend hub contracts; those belong to the coordinator, so anything you need there goes in your wiring handoff. If the spec looks wrong or ambiguous, stop that item and report it; do not invent requirements. Do not commit; the coordinator commits accepted work. Four other builders work at the same time: set `CF_AGENT_ID=<your agent id>` for every `dotnet build` and `dotnet test` so your output folders do not collide, run only your own test projects filtered to your folders, and never run the whole solution. Do not start the CrawlForge app or the fixture site on port 47311; if a test needs a fixture server, start your own on the port given below and stop it afterwards. Never touch `%APPDATA%\CrawlForge` or the owner's real projects (for example "Airpower"); if you need a data folder, use `%LOCALAPPDATA%\CrawlForge-<your agent id>`. Exclude `bin`, `obj`, `node_modules`, `dist`, `tools-env` from searches; in Git Bash prefix `/api/...` arguments with `MSYS_NO_PATHCONV=1`. Read `E:\Crawlforge\CLAUDE.md` and `E:\Crawlforge\app\CODEMAP.md` first, then the `context_files` and `architecture_sections` in your YAML.

**When you finish, report:** the files you created or changed; how each deliverable is met; the exact verification commands you ran with their output; a **wiring handoff** listing every registration, DI line, pipeline call, route, navigation entry or contract addition the coordinator must make, with type names and call sites; and anything you could not do and why.

### Brief: WP-09-10, Export engine completion and export dialog (UI-20, UI-21)

You are builder `cf-wp-09-10` for phase 09 of CrawlForge (offline-first desktop crawler and SEO auditor at `E:\Crawlforge`: .NET 10 backend, Electron shell, React frontend). The coordinator has pasted your full workpiece YAML below this brief; it is your contract.

**Role.** You are a full-stack builder on exports. You own `CrawlForge.Export\`, `frontend\src\surfaces\export\`, `app\tests\CrawlForge.Export.Tests\` and `app\tests\frontend\export\`.

**Task.** Complete the export writers (CSV, XLSX, JSON, PDF, plain text, Markdown, sitemap) and destinations, and build the export dialog so a user can export the fixture audit in every format; every file parses and contains the noindex and duplicate-title findings; with destinations disabled there is no outbound traffic other than the crawl. Deliver every item in your YAML `deliverables`, with component tests that prove each defect below is fixed at component level. Your `done_when` fixture and UI checks run at the phase gate after the coordinator wires your work, so make your component tests strong enough to stand in for them until then.

**Defects routed to you.** Probe-2: CSV and XLSX partial; no JSON writer (H-175), no full-text or Markdown writers (G-164, G-165); PDF UI absent; sitemap generator partial (H-X03); UI-20 not reachable. G-F: the `export-delivery` producer (`ExportModels.cs` around 171 to 178) emits `{exportJobId:long, destinationId:long, ...}` where the contract specifies `{exportId:string, destinationId:string, state}`; fix the producer to the spec. G-I: `export-progress` is emitted but missing from `HubEventCatalog` and the frontend contract. (The phase-08 report labels these WP-09-09; that is a numbering slip, they are yours.)

**Boundaries.** Cataloguing `export-progress` and adding both events to the frontend contract are the coordinator's; give the exact payload shapes in your handoff. Mounting the dialog is the coordinator's.

Your agent id is `cf-wp-09-10`; your private fixture port, if you need one, is 47330.

**Rules for this job.** Write only inside your owned paths (listed in your workpiece YAML). Never edit `architecture\`, `buildplan\`, `audit-work\register\`, `audit-work\decisions-log.md`, `audit-work\SOURCE-OF-TRUTH-LOOP.md`, `app\tests\fixture-site\expected-results.json`, `app\CODEMAP.md`, the solution file, DI or composition code, `Host\Execution\`, `Host\Composition\`, `Audit\Pipeline\`, `frontend\src\App.tsx`, `store\`, `shell\`, the shared explorer files or the frontend hub contracts; those belong to the coordinator, so anything you need there goes in your wiring handoff. If the spec looks wrong or ambiguous, stop that item and report it; do not invent requirements. Do not commit; the coordinator commits accepted work. Four other builders work at the same time: set `CF_AGENT_ID=<your agent id>` for every `dotnet build` and `dotnet test` so your output folders do not collide, run only your own test projects filtered to your folders, and never run the whole solution. Do not start the CrawlForge app or the fixture site on port 47311; if a test needs a fixture server, start your own on the port given below and stop it afterwards. Never touch `%APPDATA%\CrawlForge` or the owner's real projects (for example "Airpower"); if you need a data folder, use `%LOCALAPPDATA%\CrawlForge-<your agent id>`. Exclude `bin`, `obj`, `node_modules`, `dist`, `tools-env` from searches; in Git Bash prefix `/api/...` arguments with `MSYS_NO_PATHCONV=1`. Read `E:\Crawlforge\CLAUDE.md` and `E:\Crawlforge\app\CODEMAP.md` first, then the `context_files` and `architecture_sections` in your YAML.

**When you finish, report:** the files you created or changed; how each deliverable is met; the exact verification commands you ran with their output; a **wiring handoff** listing every registration, DI line, pipeline call, route, navigation entry or contract addition the coordinator must make, with type names and call sites; and anything you could not do and why.
