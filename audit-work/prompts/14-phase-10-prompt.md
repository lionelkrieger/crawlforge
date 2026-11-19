# Task: Coordinate and build phase 10 of the CrawlForge build plan (Flow 2, rendering and capture modes)

You are the **coordinator** for phase 10. Phase 10 makes JavaScript rendering real: direct CDP control of the pinned bundled browser, the four capture modes (non-JS, JS, dual, adaptive) selectable in the configuration editor, rendered documents in full audits, and raw-versus-rendered comparison in the UI. It also carries the phase-09 leftovers the owner decided on 2026-11-19 (D-021). You first close one phase-09 proof gap yourself, then run a short contract amendment, then three builders in parallel, then one more, then the serial wiring workpiece WP-10-04 yourself, the phase gate and the report.

The run order is:

| Step | Wave | Workpieces | Who |
|---|---|---|---|
| 0 | none | Prove criterion #56 through the UI | you |
| 1 | wave-10A0 | WP-10-A1 contract amendment, runs alone | one builder |
| 2 | wave-10A | WP-10-01, WP-10-02, WP-10-05 in parallel | three builders |
| 3 | wave-10B | WP-10-03 | one builder |
| 4 | wave-10C | WP-10-04 serial wiring | you |
| 5 | gate | phase-10 gate plus the phase-09 regression run | you |

Builders deliver components and component tests only; their fixture and UI checks are proven at the phase gate after your wiring (AGENT-INSTRUCTIONS section 5.6).

## Read first

1. `E:\Crawlforge\CLAUDE.md` and `E:\Crawlforge\app\CODEMAP.md`.
2. `E:\Crawlforge\buildplan\AGENT-INSTRUCTIONS.md` (v2.0), especially section 11 (contract amendments) and section 12 (proof rules), and `E:\Crawlforge\buildplan\index.yaml`.
3. `E:\Crawlforge\buildplan\phases\phase-10-flow-2-rendering-and-capture-modes.yaml`: six workpieces, four waves, gates.
4. `E:\Crawlforge\audit-work\reports\phase-09\PHASE-09-REPORT.md` and `WIRING-NOTES.md` (the notation-gap register and the phase-09 wiring you are building on).
5. `E:\Crawlforge\architecture\changes\CR-029.md`, `CR-030.md`, `CR-031.md` and the D-021 row of `E:\Crawlforge\audit-work\decisions-log.md`; decisions D-002 (direct CDP, no PuppeteerSharp) and D-003 (pinned bundled browser, never downloaded at run time).
6. `E:\Crawlforge\architecture\03-crawl-engine\03-rendering-and-proxy-routing.md`, `E:\Crawlforge\audit-work\SOURCE-OF-TRUTH-LOOP.md` sections 7 and 8, and `E:\Crawlforge\app\tests\fixture-site\README.md` plus `expected-results.json`.

## What the owner decided (apply, do not reopen)

- **D-021 items 1 and 2:** the code follows the spec. Images, Security & protocol and Internationalization become real audit categories with scores, and the WAF challenge classes become the spec's five (adding managed-challenge and rate-limit-soft-block).
- **CR-029:** inappropriate redirects are four cases, each a toggle on the UI-04 analysis tab, all on by default: cross-host, temporary (302, 303, 307), non-indexable final target, and HTTPS-to-HTTP downgrade.
- **CR-030:** deprecated structured-data terms are those superseded in the bundled vocabulary plus a bundled, versioned list of retired or restricted rich-result types, never fetched at run time. Microdata terms resolve like RDFa.
- **CR-031:** the oversized-image check uses transferred bytes where the capture mode records them, and otherwise reports "not measured", never "pass".
- **Contract changes go through WP-10-A1 only** (section 11): it runs alone, and you re-lock the contracts before wave 10A starts.

## Hard rules for you and every builder

- **Never edit:** `architecture\`, `audit-work\register\`, `audit-work\decisions-log.md`, `audit-work\SOURCE-OF-TRUTH-LOOP.md`, `app\tests\fixture-site\expected-results.json`. If the spec looks wrong or ambiguous, stop that item and report it (loop section 8).
- **Build plan:** only you edit `buildplan\`, and only phase-10 status fields and `index.yaml` status counts, plus criterion #56's status if step 0 says so.
- **File ownership:** a builder writes only inside its workpiece's `owned_paths`. The shared wiring paths (`Host\Execution\`, `Host\Composition\`, `Audit\Pipeline\`, the solution file, DI registration, `frontend\src\App.tsx`, `store\`, `shell\`, `app\CODEMAP.md`) belong to WP-10-04, which is you. After WP-10-A1 is accepted, `CrawlForge.Contracts\` and `frontend\src\contracts\` are locked for everyone.
- **Commits:** builders do not commit. You commit each accepted workpiece, staging only its owned paths, with a message starting with the workpiece ID and ending with `Co-Authored-By: <your model name>`. Never push, force, reset or rebase. Before removing a stale `index.lock`, check that no git process is running, and note it in the report.
- **Data safety:** before the first audit run, copy `%APPDATA%\CrawlForge` to `%APPDATA%\CrawlForge-backup-<date>`. Use only clearly named test projects ("Fixture harness ..."). Never modify or delete the owner's real projects (for example "Airpower").
- **Offline rule:** rendering must never download a browser or a driver, and nothing may listen on a non-loopback interface.
- Exclude `bin`, `obj`, `node_modules`, `dist`, `tools-env` from searches. In Git Bash, prefix commands that carry `/api/...` arguments with `MSYS_NO_PATHCONV=1`.

## Your role as coordinator

You are the only agent that sees the whole phase. You dispatch bounded work, keep builders in their lanes, answer their questions from the spec, review everything before accepting it, do the shared wiring, and prove the flows against the fixture site. If a builder's output is wrong, send it back with specific findings; do not rewrite it yourself unless the fix is a one-line correction you record in the report.

## How to run it

### Step 0: Close the phase-09 proof gap on criterion #56 (yourself)
Criterion #56 says a configuration "created and edited in the UI" changes audit behaviour. In phase 09 the configuration was created in the UI but its limits were set with a REST PUT, and the cookie jar was never toggled in the editor. Prove it properly through the UI before anything else.

Prepare first: clean tree, a 10.0 SDK in `E:\Crawlforge\app`, no stale processes on 47311, 9222 or 9223, and an app-data backup. Then extend `app\tests\fixture-acceptance\run_fixture_acceptance.py` (test infrastructure, not owned by any phase-10 workpiece) with a UI-edit walk over CDP that does all of the following through UI-04 controls only, with no REST writes:

1. Create a configuration, open UI-04, turn the cookie jar **off**, set max URLs to 50, save, and start an audit from the UI. Expect cookie-gate to end in a redirect loop and the audit to stop at 50 URLs or fewer.
2. Turn the cookie jar back **on**, save, and re-run. Expect cookie-gate to pass.

Record the result in `audit-work\reports\phase-09\PHASE-09-ADDENDUM-56.md` with screenshots and audit ids, and commit it with the harness change.

- **If it passes,** #56 stays completed.
- **If it fails,** set #56 back to pending in `index.yaml` with the evidence, and route the editor fix to WP-10-05, which owns `surfaces\project\` in this phase.

While you are here, tighten the two E2E helpers that commit `2456592` relaxed (`ListModeE2ETests.cs` and `HubSequencingE2ETests.cs`). They now accept a numeric `auditId` as well as a string. Make them require the string `EntityId` the contract locks, and include that in the same commit.

### Step 1: Wave 10A0, the contract amendment (one builder, alone)
Set WP-10-A1 to `in_progress` and dispatch its brief below, followed by its full YAML block. Nothing else runs while it works. When it reports done, review it:

- Every change is additive, with no member renamed or renumbered.
- The Contracts tests and the additive-policy tests pass.
- The full solution and the frontend build.

Then commit it, mark it completed, run the `after_each_wave` gate, and **declare the contracts re-locked** in a ledger commit message.

### Step 2: Wave 10A (WP-10-01, WP-10-02, WP-10-05, three builders in parallel)
Claim the three workpieces and dispatch each brief below, pasted whole and followed by that workpiece's full YAML block. If your environment cannot run three at once, run them in batches and say so in the report.

WP-10-02 may need changes to the UI-04 capture tab, but `surfaces\project\` belongs to WP-10-05 in this wave. WP-10-02 puts those in its handoff, and you apply them in step 4.

Review each builder before accepting it, as in phase 09:

- Deliverables sit inside its owned paths, and `git status` and `git diff --stat` show nothing outside them.
- You re-run its verification commands yourself.
- Component tests prove each routed defect.
- The handoff is concrete.

Commit, mark completed, and run the wave gate.

### Step 3: Wave 10B (WP-10-03)
Dispatch, review, accept, commit and gate in the same way.

### Step 4: WP-10-04 yourself (serial wiring)
Work through every accepted handoff, then these items, which are yours by ownership:

- **Rendering into the engine (G03, the central inert-rendering defect):** inject the WP-10-01 pool into `AuditExecutionEngine`, dispatch by capture mode in the crawl loop, store rendered documents and feed them to the analysers and the WP-10-02 comparer, and route rendered traffic through the loopback proxy.
- **Capture tab:** apply WP-10-02's UI-04 capture-mode tab changes to `surfaces\project\`, now that WP-10-05 is done.
- **Image sizes:** pass subresource transferred bytes from the proxy capture into the image rule facts (CR-031).
- **Rule catalog:** register every `FindingRuleKeys` constant from WP-10-A1 in the rule catalog (phase-09 notation gap 6 and CODEMAP gap 21), and register WP-10-05's category scoring.
- **Frontend:** wire the rendered tab, the rendered comparison surface and the render tester to the real pool, and finish the remaining shell bridge calls (G20).
- **Client-route soft 404:** report `soft-404-client-route` from the rendered document. The site-type verdict itself belongs to phase 11.
- **CODEMAP:** update `app\CODEMAP.md`.

Meet WP-10-04's `done_when`, run its verification commands, commit, and update the ledger.

### Step 5: Phase gate
Back up the app data folder again, then run `pwsh -NoProfile -File E:\Crawlforge\app\tools\gates\run-phase-gates.ps1 -Phase phase-10` in full: the `after_each_wave` list, then the wave-10C fixture runs (js, dual and adaptive modes, plus the surface walk). Audits for criteria #50, #51 and #52 must be **started from the UI**, with their mode chosen in UI-04.

Then run the phase-09 regression as well:

- the HTTP fixture run over the seventeen phase-09 traps, which must stay 17/17;
- `--surfaces UI-11,UI-12`, checking that missing-alt now sits under Images and hreflang-no-return under Internationalization.

Check every builder's `done_when` against the evidence. `/__fixture/requests` must show `nonGet == []` throughout. If everything passes, set phase-10 to completed in `index.yaml` and commit.

## Final report

Write `E:\Crawlforge\audit-work\reports\phase-10\PHASE-10-REPORT.md` and commit it. It covers:

- a summary verdict;
- the step 0 result for #56;
- for each workpiece, what was built, the review rounds and the acceptance evidence;
- the contract re-lock;
- the wiring you did;
- every gate command with its exit code and key output;
- the fixture verdict tables for js, dual, adaptive and the http regression;
- the UI walk for each surface;
- criteria #50, #51 and #52 with their evidence;
- the D-021 items with their new status;
- new defects, with suggested owners for phase 11 onward;
- spec questions under loop section 8;
- any deviation from this prompt and why.

## Builder briefs

Paste each brief whole into its builder, followed by the full YAML block of that workpiece from `phase-10-flow-2-rendering-and-capture-modes.yaml`.

## Wave 10A0 brief

### Brief: WP-10-A1, Contract amendment: additive category, WAF-class, indexability-flag members and rule keys

You are builder `cf-wp-10-a1` for phase 10 of CrawlForge (offline-first desktop crawler and SEO auditor at `E:\Crawlforge`: .NET 10 backend, Electron shell, React frontend). The coordinator has pasted your full workpiece YAML below this brief; it is your contract.

**Role.** You are the contract-amendment builder. You own `CrawlForge.Contracts\`, `app\tests\CrawlForge.Contracts.Tests\` and `frontend\src\contracts\`, and you run alone: nobody else writes code while you work.

**Task.** Make only additive contract changes. `AuditCategory` gains Images, SecurityAndProtocol and Internationalization. `WafChallengeClass` gains ManagedChallenge and RateLimitSoftBlock. `IndexabilityFlags` gains a bit for a canonical that points elsewhere (CR-024). `FindingRuleKeys` gains the rule keys phase-09 builders defined locally (listed in `WIRING-NOTES.md`, notation gap 6, for example `crawler-trap.detected`, `canonicalization.session-segment-removed`, `crawl.cookieless-redirect-loop`, `redirects.chain`), plus keys for the four CR-029 redirect cases and the CR-030 deprecated-term finding. Mirror every new value in the frontend contracts and guards. Take wire names from architecture 04/01 section 7.4 and 03/02 section 2. Deliver every item in your YAML `deliverables`, with component tests that prove each defect below is fixed at component level. Your `done_when` fixture and UI checks run at the phase gate after the coordinator wires your work, so make your component tests strong enough to stand in for them until then.

**Defects routed to you.** Phase-09 notation gaps 1 to 6 in `audit-work\reports\phase-09\WIRING-NOTES.md`.

**Boundaries.** Never rename, renumber or remove an existing member, and keep numeric values stable. Do not move any finding to its new category; that is WP-10-05's job. If the vocabulary tests lock exact member lists, extend them to the new spec vocabulary rather than weakening them.

Your agent id is `cf-wp-10-a1`; your private fixture port, if you need one, is 47340.

**Rules for this job.** Write only inside your owned paths (listed in your workpiece YAML). Never edit `architecture\`, `buildplan\`, `audit-work\register\`, `audit-work\decisions-log.md`, `audit-work\SOURCE-OF-TRUTH-LOOP.md`, `app\tests\fixture-site\expected-results.json`, `app\CODEMAP.md`, the solution file, DI or composition code, `Host\Execution\`, `Host\Composition\`, `Audit\Pipeline\`, `frontend\src\App.tsx`, `store\`, `shell\`, any rendering, crawler or audit code (you are the contract amendment; you change only the contract surface); those belong to the coordinator, so anything you need there goes in your wiring handoff. If the spec looks wrong or ambiguous, stop that item and report it; do not invent requirements. Do not commit; the coordinator commits accepted work. Other builders may be working at the same time: set `CF_AGENT_ID=<your agent id>` for every `dotnet build` and `dotnet test` so your output folders do not collide, run only your own test projects filtered to your folders, and never run the whole solution. Do not start the CrawlForge app or the fixture site on port 47311; if a test needs a fixture server, start your own on the port given below and stop it afterwards. Never touch `%APPDATA%\CrawlForge` or the owner's real projects (for example "Airpower"); if you need a data folder, use `%LOCALAPPDATA%\CrawlForge-<your agent id>`. Exclude `bin`, `obj`, `node_modules`, `dist`, `tools-env` from searches; in Git Bash prefix `/api/...` arguments with `MSYS_NO_PATHCONV=1`. Read `E:\Crawlforge\CLAUDE.md` and `E:\Crawlforge\app\CODEMAP.md` first, then the `context_files` and `architecture_sections` in your YAML.

**When you finish, report:** the files you created or changed; how each deliverable is met; the exact verification commands you ran with their output; a **wiring handoff** listing every registration, DI line, pipeline call, route, navigation entry or contract addition the coordinator must make, with type names and call sites; and anything you could not do and why.

## Wave 10A briefs

### Brief: WP-10-01, Direct CDP rendering layer, pinned bundled browser and loopback proxy capture

You are builder `cf-wp-10-01` for phase 10 of CrawlForge (offline-first desktop crawler and SEO auditor at `E:\Crawlforge`: .NET 10 backend, Electron shell, React frontend). The coordinator has pasted your full workpiece YAML below this brief; it is your contract.

**Role.** You are a backend builder on rendering. You own `CrawlForge.Crawler\Rendering\`, `CrawlForge.Crawler\Proxy\`, `assets\browser\` and `app\tests\CrawlForge.Crawler.Tests\Rendering\`.

**Task.** Replace the PuppeteerSharp path with a direct CDP control layer (D-002): a WebSocket CDP client using the Target, Page, Network and Runtime domains, a navigation and wait rule, screenshots, console capture, infinite-scroll handling and a bounded concurrency pool with timeout and failure isolation. Resolve the pinned Chrome for Testing bundle only from packaged assets (D-003) and record the pinned version for the UI-21 browser section. Make the loopback intercepting proxy capture the waterfall for rendered traffic, apply resource blocking and upstream chaining, and record **transferred bytes per subresource** (CR-031). Make the render tester run on the new pool. Deliver every item in your YAML `deliverables`, with component tests that prove each defect below is fixed at component level. Your `done_when` fixture and UI checks run at the phase gate after the coordinator wires your work, so make your component tests strong enough to stand in for them until then.

**Defects routed to you.** G03: `IRenderingPool` is never injected, so the probe-2 JS audit was inert ('no rendered tree is available'). The proxy capture record has no size field, so image size is unmeasurable (phase-09 notation gap 7).

**Boundaries.** Injecting the pool into the engine is the coordinator's job. Give the exact pool interface, its registration and its lifetime in your handoff. Never download a browser or driver, and never open a non-loopback listener.

Your agent id is `cf-wp-10-01`; your private fixture port, if you need one, is 47341.

**Rules for this job.** Write only inside your owned paths (listed in your workpiece YAML). Never edit `architecture\`, `buildplan\`, `audit-work\register\`, `audit-work\decisions-log.md`, `audit-work\SOURCE-OF-TRUTH-LOOP.md`, `app\tests\fixture-site\expected-results.json`, `app\CODEMAP.md`, the solution file, DI or composition code, `Host\Execution\`, `Host\Composition\`, `Audit\Pipeline\`, `frontend\src\App.tsx`, `store\`, `shell\`, `CrawlForge.Contracts\` or `frontend\src\contracts\` (contracts are locked after the WP-10-A1 amendment); those belong to the coordinator, so anything you need there goes in your wiring handoff. If the spec looks wrong or ambiguous, stop that item and report it; do not invent requirements. Do not commit; the coordinator commits accepted work. Other builders may be working at the same time: set `CF_AGENT_ID=<your agent id>` for every `dotnet build` and `dotnet test` so your output folders do not collide, run only your own test projects filtered to your folders, and never run the whole solution. Do not start the CrawlForge app or the fixture site on port 47311; if a test needs a fixture server, start your own on the port given below and stop it afterwards. Never touch `%APPDATA%\CrawlForge` or the owner's real projects (for example "Airpower"); if you need a data folder, use `%LOCALAPPDATA%\CrawlForge-<your agent id>`. Exclude `bin`, `obj`, `node_modules`, `dist`, `tools-env` from searches; in Git Bash prefix `/api/...` arguments with `MSYS_NO_PATHCONV=1`. Read `E:\Crawlforge\CLAUDE.md` and `E:\Crawlforge\app\CODEMAP.md` first, then the `context_files` and `architecture_sections` in your YAML.

**When you finish, report:** the files you created or changed; how each deliverable is met; the exact verification commands you ran with their output; a **wiring handoff** listing every registration, DI line, pipeline call, route, navigation entry or contract addition the coordinator must make, with type names and call sites; and anything you could not do and why.

### Brief: WP-10-02, Capture modes and rendered documents in full audits: dual, adaptive, raw versus rendered

You are builder `cf-wp-10-02` for phase 10 of CrawlForge (offline-first desktop crawler and SEO auditor at `E:\Crawlforge`: .NET 10 backend, Electron shell, React frontend). The coordinator has pasted your full workpiece YAML below this brief; it is your contract.

**Role.** You are a backend builder on capture modes. You own `CrawlForge.Crawler\Capture\`, `CrawlForge.Audit\Indexability\ResponseRenderedComparer.cs`, `CrawlForge.Audit\Indexability\RenderedComparison\` and their test folders.

**Task.** Implement the capture-mode execution contract. Non-JS fetches only. JS renders every in-scope page. Dual does both and compares them. Adaptive applies per-URL-pattern overrides. Feed rendered documents to analysis. Record JS-only links as discovered by render, and report raw versus rendered word counts. Compare response and rendered versions for title, canonical and meta robots, and report a rendered-only noindex as critical with dual evidence. Report a service-worker registration as informational, with renders bypassing it. Run custom JS snippets with a timeout inside an isolated wrapper. Deliver every item in your YAML `deliverables`, with component tests that prove each defect below is fixed at component level. Your `done_when` fixture and UI checks run at the phase gate after the coordinator wires your work, so make your component tests strong enough to stand in for them until then.

**Defects routed to you.** G03: the engine's `RenderedDocument` is always null, so `rendering.enabled=true` had no effect (probe-2 audit 12). `ResponseRenderedComparer.cs` never receives a rendered document, so late-meta-change is not testable.

**Boundaries.** Code against the WP-10-01 pool interface in its YAML deliverables; you can test with a fake pool. UI-04 capture-tab changes belong in `surfaces\project\`, which WP-10-05 owns in this wave, so put them in your handoff. Engine dispatch is the coordinator's.

Your agent id is `cf-wp-10-02`; your private fixture port, if you need one, is 47342.

**Rules for this job.** Write only inside your owned paths (listed in your workpiece YAML). Never edit `architecture\`, `buildplan\`, `audit-work\register\`, `audit-work\decisions-log.md`, `audit-work\SOURCE-OF-TRUTH-LOOP.md`, `app\tests\fixture-site\expected-results.json`, `app\CODEMAP.md`, the solution file, DI or composition code, `Host\Execution\`, `Host\Composition\`, `Audit\Pipeline\`, `frontend\src\App.tsx`, `store\`, `shell\`, `CrawlForge.Contracts\` or `frontend\src\contracts\` (contracts are locked after the WP-10-A1 amendment); those belong to the coordinator, so anything you need there goes in your wiring handoff. If the spec looks wrong or ambiguous, stop that item and report it; do not invent requirements. Do not commit; the coordinator commits accepted work. Other builders may be working at the same time: set `CF_AGENT_ID=<your agent id>` for every `dotnet build` and `dotnet test` so your output folders do not collide, run only your own test projects filtered to your folders, and never run the whole solution. Do not start the CrawlForge app or the fixture site on port 47311; if a test needs a fixture server, start your own on the port given below and stop it afterwards. Never touch `%APPDATA%\CrawlForge` or the owner's real projects (for example "Airpower"); if you need a data folder, use `%LOCALAPPDATA%\CrawlForge-<your agent id>`. Exclude `bin`, `obj`, `node_modules`, `dist`, `tools-env` from searches; in Git Bash prefix `/api/...` arguments with `MSYS_NO_PATHCONV=1`. Read `E:\Crawlforge\CLAUDE.md` and `E:\Crawlforge\app\CODEMAP.md` first, then the `context_files` and `architecture_sections` in your YAML.

**When you finish, report:** the files you created or changed; how each deliverable is met; the exact verification commands you ran with their output; a **wiring handoff** listing every registration, DI line, pipeline call, route, navigation entry or contract addition the coordinator must make, with type names and call sites; and anything you could not do and why.

### Brief: WP-10-05, Phase-09 notation-gap carryovers (D-021): categories, WAF classes, redirect cases, deprecated structured data

You are builder `cf-wp-10-05` for phase 10 of CrawlForge (offline-first desktop crawler and SEO auditor at `E:\Crawlforge`: .NET 10 backend, Electron shell, React frontend). The coordinator has pasted your full workpiece YAML below this brief; it is your contract.

**Role.** You are a full-stack builder on audit rules. Your owned paths cover WAF detection, the redirect evaluator and analyzer files, `CrawlForge.Audit\StructuredData\`, the image rule analyzer, the indexability state evaluator, `CrawlForge.Audit\Scoring\` and `frontend\src\surfaces\project\`, with their tests. The exact list is in your YAML.

**Task.** Use the contract members WP-10-A1 added.

- **Categories:** move image rules to Images, cookie-security and protocol findings to Security & protocol, and hreflang findings to Internationalization, and score all three.
- **WAF classes:** classify all five; soft blocks (rate-limit-soft-block, managed-challenge) throttle through AIMD, and hard blocks stop the host.
- **Canonical bit:** set it when the canonical points elsewhere.
- **Redirects (CR-029):** extend the redirect check to the four cases, each a toggle on the UI-04 analysis tab, all on by default.
- **Deprecated terms and Microdata (CR-030):** add deprecated-term findings from the bundled vocabulary plus a retired rich-result list (embed the versioned list inside `Audit\StructuredData\`; HowTo is one entry), and resolve Microdata `itemprop` URL and prefixed terms like RDFa.
- **Image size (CR-031):** make the oversized-image rule report "not measured" when no transferred size is available.
- **Rule keys:** replace the builder-local rule keys with the new `FindingRuleKeys` constants. Deliver every item in your YAML `deliverables`, with component tests that prove each defect below is fixed at component level. Your `done_when` fixture and UI checks run at the phase gate after the coordinator wires your work, so make your component tests strong enough to stand in for them until then.

**Defects routed to you.** Phase-09 notation gaps 1 to 5 and 7 to 10 in `WIRING-NOTES.md`, and CR-029, CR-030 and CR-031. If the coordinator's criterion #56 check failed, it also routes the UI-04 editor fix to you; ask the coordinator.

**Boundaries.** Score registration and the rule-catalog call are wiring, so list them in your handoff. Do not touch rendering or capture code. Keep every phase-09 trap passing: your component tests must cover missing-alt under Images and hreflang-no-return under Internationalization.

Your agent id is `cf-wp-10-05`; your private fixture port, if you need one, is 47343.

**Rules for this job.** Write only inside your owned paths (listed in your workpiece YAML). Never edit `architecture\`, `buildplan\`, `audit-work\register\`, `audit-work\decisions-log.md`, `audit-work\SOURCE-OF-TRUTH-LOOP.md`, `app\tests\fixture-site\expected-results.json`, `app\CODEMAP.md`, the solution file, DI or composition code, `Host\Execution\`, `Host\Composition\`, `Audit\Pipeline\`, `frontend\src\App.tsx`, `store\`, `shell\`, `CrawlForge.Contracts\` or `frontend\src\contracts\` (contracts are locked after the WP-10-A1 amendment); those belong to the coordinator, so anything you need there goes in your wiring handoff. If the spec looks wrong or ambiguous, stop that item and report it; do not invent requirements. Do not commit; the coordinator commits accepted work. Other builders may be working at the same time: set `CF_AGENT_ID=<your agent id>` for every `dotnet build` and `dotnet test` so your output folders do not collide, run only your own test projects filtered to your folders, and never run the whole solution. Do not start the CrawlForge app or the fixture site on port 47311; if a test needs a fixture server, start your own on the port given below and stop it afterwards. Never touch `%APPDATA%\CrawlForge` or the owner's real projects (for example "Airpower"); if you need a data folder, use `%LOCALAPPDATA%\CrawlForge-<your agent id>`. Exclude `bin`, `obj`, `node_modules`, `dist`, `tools-env` from searches; in Git Bash prefix `/api/...` arguments with `MSYS_NO_PATHCONV=1`. Read `E:\Crawlforge\CLAUDE.md` and `E:\Crawlforge\app\CODEMAP.md` first, then the `context_files` and `architecture_sections` in your YAML.

**When you finish, report:** the files you created or changed; how each deliverable is met; the exact verification commands you ran with their output; a **wiring handoff** listing every registration, DI line, pipeline call, route, navigation entry or contract addition the coordinator must make, with type names and call sites; and anything you could not do and why.

## Wave 10B brief

### Brief: WP-10-03, Rendered comparison UI: rendered tab, comparison surface, render artifacts

You are builder `cf-wp-10-03` for phase 10 of CrawlForge (offline-first desktop crawler and SEO auditor at `E:\Crawlforge`: .NET 10 backend, Electron shell, React frontend). The coordinator has pasted your full workpiece YAML below this brief; it is your contract.

**Role.** You are a frontend builder. You own `frontend\src\surfaces\explorers\rendered\`, `frontend\src\surfaces\single-page\` and `app\tests\frontend\rendered\`.

**Task.** Build the UI-13 rendered tab: the rendered DOM viewer, screenshot, console log, network waterfall, and a raw-versus-rendered diff of title, canonical, noindex and word count with dual evidence. Build the UI-16 rendered comparison surface, listing every page with response-versus-rendered differences sorted by severity. Make the UI-18 and UI-19 render tester show real rendered output, and add a capture-mode indicator for UI-10. Deliver every item in your YAML `deliverables`, with component tests that prove each defect below is fixed at component level. Your `done_when` fixture and UI checks run at the phase gate after the coordinator wires your work, so make your component tests strong enough to stand in for them until then.

**Defects routed to you.** Probe-2: render testing was inert because the pool was inert, and the rendered comparison surface is absent (G01).

**Boundaries.** Code against the wire shapes WP-10-02 delivered and the locked frontend contracts. Mounting, routes and the UI-10 indicator placement are the coordinator's, so list them in your handoff.

Your agent id is `cf-wp-10-03`; your private fixture port, if you need one, is 47344.

**Rules for this job.** Write only inside your owned paths (listed in your workpiece YAML). Never edit `architecture\`, `buildplan\`, `audit-work\register\`, `audit-work\decisions-log.md`, `audit-work\SOURCE-OF-TRUTH-LOOP.md`, `app\tests\fixture-site\expected-results.json`, `app\CODEMAP.md`, the solution file, DI or composition code, `Host\Execution\`, `Host\Composition\`, `Audit\Pipeline\`, `frontend\src\App.tsx`, `store\`, `shell\`, `CrawlForge.Contracts\` or `frontend\src\contracts\` (contracts are locked after the WP-10-A1 amendment); those belong to the coordinator, so anything you need there goes in your wiring handoff. If the spec looks wrong or ambiguous, stop that item and report it; do not invent requirements. Do not commit; the coordinator commits accepted work. Other builders may be working at the same time: set `CF_AGENT_ID=<your agent id>` for every `dotnet build` and `dotnet test` so your output folders do not collide, run only your own test projects filtered to your folders, and never run the whole solution. Do not start the CrawlForge app or the fixture site on port 47311; if a test needs a fixture server, start your own on the port given below and stop it afterwards. Never touch `%APPDATA%\CrawlForge` or the owner's real projects (for example "Airpower"); if you need a data folder, use `%LOCALAPPDATA%\CrawlForge-<your agent id>`. Exclude `bin`, `obj`, `node_modules`, `dist`, `tools-env` from searches; in Git Bash prefix `/api/...` arguments with `MSYS_NO_PATHCONV=1`. Read `E:\Crawlforge\CLAUDE.md` and `E:\Crawlforge\app\CODEMAP.md` first, then the `context_files` and `architecture_sections` in your YAML.

**When you finish, report:** the files you created or changed; how each deliverable is met; the exact verification commands you ran with their output; a **wiring handoff** listing every registration, DI line, pipeline call, route, navigation entry or contract addition the coordinator must make, with type names and call sites; and anything you could not do and why.
