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