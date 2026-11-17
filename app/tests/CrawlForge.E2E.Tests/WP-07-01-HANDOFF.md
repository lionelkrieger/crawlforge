# WP-07-01 — Cross-Module E2E Verification Handoff

Owner: cf-wp-01-01 · Project: `app/tests/CrawlForge.E2E.Tests` (verification-only; no product code edited from this workpiece)
Status: **80 / 80 GREEN — exit 0** (final consolidated run, post-D12; all twelve defect fixes proven).

## How to run

```powershell
$env:PATH = "C:\Users\lione\AppData\Local\Microsoft\dotnet;" + $env:PATH
$env:CF_AGENT_ID = '<your-agent-id>'   # build scoping
dotnet test E:\Crawlforge\app\tests\CrawlForge.E2E.Tests
```

- Every test composes the REAL backend (`CrawlForgeBackend.StartAsync`, full module registration, real Kestrel on 127.0.0.1) against scripted loopback fixture origins. No mocks of product internals; substitution only at locked seams (`IHubClientChannel` recording transport, `IDbProvider` MySQL slot-in via the post-registration configure hook).
- MySQL suites require the live local server `127.0.0.1:3306` (root, empty password, MySQL 8.0.28) and exclusive use of `crawlforge_e2e` (each test DROPs/CREATEs it — separate commands + pool clear). Do not run other agents' MySQL tests against that database concurrently.
- All network I/O stays on loopback; offline proofs assert ZERO origin requests by request count.

## Suite inventory (80 tests)

| Suite | Tests | Coverage |
|---|---|---|
| AimdAdaptationE2ETests | 9 | Additive increase to resolved ceiling; 429/503/TTFB-spike multiplicative decrease; 429 cooldown freeze; Retry-After pause recorded; manual pin suspends adaptation; disabled controller = static politeness; checkpoint survives pause/resume; per-host independence. |
| AuditLifecycleE2ETests | 6 | Unlimited ceilings without overrides; pause/resume checkpoint; cancel retains partial data; crash-recovery restart resumes from checkpoint; scheduled run unattended with history + stable next-due; missed schedule catch-up exactly once. |
| WafChallengeClassificationE2ETests | 7 | 200-OK challenge pages blocked with provider attribution (Cloudflare, DataDome, Akamai, Imperva, PerimeterX); single-facet corroboration guard leaves page unblocked; generic body-only markers → suspected notice, never blocks. |
| WafDetectionReportingE2ETests | 2 | Detection row persisted + REST list; waf-block alert emitted + block metrics. |
| ListModeE2ETests | 8 | List ingestion dedupe/invalid counts, re-ingestion, ListOnly/ListBounded policy, REST start snapshot + fieldErrors validation, 5-tier redirect matrix verdicts/severities/aggregates (DI), revalidation idempotence (D4), REST upload + auto-seed + results on the uploading audit. |
| OfflineReauditE2ETests | 8 | Zero-network re-audit (request-count proof), content comparison, custom extraction/search/JS re-run from stored bodies, WAF copy + block metrics, scoped-word-count, refresh:true re-fetch, live custom-rule persistence (D5, numeric enum payload), REST reaudit refresh:false offline (D2). |
| MySqlParityE2ETests | 4 | Live connection-test verdicts + VERSION() recording; full-audit report parity SQLite↔MySQL (identical metric dimensions incl. urlId); archive switch → MySQL → explicit revert with body fidelity; migration idempotence + dialect purity. |
| SinglePageAnalysisE2ETests | 5 | Analysis without project: no persistence, fetch-once, inventory + findings, ruleKeys filter, 400 fieldErrors.url, additive Cloudflare wafVerdict, 404 no-persist. |
| McpDelegationE2ETests | 8 | Delegated flow initialize→tools/list (8 tools)→start-audit→progress→offline re-audit; action log (transport, ≤512-char JSON summaries, purge); invalid token 401; rotation; missing session 400; dispatch-level failures logged Failed; notifications 202 not logged; stdio header; tool-level failures logged Failed (D7). |
| HubSequencingE2ETests | 6 | Live-audit stage sequencing + exactly-one audit-completed on group audit-{id}; no broadcast leak; crawl-progress latest-wins ≤1/s coalescing; independent per-host throttle keys; connection-gated 2s system-health heartbeat; group-vs-broadcast routing; subscribe/unsubscribe dedupe. |
| RestEnvelopeConformanceE2ETests | 5 | Reflection inventory (110 routes = 88 locked baseline + 22 additive, no duplicates); full-route envelope sweep (data/pagination/error omission, pagination.total, stable error codes, fieldErrors map); every contract enum lowercase-no-hyphen round-trip (only catch-up hyphenated); live wire spot checks; validation fieldErrors map (D8). |
| ExportValidityE2ETests | 7 | Independent readers on downloaded bytes: OpenXml xlsx (dataset sheets + rows), PdfSharp pdf (%PDF + pages), ZIP-of-RFC4180 csv (quote-aware), self-contained HTML (zero external refs); csv/html download labels (D11); export validation envelopes. |
| PluginLifecycleE2ETests | 3 | Local discovery + SHA-256 integrity; enable/disable + grant/revoke; commands/unknown grants → 400; rescan idempotence; vanished-directory removal; malformed-manifest skip; network declarations NEVER become active grants (consent invariant §6.3/6.4/6.7/6.8). |
| AimdHostThrottleE2ETests | 2 | Full-stack 429 pressure: host_throttle_events history (http-429, multiplicative decrease + floor clamp), coalesced typed hub events ≤1/s/host, host-throttle completion metric summary, hosts explorer REST; override validation envelopes. |

## Defect ledger — all twelve driven to fix by this gate

| # | Defect | Evidence (file:line at discovery) | Fix owner | Proof test(s) |
|---|---|---|---|---|
| D1 | Redirect-matrix validator not DI-registered | EngineServicesModule.cs (missing registration) | cf-wp-00-04 | ListModeE2ETests 5-tier matrix |
| D2 | REST re-audit bypassed refresh:false offline path | AuditsController.cs:290-306 | cf-wp-00-04 | OfflineReaudit REST refresh:false |
| D4 | Matrix revalidation duplicated findings | findings insert without delete | cf-wp-00-04 | RedirectMatrix_Revalidation_DoesNotDuplicateFindings |
| D5 | Custom content not persisted on live audits | AuditExecutionEngine PersistCustomContentAsync wiring | cf-wp-00-04 | LiveAudit_CustomExtractionRules_PersistExtractedContent (payload: numeric enums, valueType=0) |
| D6 | MySQL index names > 64 chars | SchemaCatalog.Additive.cs:180/:256 | cf-wp-00-05 | MySqlParity migrations + full audit |
| D7 | MCP tool-level failures logged Success | McpJsonRpcDispatcher.cs:102-106 (result.isError ignored) | cf-wp-00-04 | ToolLevelFailures_AreLoggedAsFailed |
| D8 | Framework ProblemDetails bypassed envelope on 18 routes | [ApiController] auto-validation, no InvalidModelStateResponseFactory | cf-wp-00-04 | EveryRoute_ReturnsTheUniformEnvelope + ValidationErrors_UseTheEnvelopeFieldErrorsMap |
| D9 | All 9 /api/tools/* routes empty 500 | ScopePreviewService unregistered in Host DI | cf-wp-00-04 | EveryRoute sweep (tools routes) |
| D10 | MySQL report reader wrong columns / GetString-on-INT | AuditScoreDto count columns (no JSON column) | cf-wp-00-05 | FullAudit_OnMySql parity IDENTICAL |
| D11 | Export download labels mismatched bytes | ExportsController.cs:188-195 (csv text/csv vs ZIP; html application/zip vs HTML) | cf-wp-00-04 | CsvDownload_LabelsMatchTheZipBytes + HtmlDownload_LabelsMatchHtmlBytes |
| D12 | MySQL streaming export ambient-transaction mismatch | RepositoryOps.cs:98 via ProjectArchiveService.cs:121 (revert export) | cf-wp-00-05 (task-49) | ArchiveSwitch_ToMySql_AndExplicitRevert_ToEmbedded |

Notes:
- Defect numbering follows the orchestrator's ledger; D3 does not appear in the E2E-driven defect set.
- D5 lingering red after the fix was a TEST payload bug ("text" vs numeric enum under JsonSerializerDefaults.Web) — honestly reported, not a product defect.
- D10 numbering follows the lead's ledger (export-label defect originally reported as "D10" was renamed D11 to avoid collision).

## Observations for WP-07-04 sign-off (not defects)

1. **GET_LOCK first-migration hardening (MySQL):** SchemaMigrator is idempotent per instance (semaphore + transactional ledger) and the Host migrates exactly once (ExecutionModule.cs:66). Concurrent FIRST starts of two processes against one fresh MySQL database can race (observed via cross-agent test contention). A MySQL `GET_LOCK` around first migration would make concurrent first-starts safe. Optional hardening, outside the locked single-backend model.
2. **ListMode REST load-flake watch:** `RestStart_ListMode_SnapshotsTheListAndValidatesInput` failed once in a full parallel run ("Audit 1 failed while waiting") and passes in isolation — resource contention under heavy parallel load, not a deterministic regression. Watch in the final consolidated run.
3. **Route surface growth:** 110 controller routes = 88 locked baseline + 22 additive WP-06 growth; the conformance sweep covers ALL of them. Future route additions are caught by the inventory test (≥88 + no duplicates).
4. **Plugin auto-enable:** lead ruling — §6 does not lock disabled-by-default for per-user plugins; auto-enable is permissible. The NETWORK consent invariant is locked and verified (pending-consent, default-deny, CSP).
5. **Plugin wire shapes:** rescan returns `{added, removed, plugins[PluginRegistryEntryWire]}`; list/update use the same entry wire (`enabled`, `state`, `directory`). Backend registry carries read-data/ui-contributions only.

## Final consolidated result

**80 / 80 GREEN — exit code 0** (full suite, 4 m 43 s, post-D12 build, all fixes landed).

<!-- CONSOLIDATED-RESULT-START -->
Run: `dotnet test E:\Crawlforge\app\tests\CrawlForge.E2E.Tests` (agent-scoped build cf-wp-01-01).
Result: Passed! - Failed: 0, Passed: 80, Skipped: 0, Total: 80, Duration: 4 m 43 s. EXIT=0.

- ArchiveSwitch_ToMySql_AndExplicitRevert_ToEmbedded: GREEN after D12 (switch → MySQL report parity IDENTICAL → body fidelity → explicit revert into fresh embedded DB → parity IDENTICAL → bodies survive round trip). Criterion #18 fully re-proven on live MySQL 8.0.28.
- All twelve defect flips green: D1/D2/D4/D5 (list/offline/matrix/custom), D6/D10/D12 (MySQL schema/reader/export), D7 (MCP action log), D8/D9 (envelope/tools activation), D11 (export labels).
- ListMode REST load-flake did NOT recur under full parallel load (watch item closed for this run).
- MySQL window ran with cf-wp-00-05 holding their live suites (crawlforge_e2e exclusive; their D12 proofs used isolated crawlforge_d12).

History: pre-fix full run 71/77 green → focused post-fix verification → final 80/80.
<!-- CONSOLIDATED-RESULT-END -->

## Addendum — defect amendments D13/D14/D15 (task-51, fix owner cf-wp-01-01)

Live wiring gaps found by the WP-07-04 acceptance harness; fixed with additive
composition wiring (analyzers/classifiers unchanged except the D13 live-snapshot
swap), each proven by new live E2E suites:

- **D13 (#13 suppression)**: `UserDictionaryLiveSync` + write-repository decorator
  + startup participant feed persisted `user_dictionary` entries into the
  singleton `ContentQualityAnalyzer`. Proof: `DictionarySuppressionLiveE2ETests` (2).
- **D14 (#38 site)**: `RunSitePassAsync` invoked at the `AnalyzePhaseAsync`
  convergence point (live + offline re-audit); site findings + metrics persisted.
  Proof: `NgramSitePassLiveE2ETests` (2).
- **D15 (#48 zone/equity)**: zone-aware discovery (anchor text, dom_zone,
  evidence, rel flags) + `LinkGraphEnrichmentPass` (target resolution, weighted
  PageRank equity metrics, per-edge equity stamp) + re-audit graph copy/remap.
  Proof: `LinkGraphEquityLiveE2ETests` (2).

Battery: solution build 0/0; Audit 577, Intelligence 341, Host 32, Api 219,
Mcp 87 — all exit 0; governance passed, zero violations; **full E2E 86/86 exit 0**
(quiet machine; one WAF timeout flake under concurrent unit-suite load cleared on
re-run — same load-flake class as the ListMode watch item).