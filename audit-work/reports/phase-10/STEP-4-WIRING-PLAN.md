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
- WAF evaluator: 05's five-class evaluator replaces/extends the registered
  `WafChallengeEvaluator` (:91–93) per handoff; soft-class AIMD throttle +
  hard-class host stop wire through the existing frontier/AIMD surfaces
  (engine WAF record path :1318–1331).
- Redirect appropriateness: 05's evaluator/analyzer join the engine's
  redirect-findings path (:1334–1356) per handoff; the four toggles read
  `configuration.RedirectChecks` (null = all ON).

## 5. Rule-catalog registration (gap #21 pattern)

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

## 7. Harness conversion (python — safe to edit during gate runs)

`run_fixture_acceptance.py`:

- `wire_probe_run` (:366–404) → real UI-started capture-mode audits for
  dual/adaptive (and js): create project/config via REST with
  `captureMode` (binding LIVE), open UI-04 editor over CDP, select the mode
  in the capture tab (testids from 02's handoff), save, read back, start the
  audit from the hub UI (`ui_entry_audit` pattern :179+), pull data,
  evaluate the trap matrix. Keep the wire-probe evidence as a historical
  note only.
- `t_late_meta_change` (verdicts.py): strengthen for rendered-only noindex
  CRITICAL dual evidence once 02's comparer evidence shape is known.
- UI-11/UI-12 walkers (ui_walker.py): add category assertions (Images /
  Internationalization scores present; missing-alt under Images;
  hreflang-no-return under Internationalization) for the phase-09
  regression at step 5.

## 8. Pre-crawl validation

Browser resolution failure must fail BEFORE the crawl (§5.6.3):
`PreCrawlValidator` (registered :103–105) gains the browser check per 01's
handoff — none of bundled CfT / user path / Chrome / Edge → validation
failure with the actionable message.

## 9. Verification order (step 5)

1. `run-phase-gates.ps1 -Phase phase-10` (set MSBUILDDISABLENODEREUSE=1 in
   the calling environment — prevents the Acceptance stdout-pipe deadlock;
   record in the report).
2. UI-started #50/#51/#52 evidence (js/dual/adaptive fixture flows).
3. Phase-09 regression: 17/17 HTTP traps + `--surfaces UI-11,UI-12`.
4. Restore network-audit/evidence + TRACEABILITY side effects; keep
   captured/ evolved shapes.