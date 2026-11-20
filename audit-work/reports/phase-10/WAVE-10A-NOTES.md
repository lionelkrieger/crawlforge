# Wave-10A coordinator notes (phase 10)

Contracts are RE-LOCKED as of commits `2cb4311` (WP-10-A1 amendment) and
`0307fe8` (coordinator integration: schema v5 + wire mapping). No builder in
wave 10A may edit `CrawlForge.Contracts\` or `frontend\src\contracts\`; any
missing contract surface goes in the wiring handoff (a new amendment workpiece
would be required — avoid by using the locked surfaces below).

## Locked surfaces every wave-10A builder codes against (WP-10-A1)

- Enums: `AuditCategory` (14 members; Images=11, SecurityAndProtocol=12,
  Internationalization=13), `WafChallengeClass` (6; ManagedChallenge=4,
  RateLimitSoftBlock=5), `IndexabilityFlags.CanonicalElsewhere=16`,
  `DiscoverySource.Render=5`, `ContentArtifactKind.NetworkWaterfall=3`,
  `CaptureMode` (NonJs/Js/Dual/Adaptive), `PatternCaptureMode`
  (NonRendered/Rendered), `CaptureSource` (Raw/Rendered/Both).
- Wire tokens (EnumWireNames is the single authority; the Api converter
  delegates): lowercase CLR names except the frozen hyphenated tokens
  `catch-up`, `managed-challenge`, `rate-limit-soft-block`, `non-js`,
  `non-rendered`. Existing `interstitial`/`botblock` wires are LOCKED even
  though 03/02 §2.2 spells them `js-interstitial`/`bot-block` — that deviation
  is a recorded loop-§8 owner observation; do NOT rename.
- `CrawlConfigurationDto.CaptureMode/CaptureSettings/RedirectChecks` (nullable
  additive; null = baseline). REST binding is LIVE: top-level `captureMode`,
  `captureSettings`, `redirectChecks` on configuration read/create/update
  (proven by `CaptureConfigurationRouteTests`, 5 tests over real SQLite).
  `CaptureSettings` knobs: adaptiveSampleSizePerGroup (default 4, spec range
  3–5), materialChangeWordCountThreshold (0.20), dualStorageWarningThresholdBytes
  (1 GiB, WARNING only — never a hard cap, exclusion #8), renderWaitQuietMs
  (500), renderWaitHardCapMs (20000), renderingPoolSize (null = half logical
  processors min 2, `CaptureSettings.DefaultRenderingPoolSize`), patternOverrides.
  `RedirectCheckSettings`: crossHost/temporaryStatus/nonIndexableTarget/
  httpsDowngrade, all default true; absent/null document = all ON.
- `UrlRecordDto.CaptureSource` + `DiscoverySource` project onto URL rows
  (`captureSource`/`discoverySource` wire fields, live).
- `RenderedPage.NetworkWaterfall` (`IReadOnlyList<RenderedNetworkEntry>?`) +
  `RenderedNetworkEntry(Url, ResourceType, Method, StatusCode, MimeType,
  TransferredBytes, EncodedDataLength, TimingJson, FromCache, FromServiceWorker)`
  — CR-031 reads `TransferredBytes`. Storage kind `NetworkWaterfall`.
- `FindingRuleKeys` (65 keys): the full `render.*-delta` family (status/title/
  metadata/text/links/structured-data/resources/canonical/meta-robots/noindex),
  `render.service-worker-registered`, `content.javascript-dependent-template`,
  `redirects.inappropriate-{cross-host,temporary-status,non-indexable-target,
  https-downgrade}`, `structured-data.deprecated-term`, plus the promoted
  phase-09 keys. Soft-404 client routes REUSE `indexability.soft-404` (no new
  key was locked).
- Schema migration v5 already landed: `crawl_configurations.capture_mode/
  capture_settings_json/redirect_checks_json`, `url_records.capture_source`.
  Do not add schema work; if a new column seems needed, hand it off.
- Frontend: `ANALYSIS_CATEGORIES` already extended to 14 (labels verbatim from
  `AUDIT_CATEGORY_LABELS`); `wireConformance`/`configurationEditor` guards
  already extended; `FirstRunSeed` toggles already 14. Do not redo or revert.

## WP-10-01 specific

1. **CR-006 amendment (03/03 §5.6.2) overrides the brief/YAML wording.** The
   living spec captures rendered traffic DIRECTLY in the rendering control
   layer (CDP Network domain → waterfall + transferred bytes). The loopback
   proxy routes plain-HTTP NON-rendered traffic. The brief's "proxy capture
   for rendered traffic" contradicts the spec — follow the spec and record the
   deviation in your report. The proxy still needs the per-subresource
   transferred-bytes size field (phase-09 gap 7) for non-rendered image
   measurement (CR-031 http mode).
2. Fill `RenderedPage.NetworkWaterfall` from CDP Network events; store the
   waterfall artifact under `ContentArtifactKind.NetworkWaterfall`.
3. Browser resolution chain (§5.6.3): pinned bundled Chrome for Testing
   (`assets/browser`) → user-configured path → local Chrome → Edge; none
   found → pre-crawl validation failure. `assets/browser` is EMPTY and runtime
   downloads are forbidden (D-003, offline-first): deliver the resolver, the
   pin manifest and the fallback chain with component tests using a fake
   bundle layout. Physically bundling the CfT binary is owner packaging item
   O-006 — do not download anything.
4. Wait rule (§5.6.2, CR-009): DOM + network quiet 500 ms, hard cap 20 s, both
   configurable through `CaptureSettings`; streaming responses excluded from
   the quiet test; no fixed delays. Pool: half logical processors min 2, fresh
   browser context per page, timeout + failure isolation.
5. Service workers bypassed on every render (§7.19); expose the registration
   fact so WP-10-02 can emit the informational finding. Hash-route mode off by
   default; client-side redirects are a distinct chain type (handoff if the
   chain recorder needs a new type — contracts are locked).
6. `IRenderingPool.RenderAsync(RenderRequest) → RenderedPage` is locked — do
   not change the signature. Hand off the exact DI registration + lifetime.
   The EXISTING `HeadlessRenderingPool` (your folder) is PuppeteerSharp-based;
   D-002 (2026-11-17) mandates driving CDP DIRECTLY, replacing PuppeteerSharp,
   as part of THIS rendering rebuild. Your CDP control layer replaces that
   implementation; write NO new PuppeteerSharp code. It is the only
   PuppeteerSharp consumer in CrawlForge.Crawler — the csproj PackageReference
   removal is coordinator wiring (flag it in your handoff). The concrete pool
   also exposes a `RenderAsync(request, RenderArtifactContext?, ct)` overload
   that persists RenderedDom + Screenshot artifacts; keep an equivalent path
   and document it (the engine needs artifact persistence per rendered URL,
   plus the new NetworkWaterfall artifact).
7. Governance: outbound client construction only inside approved boundary
   scopes (the Crawler namespace/path is approved; see
   `CrawlTargetTransportFactory` for the pattern); no non-loopback listener
   (NET-01). The governance gate runs `--fail-on-violation`.

## WP-10-02 specific

1. Code against the locked `IRenderingPool` (WP-10-01 builds it in parallel;
   test with a fake pool). Engine dispatch is the coordinator's: expose a
   capture-mode component with an exact interface the engine can call from
   `CrawlPhaseAsync`, and document how rendered documents reach
   `BuildStoredContextAsync` (`PageAuditContext.RenderedDocument` is null
   today). `PageAuditContext` allows additive optional properties, but the
   Contracts project is locked — if a new context member is unavoidable, put
   it in the handoff as an amendment request with justification.
2. Mode semantics (§5.6.1): non-JS fetch only (default); JS renders every
   in-scope page; dual stores BOTH captures and compares every URL; adaptive
   samples 3–5 per URL-path-pattern group (default 4) and switches the group
   when material change is observed (title/canonical/meta-robots/hreflang diff
   OR different link set OR visible word-count change > 20%, configurable).
   Switching emits `content.javascript-dependent-template`. Manual per-pattern
   overrides WIN over adaptive and apply on top of every mode.
3. §7.18 severities: title/canonical/meta-robots/hreflang differences = high;
   rendered-only noindex (either direction) = CRITICAL with dual evidence
   (raw + rendered). Use the locked `render.*-delta` keys.
4. JS-only links: discover from the rendered DOM, record
   `DiscoverySource.Render`; report raw vs rendered word counts
   (`render.text-delta` evidence carries totalWordCount).
5. Service-worker registration → `render.service-worker-registered`
   informational; renders bypass it. Soft-404 client route → reuse
   `indexability.soft-404`.
6. Dual storage: warn above `DualStorageWarningThresholdBytes` (default
   1 GiB); NEVER a hard cap (exclusion #8).
7. Custom JS snippets (§7.9/§7.17): timeout + isolated wrapper; extraction
   source selectable raw/rendered/both (`CaptureSource`).
8. UI-04 capture-tab changes go in your HANDOFF (surfaces/project is owned by
   WP-10-05 this wave; the coordinator applies your tab after 05 completes).
   The captureMode/captureSettings/redirectChecks wire binding already exists
   — your tab binds to those fields. UI-10 indicator placement is coordinator
   wiring (handoff).

## WP-10-05 specific

1. Criterion #56 PASSED at step 0 (commit `1e30ab9`, 17/17 checks) — no UI-04
   editor fix is routed to you.
2. A1 handoff items for you: `IssueExplorer.tsx` wire category list +=
   images/securityandprotocol/internationalization;
   `RedirectAppropriatenessEvaluator` flags += NonIndexableTarget=4,
   HttpsDowngrade=8, All=15 (+ fix the stale comment);
   `RedirectFindingAnalyzer.CheckName` += "non-indexable-target"/
   "https-downgrade"; scoring is enum-driven and picks up 14 automatically —
   verify, don't hardcode; replace ALL builder-local rule keys with the
   `FindingRuleKeys` constants.
3. Already done by the coordinator (do not redo/revert): ANALYSIS_CATEGORIES
   14, frontend guards extended, FirstRunSeed 14 toggles, REST capture fields
   live, schema v5.
4. CR-029: the four toggles bind to `redirectChecks` on the configuration
   wire (UI-04 analysis tab). Each finding names the case, the triggering hop
   and the final target; keys `redirects.inappropriate-*`. Absent/null = all
   four ON. Component tests must show each toggle flipping its finding.
5. CR-030: bundled, VERSIONED retired/restricted rich-result list embedded in
   `Audit/StructuredData` (HowTo is one entry); NEVER fetched at runtime.
   Microdata `itemprop` URL and prefixed terms resolve like RDFa.
6. CR-031: oversized-image uses transferred bytes where captured (rendered
   waterfall `TransferredBytes`, or the proxy size field in http mode);
   otherwise report "not measured" — NEVER "pass".
7. WAF: five classes; soft blocks (managed-challenge, rate-limit-soft-block)
   throttle through AIMD; hard blocks (captcha, interstitial, botblock) stop
   the host. New wire tokens are hyphenated via EnumWireNames; existing
   interstitial/botblock stay as locked.
8. Keep every phase-09 trap passing: component tests cover missing-alt under
   Images and hreflang-no-return under Internationalization (the phase gate
   re-runs 17/17 + UI-11/UI-12 category assertions).
9. Category moves (D-021): image rules → Images; cookie-security/protocol →
   SecurityAndProtocol; hreflang → Internationalization; all three scored.
   `IndexabilityStateEvaluator` sets `CanonicalElsewhere` when the canonical
   points elsewhere (CR-024).

## All builders

- The working tree carries uncommitted coordinator ledger edits
  (`buildplan/`, `audit-work/reports/phase-10/`) — ignore them in lane checks;
  they are not yours to commit or revert.
- `CF_AGENT_ID=<your agent id>` for every dotnet build/test; own test projects
  filtered to your folders only; never the whole solution.
- Private fixture ports: 01→47341, 02→47342, 05→47343. Never 47311, never the
  app, never `%APPDATA%\CrawlForge`, never "Airpower". Data dirs
  `%LOCALAPPDATA%\CrawlForge-<agent id>`.
- Frontend tests: vitest default-timeout contention flakes are documented;
  use `--testTimeout=30000` and avoid running suites while benchmarks run.
- `done_when` fixture/UI checks run at the PHASE GATE after coordinator
  wiring (AGENT-INSTRUCTIONS §5.6) — component tests stand in until then.
- Report format: files changed; deliverable-by-deliverable; exact verification
  commands + output; wiring handoff with type names and call sites; blockers.