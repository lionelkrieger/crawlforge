# WP-09-09 BUILDER BRIEF — URL Explorer & URL Detail (wave 9B)

You are builder **cf-wp-09-05** reused for WP-09-09 (assigned_agent records the actual name). Read this whole brief, then the context files, then implement.

## Mission model (AGENT-INSTRUCTIONS §5.6)
Components + component tests ONLY. You build surfaces\explorers\urls\. The shared shell (App.tsx, shell\AuditViews.tsx ReportView, store\, contracts\, explorers\ SHARED modules) is LEAD-OWNED (WP-09-11): the lead mounts your components and adds report NAV buttons. Nothing reachable until wiring; done_when UI-walk proof at PHASE GATE. Wire-ready components + EXACT testids/markers + vitest tests + mounting handoff.

## Hard rules
- Write ONLY inside owned_paths. NEVER edit: architecture\, audit-work\register\, buildplan\, decisions-log.md, SOURCE-OF-TRUTH-LOOP.md, fixture-site\expected-results.json, frontend shared shell (App.tsx, shell\*, store\*, contracts\*, explorers\ root/shared files — your urls\ subfolder is yours), surfaces\project\, surfaces\dashboards\, surfaces\explorers\issues\, surfaces\export\, surfaces\single-page\, backend code, harness files (ui_walker.py = acceptance oracle, never edit/weaken).
- No git commits. No new npm/NuGet dependencies. No app launches. No fixture-harness runs. No CDP.
- file:// load — Vite base './'; no router assumptions beyond ?route= (lead handles).
- Spec only source of truth; ambiguity → document + report (loop §8). Sibling breakage → report, don't edit.
- Virtualization (06/02 §8.2): inventory grid ≤50 DOM rows regardless of dataset size.
- Environment: `$env:CF_AGENT_ID='cf-wp-09-09'` for builds/tests; scratch %LOCALAPPDATA%\CrawlForge-cf-wp-09-09. NEVER touch %APPDATA%\CrawlForge or the owner's "Airpower" project. SDK pinned 10.0.404 for any dotnet use (never .NET 11 preview).

## owned_paths
- E:\Crawlforge\app\src\frontend\src\surfaces\explorers\urls\
- E:\Crawlforge\app\tests\frontend\urls\

## Full workpiece YAML (authoritative)
```yaml
- workpiece_id: "WP-09-09"
  title: "Report Window — URL Explorer & URL Detail (Inventory, Elements, Links, SERP Preview)"
  phase_id: "phase-09"
  status: "pending"
  parallel_group: "wave-9B"
  depends_on_workpieces: ["WP-08-04"]
  context_files:
    - "E:\\Crawlforge\\architecture\\06-frontend-shell-and-ui\\02-virtualization-and-explorers.md"
    - "E:\\Crawlforge\\architecture\\06-frontend-shell-and-ui\\04-ui-surface-map.md"
    - "E:\\Crawlforge\\architecture\\04-audit-and-analysis-engine\\02-core-seo-and-indexability-rules.md"
  architecture_sections:
    - file: "E:\\Crawlforge\\architecture\\06-frontend-shell-and-ui\\02-virtualization-and-explorers.md"
      sections: ["8.2", "7.1"]
    - file: "E:\\Crawlforge\\architecture\\06-frontend-shell-and-ui\\04-ui-surface-map.md"
      sections: ["1", "4"]
    - file: "E:\\Crawlforge\\architecture\\04-audit-and-analysis-engine\\02-core-seo-and-indexability-rules.md"
      sections: ["15.3"]
  capabilities:
    - { id: "CAP-H-169", baseline_state: "unwired" }
    - { id: "CAP-H-170", baseline_state: "unwired" }
    - { id: "CAP-A-003", baseline_state: "partial" }
    - { id: "CAP-A-007", baseline_state: "missing_ui" }
    - { id: "CAP-A-009", baseline_state: "missing_ui" }
    - { id: "CAP-A-025", baseline_state: "missing_ui" }
    - { id: "CAP-A-026", baseline_state: "missing_ui" }
    - { id: "CAP-A-027", baseline_state: "missing_ui" }
    - { id: "CAP-A-028", baseline_state: "missing_ui" }
    - { id: "CAP-A-029", baseline_state: "missing_ui" }
    - { id: "CAP-B-X04", baseline_state: "missing_ui" }
  ui_surfaces: ["UI-13"]
  fixture_traps: ["redirect-chain", "noindex", "canonical-conflict", "broken-links", "orphan"]
  contract_tests:
    - payload: "URL inventory paged query (status, indexability, depth, content-type filters)"
      test: "UrlExplorerQueryConformanceTests"
    - payload: "URL detail: element inventory (title/meta/canonical/hreflang), redirect chain, inlinks/outlinks, assets, SERP pixel measurement"
      test: "UrlDetailWireConformanceTests"
  reopens:
    - workpiece_id: "WP-06-03"
      evidence: "probe-2: UI-13 not reachable; G01 report surfaces unmounted"
    - workpiece_id: "WP-03-03"
      evidence: "SerpPixelMeasurer exists but no preview surface shows truncation (B-X04 missing_ui)"
  owned_paths:
    - "E:\\Crawlforge\\app\\src\\frontend\\src\\surfaces\\explorers\\urls\\"
    - "E:\\Crawlforge\\app\\tests\\frontend\\urls\\"
  deliverables:
    - "UI-13 URL explorer: virtualized inventory with advanced filtering (status, indexability, depth, content-type, custom columns), sorting, pinning; URL detail with element inventory (meta robots, canonical), full redirect chain, inlinks/outlinks with anchor and dom_zone, asset lists (CSS/JS/images/fonts/documents), SERP pixel preview with truncation indicator"
    - "Drill-down from any finding/issue to URL detail; internal/external link analysis visible per URL"
  verification_commands:
    - "cd E:\\Crawlforge\\app\\src\\frontend && npm run build"
    - "cd E:\\Crawlforge\\app && npm run test -- --run tests/frontend/urls"
  done_when: "Phase-gate UI walk: Report window navigation reaches URL explorer; /redirect/chain-1 detail shows the three-hop chain; /pages/noindex.html shows noindex in the element inventory; /pages/canonical-conflict.html shows both canonical targets; filtering by status=404 lists the broken-link target."
  acceptance_criteria_refs: [48, 49]
```

## ACCEPTANCE ORACLE — exact walker contract (ui_walker.py, read-only)
- **UI-13**: walker looks for report NAV affordances via _has_nav = BUTTONS or TESTIDS only (body text does NOT count): markers "URL explorer" / "URLs". → Lead adds the nav button; your UrlExplorer component root MUST carry testid **`url-explorer`**; hand off nav label exactly "URL explorer".
- **UI-11** also accepts testid `url-explorer` as a grid marker — your component satisfies it when mounted.
- done_when detail proofs (phase gate, after lead wiring + WP-09-06 REST mapper): /redirect/chain-1 shows THREE hops; /pages/noindex.html shows noindex in element inventory; /pages/canonical-conflict.html shows BOTH canonical targets; status=404 filter lists the broken-link target. Your detail component must render all of these from the wire shapes below.

## Wire shapes (code against these; contracts read-only)
- REST URL inventory + detail: read CrawlForge.Api\Controllers\AuditExplorerController.cs + AuditExplorerWireContracts.cs for EXACT shapes: paged inventory query (status/indexability/depth/contentType filters, sorting), UrlDetailWire (element inventory groups, RedirectChain at :149, inlinks/outlinks, assets).
- **RedirectChain wire** (WP-09-06 maps it in parallel from wave-9A storage): RedirectHopWire(Url, StatusCode, Location) at AuditExplorerWireContracts.cs:30; chain payload {hops:[{ordinal,url,toUrl,statusCode}], finalUrl, finalStatusCode, loopDetected, chainCapExceeded}; legacy single-hop fallback (redirectTargetUrl) when no chain row. Render hop-by-hop (ordinal, source URL, status, target) + loop/cap badges. Storage = url_elements group "redirect-chain" (lead + 09-06 handle; you consume the WIRE).
- Element inventory: url_elements groups (document_head {title, metaDescription, metaRobots, viewport, charset, canonicalUrl, alternates, openGraph, twitterCard, favicons}, header_directives, structured_data, anchors, images, ...). noindex display from metaRobots/header_directives; canonical-conflict shows BOTH targets (htmlValue vs headerValue / multiple canonicals) — evidence shapes in wave-9A handoffs (audit-work\reports\phase-09\handoffs\ + WIRING-NOTES.md).
- Inlinks/outlinks: link_edges wire (anchorText, relationFlags, isInternal, domZone, zoneEvidence, targetUrlId/canonicalTargetUrl). Internal/external analysis per URL.
- Assets: CSS/JS/images/fonts/documents groups from inventory.
- **SERP pixel preview**: SerpPixelMeasurer EXISTS (WP-03-03, frontend — find it via grep; consume, don't rewrite). Show title/description pixel widths + truncation indicator (04/02 §15.3).
- Indexability column: CR-024 semantics — isIndexable = SEARCH-indexable (noindex pages are fetchable but NOT indexable; lead wires the evaluator at the engine write path). Filter "indexability" uses the wire field.
- Counters wire keys queued/fetched/error/blocked/wafBlocked (09-06 fixes mapper in parallel).

## Component tests (vitest, tests\frontend\urls\)
Real-producer→real-consumer: ACTUAL REST wire JSON fixtures (copy from Api wire contracts/Api.Tests seeds + the chain payload shape above). Assert: url-explorer testid; virtualization ≤50 DOM rows at 10k rows; filters/sorting/pinning; detail renders 3-hop chain + loop badge; noindex inventory display; dual canonical targets; inlinks/outlinks with domZone; asset lists; SERP truncation indicator; drill-down callback (onOpenUrl); empty/loading/error states.

## Verification (must end green, exact output in report)
1. `cd E:\Crawlforge\app\src\frontend && npm run build`
2. `cd E:\Crawlforge\app && npm run test -- --run tests/frontend/urls`

## Final report format
Files · per-deliverable evidence · exact command output · MOUNTING HANDOFF (export names + props + testid url-explorer + nav label "URL explorer" + onOpenUrl callback + store/apiClient additions needed) · contract-test payloads for lead's UrlExplorerQuery/UrlDetailWire conformance suites · blockers/ambiguities. No commits.