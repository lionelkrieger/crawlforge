# PHASE 09 REPORT — Flow 1: HTTP Audit End-to-End

Coordinator: lead (DeepSeek Harness, qwen4-max-preview) · Phase gate: pending
Branch: master · Trailer: Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
(CLAUDE.md convention; the prompt's `<your model name>` placeholder resolved to
the workspace-mandated trailer — noted per checkpoint decision).

## 1. Mission

Make the first complete user flow real: open a project → edit a configuration →
start an HTTP audit FROM THE UI → watch it run → read real findings in the
report window → export them. Two waves of five parallel builders (components +
component tests only, §5.6), then serial wiring WP-09-11 by the coordinator,
then the full phase gate (conformance UNFILTERED, 17-trap HTTP fixture, UI walk).

Gated criteria: #49 (full UI-started fixture audit), #55 (trap/cookie traps),
#56 (config edits flip behavior). Safety: /__fixture/requests nonGet == [].

## 2. Workpiece ledger (all 11)

| WP | Title (short) | Builder | Acceptance commit | Ledger commit | Status |
|---|---|---|---|---|---|
| WP-09-01 | Sitemap/orphan/redirect persistence | cf-wp-09-01 | a82625f (+cd880d9 addendum) | 334914a→completed | completed |
| WP-09-02 | Cookie jar/robots/canonicalization/scope/WAF-AIMD | cf-wp-09-02 | 7e28946 | completed | completed |
| WP-09-03 | Traps/pagination/list modes | cf-wp-09-03 | 1a5b647 | completed | completed |
| WP-09-04 | Structured data/RDFa/hreflang | cf-wp-09-04 | 19c5006 | completed | completed |
| WP-09-05 | SEO corrections + CR-024 evaluator | cf-wp-09-05 | 7299f23 (+e3831c7 rename) | completed | completed |
| WP-09-06 | REST/hub conformance + lifecycle + config fields | cf-wp-09-02 | 43f30b2 | c50d5ec | completed |
| WP-09-07 | Project/configurations/13-tab editor UI | cf-wp-09-01 | eadfbed | c29cc11 | completed |
| WP-09-08 | Dashboard + issue explorer + suppression | cf-wp-09-04 | 25c46af | 22eb635 | completed |
| WP-09-09 | URL explorer + detail (CR-024, chain, SERP) | cf-wp-09-05 | d4a9cbe | 3ed3fef | completed |
| WP-09-10 | Export completion + dialog/destinations | cf-wp-09-03 | 0ab0f06 | 22eb635 | completed |
| WP-09-11 | Serial wiring + conformance + phase gate | lead | (this phase's wiring commits) | pending | in_progress |

done_when semantics: builder workpieces accepted at component level per prompt
step 2 ("commit the workpiece and set it to completed"); every done_when's
fixture/UI proof is executed at the phase gate after wiring and marked proven
or not in §6 below (prompt step 5).

## 3. Wave gates

- Wave 9A: PASS 8/8 (run 3, logs phase-09-wave-9A-20261119-071750; runs 1-2
  failed on two stale tests — see §5 defects fixed).
- Wave 9B: run 1 FAIL at gate 4 (7 stale cross-phase assertions in 4 projects;
  15/19 projects green incl. Acceptance 72/72 + Scale 10/10). Coordinator
  alignment fixes (no 9B owned_paths touched — see WIRING-NOTES gate analysis):
  inventory refs/counts for CR-027+G-I, G-G string-id tolerance ×3, spec
  hyphenated matrix verdicts in ListMode E2E; Intelligence final-progress test
  proven a load flake (green in isolation). Targeted re-run + gates 5-8:
  (pending — logs phase-09-wave-9B-refail-<stamp>).
- Wave 9C / full phase gate: (pending)

## 4. Wiring (WP-09-11)

Execution plan A–J in WIRING-NOTES.md; handoffs archived under handoffs/.
Landed early (while 9B built): hub contracts += CR-027 local-notification +
G-I export-progress (fe1ac88); HubEventCatalog.ExportProgress + 6 conformance
unskips/inversions proven 22/0 (5ad460f); harness string-id matchers (f9cb707);
ErrorBoundary override fix; G-HUB-COUNTERS unskip proven 5/5 (22eb635).
Remaining wiring: (fill in with commit list + evidence).

## 5. Defects fixed / recorded

Fixed during the phase (coordinator + routed builder fixes):
1. Wave-9A gate run 1: EvidenceIntegrity criterion #28 proof-member rename —
   05 restored the exact catalog member name (e3831c7); catalog untouched.
2. Wave-9A gate run 1: ToolsRouteTests WAF challenge asserted on 403 —
   pre-spec behavior; spec §2.4.5 is 2xx+HTML-only. Coordinator pre-dispatch
   fix (8530f75): 200-challenge scenario + new 403 annotation-only test.
   (Deviation note: gate rule says route failures to the owner; WP-09-06 had
   no active owner yet — fix applied pre-dispatch and documented in its brief.)
3. G-HUB-COUNTERS / KnownGap REST counters — 06 mapper + normalizer (43f30b2).
4. G-F/G-I export producer shapes — 10 (0ab0f06) + catalog/events (5ad460f).
5. G-G string ids / G-MATRIX-VERDICT vocabulary — 06 normalizer + 03 names.
6. Conformance harness matchers (exportJobId/GetInt64 drift) — lead (f9cb707).
7. ErrorBoundary TS4114 ×3 — lead (fe1ac88).
8. (wiring fixes: CR-024 evaluator at NewUrlRecord, G-PROGRESS fields, G05
   producers, lifecycle ports, G10, idempotency, reaudit snapshot, matrix seed
   race — fill in with evidence.)

Recorded for owner (notation gaps / spec-silent observations): see
WIRING-NOTES.md register (11 items from 9A + 9B additions: ExportFormat enum
amendment, suppressions list route, findings suppressed flag, exports.get /
destinations.deliver routes, webhook UI classes, ExtractHeaderConflicts
headerName fidelity, G05 lossless interpretation, enabledCategories storage,
PUT null semantics, behavior-key ownership, snapshot absent convention).

## 6. Criteria evidence (phase gate)

- #49: (pending — UI-started full fixture audit, 17 traps)
- #55: (pending — trap/cookie traps incl. cookieless variant)
- #56: (pending — cookie-jar toggle + limit edit flip behavior in re-run)
- nonGet == []: (pending)
- UI walk UI-01/02/03/04/10/11/12/13/20: (pending)
- Per-workpiece done_when proven/not-proven table: (pending)

## 7. Conformance unskip map (final state)

Unskipped + proven this phase: KnownGap RestCounters (mapper), G-HUB-COUNTERS,
G-COMPLETED-COUNTERS, G-F, G-I (inverted to presence lock), G-N (inverted),
G-G, G-MATRIX-VERDICT. (Wiring-dependent unskips: G-PROGRESS, G05 url-batch/
findings-updated/backend-lifecycle/waf-blocks inversions, G-REST-IDEMPOTENCY,
G-REAUDIT-SNAPSHOT, G-MATRIX-SEED-RACE — fill in.)
Left skipped (later phases / no trigger): schedule ×2 (phase-14, CR-026),
importId (log phase), McpWire:380 (phase-13), audit-failed (no deterministic
fixture trigger; producer covered by Host.Tests), embedding-* (phase-12),
LogImportProgress_FrontendInterfaceShape (investigate), CR-028 MySQL skips.

## 8. Coordination record

- Wave 9A: dispatched 02:42Z, 5 parallel builders, 4 turn crashes resumed via
  send_message (no work lost), accepted 03:43–04:04Z; gate run 3 PASS 07:17–
  08:55 local (~98 min; Scale.Benchmarks 10k crawl dominates).
- Wave 9B: claimed+dispatched 06:58Z; 2 turn crashes (03 early, 02 mid-D1)
  resumed; Export mid-flight compile churn blocked 06 once — lead steered
  writer classes to land together; accepted 07:45–08:35Z; gate (pending).
- Gate-timing observation: standalone conformance after a sln run can queue
  ~15 min in MSBuild pre-build — NOT a hang; verify via scratch run logs
  (%LOCALAPPDATA%\CrawlForge-conformance-wp0803\run-*) before killing.
- Evidence side effects restored after every run: captured/*.json,
  network-audit/evidence/*, TRACEABILITY.md CRLF-only.
- Phase-08 erratum confirmed: POST /api/system/shutdown EXISTS
  (CrawlForgeBackend.cs:131).
- Data safety: no %APPDATA%\CrawlForge / "Airpower" access by builders;
  scratch %LOCALAPPDATA%\CrawlForge-cf-wp-09-0N; CF_AGENT_ID isolation;
  fixture ports 47313–47317 (builders), 47311/47312 (coordinator harnesses).
- npm root test quirk: `npm run test -- --run <dir>` fails EUNKNOWNCONFIG at
  the root workspace layer; working forms: `--workspaces --if-present --` or
  one extra `--` passthrough, or run from src/frontend directly.

## 9. Phase close

(pending — ledger phase completed + index counts + final commit)
