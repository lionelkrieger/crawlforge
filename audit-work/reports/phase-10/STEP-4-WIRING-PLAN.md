# Step-4 wiring plan (coordinator, wave-10C WP-10-04)

Mapped against the tree at commits 2cb4311 / 0307fe8 / f418243 / f6dde46.
Builder handoffs (type names + call sites) refine the component seams below;
nothing here edits builder-owned files.

## 1. Rendering dispatch in the crawl loop (G03)

Seam: `AuditExecutionEngine.CrawlPhaseAsync` success path, after link
discovery + trap observation (`AuditExecutionEngine.cs:1365–1373`), before
the checkpoint flush (:1375).

- WP-10-02's capture-mode component decides per URL whether to render
  (mode + pattern overrides + adaptive sampling state). The engine calls it
  with (auditId, urlId, entry, fetch, document, configuration, ct).
- The component calls `IRenderingPool` (WP-10-01's CDP pool). Artifact
  persistence: the existing concrete pool exposes
  `RenderAsync(request, RenderArtifactContext?, ct)` which stores
  RenderedDom + Screenshot (`HeadlessRenderingPool.cs:114,462–472`); the
  phase-10 pool must also store the NetworkWaterfall artifact
  (`ContentArtifactKind.NetworkWaterfall=3`) — handoff confirms the shape.
- JS-only links: the component discovers rendered-DOM links and enqueues
  them with `DiscoverySource.Render` (engine's `DiscoverLinksAsync` takes
  the source; extend the call or add a rendered-link pass — handoff).
- `urlRecord.CaptureSource` update: raw-only = Raw; rendered-only facts =
  Rendered; dual = Both. The row is written at :1333 BEFORE the render
  decision point — either move the write after the capture step or issue a
  follow-up `_urlWrites.UpdateAsync` with the capture source (additive DTO
  member already lands via schema v5).
- Adaptive pattern-group state lives in the component for the audit's
  lifetime; material-change switches emit
  `content.javascript-dependent-template` through the normal finding path.
- Render failures are isolated per URL (pool contract); a failed render in
  js/dual mode records the failure without failing the audit (spec §5.6.2
  timeout/failure isolation) — handoff defines the finding, if any.

## 2. Stored-context rendered extension

Seam: `BuildStoredContextAsync` (`AuditExecutionEngine.cs:2665–2700`).

- After the Body artifact read, list the same url's artifacts for
  `ContentArtifactKind.RenderedDom`; when present, open + parse via
  `_parser.Parse` and pass as the SIXTH positional argument
  (`PageAuditContext.RenderedDocument`, `PipelineContracts.cs:84–92`) —
  currently hardcoded null at :2699. No contract change needed.
- `AuditPipeline` already invokes `IResponseRenderedComparer` when the
  rendered document is present (`AuditPipeline.cs:41,64,156`; DI registered
  `EngineServicesModule.cs:148,223`). WP-10-02's comparer implementation
  replaces the phase-02 stub inside `Audit/Indexability/` — the pipeline
  call site is unchanged.
- CR-031 image bytes: `ImageRuleAnalyzer` (05-owned) reads the
  NetworkWaterfall artifact per URL through injected
  `IReadRepository<ContentArtifactDto>` + `IContentStore` (precedent:
  `UrlStructureAnalyzer` takes `IReadRepository<UrlRecordDto>`,
  `EngineServicesModule.cs:171–172`). DI update at :183 per 05's handoff.
  `ImageFact.TransferredBytes` already exists (`ImageRuleAnalyzer.cs:37–41`).
- **Stale-test reconciliation (02 flag, CONFIRMED)**:
  `Audit.Tests/Indexability/ResponseRenderedComparerTests.cs:43–73` asserts
  Warning for title/metadata deltas; §7.18 + locked catalog mandate Error.
  Key strings already match the locked render.* family. Coordinator updates
  the severity assertions at wiring (Error for the §7.18 family; CRITICAL
  rendered-only noindex either direction; status-delta stays Warning).
  Stale-test fix, NOT a spec deviation — record in the phase report.
- **SW fact interop (02 flag)**: 02 detects service-worker registration via
  script scan through the locked interface; 01's concrete pool exposes
  ServiceWorkerRegistrationFact. At wiring, prefer the CDP-level fact when
  it reaches the stored render artifact; script scan is the fallback.
  Finding key render.service-worker-registered (informational) either way.

## 3. Post-crawl passes

Seam: `RunPostCrawlPassesAsync` (:1522). The comparer runs per URL inside
the pipeline (section 2), not here — unless 02's handoff places a site-level
aggregation pass here (e.g. adaptive summary). Register whatever the handoff
names, following the existing try/catch-continue pattern (:1569–1572).

## 4. DI / composition (EngineServicesModule.cs — coordinator-owned)

- `IRenderingPool` registration :100–102 → 01's new pool + options from
  `CaptureSettings` (pool size, wait knobs) + browser resolver chain.
  Lifetime stays singleton; disposal already handled (IAsyncDisposable).
- Remove the `PuppeteerSharp` PackageReference from
  `CrawlForge.Crawler.csproj` once 01's replacement lands (D-002; the pool
  was the ONLY consumer — verified by grep).
- Capture-mode component registration per 02's handoff.
- `ImageRuleAnalyzer` construction :183 per 05's handoff (artifact reads).
- **D-021 alt-check takeover (05 early finding, APPROVED)**: 05's
  ImageRuleAnalyzer takes over `accessibility.image-alt` under
  AuditCategory.Images. The production AccessibilityAnalyzer construction
  (:176 `new AccessibilityAnalyzer()`) must pass AccessibilityAnalyzerOptions
  with `RuleOverrides["accessibility.image-alt"] = new(Enabled: false)` —
  single producer, no double emission (mechanism proven by
  AccessibilityAnalyzerTests). Check SinglePageTester/ToolsController for a
  second construction; apply the same override. The key is NOT in locked
  FindingRuleKeys (producer-local AccessibilityRuleKeys constant) — keep the
  exact string; harness t_missing_alt matches it. Full reply queued in
  REPLY-TO-WP-10-05-ALT-FINDING.md (send on 05 completion).
- WAF evaluator: 05's five-class evaluator replaces/extends the registered
  `WafChallengeEvaluator` (:91–93) per handoff; soft-class AIMD throttle +
  hard-class host stop wire through the existing frontier/AIMD surfaces
  (engine WAF record path :1318–1331).
- Redirect appropriateness: 05's evaluator/analyzer join the engine's
  redirect-findings path (:1334–1356) per handoff; the four toggles read
  `configuration.RedirectChecks` (null = all ON).
- **Proxy artifact convention (CR-031 http-mode, ruling #2)**: verify 01
  persists proxy subresource captures as ContentArtifactKind.NetworkWaterfall
  artifacts in the locked RenderedNetworkEntry JSON-array shape with
  TransferredBytes filled; 05's ImageRuleAnalyzer reads the same shape.
  ProxyCapturedRequest context record unchanged (no byte field — fine).
- **Catalog reseed (05 handoff)**: drop the legacy redirects.inappropriate
  seed (constant retained for compile compat until reseed; nothing emits
  it), add the four locked redirects.inappropriate-* case keys + all other
  phase-10 keys with 05's category/severity/priority list. Exact list
  (05 report §4.2): redirects.inappropriate-{cross-host,temporary-status,
  non-indexable-target,https-downgrade} Indexability/Warning/60/low;
  structured-data.deprecated-term StructuredData/Warning/55/low;
  image.oversized Images/Warning/60/low; image.format Images/Warning/50/low;
  accessibility.image-alt Images/Error/85/low. MOVE hreflang:
  HeaderHtmlEquivalencyAnalyzer.cs:237 (StructuredData→Internationalization),
  RuleDefinition.cs:84 HreflangHeaderHtmlConflict (Indexability→
  Internationalization), i18n.hreflang-* seeds → Internationalization.
- **01 handoff items**: per-audit RenderingPoolOptions.FromCaptureSettings
  (snapshot.CaptureSettings); concrete HeadlessRenderingPool.RenderDetailedAsync
  → CdpRenderResult { Page, ServiceWorkerRegistrations, Browser } for SW
  facts + browser identity where needed (comparer script-scan remains the
  contract-safe fallback); remove unused PuppeteerSharp PackageReference
  from CrawlForge.Crawler.csproj (Audit KEEPS its own — LabRenderingProbe);
  pre-crawl validation: rendering-dependent capture modes resolve browser,
  BrowserResolutionException → validation failure, non-JS unaffected;
  CR-031 http-mode call site: after plain-HTTP fetch through the proxy →
  proxy.DrainDetailedCapturedAsync() → NetworkWaterfallArtifactWriter.
  PersistProxyCapturesAsync(store, auditId, urlId, captures, ct) (kind 3,
  camelCase array, one per URL — matches 05's reader; G09 fetcher proxy
  wiring remains coordinator).
- **Missed-surface amendment (executed under WP-10-04 coordinator
  ownership; documented as WP-10-A2-style in the phase report)**:
  (a) C# Api RenderTestResponse += optional NetworkWaterfall
  (IReadOnlyList<RenderedNetworkEntry>? = null, 7th positional) +
  ToolsController:226 mapper from page.NetworkWaterfall — the TS mirror
  ALREADY carries networkWaterfall (A1, tools.ts:142) and
  FrontendMirrorParityTests:169 asserts only the TS side, so this is
  wire-completion, not a lock break; extend the render-test conformance
  test to feed real producer output. (b) UI-21 browser section: NEW
  additive REST surface (SettingsController GET browser) bound to
  HeadlessRenderingPool.ResolveBrowserInfo()/TryLoadPinManifest()
  (ResolvedBrowser { ExecutablePath, Source, Product, Version,
  PinnedVersion }); new TS mirror interface + settings surface binding
  (coordinator-owned surfaces). DEFERRED amendments (no contract change):
  ProxyCapturedRequest.TransferredBytes (artifact path covers CR-031),
  RenderedPage.ServiceWorkerRegistrations/browser identity (concrete
  RenderDetailedAsync covers; 02 script-scan fallback).

## 5. Rule-catalog registration (gap #21 pattern)

Verified anchors (2026-11-20): NO render.*/phase-10 keys are seeded
anywhere today. Two seed sites:
- `AuditRuleCatalog` constructor (Audit/Pipeline/RuleDefinition.cs:50–85,
  Seed(ruleKey, category, severity, priority, difficulty) helper :101):
  add the render.*-delta family + render.dual-storage-warning (02's
  builder-local CaptureRuleKeys) + render.service-worker Notice here;
  MOVE :84 HreflangHeaderHtmlConflict Indexability→Internationalization.
- Host `EngineServicesModule.cs:252+` catalog.Register block: REPLACE the
  legacy RedirectRuleKeys.Inappropriate seed (:270–272) with 05's four
  case keys; ADD structured-data.deprecated-term, image.oversized,
  image.format, accessibility.image-alt (exact string; NOT in locked
  FindingRuleKeys — producer-local constant) per 05 report §4.2; MOVE
  i18n.hreflang-* seeds → Internationalization (find their current site).
  Also HeaderHtmlEquivalencyAnalyzer.cs:237 category move (05 handoff).

`AuditRuleCatalog` constructor seeds (`Audit/Pipeline/RuleDefinition.cs`,
coordinator-owned) += phase-10 definitions:

- `render.*-delta` family — §7.18: title/canonical/meta-robots/hreflang =
  Error(high); rendered-only noindex either direction = Critical; text/links/
  structured-data/resources/status per 02's handoff severities.
- `render.service-worker-registered` — Notice (informational).
- `content.javascript-dependent-template` — Warning.
- `redirects.inappropriate-{cross-host,temporary-status,non-indexable-target,
  https-downgrade}` — per CR-029/05 handoff (Indexability).
- `structured-data.deprecated-term` — Warning (CR-030).
- Category moves: image.* → Images; cookie-security/protocol keys →
  SecurityAndProtocol; hreflang.* → Internationalization (match producers).
- Soft-404 client route reuses `indexability.soft-404` (already seeded or
  producer-defined — verify).

## 6. Frontend (after WP-10-05 completes — surfaces/project ownership)

- Apply 02's UI-04 capture-tab handoff (mode select + knobs + pattern
  overrides + redirect toggles live in the analysis tab per CR-029). The
  capture tab is the EXISTING "Crawl as and capture" tab (CrawlAsPanel,
  tab id `crawl-as`); CR-029 toggles go in "Analysis categories"
  (CategoriesPanel). ConfigurationFormValue gains captureMode/
  captureSettings/redirectChecks fields + wire round-trip.
- UI-13 rendered tab / UI-10 capture indicators per handoffs (G20 remainder).
- UI-11/UI-12 category surfaces already carry 14 categories via
  ANALYSIS_CATEGORIES (committed); verify score rendering for the 3 new
  categories at the phase gate.
- **Dashboard label gap (coordinator-owned surfaces/dashboards):**
  `ReportDashboard.tsx:202` renders the raw wire token (`entry.category`)
  as the display label — "securityandprotocol"/"internationalization" would
  show unreadable. Map tokens → `AUDIT_CATEGORY_LABELS` (frozen catalog,
  frontend/src/contracts/vocabulary.ts) for the score rows AND the category
  distribution chart (:223+). Testids stay token-based
  (`dashboard-score-row-{token}`) — the harness asserts on those.
- IssueExplorer wire list += 3 (surfaces/explorers/issues — OUTSIDE 05's
  ownership; coordinator applies 05's handoff line).

## 7. Harness conversion — IMPLEMENTED (live verification at step 5)

Done during the wave-10A gate retry (python-only, 39/39 harness unit tests):
- `wire_probe_run` REPLACED by `ui_capture_mode_audit(mode, rest, cdp, ...)`
  (run_fixture_acceptance.py:366+): UI dialog creation → REST limit bounding
  → project-row → overview-nav-configurations → configuration-edit-{cid} →
  config-tab-crawl-as → config-capture-mode native select via React value
  setter → configuration-save → REST read-back asserts captureMode==mode AND
  rendering.enabled==true → back-to-hub → start-audit-{pid} (NO REST
  fallback — capture-mode acceptance requires a UI start) → start_with_retry
  → pull data → evaluate_matrix on real evidence → UI-10 inspection.
  run_id f"{mode}-ui"; both pooled-matrix matchers updated.
- Dispatch: js/dual/adaptive ALL route through ui_capture_mode_audit
  (js was REST-started before; criterion #50 wants UI-started js evidence).
- verdicts.py gating: dual/adaptive no longer blanket-not_testable; the
  legacy wire_probe_evidence branch kept for old contexts; capture=='dual'
  now requires mode=='dual' (adaptive samples, cannot guarantee dual capture
  of the trap page).
- t_late_meta_change STRENGTHENED: asserts title delta dual values (raw
  'Raw title before script' vs rendered 'Rendered title after script'),
  canonical delta field==canonical → late-meta-other, and render.noindex-
  delta critical:true + direction rendered-only + rawNoindex false +
  renderedNoindex true + locked Error severity. partial verdict when some
  evidence present.
- UI dependency: config-capture-mode must be a NATIVE select (step-4 UI
  implementation decision) and mode != non-js must set rendering.enabled.
- ui_walker.py category assertions DONE earlier (e654361).

Original design notes (kept for reference):

`run_fixture_acceptance.py` — replace `wire_probe_run` (:366–404) with
`ui_capture_mode_audit(mode, ...)` for js/dual/adaptive (dispatch :969):

1. UI New project dialog (reuse the ui_entry_audit :188–231 flow verbatim:
   nav-hub → new-project-button → create-project-modal → fill name/url/
   description → create-project-submit → evidence-poll POST /api/projects
   + REST name match, never a fixed sleep). Name: "Fixture harness {mode}
   {ts}".
2. REST PUT limits bounding (bound_configuration_limits, BULK_LIMITS) —
   same as ui_entry_audit :239 (UI has no limit controls; recorded).
3. **Mode selection IN THE UI (criterion requirement)**: open the config
   editor over CDP (project overview → Configurations → open config;
   editor testids config-editor-tabs / config-tab-crawl-as), set
   `config-capture-mode` to the mode token (02's handoff testid; native
   select → React value-setter + bubbling change eval, same technique as
   the walker's _set_category_filter; Mantine Select → click + option).
   Mode != non-js implies rendering.enabled=true in the product binding
   (coordinator UI implementation decision); verify via REST read-back
   captureMode == mode AND rendering.enabled == true. Adaptive: also set
   config-capture-adaptive-sample-size if a non-default is needed (default
   4 is fine). Screenshot the capture tab per mode (UI-04 evidence).
4. Save through the UI if the editor requires it (config save testid —
   verify at implementation), then start the audit IN THE UI (same
   start-audit-button / project-start-audit-button + confirmation flow
   :246–300, incl. the disabled-button configuration-snapshot race
   recovery — never REST-start).
5. Poll to terminal (600s cap), pull_audit_data, safety_check nonGet==[],
   evaluate_matrix(traps, mode, ctx, data, selected) with REAL evidence
   (t_js_only_link: target row + render.links-delta; t_script_injected_
   content: render.text-delta ev.words≥100; t_late_meta_change:
   render.metadata-delta/title-delta; t_service_worker: informational).
6. UI-10 inspection + capture-mode indicator evidence (coordinator defines
   the indicator testid at wiring; record it here when implemented).
7. Keep the old wire-probe JSON shape as a historical note field only.

`verdicts.py` — strengthen `t_late_meta_change` (:~430): assert the DUAL
evidence values (raw title 'Raw title before script' vs rendered 'Rendered
title after script'; canonical change) and the rendered-only noindex case
(Error + critical:true + direction, locked vocabulary — §7.18 CRITICAL
maps to Error+critical flag). Evidence shape per 02's report §2.7:
raw+rendered directives, meta AND X-Robots-Tag, direction field.

`ui_walker.py` — DONE (commit e654361): ui_11 category_scores evidence +
ui_12 category_probe (images→accessibility.image-alt, internationalization→
hreflang). Step-5 regression asserts on those extra fields.

## 8. Pre-crawl validation + soft-404 client route

Browser resolution failure must fail BEFORE the crawl (§5.6.3):
`PreCrawlValidator` (registered EngineServicesModule:103–105) gains the
browser check per 01's handoff — none of bundled CfT / user path / Chrome /
Edge → validation failure with the actionable message.

**soft-404-client-route (WP-10-04 done_when)** — design verified against
source: PageAuditContext.RenderedDocument ALREADY exists (locked
PipelineContracts.cs:90) → NO contract change. Extend
`Soft404Detector.DetectAsync` (Audit/Indexability/Soft404Detector.cs:36):
after the raw-document evaluation, when NOT isSoft404 AND
context.RenderedDocument is not null, run the SAME phrase/structural
heuristics over the rendered title + visible text; on match emit the
verdict with reason "rendered-client-route" and evidence carrying
source="rendered" + rendered title/wordCount + raw title (dual context).
The pipeline's dedicated ISoft404Detector step stays the SOLE emitter
(WP-09-05 dedup preserved — one indexability.soft-404 per URL). Fixture:
/spa/does-not-exist raw SPA shell (no error phrase) vs rendered 'Page not
found' title → 0.9 confidence. Harness t_soft_404_client_route matches any
'soft-404' key ✓. Component test: raw-clean + rendered-error → classified;
raw-error unchanged; no rendered doc → unchanged behavior.

## 9. Verification order (step 5)

1. `run-phase-gates.ps1 -Phase phase-10` (set MSBUILDDISABLENODEREUSE=1 in
   the calling environment — prevents the Acceptance stdout-pipe deadlock;
   record in the report).
2. UI-started #50/#51/#52 evidence (js/dual/adaptive fixture flows).
3. Phase-09 regression: 17/17 HTTP traps + `--surfaces UI-11,UI-12`.
4. Restore network-audit/evidence + TRACEABILITY side effects; keep
   captured/ evolved shapes.