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

## After all three accepted
- [ ] Wave-10A after_each_wave gate (8 commands; MSBUILDDISABLENODEREUSE=1;
      Security supplement protocol if the DPAPI stall recurs).
- [ ] Ledger completed marks + index counts; dispatch wave-10B (03).