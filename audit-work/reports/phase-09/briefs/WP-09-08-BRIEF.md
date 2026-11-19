# WP-09-08 BUILDER BRIEF — Report Dashboard & Issue Explorer (wave 9B)

You are builder **cf-wp-09-04** reused for WP-09-08 (assigned_agent records the actual name). Read this whole brief, then the context files, then implement.

## Mission model (AGENT-INSTRUCTIONS §5.6)
Components + component tests ONLY. You build surface components in surfaces\dashboards\ and surfaces\explorers\issues\. The shared shell (App.tsx, shell\AuditViews.tsx ReportView, store\, contracts\) is LEAD-OWNED (WP-09-11): the lead removes the placeholder Paper (AuditViews.tsx:312-322, text "mount into this workspace from the report workpieces"), mounts your components, and adds report NAV buttons. Nothing is reachable until wiring; done_when UI-walk proof happens at the PHASE GATE. Your job: wire-ready components with the EXACT testids/markers below + vitest tests + mounting handoff.

## Hard rules
- Write ONLY inside owned_paths. NEVER edit: architecture\, audit-work\register\, buildplan\, decisions-log.md, SOURCE-OF-TRUTH-LOOP.md, fixture-site\expected-results.json, frontend shared shell (App.tsx, shell\*, store\*, contracts\*, explorers\ SHARED files — your issues\ subfolder is yours, the explorers\ root/shared modules are lead's), surfaces\project\, surfaces\explorers\urls\, surfaces\export\, surfaces\single-page\, backend code, harness files (ui_walker.py is the acceptance oracle — never edit/weaken).
- No git commits. No new npm/NuGet dependencies (React 19 + Mantine 8 + Zustand installed). No app launches. No fixture-harness runs. No CDP.
- file:// load — Vite base './' stays; no router assumptions beyond ?route= (lead handles).
- Spec only source of truth; ambiguity → document + report (loop §8). Sibling breakage → report, don't edit.
- Virtualization rule (06/02 §8.2): grids render ≤50 DOM rows regardless of dataset size.
- Environment: `$env:CF_AGENT_ID='cf-wp-09-08'` for builds/tests; scratch %LOCALAPPDATA%\CrawlForge-cf-wp-09-08. NEVER touch %APPDATA%\CrawlForge or the owner's "Airpower" project. SDK pinned 10.0.404 for any dotnet use (never .NET 11 preview).

## owned_paths
- E:\Crawlforge\app\src\frontend\src\surfaces\dashboards\
- E:\Crawlforge\app\src\frontend\src\surfaces\explorers\issues\
- E:\Crawlforge\app\tests\frontend\report\

## Full workpiece YAML (authoritative)
```yaml
- workpiece_id: "WP-09-08"
  title: "Report Window — Dashboard & Issue Explorer (Real Data, Suppression, Scores)"
  phase_id: "phase-09"
  status: "pending"
  parallel_group: "wave-9B"
  depends_on_workpieces: ["WP-08-04"]
  context_files:
    - "E:\\Crawlforge\\architecture\\06-frontend-shell-and-ui\\02-virtualization-and-explorers.md"
    - "E:\\Crawlforge\\architecture\\06-frontend-shell-and-ui\\04-ui-surface-map.md"
    - "E:\\Crawlforge\\architecture\\04-audit-and-analysis-engine\\01-pipeline-and-content-scoping.md"
  architecture_sections:
    - file: "E:\\Crawlforge\\architecture\\06-frontend-shell-and-ui\\02-virtualization-and-explorers.md"
      sections: ["8.2", "7.1"]
    - file: "E:\\Crawlforge\\architecture\\06-frontend-shell-and-ui\\04-ui-surface-map.md"
      sections: ["1", "4"]
    - file: "E:\\Crawlforge\\architecture\\04-audit-and-analysis-engine\\01-pipeline-and-content-scoping.md"
      sections: ["7.5"]
  capabilities:
    - { id: "CAP-H-168", baseline_state: "unwired" }
    - { id: "CAP-I-192", baseline_state: "missing_ui" }
    - { id: "CAP-I-193", baseline_state: "missing_ui" }
    - { id: "CAP-I-X01", baseline_state: "missing_ui" }
    - { id: "CAP-I-X02", baseline_state: "missing_ui" }
    - { id: "CAP-B-036", baseline_state: "missing_ui" }
    - { id: "CAP-B-038", baseline_state: "missing_ui" }
    - { id: "CAP-B-039", baseline_state: "missing_ui" }
    - { id: "CAP-B-041", baseline_state: "missing_ui" }
    - { id: "CAP-B-042", baseline_state: "missing_ui" }
    - { id: "CAP-B-043", baseline_state: "missing_ui" }
    - { id: "CAP-B-044", baseline_state: "missing_ui" }
    - { id: "CAP-B-045", baseline_state: "missing_ui" }
    - { id: "CAP-B-055", baseline_state: "missing_ui" }
    - { id: "CAP-B-058", baseline_state: "missing_ui" }
    - { id: "CAP-B-059", baseline_state: "missing_ui" }
  ui_surfaces: ["UI-11", "UI-12"]
  fixture_traps: ["noindex", "duplicate-titles", "thin-page", "missing-alt", "canonical-conflict", "robots-blocked", "hreflang-no-return", "structured-data-errors"]
  contract_tests:
    - payload: "dashboard summary payload (counters, category scores, top issues) — non-zero on a real audit"
      test: "DashboardDataConformanceTests"
    - payload: "issue explorer paged findings query (severity, affected-URL count, suppression state)"
      test: "IssueExplorerQueryConformanceTests"
    - payload: "suppression toggle -> score recompute delta"
      test: "SuppressionScoreConformanceTests"
  reopens:
    - workpiece_id: "WP-06-03"
      evidence: "G01: AuditViews.tsx:311-321 placeholder; probe-2 UI-11 reachable_but_placeholder, UI-12 not reachable"
    - workpiece_id: "WP-03-08"
      evidence: "suppression/scoring engine exists but no UI path reaches it (I-X01/I-X02 missing_ui)"
  owned_paths:
    - "E:\\Crawlforge\\app\\src\\frontend\\src\\surfaces\\dashboards\\"
    - "E:\\Crawlforge\\app\\src\\frontend\\src\\surfaces\\explorers\\issues\\"
    - "E:\\Crawlforge\\app\\tests\\frontend\\report\\"
  deliverables:
    - "UI-11 dashboard renders real audit data: counters, category scores, severity distribution, top issues, crawl health — no placeholder frames"
    - "UI-12 issue explorer: virtualized findings grid (<=50 DOM rows), severity/affected-URL prioritisation, filter by category/rule/status, finding detail with evidence URLs, suppress/unsuppress with score recompute, suppressed-findings view"
    - "All HTTP-trap findings visible with evidence drill-down to URL detail"
  verification_commands:
    - "cd E:\\Crawlforge\\app\\src\\frontend && npm run build"
    - "cd E:\\Crawlforge\\app && npm run test -- --run tests/frontend/report"
  done_when: "Phase-gate UI walk: Audits › open audit opens the report window on Dashboard with non-zero counters; Report window navigation reaches Issue explorer; the fixture's duplicate-title, thin-page, noindex and structured-data findings are visible with evidence; suppressing a finding changes the category score and is reversible."
  acceptance_criteria_refs: [7, 32, 49]
```

## ACCEPTANCE ORACLE — exact walker contract (ui_walker.py, read-only)
- **UI-10/11**: walker clicks nav-audits → open-audit-{id} → expects testids `report-view` + `report-state` with an UPPERCASE state word + `report-progress`/`hosts-table` (these are EXISTING shell telemetry — lead-owned, already live). Then UI-11: the report dump must NOT contain the placeholder text "mount into this workspace from the report workpieces" (lead removes that Paper) AND must contain ≥1 of testids: `findings-grid`, `url-explorer`, `issues-grid`, `dashboard-charts`. → Your Dashboard component root MUST carry testid **`dashboard-charts`** and render real counters/scores (non-zero on a real audit).
- **UI-12**: walker looks for report NAV affordances via _has_nav = BUTTONS or TESTIDS only (body text does NOT count): markers "Issue explorer" / "Issues". → The lead adds the nav button in ReportView; your IssueExplorer component root MUST carry testid **`issues-grid`** and the nav label you hand off should be exactly "Issue explorer".
- Evidence drill-down: finding detail → URL detail navigation is lead-mounted cross-surface (hand off the callback prop shape, e.g. onOpenUrl(urlId)).

## Wire shapes (code against these; contracts read-only)
- REST findings/dashboard: read CrawlForge.Api\Controllers\AuditExplorerController.cs + AuditExplorerWireContracts.cs + DashboardController (if present) for the EXACT paged findings query params + FindingWire shape (ruleKey, category, severity, urlId, evidenceJson, suppression state) and dashboard summary (counters, category scores, top issues). Counters wire keys: queued/fetched/error/blocked/wafBlocked (WP-09-06 fixes the mapper in parallel — code against the WIRE contract keys, not storage keys).
- Suppression/scoring engine EXISTS (WP-03-08 backend): discover the REST surface (suppress/unsuppress endpoints + score recompute). If an endpoint is missing, document it as a handoff item for WP-09-06/lead — do NOT edit backend. Suppressed-findings view + reversible toggle with score delta are deliverables.
- Category scores: spec 04/01 §7.5 (severity-weighted, suppression recompute). Severity distribution + crawl health (WAF/throttle) panels per 06/02 §8.2.
- Hub live updates: contracts\hub\events.ts READ-ONLY; subscribe via existing store/liveProgress surfaces if available (props/callbacks preferred; document store additions needed as handoff).
- Finding evidence shapes from wave 9A (for detail rendering): camelCase evidenceJson per rule — e.g. structured-data.invalid {url,format,ordinal,type,errors[],missingRequired[],nestedEntityTypes[],richResult}; content.duplicate-title {title,urlCount,urls:[{urlId,url}]}; indexability.soft-404 {confidence,...}; canonical.header-html-conflict {headerValue,htmlValue,resolvedHeader,resolvedHtml,diff}. Render evidence generically (key/value + URL links) with rule-specific niceties where cheap.

## Component tests (vitest, tests\frontend\report\)
Real-producer→real-consumer: feed ACTUAL REST wire JSON (copy shapes from Api wire contracts/Api.Tests seeds) into components. Assert: dashboard-charts testid + non-zero counters/scores render; issues-grid virtualization ≤50 DOM rows with 10k-row fixture; filters (category/rule/status/severity); suppression toggle calls REST + score delta + reversibility; suppressed view; evidence drill-down callback; empty/loading/error states (no fake data).

## Verification (must end green, exact output in report)
1. `cd E:\Crawlforge\app\src\frontend && npm run build`
2. `cd E:\Crawlforge\app && npm run test -- --run tests/frontend/report`

## Final report format
Files · per-deliverable evidence · exact command output · MOUNTING HANDOFF (export names + props + testids dashboard-charts/issues-grid + nav label "Issue explorer" + onOpenUrl callback + store/apiClient additions needed + suppression endpoint inventory) · contract-test payloads for lead's DashboardData/IssueExplorerQuery/SuppressionScore conformance suites · blockers/ambiguities. No commits.