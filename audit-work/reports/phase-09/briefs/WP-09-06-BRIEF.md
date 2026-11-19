# WP-09-06 BUILDER BRIEF — Audit & Configuration REST Conformance (wave 9B)

You are builder **cf-wp-09-02** reused for WP-09-06 (assigned_agent records the actual name). Read this whole brief, then the context files, then implement.

## Mission model (AGENT-INSTRUCTIONS §5.6)
Components + component tests ONLY. Nothing you build is reachable until the lead wires it in WP-09-11 (serial). done_when fixture/UI proof happens at the PHASE GATE after wiring — your job is wire-ready code + tests + an exact handoff. Exception: your owned_paths ARE the live REST/hub surface, so your fixes take effect immediately for Api.Tests; the KnownGap conformance test must turn green from your mapper fix alone (the conformance test project is lead-owned — do NOT edit it).

## Hard rules
- Write ONLY inside owned_paths (below). NEVER edit: architecture\, audit-work\register\, decisions-log.md, SOURCE-OF-TRUTH-LOOP.md, fixture-site\expected-results.json, buildplan\ (lead-only), Host\ (Execution/Composition), Crawler\, Audit\, Contracts\, Data.*\, frontend\, tests\CrawlForge.ContractConformance.Tests\, other test projects.
- No git commits (lead commits accepted work). No new NuGet/npm dependencies. No app launches. No fixture-harness runs.
- SDK pinned 10.0.404: `dotnet --version` inside E:\Crawlforge\app must print 10.0.x. NEVER use the .NET 11 preview. Avoid .NET-11-only APIs (e.g. Regex.Pattern).
- Set `$env:CF_AGENT_ID='cf-wp-09-06'` for your builds/tests. Scratch: %LOCALAPPDATA%\CrawlForge-cf-wp-09-06. NEVER touch %APPDATA%\CrawlForge or the owner's "Airpower" project.
- Spec is the only source of truth. If wrong/ambiguous/contradictory: stop that item, document your interpretation, report it (loop §8). Never invent spec or change upstream documents.
- Sibling compile breakage outside your paths: report exact errors to lead; do not edit.
- Every cross-layer payload you add/change needs a real-producer→real-consumer test (CLAUDE.md wire-shape rule).
- HOTFIX-01 host-throttle event shape is LOCKED (rateBefore/rateAfter/concurrencyBefore/concurrencyAfter) — never change it.
- CR-026: schedule field rename belongs to WP-12-10 — do NOT touch schedule code/fields.
- CR-028: MySQL deferred — skipped tests stay skipped.

## owned_paths
- E:\Crawlforge\app\src\backend\CrawlForge.Api\Controllers\
- E:\Crawlforge\app\src\backend\CrawlForge.Api\Hubs\
- E:\Crawlforge\app\tests\CrawlForge.Api.Tests\

## Full workpiece YAML (authoritative)
```yaml
- workpiece_id: "WP-09-06"
  title: "Audit & Configuration REST Conformance — Counter Keys, Run Lifecycle, Hub Run Events"
  phase_id: "phase-09"
  status: "pending"
  parallel_group: "wave-9B"
  depends_on_workpieces: ["WP-08-03"]
  context_files:
    - "E:\\Crawlforge\\architecture\\05-api-hub-and-mcp\\01-rest-api-contract.md"
    - "E:\\Crawlforge\\architecture\\05-api-hub-and-mcp\\02-realtime-websocket-hub.md"
    - "E:\\Crawlforge\\architecture\\03-crawl-engine\\02-frontier-fetching-and-waf.md"
  architecture_sections:
    - file: "E:\\Crawlforge\\architecture\\05-api-hub-and-mcp\\01-rest-api-contract.md"
      sections: ["10.1", "10.2", "10.4"]
    - file: "E:\\Crawlforge\\architecture\\05-api-hub-and-mcp\\02-realtime-websocket-hub.md"
      sections: ["10.3"]
    - file: "E:\\Crawlforge\\architecture\\03-crawl-engine\\02-frontier-fetching-and-waf.md"
      sections: ["5.7"]
  capabilities:
    - { id: "CAP-A-032", baseline_state: "partial" }
    - { id: "CAP-A-033", baseline_state: "partial" }
    - { id: "CAP-A-034", baseline_state: "partial" }
    - { id: "CAP-L-X05", baseline_state: "partial" }
    - { id: "CAP-H-184", baseline_state: "works" }
  ui_surfaces: ["UI-02", "UI-10"]
  fixture_traps: ["noindex", "redirect-chain"]
  contract_tests:
    - payload: "GET /api/audits/{id} counters — engine counters_json keys mapped to wire keys (discovered/fetched/analyzed/failed -> queued/fetched/error/blocked/wafBlocked per contract)"
      test: "AuditCountersWireConformanceTests (KnownGap WP-09-06 turned green here)"
    - payload: "POST /api/audits/{id}/pause|resume|cancel; crash-recovery resume from checkpoint"
      test: "AuditLifecycleConformanceTests"
    - payload: "hub url-batch, findings-updated, audit-failed events published during a run"
      test: "RunHubEventConformanceTests (G05 subset)"
    - payload: "configuration CRUD REST (create/read/update/delete) round-trip"
      test: "ConfigurationCrudConformanceTests (G18)"
  reopens:
    - workpiece_id: "WP-05-01"
      evidence: "hotfix-01-report.md §6: AuditWireMapper.ParseCounters expects queued/fetched/error/blocked/wafBlocked but engine writes discovered/fetched/analyzed/failed — dashboard counters read 0"
    - workpiece_id: "WP-05-02"
      evidence: "G05: url-batch, findings-updated, audit-failed never published"
    - workpiece_id: "WP-02-02"
      evidence: "probe-2: pause/resume partial; checkpoint resume not exercised through the API"
  owned_paths:
    - "E:\\Crawlforge\\app\\src\\backend\\CrawlForge.Api\\Controllers\\"
    - "E:\\Crawlforge\\app\\src\\backend\\CrawlForge.Api\\Hubs\\"
    - "E:\\Crawlforge\\app\\tests\\CrawlForge.Api.Tests\\"
  deliverables:
    - "Counter-key mismatch fixed at the wire mapper with a conformance test proving engine->wire->UI agreement; run view counters non-zero during a fixture audit"
    - "Pause/resume/cancel endpoints verified against checkpoint persistence; crash-recovery resume path exercised; audit-failed published with error detail"
    - "url-batch and findings-updated published with contract payload shapes; configuration CRUD round-trips through REST (editor UI consumes it in WP-09-07)"
  verification_commands:
    - "dotnet test E:\\Crawlforge\\app\\tests\\CrawlForge.Api.Tests"
    - "dotnet test E:\\Crawlforge\\app\\tests\\CrawlForge.ContractConformance.Tests --filter FullyQualifiedName~AuditCounters"
  done_when: "Phase-gate fixture run (http): counters on GET /api/audits/{id} match the fixture crawl (183 URLs discovered class); pause/resume mid-audit preserves data; UI walk: UI-10 shows live non-zero counters and pause/resume controls work."
  acceptance_criteria_refs: [6, 41, 49]
```

## Pre-dispatch coordinator change to your owned paths (inherit, don't revert)
The lead fixed a wave-9A gate blocker in Api.Tests\Controllers\ToolsRouteTests.cs (commit recorded in git log): UrlCheck_DetectsWafChallenge_FromCatalogue now uses a 200+HTML challenge (spec §2.4.5: only 2xx+HTML can transition to waf-challenge); NEW UrlCheck_Non2xxChallengeSignature_IsAnnotationOnly covers the 403 annotation-only case. Api.Tests baseline is 220 tests. Keep both; extend, never weaken.

## Deliverable detail + lead rulings

### D1 Counter keys (the KnownGap — highest priority)
- Engine writes counters_json keys discovered/fetched/analyzed/failed (AuditExecutionEngine state.Counters; checkpoint CountersJson same). Wire contract (05/01 §10.1) keys: queued/fetched/error/blocked/wafBlocked. AuditWireMapper.ParseCounters (Api, ~:22) currently expects wire keys from storage → reads 0.
- FIX AT THE WIRE MAPPER: map storage keys → wire keys. Read spec §10.1 for the exact semantics of blocked/wafBlocked (engine may not produce them separately today — map what exists, derive blocked/wafBlocked from url_records status classes if the spec defines them that way, or 0 with a documented handoff note if the engine must produce them — engine counters are Host-owned, lead wires). The KnownGap test AuditCountersWireConformanceTests.RestCounters_MatchEngineCounters (ContractConformance project, lead-owned, currently the ONLY failing test unfiltered) feeds real engine producer output → your mapper → wire shape. Your fix must make it pass WITHOUT editing that project. Run the AuditCounters filter to prove it.
- Api.Tests currently SEED consumer-key counters JSON (AuditRouteTests.cs:140, AuditExplorerRouteTests.cs:234) — you own Api.Tests: update seeds to PRODUCER keys (discovered/fetched/analyzed/failed) so tests exercise the real mapping, keep assertion strength.
- G-COMPLETED-COUNTERS / G-HUB-COUNTERS: completed-run counters + hub crawl-progress final payload must agree with REST. Hub payload production in Api\Hubs is yours; engine-side emission points are lead's — handoff exact recipes.

### D2 Run lifecycle
- POST /api/audits/{id}/pause|resume|cancel: verify against checkpoint persistence (FrontierSnapshot + AimdStateJson + CountersJson). Exercise crash-recovery resume through the API in Api.Tests (in-process host + real SQLite scratch). Fix what's broken in Controllers scope; engine-side checkpoint defects → handoff.
- audit-failed published with error detail (hub event already in frontend contract — shape in contracts\hub\events.ts, read-only for you). Publication point if engine-side → handoff; Api-side failure paths (start validation etc.) → publish here.
- G10 ORDERING FIX (from cf-wp-09-03 handoff): AuditsController.StartAuditAsync starts engine at ~:140 BEFORE persisting UrlListJson at ~:154-160. Move persist BEFORE start. Engine reads list seeds via ListSeedResolver.ResolveForAuditAsync (lead wires the engine half). Add Api.Tests regression: start list-mode audit → url_list_json persisted before engine observes it.
- G-G numeric hub ids: hub event payloads must carry STRING auditId/urlId per frozen frontend contract (matrix-row-validated already fixed by WP-09-03 to strings + MatrixVerdictNames vocabulary). Audit/hub payloads in Api\Hubs: align to events.ts shapes (read it; do not edit it).

### D3 G05 subset + config CRUD
- url-batch / findings-updated: contract payload shapes per 05/02 §10.3 + events.ts. Implement Api-side publication surface (hub service methods + payloads + tests). Engine-side emission call sites (batch flush, finding emit) are Host-owned → exact handoff recipes (method signatures + where to call).
- Configuration CRUD (G18): ConfigurationsController create/read/update/delete round-trip through REST with real SQLite. Editor UI (WP-09-07) consumes this — keep the DTO shape stable per 05/01 §10.4; PolitenessJson storage shape {"aimd":{...}} (ProjectWireMapper) must round-trip (HOTFIX-01 parser accepts both shapes).

### D4 REST mappers from wave-9A handoffs (Controllers scope)
- Redirect chain (cf-wp-09-01): AuditExplorerController URL detail ~:215-217; elements query ~:205 already loads url_elements rows. Map element_group "redirect-chain" payload {hops:[{ordinal,url,toUrl,statusCode}], finalUrl, finalStatusCode, loopDetected, chainCapExceeded} → UrlDetailWire.RedirectChain (wire record AuditExplorerWireContracts.cs:149; RedirectHopWire(Url, StatusCode, Location) at :30). Keep legacy single-hop fallback (RedirectTargetUrl) when no chain row. Full recipe: audit-work\reports\phase-09\handoffs\WP-09-01-HANDOFF.md.
- Matrix verdict REST drift (cf-wp-09-03): RedirectMatrixController.cs:161 ToWireValue (lowercase enum names) → MatrixVerdictNames.Of(verdict) (CrawlForge.Crawler.ListMode); :122 verdict-filter Enum.TryParse → MatrixVerdictNames.Parse. Vocabulary: pass/target-mismatch/chain-exceeded/final-non-200/canonical-mismatch. REST == hub == metric dimensions after this.

## Verification (must end green, exact output in report)
1. `dotnet test E:\Crawlforge\app\tests\CrawlForge.Api.Tests` (cwd app, CF_AGENT_ID set)
2. `dotnet test E:\Crawlforge\app\tests\CrawlForge.ContractConformance.Tests --filter FullyQualifiedName~AuditCounters` — KnownGap GREEN here.
Also run `dotnet build src\backend\CrawlForge.Host` to prove transitive compile.

## Final report format
Files (owned only) · per-deliverable evidence · exact command output · wiring handoff (engine-side emission points, hub service signatures, anything Host-owned) · contract-test payloads for lead's conformance suites (AuditLifecycle/RunHubEvent/ConfigurationCrud shapes) · blockers/ambiguities/notation gaps. No commits.