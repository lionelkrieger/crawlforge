# PHASE-10-REPORT — Flow 2: Rendering and Capture Modes

Status: IN PROGRESS (skeleton; completed at phase close)
Coordinator: lead (Claude Opus 5.5)
Phase YAML: buildplan/phases/phase-10-flow-2-rendering-and-capture-modes.yaml

## 1. Run order and workpieces

| Step | Wave | Workpieces | Mechanism | Commits |
|---|---|---|---|---|
| 0 | — | criterion #56 UI proof | coordinator | 1e30ab9 |
| 1 | 10A0 | WP-10-A1 contract amendment | subagent + coordinator integration | 2cb4311, f418243, 0307fe8, f6dde46, dc005d4, 6ea666b (ledger + RE-LOCK) |
| 2 | 10A | WP-10-01 / WP-10-02 / WP-10-05 | 3 parallel subagents | pending |
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

## 8. Workpiece acceptance summary (filled at reviews)

(pending)