# Wave-10A review checklist (coordinator acceptance criteria)

Review each builder report against its YAML deliverables + these gates.
Accept → commit (workpiece ID first, Co-Authored-By trailer) → mark ledger
completed. Reject → send_message to the subagent UUID (works after
completion) with the specific gaps; re-review.

## All builders
- [ ] Only owned paths touched (git status lane check; ignore coordinator
      buildplan/report edits).
- [ ] No edits to Contracts / frontend contracts / architecture / buildplan /
      register / decisions-log / SOURCE-OF-TRUTH-LOOP / expected-results /
      CODEMAP / sln / DI / Host Execution+Composition / Audit Pipeline /
      App.tsx / store / shell.
- [ ] CF_AGENT_ID used; verification commands from YAML run with output in
      the report; no whole-solution runs.
- [ ] No commits by the builder; no 47311/app/%APPDATA%/Airpower usage.
- [ ] Wiring handoff present: type names, DI lines, lifetimes, call sites.
- [ ] Deviations recorded (loop §8), spec never edited.

## WP-10-01
- [ ] Direct CDP (WebSocket; Target/Page/Network/Runtime), NO new
      PuppeteerSharp code; HeadlessRenderingPool replaced.
- [ ] CR-006 honored: rendered waterfall via CDP Network →
      RenderedPage.NetworkWaterfall (locked shape); proxy = non-rendered
      traffic + transferred bytes per subresource; deviation recorded.
- [ ] Browser resolution chain + pin manifest + no downloads; pre-crawl
      validation failure path; UI-21 pinned-version surface handoff.
- [ ] Wait rule (quiet 500 ms / cap 20 s, configurable, streaming excluded);
      pool sizing; fresh context per page; failure isolation; SW bypass +
      registration fact.
- [ ] RenderResultConformanceTests / CdpControlLayerConformanceTests /
      BrowserProvisioningConformanceTests present + green.
- [ ] Governance-safe construction (approved scopes; no non-loopback
      listener).
- [ ] Handoff: DI registration + lifetime, artifact persistence path
      (RenderedDom/Screenshot/NetworkWaterfall), render-tester injection,
      csproj PackageReference removal flag.
- [ ] Proxy captures persisted as NetworkWaterfall-kind artifacts, locked
      RenderedNetworkEntry JSON-array shape, TransferredBytes filled
      (CR-031 http-mode; coordinator ruling #2) — shape matches 05's reader.

## WP-10-02
- [ ] Four modes end-to-end semantics (§5.6.1) incl. adaptive sampling +
      material-change switch finding + manual override precedence.
- [ ] Dual storage WARNING only (never hard cap).
- [ ] Comparer behind locked IResponseRenderedComparer; §7.18 severities;
      rendered-only noindex CRITICAL dual evidence; locked render.* keys;
      soft-404 reuses indexability.soft-404; SW informational + bypass.
- [ ] JS-only links DiscoverySource.Render; word counts (ev.words shape).
- [ ] Custom JS snippets timeout + isolated wrapper; CaptureSource
      raw/rendered/both.
- [ ] CaptureModeConformanceTests / RenderedDocumentConformanceTests /
      RawRenderedDiffFindingTests present + green (fake pool).
- [ ] Handoff: capture component interface + engine call sites +
      BuildStoredContextAsync artifact flow + UI-04 capture-tab design with
      testids (existing crawl-as tab) + UI-10 indicator.

## WP-10-05
- [ ] 14 categories scored; image/hreflang/cookie-security moves; producers
      emit the new categories; scoring enum-driven verified.
- [ ] Five WAF classes; soft AIMD / hard host-stop semantics; locked wire
      tokens (no renames).
- [ ] CanonicalElsewhere bit (CR-024).
- [ ] CR-029 four toggles (redirectChecks form field + wire round-trip +
      CategoriesPanel UI); absent/null = all ON; per-toggle finding flip
      tests; keys redirects.inappropriate-*.
- [ ] CR-030 bundled VERSIONED list (HowTo), never fetched; Microdata
      resolves like RDFa; structured-data.deprecated-term.
- [ ] CR-031 not-measured ≠ pass; transferred-bytes path via artifact reads
      (fake artifacts in tests).
- [ ] Builder-local keys replaced with FindingRuleKeys; IssueExplorer line
      in HANDOFF (not edited).
- [ ] AuditScoreWireConformanceTests + 17/17 stand-in component tests
      (missing-alt under Images, hreflang-no-return under
      Internationalization).
- [ ] Coordinator baseline NOT reverted (ANALYSIS_CATEGORIES 14, guards,
      seed, REST binding).
- [ ] Handoff: rule-catalog seed list (key/category/severity/priority),
      score registration, ImageRuleAnalyzer DI, WAF evaluator surface.
- [ ] ImageRuleAnalyzerTests.cs diff matches EXACTLY the five itemized
      assertions of coordinator ruling #1 (no deletions, no extra
      weakening; each change cited to D-021/CR-031).
- [ ] Alt takeover reuses ImageAltRule semantics; evidence shape identical;
      RuleOverrides disable in handoff (+ SinglePageTester path).
- [ ] CR-031 note reframed per ruling #2 (artifact path, not contract gap).

## After all three accepted
- [ ] Wave-10A after_each_wave gate (8 commands; MSBUILDDISABLENODEREUSE=1;
      Security supplement protocol if the DPAPI stall recurs).
- [ ] Ledger completed marks + index counts; dispatch wave-10B (03).

## Acceptance records

### WP-10-02 — ACCEPTED 2026-11-20T04:15Z (coordinator)

Verification: canonical Audit filter re-run by coordinator (CF_AGENT_ID
unset): RenderedComparison 25/25. Crawler CaptureModes 48/48 via builder's
identical-source scratch project (official command gated by 01's WIP
CS9007; runs unchanged at the wave gate — canonical backstop). Lane purity
verified: only owned paths (Capture/, ResponseRenderedComparer.cs,
RenderedComparison/, CaptureModes tests, RenderedComparison tests).
All 10 deliverables proven with real producer→consumer conformance.

Coordinator rulings on the report:
1. Amendment #1 (render.dual-storage-warning → FindingRuleKeys): NO
   mid-wave contract unlock. The key string is registered in the
   coordinator-owned AuditRuleCatalog at wiring (functional, scored,
   displayed); FindingRuleKeys promotion recorded as an OWNER ITEM for the
   next amendment batch.
2. Amendment #2 (render.hreflang-delta): DEFERRED — behavior already
   spec-correct (Error via render.metadata-delta field hreflang); clarity
   improvement only. Observation in the phase report.
3. Amendment #3 (RenderedPage.ServiceWorkerRegistrations): DEFERRED —
   optional; script-scan detection works through the locked interface;
   artifact-based interop preferred at wiring when available.
4. Severity vocabulary mapping: locked FindingSeverity = Error/Warning/
   Notice/Pass (no Critical). Spec §7.18 "CRITICAL" rendered-only noindex
   maps to Error (top of vocabulary) + critical:true evidence + direction
   + dual evidence — intent satisfied; recorded as a spec-vocabulary
   observation, not a deviation.
5. Stale test ResponseRenderedComparerTests.cs:55 (title Warning):
   coordinator fixes at wiring (Warning→Error; :73 description-metadata
   Warning stays valid — §7.18 names meta-robots, not general metadata).
6. Engineering choices (clamp 3–5/default 4, path grouping, per-run
   adaptive state, dual-storage accounting, template-switch Warning):
   accepted — spec leaves these open; documented in the report.
7. Queued reply REPLY-TO-WP-10-02-STATUS.md superseded by this acceptance
   (builder completed in line with all rulings; no send-back round needed).

### WP-10-05 — ACCEPTED 2026-11-20T04:30Z (coordinator)

Verification (coordinator canonical re-runs, CF_AGENT_ID unset):
Audit filter StructuredData|Scoring|IndexabilityStateEvaluator|ImageRuleAnalyzer
326/326; Crawler filter Waf|RedirectFinding 136/136 (shared project compiles
again — 01 fixed the CS0050/CS9007 blockers); frontend tests/frontend/project
56/56 + typecheck exit 0. Lane purity: exactly the owned set + the ONE
granted extension file; diff reviewed against the itemized five-assertion
list — exact match, no deletions, no weakening, CR citations present.

Coordinator rulings / observations:
1. Granted extension applied exactly (FINDINGS-2 §1) — accepted.
2. Alt takeover reuses internal ImageAltRule + AccessibilityRuleContext
   (zero drift) — approved design; DI disable is coordinator wiring §4
   (ONE construction covers full-audit + single-page paths — verified by
   builder; coordinator re-verifies at wiring).
3. CR-031 artifact-path convention applied (no contract change); reader
   accepts the instructed array shape + wrappers; coordinator verifies
   01's proxy storage shape at wiring.
4. SecurityAndProtocol: VERIFIED no producer exists anywhere (coordinator
   grep: no mixed-content/security/protocol analyzer; the mixed-content
   trap is a TLS trap, not_testable in the HTTP 17/17 walk). Category is
   scored + wire-ready (conformance test with a representative finding).
   §7.4 security row = unbuilt future-workpiece scope — observation for
   the phase report, NOT a phase-10 gap (D-021 requires the category
   scored, which it is).
5. Catalog reseed handoff accepted (4 redirect cases Indexability/Warning/
   60/low; deprecated-term StructuredData/Warning/55/low; image.oversized
   Images/Warning/60/low; image.format Images/Warning/50/low;
   accessibility.image-alt Images/Error/85/low; DROP legacy seed; MOVE
   hreflang seeds + HeaderHtmlEquivalencyAnalyzer:237 + RuleDefinition:84
   → Internationalization). Coordinator applies at wiring.
6. WAF asset soft-class entries (managed-challenge/rate-limit-soft-block)
   = coordinator wiring §4.6 (parse path test-proven).
7. Engine redirect target-facts wiring: call precedes terminal parse today
   — coordinator places re-evaluation after parse (status-only fallback
   until then); WAF RecordVerdict + frontier host stop = coordinator.
8. Legacy redirects.inappropriate constant retained for compile compat;
   never emitted; dropped at reseed. redirects.loop/chain stay local (not
   in FindingRuleKeys) — same owner-item treatment as dual-storage key.
9. Queued replies REPLY-TO-WP-10-05-ALT-FINDING.md / FINDINGS-2 superseded
   by this acceptance (all rulings applied by the builder; no send-back).

### WP-10-01 — ACCEPTED 2026-11-20T05:25Z (coordinator, after one send-back round)

Verification (coordinator canonical re-runs, CF_AGENT_ID unset):
Audit LabProbeIntegration 5/5 (strict loopback assertion UNRELAXED, test
file untouched); Crawler Rendering 173/173 incl. real-browser integration;
source build 0/0. Lane purity: only the four owned folders + report.

Send-back history (two dispatches + one fix round):
- First dispatch (632da47d) failed at startup, zero changes; re-dispatched
  (ba00a8d0) with identical brief.
- Implementation accepted in substance; §5.3 blocker ("inherent
  unsuppressible Chromium vendor egress; relax the strict assertion")
  REFUTED by coordinator canonical run: 4/5 lab tests pass the same
  assertion with the same proxy + Chrome; only the new CDP-pool test
  failed. Fix round dispatched (7de020f4) with the evidence.
- Coordinator's old-headless hypothesis was itself refuted by the fix
  agent's live Win32_Process command-line capture: local Chrome is
  157.0.8103.79 (not 131; resolver fallback working, O-006 pending);
  PuppeteerSharp 25.17.1 HeadlessMode.True emits --headless=new (old
  headless removed since Chrome 132). TRUE root cause: missing about:blank
  startup URL → new-headless Chrome opens the New Tab Page → NTP search
  preconnect emits vendor CONNECT tunnels through the launch proxy.
  Fix: inert about:blank startup tab (CdpBrowserProcess.cs:103–111),
  --headless=new kept. Bisect proof: 3× real CONNECT tunnels without the
  URL, zero with it. §5.3 recommendation RETRACTED in the report.
- BONUS defect found+fixed during re-verification: wait-rule
  ReadDomQuietStateAsync parsed fractional DOMHighResTimeStamp with
  TryGetInt64 → -1 → long.MaxValue ("infinitely quiet") → premature settle
  under load. Fixed: TryGetDouble (CdpPageSession.cs:714) + observer-absent
  soundness gate (:276–285). Pool's own proxy-opt-in test STRENGTHENED to
  the same strict loopback boundary (all captures incl. CONNECT).

Coordinator rulings:
1. CR-006 deviation (spec over brief/YAML wording) — recorded, correct.
2. RouteThroughProxy compat shim kept (accepted test needs non-empty
   capture); waterfall stays CDP-direct regardless; default renders not
   proxy-routed — correct per CR-006.
3. Proxy-artifact instruction executed exactly (locked RenderedNetworkEntry
   camelCase array, kind 3, verified against 05's reader end-to-end).
4. Amendments: §4.7 requests 1–2 DEFERRED (artifact path + concrete
   RenderDetailedAsync cover them); request 3 (RenderTestResponse.
   NetworkWaterfall C# member + mapper) + UI-21 browser-info REST surface
   execute under WP-10-04 at step 4 (TS mirror already carries
   networkWaterfall from A1; parity test asserts TS side only — wire
   completion, not a lock break).
5. O-006 (physical CfT 131.0.6778.85 bundling) remains the owner item;
   resolver + pin manifest + fallback chain + fake-bundle tests delivered.
6. Mechanism lesson: verify launch-posture hypotheses against live process
   evidence before ruling; the fix agent correctly reported the refutation
   instead of blindly applying the instructed change (loop §8 spirit).