# PHASE-10-REPORT — Flow 2: Rendering and Capture Modes

Status: IN PROGRESS (skeleton; completed at phase close)
Coordinator: lead (Claude Opus 5.5)
Phase YAML: buildplan/phases/phase-10-flow-2-rendering-and-capture-modes.yaml

## 1. Run order and workpieces

| Step | Wave | Workpieces | Mechanism | Commits |
|---|---|---|---|---|
| 0 | — | criterion #56 UI proof | coordinator | 1e30ab9 |
| 1 | 10A0 | WP-10-A1 contract amendment | subagent + coordinator integration | 2cb4311, f418243, 0307fe8, f6dde46, dc005d4, 6ea666b (ledger + RE-LOCK) |
| 2 | 10A | WP-10-01 / WP-10-02 / WP-10-05 | 3 parallel subagents (+1 fix round for 01) | 7c4b8ac (01), 690f2ed (02), dc15529 (05) |
| 3 | 10B | WP-10-03 | subagent | pending |
| 4 | 10C | WP-10-04 serial wiring | coordinator | pending |
| 5 | — | phase gate + criteria #50/51/52 + phase-09 regression | coordinator | pending |

## 2. Owner decisions applied (not reopened)

- D-021: three new scoring categories (Images, Security & protocol,
  Internationalization); five WAF classes.
- CR-029: four inappropriate-redirect cases, toggleable in UI-04 analysis
  tab, all on by default.
- CR-030: bundled versioned retired/restricted rich-result list, never
  fetched at runtime; Microdata terms resolve like RDFa.
- CR-031: oversized-image uses transferred bytes where captured, else
  "not measured" — never "pass".

## 3. Deviations and observations (loop §8)

- **CR-006 vs WP-10-01 brief/YAML**: the brief's "proxy capture for rendered
  traffic" contradicts living spec 03/03 §5.6.2 (rendered traffic captured
  DIRECTLY by the CDP control layer; proxy routes non-rendered plain-HTTP).
  Spec followed; deviation recorded in the dispatch + builder report.
- **Wire tokens interstitial/botblock** deviate from 03/02 §2.2
  (js-interstitial/bot-block): pre-existing locked wires, NOT
  additive-fixable; owner observation carried from phase 09.
- (builder deviations appended at review)

## 4. Gate incidents (full log: GATE-INCIDENTS.md)

- MSBuild nodeReuse stdout-pipe deadlock (Acceptance verify-packaging):
  orphan nodes hold the pipe; MSBUILDDISABLENODEREUSE=1 prevents it.
- Transport boundary lock extended to CrawlTargetTransportFactory (f6dde46).
- Excel scale-test sheet selector: D-021 grew the summary sheet past the
  10 MB heuristic; product export correct (500001 rows); name-based
  resolution fix (dc005d4).
- Security.Tests DPAPI/SPP environmental testhost stall; canonical
  --no-build supplement 92/92.
- NET-02 governance fix: CrawlTargetTransportFactory in approved
  Crawler/Fetching scope (f418243). Note: Composition.CrawlTargetHttpClientFactory
  (BoundaryHttpClients.cs) is a pre-existing parallel factory — unify in a
  later phase (cleanup observation, no behavior impact).
- Schema v5 migration lesson: FK table-rebuild (v3) must render the shape
  AS OF its own version (SchemaCatalog.AdditiveVersionTables), never the
  final AllTables shape.

## 5. Mechanism notes

- spawn_teammate cap (8) reached by inactive phase-09 teammates → phase-10
  builders dispatched as subagents (fresh context, complete briefs;
  mid-flight steering impossible; post-completion send_message rounds work).
- Contracts re-locked at A1 acceptance (ledger commit 6ea666b message).

## 6. Owner items

- O-006: physically bundle the pinned Chrome for Testing binary into
  app/assets/browser (packaging; D-003 forbids runtime downloads). The
  resolver + pin manifest + fallback chain are delivered by WP-10-01.

## 7. Acceptance evidence (filled at step 5)

- Phase gate: run-phase-gates.ps1 -Phase phase-10 (12 commands).
- Criteria #50/#51/#52: UI-started js/dual/adaptive fixture audits, mode
  selected in UI-04; trap verdicts + screenshots.
- Phase-09 regression: 17/17 HTTP traps + --surfaces UI-11,UI-12 with D-021
  category assertions (walker category_scores + category_probe evidence).
- nonGet == [] after every audit; expectations never edited.

## 8. Workpiece acceptance summary

### WP-10-01 — direct CDP rendering (7c4b8ac, accepted 05:25Z after send-back)
173/173 Rendering (builder ×2 + coordinator canonical), LabProbeIntegration
5/5 canonical, build 0/0. D-002 CDP layer replaces PuppeteerSharp (pool
name/ctor preserved); CR-006 CDP-direct waterfall; CR-009 wait rule;
D-003 pin manifest + resolver chain (O-006 bundling pending owner);
CR-031 rendered + proxy bytes; proxy artifacts in the locked
RenderedNetworkEntry shape verified against 05's reader.
**Defects found+fixed**: (a) NTP vendor egress — missing about:blank
startup URL let new-headless Chrome open the New Tab Page, whose search
preconnect tunneled vendor CONNECTs through the launch proxy; the initial
"relax the strict assertion" recommendation was REFUTED by coordinator
evidence (4/5 sibling lab tests pass the same assertion) and retracted;
fixed with the inert startup tab, assertion unrelaxed. (b) Latent wait-rule
bug: fractional DOMHighResTimeStamp parsed via TryGetInt64 → long.MaxValue
→ premature settle under load; fixed TryGetDouble + observer-absent gate.
**Lessons**: local Chrome is 157 (not the pinned 131 — resolver fallback
working); PuppeteerSharp HeadlessMode.True emits --headless=new (old
headless removed since Chrome 132); verify launch-posture hypotheses
against live process command lines before ruling.

### WP-10-02 — capture modes + comparer (690f2ed, accepted 04:15Z)
Audit RenderedComparison 25/25 (builder + coordinator canonical);
CaptureModes 48/48 (identical-source scratch; official command at wave
gate). Four modes + override precedence; material-change switching;
dual-storage WARNING never a cap; §7.18 severities (locked vocabulary
Error/Warning/Notice/Pass — spec CRITICAL maps to Error + critical:true +
dual evidence); word counts reach scopeDiagnostics (harness ev.words);
JS-only links; SW informational; isolated Jint snippets; CaptureSource
through real SQLite; G03 closed at component level. Amendments deferred
(dual-storage key catalog-registered at wiring + owner item; hreflang-delta
and SW-fact promotions unnecessary today).

### WP-10-05 — D-021 carryovers (dc15529, accepted 04:30Z)
Audit 326/326 + Crawler Waf|RedirectFinding 136/136 + frontend project
56/56 + typecheck 0 (builder + coordinator canonical). Images/
Internationalization moves incl. zero-drift alt takeover; SecurityAndProtocol
scored/wire-ready (verified: no producer exists — §7.4 security row is
future-workpiece scope; mixed-content trap is TLS-only, outside HTTP 17/17);
five WAF classes soft→AIMD/hard→HostStop; CanonicalElsewhere bit 16;
CR-029 four toggles (null=all ON, per-toggle flip tests); CR-030 versioned
bundled list + Microdata LocalName; CR-031 not-measured≠pass via artifact
reads; AuditScoreWireConformanceTests real producer→score→read-back.
Granted one-file test extension applied exactly (five itemized assertions).

### Wave-10A gate — PASS (retry, 2026-11-20T09:35Z)

First attempt (07:26Z): 18/20 test projects green incl. Scale 10/10
(51m39s) and Security 92/92 (no DPAPI stall this time); 2 failures, BOTH
stale assertions superseded by accepted behavior changes — reconciled by
the coordinator (test-only, behavior-strengthening, cited):
- Audit ResponseRenderedComparerTests.TitleAlteredByScript: Warning→Error
  (FS-CRAWL-001 §7.18, WP-10-02).
- Crawler SitemapLiveFixtureTests.Live_RedirectChain: legacy
  redirects.inappropriate → redirects.inappropriate-temporary-status
  (CR-029 per-case keys, WP-10-05).
Retry (08:37Z, logs phase-10-wave-10A-retry-20261120-083757): ALL 8
commands exit 0 — host build, frontend build, electron tsc, solution tests
(20 projects; Audit 838/838, Crawler 1077/1077 incl. CaptureModes 48 +
Rendering 173 official runs), ContractConformance, governance,
traceability, buildplan. MSBUILDDISABLENODEREUSE=1 throughout.
Note: owner commit cd49fb3 (D-022 gate rhythm v2.1 for phases 11–12 —
phase 10 unaffected) accidentally swept the coordinator's uncommitted
WP-10-04 WIP (captured shapes, evidence, the comparer reconciliation,
plan, first gate log); nothing changed on disk; carried forward here.