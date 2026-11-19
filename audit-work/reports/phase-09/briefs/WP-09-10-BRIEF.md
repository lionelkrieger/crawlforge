# WP-09-10 BUILDER BRIEF — Export Engine Completion & Export Dialog (wave 9B)

You are builder **cf-wp-09-03** reused for WP-09-10 (assigned_agent records the actual name). Read this whole brief, then the context files, then implement.

## Mission model (AGENT-INSTRUCTIONS §5.6)
Components + component tests ONLY. Backend: complete the Export project writers/engine. Frontend: build surfaces\export\ (NEW). The shared shell (App.tsx, shell\AuditViews.tsx ReportView nav, store\, contracts\, Settings shell) is LEAD-OWNED (WP-09-11): lead mounts your dialog + adds the report "Export" nav button + mounts the UI-21 destinations section in Settings. Export REST endpoints live in Api\Controllers (WP-09-06 owns) — if an endpoint you need is missing, document it as a handoff item; do NOT edit Api. Nothing reachable until wiring; done_when fixture/UI proof at PHASE GATE.

## Hard rules
- Write ONLY inside owned_paths. NEVER edit: architecture\, audit-work\register\, buildplan\, decisions-log.md, SOURCE-OF-TRUTH-LOOP.md, fixture-site\expected-results.json, Api\, Host\, Contracts\, Data.*\, Audit\, Crawler\, frontend shared shell (App.tsx, shell\*, store\*, contracts\*, other surfaces), harness files (ui_walker.py = acceptance oracle, never edit/weaken).
- **CrawlForge.Export.csproj is ORCHESTRATOR-MANAGED — do NOT edit it.** Existing packages suffice: DocumentFormat.OpenXml (XLSX), PdfSharp (PDF), FluentFTP + SSH.NET (destinations), System.Security.Cryptography.Pkcs (transitive pin). No new NuGet/npm dependencies anywhere.
- No git commits. No app launches. No fixture-harness runs. No CDP. No outbound network in tests (destinations tested against local doubles only; zero non-crawl outbound is an acceptance invariant).
- SDK pinned 10.0.404 (dotnet --version in app = 10.0.x; never .NET 11 preview; avoid Regex.Pattern etc.). $env:CF_AGENT_ID='cf-wp-09-10' for builds/tests. Scratch %LOCALAPPDATA%\CrawlForge-cf-wp-09-10. NEVER touch %APPDATA%\CrawlForge or "Airpower".
- Spec only source of truth; ambiguity → document + report (loop §8). Sibling breakage → report, don't edit.
- CR-028: MySQL deferred — skipped tests stay skipped. CR-026: no schedule code.

## owned_paths
- E:\Crawlforge\app\src\backend\CrawlForge.Export\
- E:\Crawlforge\app\src\frontend\src\surfaces\export\   (NEW)
- E:\Crawlforge\app\tests\CrawlForge.Export.Tests\
- E:\Crawlforge\app\tests\frontend\export\             (NEW)

## Full workpiece YAML (authoritative)
```yaml
- workpiece_id: "WP-09-10"
  title: "Export Engine Completion & Export Dialog (CSV, XLSX, JSON, PDF, Text, Markdown, Sitemap, Destinations)"
  phase_id: "phase-09"
  status: "pending"
  parallel_group: "wave-9B"
  depends_on_workpieces: ["WP-08-03", "WP-08-04"]
  context_files:
    - "E:\\Crawlforge\\architecture\\06-frontend-shell-and-ui\\02-virtualization-and-explorers.md"
    - "E:\\Crawlforge\\architecture\\05-api-hub-and-mcp\\03-local-mcp-server-and-agents.md"
  architecture_sections:
    - file: "E:\\Crawlforge\\architecture\\06-frontend-shell-and-ui\\02-virtualization-and-explorers.md"
      sections: ["8.4", "8.5"]
    - file: "E:\\Crawlforge\\architecture\\05-api-hub-and-mcp\\03-local-mcp-server-and-agents.md"
      sections: ["11.8"]
  capabilities:
    - { id: "CAP-H-173", baseline_state: "partial" }
    - { id: "CAP-H-174", baseline_state: "partial" }
    - { id: "CAP-H-175", baseline_state: "missing_engine" }
    - { id: "CAP-H-177", baseline_state: "missing_ui" }
    - { id: "CAP-H-X02", baseline_state: "missing_ui" }
    - { id: "CAP-H-X03", baseline_state: "partial" }
    - { id: "CAP-G-164", baseline_state: "missing_engine" }
    - { id: "CAP-G-165", baseline_state: "missing_engine" }
  ui_surfaces: ["UI-20", "UI-21"]
  fixture_traps: ["noindex", "duplicate-titles"]
  contract_tests:
    - payload: "export request/response (format, scope, columns) and produced file bytes"
      test: "ExportFormatConformanceTests — CSV/XLSX/JSON/PDF/text/Markdown/sitemap validated by parsing the output"
    - payload: "export destination CRUD (encrypted credentials at rest)"
      test: "ExportDestinationConformanceTests"
  reopens:
    - workpiece_id: "WP-05-03"
      evidence: "probe-2: CSV/XLSX partial; no JSON writer (H-175), no full-text/Markdown writers (G-164/165); PDF UI absent"
    - workpiece_id: "WP-06-05"
      evidence: "sitemap generator partial (H-X03); export dialog UI-20 not reachable"
  owned_paths:
    - "E:\\Crawlforge\\app\\src\\backend\\CrawlForge.Export\\"
    - "E:\\Crawlforge\\app\\src\\frontend\\src\\surfaces\\export\\"
    - "E:\\Crawlforge\\app\\tests\\CrawlForge.Export.Tests\\"
    - "E:\\Crawlforge\\app\\tests\\frontend\\export\\"
  deliverables:
    - "Export writers completed: multi-sheet XLSX, CSV, JSON (agent-contract shape), PDF report, full page text (.txt), full page Markdown (.md), XML sitemap; every writer produces valid parseable local files offline"
    - "UI-20 export dialog reachable from every report surface: format/scope/column selection incl. dynamic extraction columns, local download; destination export only to user-configured encrypted destinations"
    - "UI-21 export-destination settings section (CRUD, credential encryption via ISecretProtector, enable/disable)"
  verification_commands:
    - "dotnet test E:\\Crawlforge\\app\\tests\\CrawlForge.Export.Tests"
    - "cd E:\\Crawlforge\\app\\src\\frontend && npm run build"
  done_when: "Phase-gate fixture run: export the fixture audit in every format; each file parses and contains the noindex/duplicate-title findings; UI walk: Report window › Export reaches UI-20 and downloads a real XLSX and JSON; disabling destinations restores zero non-crawl outbound."
  acceptance_criteria_refs: [7, 14, 49]
```

## Existing code (extend, don't rewrite)
Export project already has: ExportEngine, ExportJobRegistry, ExportModels, Datasets\ExportDatasetReader, Delivery\{DestinationModels, ExportDestinationDelivery, ExportTransports}, Writers\{CsvExportWriter, ExcelExportWriter, HtmlReportWriter, LocalFontResolver, PdfSummaryWriter, SitemapWriter}. Packages: DocumentFormat.OpenXml, PdfSharp, FluentFTP, SSH.NET (csproj orchestrator-managed).
Gaps to close: JSON writer (agent-contract shape per 05/03 §11.8), full-page-text .txt + Markdown .md writers (G-164/165), multi-sheet XLSX completion (H-174), PDF report completion (H-173; PdfSummaryWriter exists), sitemap completion (H-X03), export-delivery + export-progress hub event PRODUCER components (G-F/G-I from phase-08 report — routed HERE).

## ACCEPTANCE ORACLE — exact walker contract (ui_walker.py, read-only)
- **UI-20**: walker looks for report NAV affordance via _has_nav = BUTTONS or TESTIDS only: marker "Export". → Lead adds the nav button in ReportView; your ExportDialog component must be mountable from EVERY report surface (dashboard/issues/urls — props-driven, no surface imports). Hand off nav label exactly "Export". Dialog: format/scope/column selection incl. dynamic extraction columns, local download action, destination selection (only user-configured encrypted destinations).
- **UI-21** (settings, extra evidence): walker checks sections incl. "Export destinations". → Your ExportDestinationsSection component must carry heading "Export destinations" + CRUD + enable/disable; lead mounts it in Settings shell.
- Phase-gate fixture proof (after wiring): every format parses + contains noindex/duplicate-title findings; UI downloads real XLSX + JSON; disabling destinations → zero non-crawl outbound.

## Wire shapes + hub events (contracts read-only)
- Hub: contracts\hub\events.ts has export-delivery (baseline) — READ-ONLY; export-progress may need additive contract entry → handoff item for LEAD (do not edit events.ts). Producer components in Export scope: define payload records matching events.ts export-delivery shape exactly; export-progress shape per 06/02 §8.5 (document proposed shape for lead's contract addition). Publication call sites (Api/Host) → handoff recipes.
- Export REST request/response (format, scope, columns): read Api\Controllers\ExportsController.cs (if present) + 05/01 for the endpoint shape; missing endpoints → handoff to WP-09-06/lead. JSON writer output = agent-contract shape (05/03 §11.8 — the MCP export tool consumes the same shape; read Mcp export tool for the exact contract).
- Destination credentials: ISecretProtector (CrawlForge.Security) encryption at rest — existing Delivery\ code uses it; complete CRUD + enable/disable; tests prove ciphertext-at-rest (no plaintext in storage) + local-double transports only.
- Dataset: ExportDatasetReader reads audit data via repositories — findings include wave-9A additive rule keys (content.duplicate-title, indexability.* noindex etc.); exports must carry them (generic finding rows, no per-rule hardcoding).

## Tests
- Backend (CrawlForge.Export.Tests): every writer produces VALID PARSEABLE output — parse XLSX via OpenXml, PDF via PdfSharp read-back, JSON via JsonDocument, CSV via parser, sitemap via XDocument, text/Markdown content assertions; real SQLite scratch dataset with noindex + duplicate-title findings present in every format; multi-sheet XLSX structure; destination CRUD ciphertext-at-rest; delivery to local doubles; export-delivery/progress producer payload shape tests. No outbound network.
- Frontend (tests\frontend\export\): dialog renders formats/scopes/columns incl. dynamic extraction columns; download action calls REST/apiClient (mock); destination section CRUD + enable/disable; "Export destinations" heading; real wire JSON fixtures.

## Verification (must end green, exact output in report)
1. `dotnet test E:\Crawlforge\app\tests\CrawlForge.Export.Tests` (cwd app, CF_AGENT_ID set)
2. `cd E:\Crawlforge\app\src\frontend && npm run build`
3. `cd E:\Crawlforge\app && npm run test -- --run tests/frontend/export`
Also `dotnet build src\backend\CrawlForge.Host` for transitive compile.

## Final report format
Files · per-deliverable evidence · exact command output · WIRING HANDOFF (DI registrations for new writers/engine surface, Api endpoint needs, hub publication call sites + payload shapes, events.ts additive entry proposal for export-progress, frontend mounting: dialog props + nav label "Export" + settings section) · contract-test payloads for lead's ExportFormat/ExportDestination conformance suites · blockers/ambiguities. No commits.