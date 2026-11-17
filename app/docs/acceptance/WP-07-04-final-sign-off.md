# CrawlForge Final Acceptance Sign-Off — WP-07-04

**Workpiece:** WP-07-04 (phase-07, wave-7B — FINAL) · **Builder/verifier:** cf-wp-00-04
**Scope:** Packaging Asset Verification & Final Sign-Off Across All 48 Acceptance Criteria
**Authority:** FS-CRAWL-001 v1.4 §14 (#1–#34) + FS-CRAWL-002 v1.1 §9 (#35–#48), verbatim in
`architecture/07-quality-and-verification/01-master-acceptance-criteria.md`.

> **STATUS: COMPLETE — all 48 acceptance criteria carry passing automated evidence.
> Acceptance harness 72/72 exit 0; CrawlForge.sln Release build exit 0 (0 warnings,
> 0 errors); packaging verification 18/18 exit 0.** The three live-wiring defects
> found by this workpiece (D13/D14/D15) were routed to cf-wp-01-01 (task-51),
> fixed, and independently re-verified GREEN by this harness. The orchestrator
> canonically re-runs the harness + Release build before accepting WP-07-04 and
> closing phase-07 (ledger is orchestrator-only).

---

## 1. Verification method

WP-07-04 is verification-only (no product code edited). The acceptance harness
(`tests/CrawlForge.Acceptance.Tests`) consolidates the wave-7A evidence and adds
fresh targeted proof:

1. **Evidence-integrity layer** — a 48-criterion catalog
   (`AcceptanceCatalog.cs`) maps every criterion to its proof pointers (test
   methods/classes in the owning suites + wave-7A evidence artifacts). The
   theory `Criterion_ProofPointers_AreIntact` verifies every pointer exists on
   disk with the named member/marker. The wave-7A consolidated results are
   re-verified from the raw artifacts (handoff result block, boundary evidence
   index + governance scan JSON, scale fragments), and the criterion-#40
   composite gate is **recomputed from the raw BeginFrame deltas** — not merely
   referenced.
2. **Targeted live checks** (`TargetedRemainderTests.cs`) — the REAL backend
   (`CrawlForgeBackend.StartAsync`, full module registration, loopback Kestrel)
   against scripted loopback origins, covering the dispatch's remainder list:
   spelling evidence, structured-data taxonomy, readability/soft-404,
   technology catalogue locality, scoring/suppression comparability, standalone
   tools, signed-request scope, n-gram bounds, SERP pixels, header conflicts,
   zone-weighted equity.
3. **Offline packaging verification** (`packaging/verify-packaging.ps1` +
   `PackagingVerificationTests.cs`) — Release publish of the real composition
   root, asset bundling, licensing absence, update-feed absence, first-launch
   offline initialization from the PUBLISHED layout, local Chromium resolution,
   local-only Electron binary configuration.

## 2. Wave-7A evidence consolidated (all canonical, CF_AGENT_ID unset)

| Workpiece | Result | Evidence |
|---|---|---|
| WP-07-01 cross-module E2E | **80/80 exit 0** (4m43s, live MySQL 8.0.28) | `tests/CrawlForge.E2E.Tests/WP-07-01-HANDOFF.md` (CONSOLIDATED-RESULT block); 14 suites; defects D1–D12 fixed by routed owners + flip tests |
| WP-07-02 boundary & air-gap | **27/27 exit 0** + governance app-root **636 files / 0 violations** | `tools/network-audit/README.md` index: 24 evidence files + README = 25 durable artifacts; 2 scan JSONs (`tests/governance/wp-07-02-*.json`); 17 negative requirements attested |
| WP-07-03 scale & performance | **Scale 10/10 exit 0** (31m45s) + **npm grid 134/134 exit 0** | `tests/CrawlForge.Scale.Benchmarks/artifacts/benchmark-report.md` + 23 result fragments; criterion #40 ratified composite gate ALL 5 PASS |

### Criterion #40 — ratified composite gate (product-owner amendment 2026-11-16)

The literal `p95 ≤ 16.7ms` gate was formally retired by product-owner amendment
(headless compositor frame-delivery quantization at the vsync boundary; idle
baseline proved the jitter is clock pacing, not render work). The ratified
composite gate, **re-asserted by the harness from the raw `*-beginframe.json`
artifacts** on all five explorers:

| Gate facet | Threshold | Result (5/5 explorers) |
|---|---|---|
| p50 frame time | ≤ 16.7ms | 16.70ms |
| Dropped-frame rate | ≤ 1% | 0 dropped vsyncs |
| Single-vsync fraction | ≥ 95% | 98–100% |
| DOM rows | ≤ 50 | max 30 |
| Worker memory facet | heap ≤ 512MB + guaranteed termination | peak ≪ bound, 0 workers alive after layout, 5000 positions |

### Scale facets (criterion #19/#26/#41)

- Dataset seed **500,000 URLs** (+150k findings, 600k links, 180k extraction rows, 240k n-gram rows) through the real persistence layer.
- Live pipeline **10,000 URLs** crawled+analyzed end-to-end: 82,199 findings, 50,000 link edges, peak working set **212MB ≪ 2.5GB** bound (354 samples).
- Retention prune **1,324,598 rows / 12 tables** in 13.6s.
- Exports at 500k: CSV zip (55.8s) + Excel **500,001 rows/sheet** (24.2s, peak 1.25GB < bound).
- CWV: 24 rendered samples through the production LabRenderingProbe on **local Chrome** (LCP/CLS/INP all measured).
- Server GC verified via diagnostics; heartbeat 2s cadence; /metrics family parity with the hub heartbeat; Compact/Trim working-set 207MB → 9MB + WAL checkpoint to 0.

## 3. Per-criterion status (all 48)

Legend: **PASS** = passing automated evidence (wave-7A proof + harness verification).
All 48 criteria PASS; the three live facets gated during verification (#13/#38/#48)
were fixed by the routed owner and independently re-verified (§4).

| # | Criterion (abbreviated) | Status | Evidence |
|---|---|---|---|
| 1 | First launch offline → main hub | PASS | Host.Tests `Scaffold_FirstLaunch_CompletesOffline_AndOpensMainHubRoute`; Boundary FirstLaunchTests + first-launch.md (0 non-loopback); packaging first-launch from published layout |
| 2 | No OS identity stores read | PASS | Boundary IdentityStoreProofTests; identity-runtime-proof.md + identity-static-proof.md (NR-01 full tree 0) |
| 3 | Zero outbound idle/reporting; scoped audit/destination/DB traffic | PASS | Boundary IdleZeroOutbound/AuditTraffic/ExternalDbAddressing; idle-reporting.md + audit-traffic.md |
| 4 | Unlimited ceilings without overrides | PASS | E2E `FreshAudit_NoOverrides_RunsWithUnlimitedPageCeilings` |
| 5 | No license/account/entitlement concept | PASS | Boundary NoEntitlementConceptTests (14 API surfaces + MCP + settings DB); no-entitlement.md; packaging licensing-absence checks |
| 6 | Pause/resume/cancel/crash recovery | PASS | E2E AuditLifecycle pause/cancel/crash-recovery facts |
| 7 | Offline reports; valid local exports | PASS | E2E ExportValidity xlsx/pdf/csv-zip/html facts; offline content comparison |
| 8 | No remote resolution; local toggles | PASS | Boundary BundledAssetManifestTests; E2E plugin lifecycle; bundled-assets.md (10/10 catalogs local) |
| 9 | Idempotent versioned migrations; settings defaults | PASS | E2E `MySqlMigrations_AreIdempotent_AndDialectPure`; Host scaffold first-launch; packaging fresh-install probe |
| 10 | Single-page analysis without project | PASS | E2E SinglePageAnalysis suite (5 facts) |
| 11 | Scheduled unattended run + history + stable next-due | PASS | E2E `ScheduledRun_FiresUnattended_WithRunHistory_AndStableNextDue` |
| 12 | Missed schedule catch-up exactly once | PASS | E2E `MissedSchedule_RunsExactlyOnce_AsCatchUp` |
| 13 | Spelling token evidence + language + dictionary suppression | PASS | Live `Spelling_TokenEvidence_AndDictionarySuppression` (token evidence + page language + dictionary suppression after D13 fix: `UserDictionaryLiveSync` + write-repository hook + lifecycle participant, EngineServicesModule:152-191); unit `Spelling_Findings_Honor_User_Dictionary` |
| 14 | Destination export user-credentials only; zero non-crawl outbound when disabled | PASS | Boundary DestinationTransferTests; destination-transfer.md |
| 15 | Offline content comparison; stored-body re-audit | PASS | E2E OfflineReaudit comparison + zero-network facts (request-count proof) |
| 16 | MCP handshake SSE+stdio; token rejection; loopback-only; rotation | PASS | Boundary McpBoundaryTests; E2E invalid-token/rotation/stdio facts; mcp-boundary.md |
| 17 | Credentials encrypted at rest; no identity derivation | PASS | Boundary EncryptionAtRestTests (5 classes, 0 plaintext leaks); secret-protector-properties.md; SignedRequestTests store facts |
| 18 | External DB parity + archive revert + connection test | PASS | E2E MySqlParity suite (live MySQL 8.0.28; report parity IDENTICAL; D12 round-trip body fidelity) |
| 19 | Server GC profile via diagnostics | PASS | Host `Scaffold_Diagnostics_ReportsActiveServerGcProfile`; scale diagnostics-gc.json |
| 20 | Loopback metrics exposition; no identity dimensions; nothing pushed | PASS | Boundary MetricsBoundaryTests; metrics-boundary.md; scale metrics-parity.json |
| 21 | Local minidump; view/purge; never transmitted | PASS | Boundary MinidumpLocalityTests; minidump-locality.md |
| 22 | Proxy routing; intercepting proxy capture; loopback listeners only | PASS | Crawler ProxyRouting/ProxyForward tests; Boundary LoopbackListenerSweepTests; listener-sweep.md |
| 23 | MCP delegated flow; every invocation logged | PASS | E2E McpDelegation suite (8 facts incl. D7 tool-level failure logging) |
| 24 | Custom extraction typed values/caps/columns/offline re-extract/failure isolation | PASS | E2E custom-family live + offline facts (D5); Audit CustomContentExtractor/CustomBoundary suites |
| 25 | Accessibility locators/remediation/aggregates/scoring/render-off flags | PASS | Audit AccessibilityAnalyzerTests + AccessibilityRuleTests |
| 26 | Lab CWV + budgets + coverage/composition offline | PASS | Audit PerformanceAnalyzerTests; scale cwv-capture.json (24 samples, local Chrome) |
| 27 | Structured data parsing/validation/rich-result taxonomy | PASS | Audit StructuredDataValidatorTests (taxonomy); live `StructuredData_InvalidJsonLd_EmitsTaxonomyFinding` (inventory JSON-LD+Microdata capture + checker invalid/valid/rich-result) |
| 28 | Readability/reading-time/text-to-HTML; soft-404 distinct | PASS | Audit ReadabilityCalculatorTests + Soft404DetectorTests; live `Soft404_SuccessStatusErrorContent_FlaggedDistinctly` (200 + error content → warning) |
| 29 | Technology detection bundled catalogue only | PASS | Audit TechnologyDetectorTests (catalogue load + missing-asset throws); live `TechnologyCatalogue_IsLocal_AndDetectsFromBundledFingerprints` (WordPress detected from local catalogue) |
| 30 | Custom search items terms/scoping/counts/snippets/offline | PASS | E2E offline custom search re-run; Audit CustomContentSearchTests |
| 31 | Custom JS timeout/isolation/typed columns/failure isolation | PASS | Audit JintSnippetRunnerTests (timeout, thrown-error isolation, sandbox no-CLR/network); E2E offline JS re-run |
| 32 | Scores comparable; suppressions excluded/reversible | PASS* | Audit AuditScoringServiceTests `Scores_AreComparableAcrossAudits` + FindingSuppressionServiceTests; live `Scoring_ComparableAcrossAudits_AndSuppressionRecomputes` (identical overall across offline re-audit; suppression 201 + recompute) |
| 33 | Standalone tools without full audit | PASS | Api ToolsRouteTests; frontend queryBuilder suite; live `StandaloneTools_OperateWithoutAFullAudit` (robots verdicts, scope preview, llms validate; zero audits created) |
| 34 | Signed-request in-scope only; expiry; encrypted material | PASS | Crawler SignedRequestTests; live `SignedRequest_AppliesOnlyInScope` (in/out scope + expiry states) |
| 35 | WAF 200-OK blocked + attribution; corroboration guard; offline re-detect | PASS | E2E WafDetection suite (7 facts); offline WAF copy fact; bundled waf-signatures |
| 36 | AIMD increase/decrease to ceiling; hub events; state preserved | PASS | E2E AimdAdaptation (9 facts) + AimdHostThrottle live 429 pressure |
| 37 | Boilerplate scoping drives metrics; invalid selectors rejected; offline re-derive | PASS | Audit ScopeResolver/ContentScopeValidator tests; E2E scoped-word-count offline re-derivation |
| 38 | N-gram cannibalization clusters; site DF; REST/MCP surfaces; bounds | PASS | Live `Ngrams_OrdersZonesAndBounds_HonoredOnRestSurface` (per-URL orders/zones/bounds/clamp + site-wide DF materialized + shared-primary cannibalization cluster across two fixture URLs, after D14 fix: `AuditExecutionEngine.cs:863` invokes `RunSitePassAsync` in the audit lifecycle, failure-isolated); unit NGram suites; scale ngram facets |
| 39 | Plugin lifecycle; zero network without grant; failure isolation | PASS | E2E PluginLifecycle (3 facts); Boundary PluginNetworkBoundaryTests; plugin-network-vitest.md (9 suites/113 tests) |
| 40 | 60 FPS virtual scrolling @500k (ratified composite gate) | PASS | Raw BeginFrame artifacts re-verified by harness (5/5 explorers, all four facets); worker memory facet; benchmark-report amendment record; npm grid 134/134 |
| 41 | Live memory telemetry; Compact/Trim; /metrics parity; local-only | PASS | E2E heartbeat fact; scale compact-trim/heartbeat-accuracy/metrics-parity fragments; frontend statusBar suite |
| 42 | Point-and-click selector generation offline | PASS | frontend picker.test.tsx + selectorEngine.test.ts (criterion #42 suites) |
| 43 | List Mode ingestion/policies; five-tier redirect matrix | PASS | E2E ListMode suite (8 facts incl. D4 revalidation idempotence) |
| 44 | Offline log import/correlation; consent-gated DNS | PASS | Intelligence LogImportService/LogCorrelationEngine tests; Boundary LogsRestSurfaceTests; dns-consent.md; bundled bot-cidr |
| 45 | Local embeddings; semantic bands; failure isolation; no egress | PASS | Intelligence BundledOnnxProviderTests; Boundary ConsentAndEmbeddingTests; embedding-loopback.md; bundled onnx-model |
| 46 | llms.txt validation; AI governance matrix; local generator | PASS | Intelligence LlmsTxtValidation/Generator/GovernanceMatrix tests; bundled ai-agent-profiles |
| 47 | Analytics/GTM/GA4/Consent Mode v2 inventory + findings | PASS | Intelligence MarTech suites (MarTechAuditor/ConsentModeAnalyzer/DataLayerInspector); Api AnalyticsGeoRouteTests |
| 48 | Zone-weighted equity; SERP pixels; header equivalency | PASS | Live `ZoneWeightedEquity_LinkEdgesCarryZonesAndWeights` (every internal link_edge carries dom_zone + weighted equity ≠ unweighted, after D15 fix: `LinkGraphEnrichmentPass` + DI registrations EngineServicesModule:240-250 + engine link-inventory wiring); live `SerpPixels_OverBudgetTitle_EmitsTruncation`; live `HeaderHtmlConflict_EmitsErrorWithDualEvidence` (Error + dual evidence); unit WeightedLinkEquityCalculator/LinkZoneClassifier/HeaderHtmlEquivalency suites |

\* #32 live comparability + suppression-recompute wiring passes; the locked
service's exclusion semantics are proven by the unit suites.

## 4. Defect ledger — release gate (routed, NOT self-fixed)

| ID | Criterion facet | Root cause (code evidence) | Live proof | Owner / state |
|---|---|---|---|---|
| D13 | #13 dictionary suppression | `EngineServicesModule.cs:152` constructed `ContentQualityAnalyzer(scopeResolver)` without the user dictionary (ctor param `userDictionary`, `ContentQualityAnalyzer.cs:43` → `SpellingCheckInput:119`). REST `/api/dictionary` persisted entries; nothing fed them to the singleton analyzer. | `Spelling_TokenEvidence_AndDictionarySuppression` — token evidence + language passed; accepted word still flagged after dictionary add | **RESOLVED** (cf-wp-01-01, task-51): dictionary wired via `UserDictionaryLiveSync` + write-repository hook + lifecycle participant; harness re-verified GREEN |
| D14 | #38 site pass | `NGramRuleAnalyzer.RunSitePassAsync` (`NGramRuleAnalyzer.cs:118`) materializes `ngram_site_summaries` + emits cannibalization/topical-gap findings but had **zero call sites** in src. Per-URL pass was wired via AuditRuleHost. | `Ngrams_OrdersZonesAndBounds_HonoredOnRestSurface` — per-URL rows/orders/zones/clamp passed; site-scope REST + cannibalization empty | **RESOLVED** (cf-wp-01-01, task-51): `AuditExecutionEngine.RunNgramSitePassAsync` (engine:859-879) invokes the site pass in the audit lifecycle, failure-isolated, emitting findings + metric contributions; harness re-verified GREEN (site DF + cannibalization cluster live) |
| D15 | #48 zone/equity | Engine `DiscoverLinksAsync` wrote link_edges with target_url_id/anchor_text/dom_zone/weighted_equity NULL; no product code updated them. `LinkZoneClassifier`, `WeightedLinkEquityCalculator`, `LinkZoneInventoryService` were DI-unregistered and uncalled (unit suites only). | `ZoneWeightedEquity_LinkEdgesCarryZonesAndWeights` — internal edges lacked zones/weights | **RESOLVED** (cf-wp-01-01, task-51): `LinkGraphEnrichmentPass` (Host/Execution) + DI registrations (EngineServicesModule:240-250) + engine `LinkZoneInventoryService` wiring populate zones/anchors/targets/equity live (failure-isolated, engine:842-852), with re-audit remap; harness re-verified GREEN |

Why wave-7A missed them: unit proofs use constructor injection / direct
invocation, and the scale benchmark used a **seeded dataset** (direct row
inserts) — neither exercises the live lifecycle wiring. The targeted live
checks exist precisely to close that gap.

**Gate rule satisfied:** D13/D14/D15 all flipped green in this harness after the
routed fixes (independent re-verification; the verifier never edited product
code). The orchestrator canonically re-runs Acceptance.Tests + Release build +
the live wiring proofs before accepting WP-07-04 and driving the ledger to
completed.

**Verifier transparency note:** the final pre-green run showed one red on the
n-gram fact caused by a HARNESS field-name bug (`order` vs the wire field `n`
in the per-URL row assertion), not a product defect; corrected in
`TargetedRemainderTests.cs` and re-verified. One full-suite run also exhibited
a transient ~15-minute duration (build/publish contention while sibling fixes
were landing); the final quiet-machine run completed in 17s. Neither affected
product evidence.

## 5. Offline packaging verification (packaging/)

`verify-packaging.ps1` publishes the REAL composition root in Release and
probes the PUBLISHED layout (report: `packaging/artifacts/packaging-report.json`):

| Check | Result |
|---|---|
| Release publish (nuget.org only) | PASS exit 0 |
| Published executable present | PASS |
| 10/10 asset families bundled (WAF signatures, stop-words, dictionaries, font metrics, tech fingerprints, bot CIDR, agent profiles, ONNX model, export templates, plugin schema) | PASS |
| Chromium rendering runtime local-only | PASS — local Chrome resolved; `LocalBrowserResolver` probes packaged → configured → local install; missing configured path throws locally (no remote fallback) |
| Licensing manifest ABSENT | PASS — 0 manifest files, 0 licensing-shaped assemblies |
| No account-bound update feed | PASS — 0 update/account endpoints in packaged configuration |
| First-launch offline init from published layout | PASS — fresh data dir, migrations + settings defaults, health 200 + settings 200 via local handshake token |
| Electron binary local-only config | PASS — no runtime fetch (see below) |

### Electron allowScripts-blocked resolution (product-owner ratified 2026-11-16)

The npm `electron` wrapper's binary-download postinstall is blocked by
`allowScripts=false`; a runtime fetch would violate offline-first anyway.
Resolution (documented in `packaging/electron/README.md`):

- **Rendering** (the product's Chromium runtime requirement) is satisfied by
  `LocalBrowserResolver` → local Chrome/Edge (both present on the reference
  machine; scale CWV already ran on local Chrome through the production probe).
- **Desktop shell binary**: `ELECTRON_OVERRIDE_DIST_PATH` configuration +
  `packaging/vendor/electron/` user-provided dist drop-in + system-PATH
  fallback; `not-bundled` mode documented (shell unit suites run on fakes; no
  criterion requires launching the Electron binary itself).
- **PRODUCT-OWNER-RESERVED packaging decision:** the Electron dist zip is not
  hosted on the npm registry (wrapper fetches GitHub releases); bundling a dist
  into an installer requires a product-owner-approved binary source. Recorded,
  never silently resolved; no runtime fetch permitted.

## 6. Open scope decisions #1–#5 — PRODUCT-OWNER RESERVED (verbatim)

Per `architecture/07-quality-and-verification/02-open-scope-decisions.md`
(FS-CRAWL-001 v1.4 §15), recorded as unresolved and **not required by this
version**; each must be resolved by the product owner and recorded as an
explicit scope statement either way. NEVER silently resolved by this sign-off:

1. **Unattended/headless execution** — whether audits can be started from a command line without opening any window. (Scheduled audits per section 6 already run without user interaction once configured, and the local MCP interface per section 11 allows programmatic control while the application is running.)
2. **Manual product-update path** — whether a local, user-initiated update installation mechanism is in scope. No update checking of any kind exists in this version.
3. **Third-party search/analytics/backlink data enrichment** — whether to import the user's own search-console, analytics, or backlink-provider data (and optional field-performance data) into reports using the user's own credentials. These are license-independent, user-owned integrations but reintroduce outbound traffic to third parties, so they are deliberately left as an explicit product-owner choice rather than mandated. If adopted, they follow the optional/off-by-default, user-credential, encrypted-at-rest pattern of section 8.5.
4. **AMP analysis** — whether Accelerated Mobile Pages discovery and validation are in scope. Not required by this version.
5. **Raw-SQL administration tool** — whether a direct query interface over the local database is offered as a power-user/debug tool (the interactive query builder per section 8.7 is already specified). Not required by this version.

## 7. Boundary-audit attestation (17 negative requirements)

WP-07-02 (cf-wp-00-05) attested all 17 negative requirements through the real
composition root with process-level TCP monitoring (iphlpapi, no elevation),
process-lifetime assembly-load attribution, and full-tree governance scans:
identity stores never read (NR-01 static 0 + runtime attribution), no
licensing/account/entitlement concepts, no update feed, loopback-only
listeners, zero non-loopback traffic in idle/reporting/first-launch, scoped
audit/destination/external-db traffic, encrypted-at-rest credentials with no
identity derivation, local-only diagnostics, consent-gated DNS, plugin network
default-deny. Evidence: `tools/network-audit/README.md` (25 durable artifacts)
+ `tests/governance/wp-07-02-app-root-scan.json` (636 files / 0 violations /
passed=true). Monitoring limitations documented (UDP/DNS invisible to the TCP
monitor — proven by pre-network consent refusal; ETW kernel tracing requires
elevation — layered assembly-attribution + static proof used instead). The
harness re-verifies the artifact set and the zero-violation scan.

## 8. Deferred amendment candidates & performance observations

Recorded as **observations, NOT criterion failures** (no criterion mandates
throughput numbers; #19's memory bound is met with large headroom):

- **Analysis throughput ~5 URLs/s** (full analyzer suite ≈180ms/URL) is the
  live-crawl scaling bottleneck — the 10k live scale proof completed in ~30min
  at 212MB peak; a 100k fully-analyzed run would take ≈5h. Fetch frontier
  sustains ~22 URLs/s and was verified memory-bounded (185MB peak) before the
  live proof was sized to 10k (option-a ratified).
- **Unindexed LIKE filter ~350ms** on large explorers — candidate index
  amendment for a future performance workpiece.
- **MySQL GET_LOCK first-migration hardening** (optional) — concurrent
  first-migration race guard candidate from WP-07-01.
- **ListMode REST load-flake** — observed once under heavy parallel load in
  WP-07-01 history; did NOT recur in the final consolidated 80/80 run; watch
  item closed.
- **110-route inventory** (88 locked + 22 additive) — envelope conformance
  proven across all routes (E2E `EveryRoute_ReturnsTheUniformEnvelope`).
- **Electron dist binary source** — product-owner-reserved packaging decision
  (§5 above).

## 9. Verification commands & results

```powershell
$env:PATH = "C:\Users\lione\AppData\Local\Microsoft\dotnet;" + $env:PATH
$env:CF_AGENT_ID = 'cf-wp-00-04'   # agent-scoped build; canonical re-run unset
dotnet test E:\Crawlforge\app\tests\CrawlForge.Acceptance.Tests
dotnet build E:\Crawlforge\app\CrawlForge.sln -c Release
```

| Run | Result |
|---|---|
| Acceptance.Tests (agent-scoped CF_AGENT_ID=cf-wp-00-04, final, quiet machine) | **72/72 PASS exit 0** (17s) — 48-criterion evidence-integrity theory + wave-7A artifact re-verification + #40 raw-artifact composite gate + 11 targeted live checks + 6 packaging facts |
| CrawlForge.sln Release build (final) | **exit 0 — 0 warnings, 0 errors** |
| packaging/verify-packaging.ps1 | **PASS 18/18 exit 0** (report: packaging/artifacts/packaging-report.json) |
| Orchestrator canonical context (CF_AGENT_ID unset, quiet machine) | E2E **86/86** exit 0 (incl. 6 new live wiring proofs); Audit.Tests 577/577; Host.Tests 32/32; Intelligence.Tests 341/341; governance-analyzer 639 files / 0 violations — ALL exit 0 |

**Sign-off decision: COMPLETE — all 48 criteria PASS.** D13/D14/D15 RESOLVED +
independently re-verified. Pending only the orchestrator's canonical re-run of
Acceptance.Tests + Release build before accepting WP-07-04 and closing phase-07
(ledger is orchestrator-only).
