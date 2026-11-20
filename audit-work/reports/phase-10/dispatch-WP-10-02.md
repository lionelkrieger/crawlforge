# Dispatch: WP-10-02 (wave-10A) — subagent prompt

You are builder `cf-wp-10-02` for phase 10 of CrawlForge (offline-first desktop crawler and SEO auditor at `E:\Crawlforge`: .NET 10 backend, Electron shell, React frontend). The coordinator has pasted your full workpiece YAML below this brief; it is your contract.

**Role.** You are a backend builder on capture modes. You own `CrawlForge.Crawler\Capture\`, `CrawlForge.Audit\Indexability\ResponseRenderedComparer.cs`, `CrawlForge.Audit\Indexability\RenderedComparison\` and their test folders.

**Task.** Implement the capture-mode execution contract. Non-JS fetches only. JS renders every in-scope page. Dual does both and compares them. Adaptive applies per-URL-pattern overrides. Feed rendered documents to analysis. Record JS-only links as discovered by render, and report raw versus rendered word counts. Compare response and rendered versions for title, canonical and meta robots, and report a rendered-only noindex as critical with dual evidence. Report a service-worker registration as informational, with renders bypassing it. Run custom JS snippets with a timeout inside an isolated wrapper. Deliver every item in your YAML `deliverables`, with component tests that prove each defect below is fixed at component level. Your `done_when` fixture and UI checks run at the phase gate after the coordinator wires your work, so make your component tests strong enough to stand in for them until then.

**Defects routed to you.** G03: the engine's `RenderedDocument` is always null, so `rendering.enabled=true` had no effect (probe-2 audit 12). `ResponseRenderedComparer.cs` never receives a rendered document, so late-meta-change is not testable.

**Boundaries.** Code against the WP-10-01 pool interface in its YAML deliverables; you can test with a fake pool. UI-04 capture-tab changes belong in `surfaces\project\`, which WP-10-05 owns in this wave, so put them in your handoff. Engine dispatch is the coordinator's.

Your agent id is `cf-wp-10-02`; your private fixture port, if you need one, is 47342.

**Rules for this job.** Write only inside your owned paths (listed in your workpiece YAML). Never edit `architecture\`, `buildplan\`, `audit-work\register\`, `audit-work\decisions-log.md`, `audit-work\SOURCE-OF-TRUTH-LOOP.md`, `app\tests\fixture-site\expected-results.json`, `app\CODEMAP.md`, the solution file, DI or composition code, `Host\Execution\`, `Host\Composition\`, `Audit\Pipeline\`, `frontend\src\App.tsx`, `store\`, `shell\`, `CrawlForge.Contracts\` or `frontend\src\contracts\` (contracts are locked after the WP-10-A1 amendment); those belong to the coordinator, so anything you need there goes in your wiring handoff. If the spec looks wrong or ambiguous, stop that item and report it; do not invent requirements. Do not commit; the coordinator commits accepted work. Other builders may be working at the same time: set `CF_AGENT_ID=<your agent id>` for every `dotnet build` and `dotnet test` so your output folders do not collide, run only your own test projects filtered to your folders, and never run the whole solution. Do not start the CrawlForge app or the fixture site on port 47311; if a test needs a fixture server, start your own on the port given below and stop it afterwards. Never touch `%APPDATA%\CrawlForge` or the owner's real projects (for example "Airpower"); if you need a data folder, use `%LOCALAPPDATA%\CrawlForge-<your agent id>`. Exclude `bin`, `obj`, `node_modules`, `dist`, `tools-env` from searches; in Git Bash prefix `/api/...` arguments with `MSYS_NO_PATHCONV=1`. Read `E:\Crawlforge\CLAUDE.md` and `E:\Crawlforge\app\CODEMAP.md` first, then the `context_files` and `architecture_sections` in your YAML.

**When you finish, report:** the files you created or changed; how each deliverable is met; the exact verification commands you ran with their output; a **wiring handoff** listing every registration, DI line, pipeline call, route, navigation entry or contract addition the coordinator must make, with type names and call sites; and anything you could not do and why.

## Coordinator notes (authority: living architecture > locked contracts > brief/YAML)

FIRST read `E:\Crawlforge\audit-work\reports\phase-10\WAVE-10A-NOTES.md` — the locked-surface catalogue and your specific notes. Critical points:

1. Contracts are RE-LOCKED (commits 2cb4311 + 0307fe8). Code against the locked A1 surfaces: `CaptureMode` (NonJs/Js/Dual/Adaptive), `CaptureSettings` + `CapturePatternOverride` + `PatternCaptureMode`, `CaptureSource` (Raw/Rendered/Both), `CrawlConfigurationDto.CaptureMode/CaptureSettings/RedirectChecks` (nullable; null = baseline), `UrlRecordDto.CaptureSource`, `DiscoverySource.Render=5`, `ContentArtifactKind.NetworkWaterfall=3`, `RenderedPage.NetworkWaterfall`/`RenderedNetworkEntry`, and the `FindingRuleKeys` render.* family. NO edits to CrawlForge.Contracts or frontend/src/contracts; missing surfaces → handoff amendment request.
2. The REST wire binding is ALREADY LIVE (coordinator integration): top-level `captureMode` ("non-js"/"js"/"dual"/"adaptive"), `captureSettings`, `redirectChecks` on configuration read/create/update — proven by `CaptureConfigurationRouteTests` (5 tests, real SQLite). Schema v5 landed the columns. Do not add wire/schema work.
3. Mode semantics (§5.6.1): non-JS fetch only (default); JS renders every in-scope page; dual stores BOTH captures and compares EVERY URL; adaptive samples 3–5 per URL-path-pattern group (default 4, `CaptureSettings.AdaptiveSampleSizePerGroup`) and switches the group on material change (title/canonical/meta-robots/hreflang diff OR different link set OR visible word-count change > 20%, `MaterialChangeWordCountThreshold`). Switching emits `content.javascript-dependent-template`. Manual per-pattern overrides WIN over adaptive and apply on top of every mode. Dual storage warns above `DualStorageWarningThresholdBytes` (default 1 GiB) — WARNING only, NEVER a hard cap (exclusion #8).
4. §7.18 severities: title/canonical/meta-robots/hreflang differences = high (Error); rendered-only noindex EITHER direction = CRITICAL with dual evidence (raw + rendered). Use the locked keys: `render.title-delta`, `render.canonical-delta`, `render.meta-robots-delta`, `render.noindex-delta`, `render.metadata-delta`, `render.text-delta`, `render.links-delta`, `render.structured-data-delta`, `render.resources-delta`, `render.status-delta`. Soft-404 client routes REUSE `indexability.soft-404` (no new key locked). Service-worker registration → `render.service-worker-registered` informational; renders bypass service workers (§7.19).
5. Engine seams (coordinator wires; design your component for them): dispatch point is `AuditExecutionEngine.CrawlPhaseAsync` after link discovery (~:1365–1373); `BuildStoredContextAsync` (~:2665) will load the RenderedDom artifact and pass it as `PageAuditContext.RenderedDocument` (6th positional arg, currently null); `AuditPipeline` ALREADY invokes `IResponseRenderedComparer` when the rendered document is present — your comparer implementation replaces the stub behind that locked interface. Expose a capture-mode component with an exact interface + DI registration in your handoff. `PageAuditContext` allows additive optional properties but the Contracts project is locked — if a new context member is unavoidable, hand it off as an amendment request with justification (prefer artifact reads: analyzers may take `IReadRepository<ContentArtifactDto>` + `IContentStore`, precedent `UrlStructureAnalyzer`).
6. JS-only links: discover from the rendered DOM, record `DiscoverySource.Render`; report raw vs rendered word counts (`render.text-delta` evidence carries totalWordCount — the harness verdict t_script_injected_content needs ev.words ≥ 100; t_js_only_link needs the target row + `render.links-delta`; t_late_meta_change needs `render.metadata-delta`/`render.title-delta`).
7. Custom JS snippets (§7.9/§7.17): timeout + isolated wrapper; extraction source selectable raw/rendered/both (`CaptureSource`).
8. UI-04 capture-tab changes → HANDOFF only (surfaces/project is WP-10-05's this wave; the coordinator applies your tab after 05 completes). The tab is the EXISTING "Crawl as and capture" tab (CrawlAsPanel, tab id `crawl-as`) — extend it, do not invent a new tab. Bind to the live captureMode/captureSettings wire fields. UI-10 indicator placement is coordinator wiring (handoff). Provide the exact testids your controls will use — the acceptance harness drives them over CDP.
9. WP-10-01 builds the real CDP pool in parallel; code against the locked `IRenderingPool` and test with a fake pool. CR-006: rendered traffic is captured DIRECTLY by the rendering control layer (CDP Network domain → `RenderedPage.NetworkWaterfall`); the loopback proxy routes non-rendered plain-HTTP traffic. Your waterfall consumption reads the rendered capture's NetworkWaterfall / the NetworkWaterfall artifact.
10. The working tree carries uncommitted coordinator ledger/evidence edits (`buildplan/`, `audit-work/reports/phase-10/`, `app/tools/network-audit/evidence/`) — ignore them in lane checks. A canonical gate run may be in progress: run ONLY your filtered test projects with CF_AGENT_ID set; never the whole solution; retry contention flakes quietly before reporting failure.

## Your workpiece YAML (verbatim)

```yaml
  - workpiece_id: "WP-10-02"
    title: "Capture Modes & Rendered Documents in Full Audits — Dual/Adaptive, Raw-vs-Rendered Engine"
    phase_id: "phase-10"
    status: "pending"
    parallel_group: "wave-10A"
    depends_on_workpieces: ["WP-08-03", "WP-09-11"]
    context_files:
      - "E:\\Crawlforge\\architecture\\03-crawl-engine\\03-rendering-and-proxy-routing.md"
      - "E:\\Crawlforge\\architecture\\04-audit-and-analysis-engine\\02-core-seo-and-indexability-rules.md"
      - "E:\\Crawlforge\\architecture\\04-audit-and-analysis-engine\\04-custom-extraction-search-js.md"
    architecture_sections:
      - file: "E:\\Crawlforge\\architecture\\03-crawl-engine\\03-rendering-and-proxy-routing.md"
        sections: ["5.6", "5.6.1"]
      - file: "E:\\Crawlforge\\architecture\\04-audit-and-analysis-engine\\02-core-seo-and-indexability-rules.md"
        sections: ["7.18"]
      - file: "E:\\Crawlforge\\architecture\\04-audit-and-analysis-engine\\04-custom-extraction-search-js.md"
        sections: ["7.9", "7.17"]
    capabilities:
      - { id: "CAP-A-004", baseline_state: "unwired" }
      - { id: "CAP-A-X09", baseline_state: "missing_engine" }
      - { id: "CAP-A-035", baseline_state: "unwired" }
      - { id: "CAP-A-X18", baseline_state: "missing_engine" }
      - { id: "CAP-A-X20", baseline_state: "missing_engine" }
      - { id: "CAP-G-157", baseline_state: "unwired" }
    ui_surfaces: ["UI-04", "UI-10", "UI-13"]
    fixture_traps: ["js-only-link", "script-injected-content", "late-meta-change", "service-worker"]
    contract_tests:
      - payload: "audit config captureMode (non-JS/JS/dual/adaptive) + per-pattern overrides (REST + storage + engine)"
        test: "CaptureModeConformanceTests"
      - payload: "rendered document row per URL (content_artifacts) + response-vs-rendered comparison findings"
        test: "RenderedDocumentConformanceTests"
      - payload: "rendered-only noindex/title/canonical change findings with dual evidence (raw + rendered)"
        test: "RawRenderedDiffFindingTests"
    reopens:
      - workpiece_id: "WP-02-06"
        evidence: "G03: AuditExecutionEngine.cs RenderedDocument always null; rendering.enabled=true had no effect (probe-2 audit 12)"
      - workpiece_id: "WP-03-02"
        evidence: "ResponseRenderedComparer.cs exists but never receives a rendered document; probe-2 late-meta-change not_testable"
    owned_paths:
      - "E:\\Crawlforge\\app\\src\\backend\\CrawlForge.Audit\\Indexability\\ResponseRenderedComparer.cs"
      - "E:\\Crawlforge\\app\\src\\backend\\CrawlForge.Audit\\Indexability\\RenderedComparison\\"
      - "E:\\Crawlforge\\app\\src\\backend\\CrawlForge.Crawler\\Capture\\"
      - "E:\\Crawlforge\\app\\tests\\CrawlForge.Audit.Tests\\RenderedComparison\\"
      - "E:\\Crawlforge\\app\\tests\\CrawlForge.Crawler.Tests\\CaptureModes\\"
    deliverables:
      - "Capture-mode execution contract: non-JS (fetch only), JS (render every in-scope page), dual (both + comparison), adaptive (per-URL-pattern overrides deciding capture); mode honored end to end from configuration"
      - "Rendered document persisted per URL and fed to the analysis pipeline; JS-only links discovered from rendered DOM and recorded as discovered-by-render; rendered word count vs raw word count difference reported"
      - "Response-vs-rendered comparison: title/canonical/meta-robots changes, rendered-only noindex reported as critical with dual evidence; service-worker registration detected and reported as informational, renders bypass it"
      - "Custom JS snippets executed during rendering with timeout + isolated wrapper; extraction source selectable raw/rendered/both"
    verification_commands:
      - "dotnet test E:\\Crawlforge\\app\\tests\\CrawlForge.Audit.Tests --filter FullyQualifiedName~RenderedComparison"
      - "dotnet test E:\\Crawlforge\\app\\tests\\CrawlForge.Crawler.Tests --filter FullyQualifiedName~CaptureModes"
    done_when: "Phase-gate fixture runs: js mode passes js-only-link + script-injected-content; dual mode passes late-meta-change (raw title vs rendered title, canonical change, rendered-only noindex critical); adaptive mode applies per-pattern overrides; service-worker informational finding present; UI walk: UI-04 Crawl-as-and-capture tab selects the mode and UI-13 rendered tab shows the rendered DOM."
    acceptance_criteria_refs: [50, 51, 52]
```