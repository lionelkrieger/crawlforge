# WP-10-02 report — Capture modes + response-vs-rendered comparison (wave-10A)

Builder: `cf-wp-10-02` · Phase 10 · Workpiece `WP-10-02` (reopens WP-03-02) ·
Capability `CAP-A-X09` · Criteria 50/51/52 · Spec §5.6.1 (CR-005), §7.18 (CR-009),
§7.9/§7.17, §7.19 · Contracts re-locked at `2cb4311` + `0307fe8` (coded against,
never edited).

> **Verification status:** Audit lane GREEN via the official filtered command
> (25/25). Crawler CaptureModes GREEN (48/48) — proven via a private scratch
> project compiling the identical owned test files against the identical source
> projects, because the shared `CrawlForge.Crawler.Tests` project is transiently
> broken by WP-10-01's in-progress `Rendering\Cdp\NetworkWaterfallRecorderTests.cs`
> (CS9007 raw-string syntax errors; not this lane). The official Crawler filtered
> command will pass unchanged once WP-10-01's test file compiles. See §4 and §6.1.

## 1. Files created / changed (owned paths only)

### Production — `app/src/backend/CrawlForge.Crawler/Capture/` (new folder)
| File | Purpose |
|---|---|
| `CaptureModeContracts.cs` | `ICaptureModeCoordinator`, `ICaptureModeCoordinatorFactory`, `RenderedCaptureRequest`, `RenderedCaptureOutcome`, `CaptureDecision`, `RenderedLinkCandidate`, `MaterialChangeFact`, `CaptureSnippetDefinition`, `CaptureSnippetOutcome`, `ICaptureSnippetExecutor`, `CaptureDefaults`. The exact engine-facing surface. |
| `CaptureModeCoordinator.cs` | Mode dispatch (non-JS/JS/dual/adaptive), manual per-pattern overrides (win over adaptive, apply on every mode), adaptive sampling + material-change group switching, render-failure isolation, RenderedDom/NetworkWaterfall/Screenshot artifact persistence, per-URL `CaptureSource` stamping, dual-storage WARNING (never a cap), audit-level finding accumulation. Plus `CaptureModeCoordinatorFactory`. |
| `UrlPathPatternGrouping.cs` | Deterministic path-pattern group key + glob override matching (documented semantics; §5.6.1 leaves matching to the engine). |
| `CaptureDocumentFacts.cs` | Dependency-free DOM facts for crawl-time material change, rendered-only link discovery, service-worker fact (mirrors the Audit comparer; conformance-tested for agreement). |
| `CaptureSnippetExecutor.cs` | §7.17 capture-time snippet execution: raw/rendered/both source selection over the locked `IJsSnippetRunner` (isolated Jint wrapper), rendering-gate bridge, outer timeout isolation, no silent raw fallback. |
| `CaptureRuleKeys.cs` | Builder-local `render.dual-storage-warning` key (open taxonomy; promotion requested in §5). |

### Production — `app/src/backend/CrawlForge.Audit/Indexability/`
| File | Purpose |
|---|---|
| `ResponseRenderedComparer.cs` (rewritten) | Replaces the WP-03-02 stub behind the locked `IResponseRenderedComparer`. §7.18 severities: title/canonical/meta-robots/hreflang = **Error** (high); one-sided noindex EITHER direction = **Error** (critical, top of locked vocabulary) with **dual evidence**; general metadata/status = Warning; text/links/structured-data/resources = Notice; service-worker = Notice (bypassed). Uses locked `FindingRuleKeys`. Text evidence carries `words`/`totalWordCount` (rendered) + raw/rendered counts + change fraction; links evidence carries `renderedOnlyLinks`/`rawOnlyLinks`. |
| `RenderedComparison/RenderedDocumentInspection.cs` (new) | Pure directive facts (canonical/meta-robots/hreflang signatures, meta + X-Robots-Tag noindex) and service-worker detection over the contract DOM; offline-reproducible on re-audit. |

### Tests — `app/tests/CrawlForge.Crawler.Tests/CaptureModes/` (new folder)
| File | Purpose |
|---|---|
| `CaptureModeConformanceTests.cs` | **Contract test (YAML).** Locked wire tokens; null→non-JS default; per-mode dispatch; adaptive sampling (default 4, clamp 3–5) + material switching + `content.javascript-dependent-template`; configurable word-count threshold; overrides win over adaptive/every mode; dual-storage warning (no cap); real-SQLite `CaptureSource` stamping; both-capture storage. |
| `RenderedCaptureComponentTests.cs` | Render-failure isolation; JS-only link candidates; service-worker fact; CR-006 waterfall artifact from the render layer; capture-time snippets. |
| `CaptureSnippetExecutorTests.cs` | Source selection, rendering gate, outer-timeout isolation, runner-failure isolation, no-raw-fallback. |
| `UrlPathPatternGroupingTests.cs` | Group key + glob matching semantics. |
| `TestSupport/CaptureTestStack.cs` | Real SQLite (v5) + real `CompressedContentStore` + locked repositories. |
| `TestSupport/CaptureTestDoubles.cs` | Fake `IRenderingPool`, registered-parser double, recording snippet runner. |
| `TestSupport/CaptureDomBuilder.cs` | Contract-DOM builder (Crawler tests do not reference Audit). |

### Tests — `app/tests/CrawlForge.Audit.Tests/RenderedComparison/` (new folder)
| File | Purpose |
|---|---|
| `RawRenderedDiffFindingTests.cs` | **Contract test (YAML).** §7.18 severities + evidence with the REAL AngleSharp parser + REAL comparer: title/canonical/meta-robots/hreflang Error; rendered-only AND raw-only noindex critical with dual evidence; header-noindex removal; service-worker informational; text word-count evidence ≥100; rendered-only links; determinism. |
| `RenderedDocumentConformanceTests.cs` | **Contract test (YAML).** Real producer→consumer: coordinator (fake pool, real parser/store/SQLite) persists RenderedDom; reload mirrors `BuildStoredContextAsync`; REAL comparer + REAL `AuditPipeline` emit the deltas; rendered `totalWordCount` reaches the `content_scope` group (the harness `ev.words` source); JS-only target discovered-by-render; service-worker through the pipeline; non-JS produces no rendered row. |
| `CaptureSnippetSourceTests.cs` | Real `JintSnippetRunner` + executor: raw/rendered/both distinct results; per-snippet timeout isolation; no backend surface. |
| `TestSupport/RenderedComparisonSupport.cs` | Fixture HTML copied from `fixture-site/pages.mjs` + post-render snapshots; fake pool; context factory; empty rule host. |
| `TestSupport/RenderedCaptureStack.cs` | Real SQLite + content store + repositories. |

No files outside the owned paths were modified. Contracts, architecture,
buildplan, register, CODEMAP, the solution, DI/composition, `Host\Execution\`,
`Audit\Pipeline\`, frontend contracts and other builders' lanes are untouched.

## 2. Deliverable-by-deliverable

1. **Non-JS fetches only; JS renders every in-scope page; dual stores BOTH +
   compares EVERY URL; adaptive samples 3–5/group (default 4) and switches on
   material change; manual overrides WIN.** → `CaptureModeCoordinator.Decide`/
   `CaptureAsync`. Non-JS never touches the pool; JS renders every URL
   (`CaptureSource.Rendered`); Dual renders every URL (`CaptureSource.Both`,
   `MaterialChange` computed for every URL, both artifacts stored); Adaptive
   samples `Math.Clamp(settings.AdaptiveSampleSizePerGroup, 3, 5)` per
   `UrlPathPatternGrouping.GroupKey`, switches a group on material change and
   renders the rest (`adaptive:switched`). Overrides are consulted first for
   every mode and beat adaptive switching. Proven by
   `CaptureModeConformanceTests` (15 tests incl. 3 clamp theories).
2. **Material change = metadata/link diff OR word-count >20% (configurable);
   switch → `content.javascript-dependent-template`.** →
   `CaptureModeCoordinator.EvaluateMaterialChange`: title/canonical/meta-robots/
   hreflang difference, different normalized link set, or
   `|rendered−raw|/raw > threshold` (zero→nonzero always material). Threshold =
   `CaptureSettings.MaterialChangeWordCountThreshold` (default 0.20). Switch
   emits the locked key with group/sample evidence (Warning, Content). Proven by
   the adaptive tests + the configurable-threshold test.
3. **Dual storage WARNING above 1 GiB default — NEVER a hard cap.** →
   coordinator accumulates raw+rendered stored bytes in Dual mode; one Warning
   finding (`render.dual-storage-warning`, builder-local key, promotion
   requested) with `hardCap:false` evidence; rendering continues past the
   threshold. Proven by `DualStorageAboveThreshold_WarnsOnce_NeverCaps`.
4. **Feed rendered documents to analysis; fix G03.** → coordinator persists the
   `RenderedDom` artifact; §5 handoff gives the exact `BuildStoredContextAsync`
   change to load it into `PageAuditContext.RenderedDocument` (6th arg). The
   pipeline ALREADY invokes `IResponseRenderedComparer` when present;
   `RenderedDocumentConformanceTests` proves the real pipeline emits the deltas
   and the rendered word count reaches `scopeDiagnostics.totalWordCount`.
5. **JS-only links recorded as discovered-by-render.** →
   `RenderedCaptureOutcome.RenderedOnlyLinks` (absolute, anchor text, nofollow),
   normalized against the raw link set; §5 handoff enqueues them with
   `DiscoverySource.Render`. Proven by `JsOnlyLinks_…` +
   `JsOnlyLink_DiscoveredByRender_…`.
6. **Raw vs rendered word counts reported.** → comparer `render.text-delta`
   evidence carries `rawWordCount`, `renderedWordCount`, `words`,
   `totalWordCount`, `wordCountDelta`, `wordCountChangeFraction`. Proven by
   `TextDelta_EvidenceCarriesRenderedWordCount` (fixture js-content: raw 3 →
   rendered 203).
7. **Title/canonical/meta-robots/hreflang diffs = high (Error); rendered-only
   noindex EITHER direction = CRITICAL with dual evidence.** → comparer uses the
   locked `render.title-delta`/`render.canonical-delta`/`render.meta-robots-delta`
   keys at Error; hreflang rides `render.metadata-delta` (field `hreflang`) at
   Error (dedicated key promotion requested); `render.noindex-delta` at Error
   with `critical:true`, `direction`, raw+rendered directive state (meta AND
   X-Robots-Tag). Proven by `RawRenderedDiffFindingTests` (both directions +
   header case).
8. **Service-worker → `render.service-worker-registered` informational; renders
   bypass it.** → comparer detects `navigator.serviceWorker.register(...)` in
   either capture, emits Notice with `bypassed:true`, `scriptUrl`, `observedIn`.
   Coordinator exposes the fact on the outcome. The render layer bypasses SW
   (WP-10-01). Proven by `ServiceWorkerRegistration_…` +
   `ServiceWorkerPage_InformationalFinding_ThroughPipeline`.
9. **Custom JS snippets during rendering with timeout + isolated wrapper;
   extraction source raw/rendered/both.** → `CaptureSnippetExecutor` over the
   locked `IJsSnippetRunner` (Jint: no CLR/network, scoped `cf.*` API,
   per-snippet timeout + statement/memory bounds) + outer execution bound;
   source selection builds the right document view; rendered source never
   silently falls back to raw. Coordinator runs snippets during capture. Proven
   by `CaptureSnippetExecutorTests` + `CaptureSnippetSourceTests` (real Jint).
10. **Per-URL capture-source marking.** → coordinator stamps
    `UrlRecordDto.CaptureSource` (Raw/Rendered/Both) through the real repository;
    baseline non-JS keeps the documented null (=raw) sentinel. Proven against
    real SQLite in `CaptureModeConformanceTests`.

## 3. Verification commands

Run from `E:\Crawlforge\app` with `CF_AGENT_ID=cf-wp-10-02` (own test projects,
filtered to owned folders only — never the whole solution):

```powershell
$env:CF_AGENT_ID='cf-wp-10-02'
dotnet test E:\Crawlforge\app\tests\CrawlForge.Audit.Tests --filter FullyQualifiedName~RenderedComparison
dotnet test E:\Crawlforge\app\tests\CrawlForge.Crawler.Tests --filter FullyQualifiedName~CaptureModes
```

SDK confirmed `10.0.404` (`dotnet --version` inside `app`).

## 4. Verification output

### 4.1 Audit — official filtered command (GREEN)
```
dotnet test E:\Crawlforge\app\tests\CrawlForge.Audit.Tests --filter FullyQualifiedName~RenderedComparison
Passed!  - Failed: 0, Passed: 25, Skipped: 0, Total: 25, Duration: 571 ms - CrawlForge.Audit.Tests.dll (net10.0)
```
Covers `RawRenderedDiffFindingTests` (15), `RenderedDocumentConformanceTests` (5),
`CaptureSnippetSourceTests` (5). Real AngleSharp parser + real comparer + real
`AuditPipeline` + real `JintSnippetRunner` + real SQLite/content store.

### 4.2 Crawler source — builds clean
```
dotnet build E:\Crawlforge\app\src\backend\CrawlForge.Crawler\CrawlForge.Crawler.csproj
Build succeeded.  0 Warning(s)  0 Error(s)
```
(`CrawlForge.Audit` also builds clean: 0 warnings / 0 errors.)

### 4.3 Crawler CaptureModes — GREEN via private scratch (official command gated, §6.1)
The official command
`dotnet test E:\Crawlforge\app\tests\CrawlForge.Crawler.Tests --filter FullyQualifiedName~CaptureModes`
currently fails to COMPILE the shared test project because WP-10-01's
`Rendering\Cdp\NetworkWaterfallRecorderTests.cs(176/285)` has CS9007 raw-string
syntax errors (their lane). No `CaptureModes` file has any compile error.

To verify the owned tests independently of that broken file, a private scratch
project (`%LOCALAPPDATA%\CrawlForge-cf-wp-10-02\scratch-capture\ScratchCaptureTests.csproj`,
OUTSIDE the repo, not a deliverable) compiles the IDENTICAL owned
`CaptureModes\**\*.cs` files against the IDENTICAL source projects (Crawler,
Contracts, Security, Data.*):
```
dotnet test "C:\Users\lione\AppData\Local\CrawlForge-cf-wp-10-02\scratch-capture\ScratchCaptureTests.csproj"
Passed!  - Failed: 0, Passed: 48, Skipped: 0, Total: 48, Duration: 562 ms - ScratchCaptureTests.dll (net10.0)
```
Covers `CaptureModeConformanceTests` (15 incl. 3 clamp theories),
`RenderedCaptureComponentTests` (6), `CaptureSnippetExecutorTests` (7),
`UrlPathPatternGroupingTests` (20 incl. theories). The official filtered command
runs these same 48 tests unchanged once WP-10-01's test file compiles.

### 4.4 Defects found and fixed during verification
- `UrlPathPatternGrouping.GroupKey("/blog/")` returned `/*` instead of `/blog/*`
  (trailing-slash directory mis-grouped). Fixed: a directory path groups with its
  children (`/blog/` and `/blog/post-1` → `/blog/*`). Caught by
  `GroupKey_ReplacesFinalSegment` + `GroupKey_SharedDirectoryAndChildren_GroupTogether`.
- Shared-fixture path collision: `RenderedOverride_AppliesOnTopOfNonJsMode`
  reused `/blog/post-1` (already created by the adaptive-sampling test in the same
  audit). Fixed to a unique `/rendered-override/post-1`.
- Snippet tests initially assumed a `document` DOM object; the real
  `JintSnippetRunner` API is column-major `cf.submit(column, value)` over
  `cf.html`/`cf.text`/`cf.url`. Rewrote against the real API (no production change).
- Test-support accessibility/using fixes (public fixtures for `IClassFixture`,
  `CrawlForge.Contracts.Crawl` import, nullable `GetByIdAsync` results).

## 5. WIRING HANDOFF (coordinator applies; nothing here was edited by this builder)

### 5.1 DI registrations (`Host/Composition/EngineServicesModule.cs`, `RegisterAudit`)
```csharp
// Capture-time snippet executor (wraps the already-registered IJsSnippetRunner).
services.AddSingleton<ICaptureSnippetExecutor>(p =>
    new CaptureSnippetExecutor(p.GetRequiredService<IJsSnippetRunner>()));

// Per-audit capture coordinator factory. IRenderingPool is the WP-10-01 CDP
// pool (registered there); ITolerantDomParser/IContentStore already registered.
services.AddSingleton<ICaptureModeCoordinatorFactory>(p =>
    new CaptureModeCoordinatorFactory(
        p.GetRequiredService<IRenderingPool>(),
        p.GetRequiredService<ITolerantDomParser>(),
        p.GetRequiredService<IContentStore>(),
        p.GetRequiredService<IReadRepository<UrlRecordDto>>(),
        p.GetRequiredService<IWriteRepository<UrlRecordDto>>(),
        p.GetRequiredService<ICaptureSnippetExecutor>()));
```
Namespaces: `CrawlForge.Crawler.Capture`. The existing
`services.AddSingleton<IResponseRenderedComparer>(_ => new ResponseRenderedComparer())`
(`EngineServicesModule.cs:148`) now resolves to the rewritten comparer — no
registration change needed.

### 5.2 Engine call site — `AuditExecutionEngine.CrawlPhaseAsync`
Create one coordinator per audit at crawl start (after the configuration
snapshot is loaded, near the frontier setup):
```csharp
var capture = _captureFactory.Create(configuration); // ICaptureModeCoordinatorFactory injected
```
Insert AFTER raw link discovery (the `DiscoverLinksAsync` block, ~:1363–1369),
inside the `document is not null && !ctx.Directives.CrawlNoindexPages …` region
— i.e. for every successfully fetched in-scope HTML URL:
```csharp
if (document is not null)
{
    var outcome = await capture.CaptureAsync(new RenderedCaptureRequest(
        auditId, urlId, urlRecord.CanonicalUrl, urlRecord.Path, entry.Depth,
        document, bodyBytes?.Length ?? 0, configuration,
        CaptureDefaults.DesktopDevice /* or the parsed rendering device profile */,
        InfiniteScroll: /* rendering profile */, CaptureConsole: true, Stealth: /* profile */,
        Snippets: /* optional capture-time snippet definitions */), ct);

    // JS-only links → discovered-by-render (see 5.3).
    // Per-URL capture findings (currently none; SW finding is emitted by the
    // comparer at analysis time): await RecordCrawlDraftsAsync(auditId, projectId, outcome.Findings, ct);
}
```
At crawl end (before `RunTrapSitePassesAsync`, ~:1408), drain audit-level
findings (adaptive template switches + dual-storage warning):
```csharp
await RecordCrawlDraftsAsync(auditId, projectId, capture.DrainAuditFindings(), ct);
```
`RecordCrawlDraftsAsync` already stamps project suppressions and persists
through `CrawlFindings.RecordAsync` — the template-switch and storage-warning
findings flow through the accepted surface unchanged.

### 5.3 JS-only links as discovered-by-render
`DiscoverLinksAsync` (~:2495) hardcodes `DiscoverySource.Link`. Add an optional
parameter `DiscoverySource discoverySource = DiscoverySource.Link` and use it in
the two `FrontierEntry` constructions (:2559 external, :2576 enqueue). Then,
after the raw discovery call, run rendered discovery for the rendered document:
```csharp
if (outcome.Rendered && outcome.RenderedDocument is not null)
{
    await DiscoverLinksAsync(auditId, urlId, entry, outcome.RenderedDocument,
        seen, frontier, state, maxDepth, ctx, ct, DiscoverySource.Render);
}
```
The existing scope/canonicalizer/nofollow/depth machinery applies unchanged;
`seen` dedupes against raw-discovered URLs so only genuinely render-only targets
become `DiscoverySource.Render` rows. (Alternatively enqueue
`outcome.RenderedOnlyLinks` directly through the same scope gate; the
`DiscoverLinksAsync` overload is preferred because it also records zoned
`link_edges` for rendered anchors.) The URL row's `DiscoverySource` is already
persisted by `GetOrCreateUrlRowAsync`/`NewUrlRecord`; the wire field
`discoverySource` is live (WP-10-A1).

### 5.4 Rendered document reaches analysis — `BuildStoredContextAsync` (~:2665)
Today it loads only the `Body` artifact and passes `null` as the 6th argument
(:2699). Load the `RenderedDom` artifact the coordinator persisted and parse it:
```csharp
var renderedDom = artifacts.Items.FirstOrDefault(a => a.Kind == ContentArtifactKind.RenderedDom);
DomDocument? renderedDocument = null;
if (renderedDom is not null)
{
    await using var rs = await _contentStore.OpenReadAsync(renderedDom.StorageKey, ct);
    if (rs is not null)
    {
        using var rm = new MemoryStream();
        await rs.CopyToAsync(rm, ct);
        renderedDocument = _parser.Parse(Encoding.UTF8.GetString(rm.ToArray()), url.CanonicalUrl);
    }
}
// raise the artifact page size so RenderedDom/NetworkWaterfall are visible:
// ListQuery(0, 10, …) → ListQuery(0, 20, …) (Body + RenderedDom + Waterfall + Screenshot).
return new PageAuditContext(auditId, url, configuration, fetch, document, renderedDocument, null, null);
```
`AuditPipeline` already calls `IResponseRenderedComparer` when
`RenderedDocument` is present (`AuditPipeline.cs:149–158`) and analyzes the
rendered document as effective (`:103`). The `content_scope` group then carries
the rendered `fullWordCount`, which `AuditExplorerController.ExtractScopeDiagnostics`
maps to the wire `scopeDiagnostics.totalWordCount` (the harness `ev.words`
source). No pipeline edit is required.

### 5.5 Waterfall / CR-006
The coordinator persists `ContentArtifactKind.NetworkWaterfall` from
`RenderedPage.NetworkWaterfall` (render-layer CDP capture), never the proxy.
CR-031 (WP-10-03) reads `TransferredBytes` from that artifact. No proxy routing
of rendered traffic (D-002).

### 5.6 UI-04 capture-tab design (WP-10-05 owns `surfaces/project/`; coordinator applies after 05)
The tab is the EXISTING "Crawl as and capture" tab — `CrawlAsPanel`
(`editorPanels.tsx:124`, panel testid `config-panel-crawl-as`, tab id `crawl-as`).
Today its "Capture mode" block is a disabled `LaterPhaseSection`
(`config-rendering-section`, "Phase 10"). Replace that block with live controls
bound to the additive wire fields `captureMode` / `captureSettings`
(`contracts/api/projects.ts` `CaptureConfigurationFields`, already live on
read/create/update). Add to `ConfigurationFormValue`: `captureMode:
CaptureModeWire` (default `'non-js'`) and a `captureSettings` form group; map in
`configurationFromWire`/`configurationToWire` (null/absent → `'non-js'` +
defaults). Exact testids for the CDP acceptance harness:

| Control | testid | Notes |
|---|---|---|
| Section container | `config-rendering-section` | KEEP the existing id (harness stability); retitle "Capture mode", remove disabled note. |
| Mode select | `config-capture-mode` | values `non-js` / `js` / `dual` / `adaptive` (locked wire tokens). |
| Adaptive sample size | `config-capture-adaptive-sample-size` | NumberInput 3–5, default 4; visible when mode=adaptive. |
| Material-change threshold | `config-capture-material-threshold` | percent, default 20; visible when adaptive. |
| Dual storage warning threshold | `config-capture-dual-storage-threshold` | bytes (default 1073741824); visible when dual; label "warn only — no hard cap". |
| Render wait quiet ms | `config-capture-render-wait-quiet-ms` | default 500. |
| Render wait hard cap ms | `config-capture-render-wait-hard-cap-ms` | default 20000. |
| Rendering pool size | `config-capture-rendering-pool-size` | null = auto (half logical processors, min 2). |
| Pattern overrides list | `config-capture-pattern-overrides` | rows of pattern + capture. |
| Override row pattern | `config-capture-override-pattern-{i}` | text, e.g. `/blog/*`. |
| Override row capture | `config-capture-override-capture-{i}` | select `rendered` / `non-rendered`. |
| Add override row | `config-capture-override-add` | button. |

Wire mapping: `captureSettings.patternOverrides[{pattern, capture}]`,
`adaptiveSampleSizePerGroup`, `materialChangeWordCountThreshold` (fraction 0.20
↔ 20%), `dualStorageWarningThresholdBytes`, `renderWaitQuietMs`,
`renderWaitHardCapMs`, `renderingPoolSize`. UI-13 rendered tab (WP-10-05) reads
the `RenderedDom` + `NetworkWaterfall` artifacts this workpiece persists.

### 5.7 Amendment requests (Contracts re-locked; coordinator routes)
1. **`render.dual-storage-warning`** — promote the builder-local
   `CaptureRuleKeys.DualStorageWarning` into `FindingRuleKeys`. Justification:
   §5.6.1 mandates a user warning above the dual-storage threshold; no locked
   key exists. Additive, no wire break.
2. **`render.hreflang-delta`** — add a dedicated key. Justification: §7.18 names
   hreflang explicitly as high-severity; today the delta rides
   `render.metadata-delta` (field `hreflang`, Error) to stay within locked keys.
   A dedicated key improves report clarity. Until promoted, the comparer's
   behavior is spec-correct.
3. **(Optional, WP-10-01 interop)** `RenderedPage` additive
   `ServiceWorkerRegistrations` fact — WP-10-01 already exposes
   `ServiceWorkerRegistrationFact`/`CdpRenderResult` on the concrete CDP pool.
   If promoted to the locked `RenderedPage`, the comparer/coordinator can prefer
   the render-layer fact over the script scan (more precise). Not required:
   script-scan detection works through the locked interface today.

## 6. Blockers / deviations

1. **Cross-lane compile gate (transient, not this lane).** The shared
   `CrawlForge.Crawler.Tests` project currently fails to COMPILE because
   WP-10-01's in-progress test file
   `app/tests/CrawlForge.Crawler.Tests/Rendering/Cdp/NetworkWaterfallRecorderTests.cs`
   has raw-string-literal syntax errors — `CS9007` at lines 176 and 285 ("The
   interpolated raw string literal does not start with enough '$' characters…").
   Earlier observed breaks in their lane (`ResolvedBrowser` missing,
   `CdpConnection.cs:203` `ValueWebSocketReceiveResult` conversion,
   `RenderingPoolOptions.InfiniteScrollPauseMs` missing) are now resolved; the
   `CrawlForge.Crawler` SOURCE project builds clean (0/0). Only their test file
   still blocks the shared test project's compilation. This builder's
   `CaptureModes` files have zero compile errors and pass 48/48 via the private
   scratch project (§4.3); the official filtered command runs them unchanged once
   WP-10-01 fixes that file. No files outside owned paths were touched. The
   coordinator may want to nudge WP-10-01 or run the official Crawler command at
   the gate after their lane lands.
2. **Stale test assertion (spec wins; coordinator/owner action at the gate).**
   `app/tests/CrawlForge.Audit.Tests/Indexability/ResponseRenderedComparerTests.cs`
   (NOT an owned path) asserts `TitleAlteredByScript_EmitsDeltaAndWarning` →
   title delta severity `Warning`. §7.18 (CR-009) + the locked
   `FindingRuleKeys.RenderTitleDelta` doc ("high severity per §7.18") + the wave
   notes mandate **Error**. The rewritten comparer follows the spec, so that one
   stale assertion fails until updated (title delta Warning→Error). The metadata
   (description) assertion stays valid (general metadata remains Warning). This
   is the only pre-existing test affected; it is outside this lane's owned paths
   and outside the `RenderedComparison`/`CaptureModes` verification filters.
3. **Documented engineering choices (spec leaves open):**
   - Adaptive sample size clamped to the spec range 3–5 (§5.6.1 "default 3 to
     5"); application default 4 (`CaptureSettings`).
   - URL-path grouping = path with final segment replaced by `*`; override
     matching = ordinal glob (`*` spans `/`), first match wins. §5.6.1 leaves
     matching semantics to the engine.
   - Adaptive group state is per-run (in-memory); not checkpointed. On resume a
     group re-samples (spec does not require adaptive-state checkpointing).
   - Dual-storage accounting counts raw body + rendered artifacts in Dual mode;
     baseline non-JS keeps the null (=raw) `CaptureSource` sentinel to avoid
     write amplification.
   - `content.javascript-dependent-template` severity = Warning (Content);
     §5.6.1 names the finding but not its severity.
