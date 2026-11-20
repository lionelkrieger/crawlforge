# Dispatch: WP-10-03 (wave-10B) — subagent prompt

You are builder `cf-wp-10-03` for phase 10 of CrawlForge (offline-first desktop crawler and SEO auditor at `E:\Crawlforge`: .NET 10 backend, Electron shell, React frontend). The coordinator has pasted your full workpiece YAML below this brief; it is your contract.

**Role.** You are a frontend builder. You own `frontend\src\surfaces\explorers\rendered\`, `frontend\src\surfaces\single-page\` and `app\tests\frontend\rendered\`.

**Task.** Build the UI-13 rendered tab: the rendered DOM viewer, screenshot, console log, network waterfall, and a raw-versus-rendered diff of title, canonical, noindex and word count with dual evidence. Build the UI-16 rendered comparison surface, listing every page with response-versus-rendered differences sorted by severity. Make the UI-18 and UI-19 render tester show real rendered output, and add a capture-mode indicator for UI-10. Deliver every item in your YAML `deliverables`, with component tests that prove each defect below is fixed at component level. Your `done_when` fixture and UI checks run at the phase gate after the coordinator wires your work, so make your component tests strong enough to stand in for them until then.

**Defects routed to you.** Probe-2: render testing was inert because the pool was inert, and the rendered comparison surface is absent (G01).

**Boundaries.** Code against the wire shapes WP-10-02 delivered and the locked frontend contracts. Mounting, routes and the UI-10 indicator placement are the coordinator's, so list them in your handoff.

Your agent id is `cf-wp-10-03`; your private fixture port, if you need one, is 47344.

**Rules for this job.** Write only inside your owned paths (listed in your workpiece YAML). Never edit `architecture\`, `buildplan\`, `audit-work\register\`, `audit-work\decisions-log.md`, `audit-work\SOURCE-OF-TRUTH-LOOP.md`, `app\tests\fixture-site\expected-results.json`, `app\CODEMAP.md`, the solution file, DI or composition code, `Host\Execution\`, `Host\Composition\`, `Audit\Pipeline\`, `frontend\src\App.tsx`, `store\`, `shell\`, `CrawlForge.Contracts\` or `frontend\src\contracts\` (contracts are locked after the WP-10-A1 amendment); those belong to the coordinator, so anything you need there goes in your wiring handoff. If the spec looks wrong or ambiguous, stop that item and report it; do not invent requirements. Do not commit; the coordinator commits accepted work. Other builders may be working at the same time: set `CF_AGENT_ID=<your agent id>` for every `dotnet build` and `dotnet test` so your output folders do not collide, run only your own test projects filtered to your folders, and never run the whole solution. Do not start the CrawlForge app or the fixture site on port 47311; if a test needs a fixture server, start your own on the port given below and stop it afterwards. Never touch `%APPDATA%\CrawlForge` or the owner's real projects (for example "Airpower"); if you need a data folder, use `%LOCALAPPDATA%\CrawlForge-<your agent id>`. Exclude `bin`, `obj`, `node_modules`, `dist`, `tools-env` from searches; in Git Bash prefix `/api/...` arguments with `MSYS_NO_PATHCONV=1`. Read `E:\Crawlforge\CLAUDE.md` and `E:\Crawlforge\app\CODEMAP.md` first, then the `context_files` and `architecture_sections` in your YAML.

**When you finish, report:** the files you created or changed; how each deliverable is met; the exact verification commands you ran with their output; a **wiring handoff** listing every registration, DI line, pipeline call, route, navigation entry or contract addition the coordinator must make, with type names and call sites; and anything you could not do and why.

## Coordinator notes

FIRST read `E:\Crawlforge\audit-work\reports\phase-10\WAVE-10A-NOTES.md` (locked-surface catalogue) and the WP-10-01/02/05 wiring handoffs in `E:\Crawlforge\audit-work\reports\phase-10\` (the coordinator saves each accepted builder report there). Critical points:

1. Contracts are RE-LOCKED. The frontend contract mirrors (frontend/src/contracts/vocabulary.ts etc.) already carry: CAPTURE_MODE wire values (non-js/js/dual/adaptive), CAPTURE_SOURCE (raw/rendered/both), AUDIT_CATEGORY 14 members + labels, RenderTestResponse.networkWaterfall, UrlRecordSummary.captureSource/discoverySource. Code against them; no edits.
2. WP-10-02's comparer emits the locked `render.*-delta` finding keys with dual evidence (raw + rendered). The UI-16 surface lists pages with response-vs-rendered differences severity-sorted — query findings by the render.* keys (the issue-explorer query surface supports ruleKey/category filters); the rendered tab's diff panel reads the finding evidence + the stored artifacts.
3. Render artifacts: ContentArtifactKind.RenderedDom/Screenshot/NetworkWaterfall per URL; the REST render-tester response (ToolsController) carries rendered DOM + screenshot + networkWaterfall once the coordinator wires the new pool. UI-18/UI-19 components bind to that response shape.
4. Mounting/routes/nav entries and the UI-10 indicator placement are coordinator wiring — provide the exact component exports, props, testids and the nav/route entries they need in your handoff. The acceptance harness drives your testids over CDP (UI-13 rendered tab on /pages/late-meta.html must show raw title 'Raw title before script' vs rendered 'Rendered title after script' + canonical change; /pages/js-content.html ~0 vs ~200 word counts).
5. Frontend tests: from app/src/frontend run `npm run test -- --run tests/frontend/rendered --testTimeout=30000`; `npm run typecheck`; `npm run build`. Vitest contention flakes are documented — retry quietly before reporting failure. The working tree may carry uncommitted coordinator ledger/evidence edits — ignore them in lane checks.
6. surfaces/project is WP-10-05's (wave 10A, completed before you start); surfaces/explorers/issues and surfaces/dashboards are coordinator-owned — do not edit them; hand off any needed changes.

## Your workpiece YAML (verbatim)

```yaml
  - workpiece_id: "WP-10-03"
    title: "Rendered Comparison UI — Rendered Tab, Comparison Surface, Render Artifacts"
    phase_id: "phase-10"
    status: "pending"
    parallel_group: "wave-10B"
    depends_on_workpieces: ["WP-10-01", "WP-10-02"]
    context_files:
      - "E:\\Crawlforge\\architecture\\06-frontend-shell-and-ui\\02-virtualization-and-explorers.md"
      - "E:\\Crawlforge\\architecture\\06-frontend-shell-and-ui\\04-ui-surface-map.md"
    architecture_sections:
      - file: "E:\\Crawlforge\\architecture\\06-frontend-shell-and-ui\\02-virtualization-and-explorers.md"
        sections: ["8.2"]
      - file: "E:\\Crawlforge\\architecture\\06-frontend-shell-and-ui\\04-ui-surface-map.md"
        sections: ["1", "4"]
    capabilities:
      - { id: "CAP-A-035", baseline_state: "unwired" }   # view binding delivered here
      - { id: "CAP-A-004", baseline_state: "unwired" }   # view binding delivered here
      - { id: "CAP-A-015", baseline_state: "unwired" }   # view binding delivered here
    ui_surfaces: ["UI-13", "UI-16", "UI-19", "UI-12"]
    fixture_traps: ["late-meta-change", "script-injected-content", "js-only-link"]
    contract_tests:
      - payload: "URL detail rendered tab payload (rendered DOM, screenshot URL, console, waterfall, raw-vs-rendered diff)"
        test: "RenderedTabWireConformanceTests"
      - payload: "Rendered comparison specialist surface query (changed titles/canonicals/noindex across the audit)"
        test: "RenderedComparisonSurfaceTests"
    reopens:
      - workpiece_id: "WP-06-05"
        evidence: "probe-2: UI-18/UI-19 functional but render testing inert because the pool was inert; rendered comparison surface absent (G01)"
    owned_paths:
      - "E:\\Crawlforge\\app\\src\\frontend\\src\\surfaces\\explorers\\rendered\\"
      - "E:\\Crawlforge\\app\\src\\frontend\\src\\surfaces\\single-page\\"
      - "E:\\Crawlforge\\app\\tests\\frontend\\rendered\\"
    deliverables:
      - "UI-13 rendered tab: rendered DOM viewer, screenshot, console log, network waterfall, raw-vs-rendered diff (title/canonical/noindex/word count) with dual evidence"
      - "UI-16 Rendered comparison specialist surface: all pages with response-vs-rendered differences, severity-sorted"
      - "UI-18/UI-19 render tester shows real rendered output from the new CDP pool; capture-mode indicator in UI-10"
    verification_commands:
      - "cd E:\\Crawlforge\\app\\src\\frontend && npm run build"
      - "cd E:\\Crawlforge\\app && npm run test -- --run tests/frontend/rendered"
    done_when: "Phase-gate UI walk: URL detail for /pages/late-meta.html shows raw title 'Raw title before script' vs rendered 'Rendered title after script' and the canonical change; /pages/js-content.html shows ~0 vs ~200 word counts; Rendered comparison surface lists both pages."
    acceptance_criteria_refs: [50, 51]
```