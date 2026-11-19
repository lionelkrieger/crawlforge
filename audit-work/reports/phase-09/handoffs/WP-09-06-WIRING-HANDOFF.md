# WP-09-06 Wiring Handoff — REST/Hub Counter Reconciliation, Lifecycle, G05 Publication, Config Editor Fields, Redirect/Matrix Vocabulary

Builder: cf-wp-09-06 (builder cf-wp-09-02 reused) · Wave 9B · Task-9.
Owned paths touched: `app\src\backend\CrawlForge.Api\Controllers\`, `Api\Hubs\`, `app\tests\CrawlForge.Api.Tests\` ONLY.
No git commits. Conformance project, Host, Crawler, Contracts, frontend untouched.

Verification (cwd `E:\Crawlforge\app`, SDK 10.0.404, `CF_AGENT_ID=cf-wp-09-06`):
- `dotnet test tests\CrawlForge.Api.Tests` → **Passed! 275 / Failed 0 / Skipped 0** (baseline 220 incl. lead's ToolsRouteTests pair; +55 new/updated).
- `dotnet test tests\CrawlForge.ContractConformance.Tests --filter FullyQualifiedName~AuditCounters` → **Passed! 5 / Failed 0 / Skipped 0** (KnownGap `RestCounters_MatchEngineCounters` GREEN from the mapper fix alone; lead unskipped `HubCounters_MatchRestWireKeys` (G-HUB-COUNTERS) mid-session — it passes with the normalizer, see §1).
- `dotnet build src\backend\CrawlForge.Host` → **0 warnings / 0 errors** (live DI safe with all changes; new ports resolve empty until wired).

## 1. D1 — counter-key wire reconciliation (LIVE, no wiring needed)

- `Api\Controllers\AuditWireMapper.cs` `ParseCounters`: producer keys win —
  `queued←discovered`, `fetched←fetched`, `error←failed`, `blocked←blocked` (0 when absent),
  `wafBlocked←wafBlocked` (null when absent). Legacy wire keys still parse (fallback per key).
  `analyzed` is engine-internal (no wire field). Non-numeric values degrade to 0.
- `Api\Hubs\HubPayloadNormalizer.cs` (NEW, public static `Normalize(eventName, payloadJson)`):
  applied by `RealtimeHubService.DispatchAsync` to EVERY dispatched event + the system-health
  heartbeat. Maps `crawl-progress.counters` and `audit-completed.finalCounters` through the SAME
  `AuditWireMapper.ParseCounters` (REST==hub by construction); omits null `wafBlocked` (hub
  all-Number invariant); stringifies entity ids recursively (G-G: `*Id` number→string,
  `*Ids` array elementwise; idempotent; nulls/non-id numbers untouched). Routing/throttle keys
  accept numeric AND string ids (`TryReadEntityId`).
- **G-HUB-COUNTERS closed**: lead unskipped `HubCounters_MatchRestWireKeys` during the session;
  it passes with the normalizer (hub crawl-progress counters == REST wire keys). No action left.
- **Engine emission recipe (WP-09-11) — crawl-progress frozen fields** the normalizer CANNOT
  fabricate (passes through when present). Engine today emits `{auditId, counters(producer keys), queued}`.
  Add, matching `AuditWireMapper.ToDetail` derivations EXACTLY (REST==hub):
  - `phase`: `ApiEnumConverter<AuditPhase>.ToWireValue(audit.Phase)` vocabulary (same as REST detail `phase`).
  - `elapsedSeconds`: `Math.Max(0, ((FinishedAt ?? UtcNow) - StartedAt).TotalSeconds)`, rounded 3.
  - `throughputPerSecond`: `counters.fetched / elapsedSeconds` (0 when elapsed 0), rounded 3.
  - `estimatedRemainderSeconds`: `counters.discovered / throughputPerSecond` when discovered>0 && throughput>0 else null, rounded 3. (REST uses QUEUED=discovered, not frontier remainder — keep identical.)
  - Optional additive: `wafBlocked`, `throttledHostCount`, `meanEffectiveRate`, `matrixValidated`, `matrixTotal`.
- **Engine blocked/wafBlocked counters recipe**: `AuditRunCounters.ToPayload()` should add
  `blocked` = count of url_records with `StatusClass=Blocked` for the audit, `wafBlocked` = subset
  with `WafProvider != null` (derive at checkpoint/finalize from the same rows the engine already
  writes). Until emitted, REST/hub carry `blocked:0` / `wafBlocked` omitted — conformant.

## 2. D2 — lifecycle ports (Host wiring REQUIRED to close the gaps)

New Api-owned additive ports in `Api\Controllers\AuditsController.cs` (precedent:
`IReAuditExecutionPort`; injected as `IEnumerable<T>` → empty until Host registers; live Host
compiles and runs the legacy fallback today):

```csharp
public interface IAuditLifecycleExecutionPort
{
    Task<AuditLifecycleTransition> PauseAsync(long auditId, CancellationToken ct = default);
    Task<AuditLifecycleTransition> ResumeAsync(long auditId, CancellationToken ct = default);
    Task<AuditLifecycleTransition> CancelAsync(long auditId, CancellationToken ct = default);
}
public record AuditLifecycleTransition(long AuditId, AuditStatus Status, AuditPhase Phase);
// Invalid transitions throw InvalidOperationException → REST 409 invalid-state.

public interface IListAuditStartPort
{
    Task<long> StartListAuditAsync(long projectId, long configurationId, ScheduleTriggerType trigger,
        CrawlLimitOverrides? limitOverrides, string? urlListJson, bool isListMode, CancellationToken ct = default);
}
public sealed class AuditStartFailedException(long auditId, string message) : Exception { public long AuditId { get; } }
```

REST behavior (LIVE):
- pause/resume/cancel delegate to the port when registered; otherwise validated status writes
  + REST-side `stage-changed` `{auditId, previousState, newState}` (state wire vocabulary).
  Delegated path: REST does NOT publish stage-changed (engine owns emission — no double events).
- List-mode start uses `IListAuditStartPort` when registered (**G10**: payload persisted
  atomically with row creation BEFORE engine observation); fallback keeps the legacy
  start-then-persist race until wired. `AuditStartFailedException` → REST publishes
  `audit-failed` `{auditId, errorCode:"start-failed", message}` + 500 internal envelope.
- REST cancel stays stricter (Running|Paused) than engine (also Queued) — locked by tests.

**Host wiring recipes (WP-09-11):**
1. `IAuditLifecycleExecutionPort` adapter over the engine (engine already implements Mcp
   `IAuditLifecyclePort` with IDENTICAL signatures returning `AuditLifecycleSnapshot(AuditId, Status, Phase)`):
   ```csharp
   services.AddSingleton<CrawlForge.Api.Controllers.IAuditLifecycleExecutionPort>(sp =>
       new LifecyclePortAdapter(sp.GetRequiredService<CrawlForge.Mcp...IAuditLifecyclePort>()));
   // adapter: PauseAsync → map snapshot → AuditLifecycleTransition; same for Resume/Cancel.
   ```
   Engine `ResumeAsync` already rehydrates via `Task.Run(RunAuditAsync)` when `_runs` is missing
   (crash recovery) — REST status writes alone cannot do this; the port closes the D2 gap.
2. `IListAuditStartPort` implementation in the engine: same body as `StartAuditAsync` but the
   `AuditDto` row is created with `IsListMode`/`UrlListJson` set IN THE SAME `CreateAsync`
   (before `Task.Run(RunAuditAsync)`); any post-creation pipeline failure throws
   `AuditStartFailedException(auditId, message)`.
3. **Engine stage-changed emission gaps**: pause is published by the loop
   (`PublishStageAsync(paused)`); RESUME observation (`ObserveStatusAsync` Running return) and
   `ResumeAsync` rehydration are SILENT, and cancel via `FinalizeAsync` publishes
   audit-completed/failed but no stage-changed. Add `stage-changed {auditId, previousState,
   newState}` (string ids after normalizer) at: resume observation, ResumeAsync rehydration,
   cancel transition. Payload shape locked by `AuditLifecycleRouteTests` (legacy path).
4. G10 note: `ListSeedResolver.ResolveForAuditAsync` reads the row — with the port, the list is
   already there at first observation (regression test proves ordering via the fake's
   in-start observation capture).

## 3. D3 — G05 publication surface (engine call sites = lead wiring)

`Api\Hubs\AuditRunEventPublisher.cs` (NEW; mirrors accepted `ExportEventPublisher` pattern):

```csharp
public sealed class AuditRunEventPublisher(IRealtimeHub hub)  // ctor: ArgumentNullException on null
{
    Task PublishUrlBatchAsync(long auditId, IReadOnlyList<UrlBatchEntry> entries, CancellationToken ct = default);
    Task PublishFindingsUpdatedAsync(long auditId, IReadOnlyDictionary<string,int> severityDeltas,
        IReadOnlyDictionary<string,int> categoryDeltas, CancellationToken ct = default);
}
public sealed record UrlBatchEntry(string UrlId, string CanonicalUrl, string StatusClass,
    string? BlockReason = null, string? WafProvider = null);
```

- Wire shapes (frozen events.ts): url-batch `{auditId:string, urls:[{urlId:string, canonicalUrl,
  statusClass, blockReason?, wafProvider?}]}`; findings-updated `{auditId:string,
  severityDeltas:{key:int}, categoryDeltas:{key:int}}`. camelCase, null annotations omitted,
  string EntityIds at the source. `statusClass` validated ∈ {1xx,2xx,3xx,4xx,5xx}
  (ArgumentException otherwise); blocked entries carry the OBSERVED class + blockReason/wafProvider.
- **LOSSLESS delivery decision (interpretation, loop §8)**: both events publish with explicit
  `ThrottleInterval = TimeSpan.Zero` → bypass latest-wins coalescing. Coalescing url-batch would
  drop whole batches; coalescing incremental deltas would corrupt frontend counters. Cadence
  (≤1/s per audit, FS §5.7) is the PRODUCER's responsibility: engine accumulates and flushes.
- **DI**: `services.AddSingleton<AuditRunEventPublisher>();` (EngineServicesModule; IRealtimeHub
  already registered :439).
- **Call sites**: url-batch at the engine's URL-record batch flush (after url_records upserts;
  entries from the flushed rows: Id.ToString(), CanonicalUrl, StatusClassWire, BlockReason,
  WafProvider wire key); findings-updated at the finding-emit point (CrawlFindingRecorder /
  FindingEmitter flush) with deltas since the previous emission, severity/category WIRE keys
  (ApiEnumConverter vocabulary — same as REST findings).
- Hub routing: both events are in `AuditScopedEvents` → per-audit group `audit-{id}` ✓ tested
  end-to-end through the real RealtimeHubService.

## 4. D3 — configuration editor additive fields (WP-09-07 §3; LIVE)

- `AuditConfigurationWire` / `ConfigurationCreateRequest` / `ConfigurationUpdateRequest` gained
  optional `ScopeRules` (JsonElement?) + `EnabledCategories` (IReadOnlyList<string>?) — additive,
  source-compatible. Wire: `scopeRules` (object|null), `enabledCategories` (string[]|null = all).
- **Storage decision (documented)**: both ride `crawl_configurations.scope_rules_json` —
  editor sections verbatim (camelCase) + `enabledCategories` array member (canonical PascalCase
  AuditCategory names). No schema change. GET projects the WHOLE document as `scopeRules`
  (behavior keys included) and `enabledCategories` from the same document.
- **Merge rules**: shallow top-level merge. Behavior keys (description/respectRobotsTxt/
  virtualRobotsTxt/maxResponseBytes/rendering) stay owned by explicit request fields; on UPDATE
  explicit deltas apply AFTER the merge (win); on CREATE `respectRobotsTxt` falls back to the
  document value when the request omits the flag (otherwise default true). Editor sections
  replaced wholesale per top-level key; untouched sections survive.
- **Validation**: merged document through `CrawlScopeConfigParser.Parse` — hard Errors (invalid
  regexes in scope.includePatterns/excludePatterns, non-positive userAgentComparison.sampleSize)
  → 400 validation envelope, fieldErrors key `scopeRules` (joined reasons). Unknown category
  names → 400 fieldErrors `enabledCategories`. Input categories case-insensitive, deduplicated,
  canonicalized to PascalCase.
- **PUT enabledCategories semantics**: non-null replaces (empty array = all → member removed);
  null/omitted = unchanged. POST null/omitted = all.
- **Snapshot freeze (AuditsController)**: config-level `enabledCategories` WINS over the
  app-settings `AuditTogglesJson` fallback (`ResolveEnabledCategoriesAsync(configuration, ct)`);
  full set ≡ null; absent config preference → existing app-settings behavior. Frozen convention
  preserved: snapshot OMITS `enabledCategories` when all enabled (null projection removed).
- **Engine consumption (lead wiring)**: parse `configuration.ScopeRulesJson` with
  `CrawlScopeConfigParser.Parse` (total/tolerant; Errors already rejected at config time) and
  feed the options into canonicalization/scope/directives/resources/cookieJar/UA/headers/cookies;
  snapshot `enabledCategories` already consumed by the scoring pipeline convention.

## 5. D4 — redirect chain + matrix verdict vocabulary (LIVE)

- `AuditExplorerController.GetUrlDetailAsync`: persisted `redirect-chain` element
  (`RedirectChainElementGroup.GroupName`, WP-09-01) is the source of truth —
  `RedirectChainJson.DeserializePayload` (tolerant) → hops ordered by Ordinal →
  `RedirectHopWire(h.Url, h.StatusCode, h.ToUrl)` lossless. Legacy single-hop
  `RedirectTargetUrl` fallback kept for pre-wiring rows (and malformed payloads).
  Engine hookup remains WP-09-01 handoff §1 recipe (WP-09-11).
- `RedirectMatrixController`: verdict wire = `MatrixVerdictNames.Of` (was
  `ApiEnumConverter<RedirectMatrixVerdict>` lowercase-no-separator); filter parses via
  `MatrixVerdictNames.Parse` (case-insensitive input). Vocabulary:
  **pass / target-mismatch / chain-exceeded / final-non-200 / canonical-mismatch**.
  REST == hub (`MatrixRowValidatedItem.Verdict` already uses MatrixVerdictNames) == metric
  dimensions. **Lead check**: metric_summaries key `redirect-matrix` dimensions must be
  `MatrixVerdictNames.Of(verdict)` when the engine aggregates `MatrixVerdictCount` (no Host
  emission found yet — arrives with matrix wiring).
- Old wire value `targetmismatch` is GONE (updated the one test that locked it; unknown-verdict
  filter now 400s on it — frozen vocabulary only).

## 6. Contract-test payloads for lead's suites

- **AuditLifecycleConformanceTests**: REST `POST /api/audits/{id}/pause|resume|cancel` →
  `{data:{auditId:string, state:"paused"|"running"|"cancelled"}}`; invalid → 409
  `{error:{code:"invalid-state"}}`; unknown id → 404. Hub `stage-changed`
  `{auditId:string, previousState, newState}` (state vocabulary: queued/running/paused/completed/cancelled/failed).
  Hub `audit-failed` `{auditId:string, errorCode:"start-failed"|"audit-failed", message}`.
  Crash recovery: Paused row + latest `crawl_checkpoints` row (SerializedFrontier/CountersJson/
  AimdStateJson) → resume rehydrates (engine port) → Running.
- **RunHubEventConformanceTests**: `crawl-progress` normalized `{auditId:string,
  counters:{queued,fetched,error,blocked[,wafBlocked]}, ...producer extras passthrough}`;
  `url-batch` / `findings-updated` shapes §3 (string ids, lossless); `audit-completed`
  `{auditId:string, finalCounters:{queued,fetched,error,blocked[,wafBlocked]}, reportReady}`.
- **ConfigurationCrudConformanceTests**: POST/PUT/GET shapes §4; GET `scopeRules` contains
  behavior keys + editor sections + `enabledCategories`; `aimd` round-trips through
  `politeness_json {"aimd":{initialRateR0,alpha,beta,kappa,ewmaTtfbWeight}}`; invalid regex →
  400 fieldErrors.scopeRules; unknown category → 400 fieldErrors.enabledCategories.

## 7. Files (owned paths only)

Production: `Api\Controllers\AuditWireMapper.cs`, `Api\Controllers\AuditsController.cs`,
`Api\Controllers\ConfigurationsController.cs`, `Api\Controllers\ProjectWireContracts.cs`,
`Api\Controllers\ProjectWireMapper.cs`, `Api\Controllers\AuditExplorerController.cs`,
`Api\Controllers\RedirectMatrixController.cs`, `Api\Hubs\HubPayloadNormalizer.cs` (NEW),
`Api\Hubs\RealtimeHubService.cs`, `Api\Hubs\AuditRunEventPublisher.cs` (NEW).
Tests: `Controllers\AuditCounterWireRouteTests.cs` (NEW), `Controllers\AuditLifecycleRouteTests.cs` (NEW),
`Controllers\ConfigurationCrudRouteTests.cs` (NEW), `Controllers\RedirectChainAndMatrixVocabularyRouteTests.cs` (NEW),
`Hubs\HubPayloadNormalizerTests.cs` (NEW), `Hubs\AuditRunEventPublisherTests.cs` (NEW),
`Controllers\ControllerTestHost.cs`, `Controllers\ControllerDoubles.cs`,
`Controllers\AuditRouteTests.cs`, `Controllers\AuditExplorerRouteTests.cs`,
`Controllers\RedirectMatrixSemanticsRouteTests.cs`, `Hubs\RealtimeHubServiceTests.cs`,
`Hubs\HubIntegrationTests.cs`.

## 8. Interpretations / ambiguities (loop §8)

1. G05 lossless delivery (zero throttle interval) + producer-side cadence — chosen over
   latest-wins coalescing because batch/delta loss corrupts the shell (§3 rationale).
2. `enabledCategories` stored inside ScopeRulesJson (no column migration); PUT null=unchanged,
   empty array=all — documented because nullable members cannot distinguish absent vs explicit null.
3. Behavior keys owned by explicit request fields; respectRobotsTxt document fallback on CREATE
   only when the request omits the flag (preserves the existing default-true behavior otherwise).
4. Snapshot keeps "absent = all enabled" (null projection removed) — existing route test locked it.
5. Harness drift found & reported (lead fixed): export-delivery matcher field name (WP-09-10 G-F
   producer shape) + log-import matcher string tolerance (G-G). No conformance files edited by me.
6. Engine-side emission points (crawl-progress fields, resume/cancel stage-changed, blocked
   counters, G05 call sites, metric dimensions) are Host/Crawler-owned → recipes above, not edited.
