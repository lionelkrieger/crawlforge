# PHASE-08 REPORT — Hotfix Record and Foundations

- **Phase:** phase-08 (buildplan v2.0) · **Orchestrator:** lead · **Builders:** cf-wp-08-03 (WP-08-03), cf-wp-08-04 (WP-08-04)
- **Dispatch:** 2026-11-17T22:43:59Z (wave 8B, two builders in parallel) · **Phase completed:** 2026-11-18T21:51:31Z (gates 9/9 PASS) · **Report date:** 2026-11-18
- **Governing prompt:** `audit-work/prompts/12-phase-08-prompt.md` · **Spec authority:** `architecture/` (read-only throughout)
- **Product HEAD at dispatch:** 76c32ec (no `app/src` changes since probe-2 HEAD fc9ac0f — verdicts were required to reproduce, and did)

## Executive summary

Phase 08 recorded the two shipped hotfixes (WP-08-01/02) and built the two verification
foundations every later phase depends on:

1. **WP-08-03 — contract-conformance harness** (`app/tests/CrawlForge.ContractConformance.Tests`,
   commit `4424c84`): a live-backend xUnit suite covering **every cross-layer payload** —
   108 REST operations / 88 route groups, 20 hub events (18 spec + 2 drift), 11 IPC channels,
   8 MCP tools / 15 resources / 5 prompts, 40 storage entities — with a reflection-resolved
   coverage inventory, committed live captures, and exactly **one** failing test: the
   REST counter-key mismatch tagged `[Trait("KnownGap","WP-09-06")]`.
2. **WP-08-04 — fixture acceptance harness** (`app/tests/fixture-acceptance`, commits
   `10a1e5a` + `0995114`): drives the real product through **normal entry points only**
   (UI over CDP, REST, MCP stdio, schedule run-now) against the fixture site and
   **reproduces probe-2 exactly**: 4 pass / 5 partial / 10 fail / 12 not_testable in BOTH
   http and js modes, and the identical 24-surface UI walk (6 functional, 1 placeholder,
   16 not_reachable, 1 not_applicable_yet).
3. **WP-08-05 — serial wiring** (commit `3c290f9`): conformance project in `CrawlForge.sln`,
   fixture-acceptance `README.md`, and `app/tools/gates/run-phase-gates.ps1` executing each
   phase's `orchestrator_gates` on the canonical surface with the pinned SDK.

The phase's real product is the **defect inventory**: 18 recorded product defects/drifts
(§4) and 6 spec-notation/ambiguity items (§5), each with live evidence, exact citations and
routed owners — discovered without changing a single line of product code. Two independent
harnesses confirmed the same configuration-snapshot race from different angles (§4.1).
The first canonical full-solution gate run additionally exposed **5 pre-existing failures
in baseline-era test suites** (stale v1.1 plan path, pre-HOTFIX-01 hub field names, an
unguarded live-MySQL requirement), and the second exposed **2 load-sensitive test-infrastructure
defects** (a thread-unsafe embedding test double; a 90s E2E wait budget starved under
full-solution parallel load). All were triaged per §5.5/§7.4 and repaired through the §11
serialized orchestrator amendments **WP-08-A1/A2** — test-infrastructure only, no product
change, no weakened assertion (§6, gate-blocker triage).

**Parallelism: yes** — wave 8B ran both builders concurrently with disjoint write scopes and
process/port ownership; coordination events (one review send-back, two session-interruption
recoveries, one concurrent-run conflict) are logged in §6.

---

## 1. Per-workpiece status, commits, done_when proof

| Workpiece | Status | Commit(s) | Acceptance |
|---|---|---|---|
| WP-08-01 HOTFIX-01 record | completed (pre-phase) | `db53378` | recorded at phase start |
| WP-08-02 HOTFIX-01b record | completed (pre-phase) | `fc9ac0f` | recorded at phase start |
| WP-08-03 contract-conformance harness | **completed** 2026-11-18T11:02:20Z | `4424c84` (88 files) | lead review, ledger `d84304e` |
| WP-08-04 fixture acceptance harness | **completed** 2026-11-18T10:05:25Z | `10a1e5a` (55 files) + `0995114` (28 files, review send-back fix) | lead review, ledger `b0068c0` |
| WP-08-05 serial wiring | **completed** (this report's gate run) | `3c290f9` | lead (self), ledger commit with gate results |
| WP-08-A1 amendment (stale gate tests) | **completed** (§11 orchestrator amendment) | `3fd0198` | lead (self), canonical suite re-runs + full gate |
| WP-08-A2 amendment (load-robust test infra) | **completed** (§11 orchestrator amendment) | `9a7a467` | lead (self), canonical suite re-runs + full gate |

### WP-08-01 / WP-08-02 (records)

- WP-08-01: AIMD configuration wire shape + host-throttle event field names aligned;
  `WireShapeContractTests` added. Evidence: hotfix-01-report audits 6/7/8/10 completed;
  probe-2 audits 11/12/13 ran on HEAD fc9ac0f. Reopened WP-02-01/WP-05-02 as specified.
- WP-08-02: `app/global.json` pins SDK 10.0.404 (rollForward latestFeature). Evidence:
  probe-2 ran on that HEAD; every phase-08 build/test resolved 10.0.404 (gate log §7).

### WP-08-03 — done_when proof

> done_when: harness builds + starts its own Host; every inventoried payload has a
> conformance test; noindex + redirect-chain http-mode payloads captured and asserted;
> KnownGap failing test for the REST counter mismatch; storage-to-engine zero-network proof.

- **Live Host:** `Harness/LiveBackendFixture.cs` runs the launch-crawlforge.ps1 step-2
  build (`dotnet build <Host> -c Debug`, WorkingDirectory=app root → pinned SDK), starts the
  DLL headlessly with `--CrawlForge:Port=0` + `--CrawlForge:DataDirectory=<scratch outside
  repo>`, discovers the port/token from `loopback-handshake.json`, connects SignalR + MCP
  streamable-HTTP, runs the bounded fixture scenario on its **own fixture instance (47312)**.
- **Inventory coverage:** `inventory/payload-inventory.json` (2145 lines) — 108 REST ops /
  88 groups, 20 hub entries, 11 IPC, 8/15/5 MCP, 40 entities.
  `PayloadInventoryTests.EveryInventoriedPayload_HasConformanceCoverage` resolves **every**
  `coveredBy` reference to a real `[Fact]`/`[Theory]` by reflection;
  `RestCatalog_Has88GroupsAnd108Operations` + `RestCatalog_EveryRouteExistsOnBackendControllers`
  cross-check the frozen frontend catalog against live controller routes;
  `HubCatalog_MatchesSpecFrontendAndBackend` cross-checks spec/frontend/catalog name sets.
- **Trap captures:** `captured/trap-noindex-{url-row,url-detail,findings,summary}.json` and
  `captured/trap-redirect-chain-*.json` committed; `FixtureTrapCaptureTests` asserts wire
  shape (semantics deferred to WP-08-04 by design); G27/G03 limitations shape-locked.
- **Zero-network proof:** `StorageEngineConformanceTests.OfflineReaudit_ReadsStoredArtifacts_ZeroNetwork`
  re-audits an **MCP-started** source audit with the fixture STOPPED — live, passing.
  (REST-started re-audit is broken by defect §4.1; the MCP path keeps the engine DTO snapshot.)
- **KnownGap:** `AuditCountersWireConformanceTests.RestCounters_MatchEngineCounters`
  `[Trait("KnownGap","WP-09-06")]` — the ONLY failing test; asserts CONFORMANCE
  (queued↔discovered, error↔failed), so it turns green automatically when WP-09-06 fixes
  the mapping. No defect-lock tests anywhere (lead steer, §6).
- **Verification (builder AND independent lead canonical re-run, CF_AGENT_ID unset):**
  - `dotnet test …ContractConformance.Tests --filter "KnownGap!=WP-09-06"` →
    **Passed! 0 failed / 92 passed / 23 skipped (115), EXIT 0** (lead run duration 17m29s).
  - Unfiltered → **92 passed / 1 failed / 23 skipped (116)**; sole failure
    `RestCounters_MatchEngineCounters`: "engine counters_json = discovered:182/fetched:117/failed:2,
    REST wire = queued:0/fetched:117/error:0 … only 'fetched' overlaps".
  - `governance-analyzer --fail-on-violation` → `"violations": [], "passed": true`, EXIT 0.
  - Build: 0 warnings / 0 errors (TreatWarningsAsErrors).
- Builder scratch evidence retained: `%LOCALAPPDATA%\CrawlForge-conformance-wp0803`
  (runs 1–10, filtered-gate-run.log, full-run-6..10.log). Token-bearing handshake captures
  gitignored; committed captures credential-free.

### WP-08-04 — done_when proof

> done_when: reproduce probe-2's verdicts (4/5/10/12 in http AND js) and probe-2's UI
> statuses (16 not_reachable, UI-11 placeholder).

- **Full proof run** `results/20261118T000959Z` (product HEAD 10a1e5a, expected-results
  SHA-256 `9068684d683e7f4dcedabef4223d35ed69421e49ed4af591e353288a2d780211`):
  - http matrix: **4 pass / 5 partial / 10 fail / 12 not_testable** — per-trap identical to probe-2 (§2).
  - js matrix: **4 pass / 5 partial / 10 fail / 12 not_testable** — per-trap identical to probe-2 (§2).
  - UI walker: **6 functional (UI-01/10/18/19/21/23), 1 placeholder (UI-11),
    16 not_reachable (UI-02..09, 12..17, 20, 22), 1 not_applicable_yet (UI-24)** — identical to probe-2.
  - Entry points: audit 23 = **UI-started** (CR-023 proven: captured POST /api/projects
    {"name":"Fixture harness http …"} + POST /api/audits {"configurationId":"18"}; project 13,
    config 18 bounded via REST PUT — UI-04 absent, recorded); audit 24 = REST js;
    audit 25 = MCP stdio `start-audit`; audit 27 = schedule run-now. All completed.
  - `nonGet == []` after EVERY audit; tagHits inspected; clean identified-tree shutdowns.
- **Verification command** `python …run_fixture_acceptance.py --mode http --traps
  noindex,canonical-conflict,soft-404-server,missing-alt`: **3 consecutive cold-start runs
  exit 0** after the send-back fix (`095146Z`/`095402Z`/`095750Z`, all UI-start proven=true)
  **plus the lead's independent cold-start re-run exit 0** (`100328Z`, project 22/audit 33,
  committed in `d84304e`). Pre-fix lead re-run had failed exit 2 → send-back (§6).
- **Self-tests:** 39 stdlib unittest green (builder + lead re-run), incl. verdict-logic
  mutation flips, snapshot-race retry signature exactness, CDP click/fill failure propagation.
- Verdict logic reviewed: `harness/verdicts.py` — 31 evidence-driven per-trap evaluators,
  inertness DETECTED from evidence (never assumed), no hardcoded verdicts; probe-2 pooling
  convention documented and per-run verdicts retained.

### WP-08-05 — done_when proof

> done_when: full phase-08 gate list passes canonical (CF_AGENT_ID unset); fixture harness
> runs noindex through the REST entry point and the UI walker reaches UI-01/UI-10.

- `CrawlForge.ContractConformance.Tests` added to `CrawlForge.sln` (tests solution folder);
  `dotnet build CrawlForge.sln` → **0 warnings / 0 errors** (verification command 1).
- `app/tests/fixture-acceptance/README.md`: modes, CLI, entry points, verdict semantics,
  exit codes, evidence layout (DESIGN.md remains the builder's design record).
- `app/tools/gates/run-phase-gates.ps1`: parses `orchestrator_gates` via Python+PyYAML;
  canonical guard (aborts if CF_AGENT_ID set — negative-tested); pinned-SDK check
  (`dotnet --version` from app/ must be 10.0.x); dotnet commands always run from app/
  (global.json walk-up); fail-fast; per-command logs + summary under `tools/gates/logs/`;
  phase-08 KnownGap filter appended to unfiltered `dotnet test` commands (§5.4);
  TRACEABILITY.md CRLF-only side effect auto-restored (content diffs never discarded).
  - `-ListOnly`: correct 9-command plan (filter appended only to the sln test).
  - `-ValidateAll` (contract_tests self-test): **phase-08 9/9 commands resolve**;
    phase-09..13, 15, 16 fully resolve; phase-14 `CrawlForge.Integrations.Tests` correctly
    flagged not-yet-built (INFO). Commands are resolved, not executed, for future phases.
  - `-SelfTest`: **6/6 planning assertions pass** (filter transformation, already-filtered
    untouched, phase-09 never transformed, cwd rules, CF_AGENT_ID guard child abort).
- Verification command 2: `--mode http --traps noindex` → **EXIT 0**
  (`results/20261118T110910Z`, noindex pass, nonGet==[], clean shutdown).
- done_when evidence run: `--mode http --traps noindex --rest-start --surfaces UI-01,UI-10`
  → **EXIT 0** (`results/20261118T111304Z`): noindex pass via **REST entry** (audit 35,
  186 URLs); walker **UI-01 reachable_and_functional** (23 project rows, 34 audit rows,
  real aggregates) and **UI-10 reachable_and_functional** (live run data for audit 35:
  state badge, counters, host-throttle telemetry).
- **Full phase-08 gate run:** §7 (executed via the new script, canonical surface).

### WP-08-A1 — done_when proof (§11 orchestrator amendment)

> done_when: both repaired suites pass canonical (MySQL facts skip without a configured
> server); the full phase-08 gate list passes including the noindex fixture run.

- **Trigger:** gate run 1 (§7) command #4 `dotnet test CrawlForge.sln --filter
  "KnownGap!=WP-09-06"` exited 1 with exactly 5 failures, all in baseline-era suites
  (untouched since commit `100cc1d` except where noted), none in phase-08 deliverables:
  1. `Acceptance.Tests.EvidenceIntegrityTests.Wave7A_PhaseLedger_RecordsCanonicalWave7AVerification`
     — FileNotFoundException on `buildplan/phases/phase-07-integration-and-acceptance.yaml`;
     the v2.0 plan migration archived that ledger at `buildplan/v1.1/phases/` (file present,
     all four asserted evidence strings verified in it).
  2. `E2E.Tests.AimdHostThrottleE2ETests.LiveAudit_429Pressure_…` — KeyNotFoundException
     reading `previousRate` from the live host-throttle payload; HOTFIX-01 (`db53378`,
     recorded as WP-08-01) locked `rateBefore/rateAfter/concurrencyBefore/concurrencyAfter`
     (hub spec §10.3 "rate before/after; concurrency before/after"; frontend guards;
     `WireShapeContractTests`; live capture `captured/hub-host-throttle.json`) but did not
     update this baseline E2E assertion.
  3–5. `E2E.Tests.MySqlParityE2ETests` (4 facts) — "Unable to connect to any of the
     specified MySQL hosts": the suite hard-requires the v1.1 sign-off machine's live
     MySQL 8.0.28 (its own doc comment) with plain `[Fact]`; this machine has no MySQL
     server, and the three sibling MySQL suites already skip gracefully via
     `LiveServerFactAttribute` (`CF_MYSQL_TEST_HOST` convention).
- **Triage (§5.5/§7.4):** not defects in any completed phase-08 workpiece (no reopens);
  plan correction via serialized orchestrator amendment §11 → WP-08-A1 recorded in the
  phase YAML + index.yaml (55 workpieces; `check_buildplan.py` PASS, no ownership overlaps).
- **Repairs (owned_paths only; no product code; no assertion weakened):**
  1. `EvidenceIntegrityTests.cs` — path → `../buildplan/v1.1/phases/phase-07-integration-and-acceptance.yaml`;
     the four `Assert.Contains` evidence checks unchanged.
  2. `AimdHostThrottleE2ETests.cs` — the four hub-payload field reads → the locked HOTFIX-01
     names; history-DTO assertions (PreviousRate/NewRate on the REST history row — still the
     wire shape) and envelope checks unchanged.
  3. `MySqlParityE2ETests.cs` + new `Fixtures/LiveMySqlParityFact.cs` — the 4 facts use
     `LiveMySqlParityFactAttribute` (skips unless `CF_MYSQL_TEST_HOST` set, identical
     convention to the sibling suites); Settings honor `CF_MYSQL_TEST_HOST/PORT/USER/PASSWORD`
     overrides (dedicated `crawlforge_e2e` database unchanged); every assertion preserved
     when a server is configured.
- **Verification (canonical, CF_AGENT_ID unset, SDK 10.0.404 from app/):**
  - `dotnet test …CrawlForge.Acceptance.Tests` → **Passed! 72 / 0 failed / 0 skipped, EXIT 0**
    (15m13s — the suite includes live artifact/UI evidence checks).
  - `dotnet test …CrawlForge.E2E.Tests` → **Passed! 82 / 0 failed / 4 skipped, EXIT 0**
    (1m31s; the 4 skips are exactly the MySQL parity facts, skip reason names the
    `CF_MYSQL_TEST_HOST` convention; the AIMD host-throttle live audit passes against the
    locked payload shape).
  - Amendment commit `3fd0198` (8 files: 4 test files + 2 ledger files + 2 gate-run
    summaries); `check_buildplan.py` PASS (55 workpieces, no ownership overlaps).
  - Full gate re-run: §7 run 2.

### WP-08-A2 — done_when proof (§11 orchestrator amendment)

> done_when: both repaired suites pass canonical; the full phase-08 gate list passes
> including the noindex fixture run.

- **Trigger:** gate run 2 (§7, post-A1) command #4 exposed 2 NEW failures — both absent
  from run 1 and from the isolated A1 verification, both load-sensitive test-infrastructure
  defects under full-solution parallel gate load (Scale.Benchmarks' 500k-row seed +
  Acceptance live UI checks + conformance live audit overlapping E2E/Intelligence):
  1. `Intelligence.Tests.Embeddings.SemanticPipelineTests.NonIndexableUrlsAreExcluded` —
     expected 4 embed calls, got 3. Root cause: `FakeEmbeddingProvider.EmbedAsync` counted
     with a non-atomic `EmbedCallCount++` and appended to a thread-unsafe `List<string>`
     while `SemanticLinkingPipeline` embeds with bounded concurrency
     (`SemanticPipelineOptions.MaxConcurrency` default **2**, `SemanticJobProgress.cs:21`;
     `Task.WhenAll` batches at `SemanticLinkingPipeline.cs:173-178`). A lost increment —
     interleaving-dependent, widened by CPU contention. The product's bounded concurrency
     is by design (§12.6 per-URL failure isolation); the double was wrong.
  2. `E2E.Tests.WafDetectionReportingE2ETests.ChallengeAudit_EmitsWafBlockAlert_AndBlockMetrics`
     — TimeoutException after the 90s completion-wait budget. **Not a product failure:**
     `E2EBackend.WaitForAuditAsync` fails fast with `InvalidOperationException` when an
     audit reaches Failed; the thrown `TimeoutException` proves the 20-URL audit was still
     RUNNING — CPU-starved past the harness budget. The same test passed in run 1 and in
     the isolated E2E verification (whole suite 1m31s). `E2EBackend.StartAuditAsync` calls
     the engine directly (no REST snapshot-overwrite race involvement).
- **Repairs (owned_paths only; no product code; no assertion weakened):**
  1. `TestDoubles.cs` — `Interlocked.Increment` counter + locked `Texts` log; every
     call-count assertion (cache reuse, re-embed, exclusion) unchanged and now accurate.
  2. `WafDetectionE2ETests.cs` — completion-wait budget 90s → 300s (harness patience under
     gate load, not a product requirement); all functional assertions (challenge findings,
     site-wide block-rate alert, ≥19 block metrics, >0.9 rate) unchanged.
  3. `Fixtures/E2EBackend.cs` — timeout message now includes the last observed audit status
     (diagnostic only; fail-fast-on-Failed unchanged).
- **Verification (canonical, CF_AGENT_ID unset, SDK 10.0.404 from app/):**
  - `dotnet test …CrawlForge.Intelligence.Tests` → **Passed! 341 / 0 failed / 0 skipped, EXIT 0** (3m2s).
  - `dotnet test …CrawlForge.E2E.Tests` → **Passed! 82 / 0 failed / 4 skipped, EXIT 0** (1m31s).
  - `check_buildplan.py` PASS (56 workpieces, no ownership overlaps).
  - Full gate re-run: §7 run 3.
- Run 2 was stopped once #4's verdict was determined (fail-fast would discard #5–9 anyway);
  the surviving test tree was killed by verified parent chain (root PID 27408 → vstest →
  testhosts), ports re-verified clean.

---

## 2. Fixture harness verdicts vs probe-2 — MUST match, and they do

Source of truth: `audit-work/reports/runtime-probe-2/RUNTIME-PROBE.md` §5 (audits 11/12,
HEAD fc9ac0f) vs harness proof run `results/20261118T000959Z` (audits 23/24, HEAD 10a1e5a;
no app/src changes between). **All 31 traps × 2 modes identical; zero diffs.**

| Trap | probe-2 http/js | harness http/js | Match |
|---|---|---|---|
| redirect-chain | partial / partial | partial / partial | ✓ |
| redirect-loop | partial / partial | partial / partial | ✓ |
| noindex | pass / pass | pass / pass | ✓ |
| robots-blocked | partial / partial | partial / partial | ✓ |
| canonical-conflict | pass / pass | pass / pass | ✓ |
| broken-links | partial / partial | partial / partial | ✓ |
| orphan | fail / fail | fail / fail | ✓ |
| js-only-link | not_testable ×2 | not_testable ×2 | ✓ |
| script-injected-content | not_testable ×2 | not_testable ×2 | ✓ |
| late-meta-change | not_testable ×2 | not_testable ×2 | ✓ |
| soft-404-server | pass / pass | pass / pass | ✓ |
| soft-404-client-route | not_testable ×2 | not_testable ×2 | ✓ |
| hash-route | not_testable ×2 | not_testable ×2 | ✓ |
| client-side-redirect | not_testable ×2 | not_testable ×2 | ✓ |
| postback-links | fail / fail | fail / fail | ✓ |
| cookieless-session | fail / fail | fail / fail | ✓ |
| large-viewstate | fail / fail | fail / fail | ✓ |
| crawler-trap-filter | fail / fail | fail / fail | ✓ |
| crawler-trap-calendar | fail / fail | fail / fail | ✓ |
| cookie-gate | fail / fail | fail / fail | ✓ |
| consent-banner | not_testable ×2 | not_testable ×2 | ✓ |
| service-worker | not_testable ×2 | not_testable ×2 | ✓ |
| structured-data-errors | fail / fail | fail / fail | ✓ |
| missing-alt | pass / pass | pass / pass | ✓ |
| mixed-content | not_testable ×2 | not_testable ×2 | ✓ |
| hreflang-no-return | fail / fail | fail / fail | ✓ |
| duplicate-titles | partial / partial | partial / partial | ✓ |
| thin-page | fail / fail | fail / fail | ✓ |
| live-websocket | not_testable ×2 | not_testable ×2 | ✓ |
| click-only-navigation | not_testable ×2 | not_testable ×2 | ✓ |
| never-place-bet | not_testable ×2 | not_testable ×2 | ✓ |
| **Counts** | **4 / 5 / 10 / 12** | **4 / 5 / 10 / 12** (both modes) | ✓ |

**UI surfaces (24):** identical statuses — UI-01/10/18/19/21/23 reachable_and_functional;
UI-11 reachable_but_placeholder (static "mount into this workspace from the report
workpieces" text); UI-02..09, UI-12..17, UI-20, UI-22 not_reachable (16); UI-24
not_applicable_yet. Evidence: `surfaces.json` + 39 screenshots in the proof run.

**Conventions reproduced:** probe-2's §5 table pools audits 11/12 jointly; the harness
pools session bulk runs into `modes.{http,js}.traps` while keeping per-run verdicts in
`runs[].traps` (the js run's time-cap truncation stays visible). `nonGet==[]` held after
every audit (vacuous pass, as in probe-2 — click discovery inert). Rendering inertness and
click-discovery inertness are **detected from evidence**, so a future conforming build is
evaluated normally instead of being hardcoded not_testable.

**Refinements to probe-2 (new evidence, not contradictions):**
- probe-2 §6 recorded re-audit failing with the `$.id` error — its target was REST-started.
  The conformance harness proved **MCP-started audits re-audit cleanly** (DTO snapshot
  intact; `McpToolSurface.cs:127` bypasses the controller overwrite). Same root-cause
  family as §4.1, scope now precisely bounded.
- The **probabilistic REST/UI start failure** (§4.1) was newly exposed: probe-2's audits
  11/12/13 all won the snapshot race; the harness's js audit 18 lost it
  (`results/20261117T235434Z/audit-18-data.json`, failed <10s, 0 URLs, exact `$.id` error).

---

## 3. Contract-conformance results (incl. KnownGap)

| Run | Result | Exit |
|---|---|---|
| Filtered `KnownGap!=WP-09-06` (builder, CF_AGENT_ID=cf-wp-08-03) | 92 passed / 0 failed / 23 skipped | 0 |
| Filtered (lead canonical re-run, CF_AGENT_ID unset) | 92 passed / 0 failed / 23 skipped | 0 |
| Unfiltered (builder run 10) | 92 / 1 / 23 — sole failure = KnownGap | 1 (by design) |
| Unfiltered (lead canonical re-run) | 92 / 1 / 23 — sole failure = KnownGap | 1 (by design) |
| governance-analyzer `--fail-on-violation` (builder + lead) | violations: [], passed: true | 0 |

**The KnownGap test** (`RestCounters_MatchEngineCounters`): producer
`AuditRunCounters.ToPayload()` writes `{discovered, fetched, analyzed, failed}`
(`Host/Execution/AuditRunState.cs`); consumer `AuditWireMapper.ParseCounters`
(`Api/Controllers/AuditWireMapper.cs:22-30`) reads `{queued, fetched, error, blocked,
wafBlocked?}` with missing keys defaulting to 0. Only `fetched` overlaps → every REST
audit response silently reports queued=0/error=0/blocked=0. The test asserts the
CONFORMANT mapping (queued↔discovered, error↔failed) — it fails today and turns green
automatically when WP-09-06 reconciles the keys. Gates exclude exactly this one test
(phase-08 only; later phases run the full suite — AGENT-INSTRUCTIONS §12).

**Skipped-with-reason tests (23):** every recorded drift has a contract-side passing test
(fix-stable invariants: shared dimensions, cadence, producer contracts) plus a skipped
live test asserting the spec/frontend shape, citing its GAPS.md entry. **No defect-lock
tests** — nothing asserts broken behavior as expected (lead steer; a defect-lock would
fail when the product is fixed, and no later workpiece owns this project).

**Coverage summary** (GAPS.md): REST 108/108 live-swept (2 binary/text exemptions);
hub 12 live captures + 6 no-producer contract-side + drift skips; IPC 11 source-level
cross-checks; MCP 8 tools + 15 resources + 5 prompts live; storage 40 schema checks +
row locks. Every inventory entry's `coveredBy` resolves by reflection.

---

## 4. Recorded product defects (NO product code changed in phase 08)

All defects below were measured against live backends by the two harnesses, with committed
captures and/or retained scratch logs. Owners are lead-routed proposals for phase-09+
dispatch; the register/architecture were not edited (discoveries flow through this report).

### 4.1 G-REAUDIT-SNAPSHOT — configuration-snapshot wire-vs-entity mismatch (TWO independent confirmations)

- **Overwrite side:** `AuditsController.StartAsync` (`Api/Controllers/AuditsController.cs:140-160`)
  starts the engine (entity-DTO snapshot, numeric ids) and THEN overwrites
  `audits.configuration_snapshot_json` with the WIRE shape (`ProjectWireMapper.ToConfiguration`,
  string ids).
- **Read side:** `AuditExecutionEngine.ReAuditAsync` (`Host/Execution/AuditExecutionEngine.cs:436-438`)
  deserializes the snapshot as `CrawlConfigurationDto` with `PayloadJson` → `JsonException`
  at `$.id` → **unhandled 500, empty body, no envelope**, both refresh modes.
  `RunAuditAsync:497` / `RunAnalysisOnlyAsync:888` read the same snapshot → the main crawl
  only survives when the engine reads the row BEFORE the overwrite lands.
- **Confirmed twice, independently:**
  - cf-wp-08-03: deterministic re-audit 500 for REST/UI-started audits
    (`captured/gap-reaudit-rest-start-response.json`; scratch run-20261118-000314 backend-console.log).
  - cf-wp-08-04: probabilistic START failure — js bulk audit 18 failed <10s, 0 URLs, identical
    `CrawlConfigurationDto`/`$.id` error (`results/20261117T235434Z`). Lead-approved handling:
    retry ONLY on that exact signature, max 3 attempts, same entry point, every attempt
    recorded in `start_attempts` + `product_defects`; all-fail keeps fail + non-zero exit.
- **Scope refinement:** MCP/scheduler/matrix-started audits keep the DTO snapshot
  (`McpToolSurface.cs:127` bypasses the controller) → re-audit works (live zero-network proof).
- **Owners:** **WP-09-06** (owns `Api\Controllers\` — overwrite side) + **WP-09-11**
  (owns `Host\Execution\` — PayloadJson read side). probe-2's audits all won the race.

### 4.2 G-MATRIX-SEED-RACE — redirect-matrix autoSeedAudit duplicate insert

- `RedirectMatrixController.Upload` (`Api/Controllers/RedirectMatrixController.cs:94-95`)
  with `autoSeedAudit:true` calls `StartAuditAsync` (full crawl of the source configuration)
  then `IngestAsync` (matrix source URLs) **concurrently, no dedup coordination**. The matrix
  source URL is also a crawl seed → both inserters write the same canonical URL →
  `SQLite Error 19: UNIQUE(url_records.audit_id, canonical_url)` in `CrawlPhaseAsync` →
  seeded audit FAILED; if the crawl inserter wins, the upload itself 500s.
- Evidence: `captured/gap-matrix-seed-race.json`; scratch run-20261118-002104 backend-console.log.
- Conformance path used instead: `autoSeedAudit:false` + `GET …/redirect-matrix/results`
  (the real consumer of `RedirectMatrixValidator.ValidateAsync`, `RedirectMatrixController.cs:128`,
  emitting throttled `matrix-row-validated` batches per FS-CRAWL-002 §10.8) — live-tested.
- **Owners:** **WP-09-06** (controller ordering) + **WP-09-03** (owns `Crawler\ListMode\` —
  seeder; sibling start-before-persist defect G10) + **WP-09-11** (engine insert path).

### 4.3 G-COUNTERS — REST audit counter-key mismatch (the KnownGap)

See §3. **Owner: WP-09-06** (counter-key reconciliation; hotfix-01-report §6, CODEMAP §4.10).

### 4.4 G-PROGRESS — hub crawl-progress payload drift

- Producer `AuditExecutionEngine.PublishProgressAsync` (`AuditExecutionEngine.cs:1663-1690`):
  `{auditId:number, counters:{discovered,fetched,analyzed,failed}, queued}`.
- Consumer `CrawlProgressPayload` (`frontend/src/contracts/hub/events.ts:44-61`): string
  auditId, `counters: AuditCounters` (queued/fetched/error/blocked/wafBlocked?),
  `throughputPerSecond`, `elapsedSeconds`, `estimatedRemainderSeconds`, `phase`.
- Cadence note: ≤1/s verified as AVERAGE emission rate (60 events/62s = 0.97/s); per-gap
  received-at measurement unreliable (TCP Nagle/delayed-ACK); producer throttle source-locked.
- **Owners:** counter keys → **WP-09-06**; throughput/phase fields + numeric id → **WP-09-11**.

### 4.5 G-COMPLETED-COUNTERS / G-HUB-COUNTERS — counter-key family

- `audit-completed.finalCounters` carries engine keys (`AuditExecutionEngine.cs:1236-1290`,
  event :1282) vs consumer `AuditCounters` (`events.ts:98-102` → `audits.ts:36-43`).
- Hub live view (engine keys) vs REST snapshot (consumer keys) cannot agree key-for-key
  until G-COUNTERS is reconciled.
- **Owner: WP-09-06 family.**

### 4.6 G-G — systemic numeric ids on hub events vs string EntityId

- Producers (all numeric): engine stage-changed/crawl-progress (:1654-1690),
  audit-failed/completed (:1271-1290); `ScheduleHubEvents.cs:33-59`; `ExportModels.cs:171-178`;
  `RedirectMatrixValidator.cs:279` + `ListModeModels.cs:213-221`; `LogImportService.cs:425-503`
  (importId). Consumer: locked `EntityId` = string; every guard requires `isString`
  (`guards.ts` :272/:288/:291/:300/:310/:320/:330). Masked in store lookups by
  `hubDemux.ts` `String(...)` coercion; guards and plugin relay see the wrong type.
- Evidence: `captured/hub-numeric-auditid-drift.json`.
- **Owner: WP-09-11** (phase-09 serial wiring).

### 4.7 G-D / G-E — schedule-fired / schedule-run-completed field drift

- `ScheduleFiredPayload` producer `{scheduleDefinitionId:long, auditId:long|null, triggerType,
  startedAt}` (`ScheduleHubEvents.cs:33-37`) vs consumer `{scheduleId:string, auditId:string,
  triggerType}` (`events.ts:118-122`, guard `guards.ts:220-228` — would reject the live payload).
- `ScheduleRunCompletedPayload` producer `scheduleDefinitionId`/`completionActions`
  (:51-59) vs consumer `scheduleId`/`completionActionResults` (`events.ts:125-130`,
  guard :230-239). Skipped-overlap variant (no auditId, failureReason) documented; tests
  pair events to the harness's own run via the run-now response auditId.
- **Owner: phase-14 WP-14-01** (scheduler + wiring).

### 4.8 G-F — export-delivery field drift

- Producer `{exportJobId:long, destinationId:long, state, attempt, message?, timestamp}`
  (`ExportModels.cs:171-178`) vs consumer `{exportId:string, destinationId:string, state}`
  (`events.ts:133-137`, guard :241-249).
- **Owner: WP-09-09** (export dialog/delivery).

### 4.9 G-MATRIX-VERDICT — matrix-row-validated numeric enum verdict + numeric urlId

- Producer `MatrixRowValidatedItem(long UrlId, …, RedirectMatrixVerdict Verdict)`
  (`ListModeModels.cs:213`), hub default STJ → enums as NUMBERS. Live capture:
  `{"auditId":1,"rows":[{"urlId":3,…,"verdict":0}]}`. Consumer requires `urlId: EntityId`
  (string) + `verdict: string` (`events.ts:180-184`).
- **Owner: WP-09-11** (hub enum/id serialization family).

### 4.10 G-I — export-progress emitted but uncataloged

- Emitted on the wire; absent from `HubEventCatalog` and the frontend contract. Hub passes
  unknown events through (open-string evolution) → shell receives an event with no contract.
- **Owner: WP-09-09.**

### 4.11 G-N — local-notification: backend-only additive event

- `HubEventCatalog.LocalNotification` (:75) + `HubLocalNotificationSink`
  (`ScheduleHubEvents.cs:119-134`) emit `{title, message}`; absent from spec §10.3 and the
  frontend contract; the accepted scheduler uses it for OS-notification requests.
- **Owner: orchestrator routing** (additive-event registration into spec/frontend via CR).

### 4.12 G05 — six spec events with no producer

- `url-batch`, `findings-updated`, `backend-lifecycle`, `waf-blocks`, `embedding-progress`,
  `embedding-completed`: specified (02-realtime-websocket-hub §10.3/§7.2 + App C) and
  cataloged, but no backend code publishes them (CODEMAP §6 producer table).
- **Owners:** the phases that wire each surface (hub wiring / WAF / semantics).

### 4.13 G-MCP-PAGING — MCP resource paging vs CR-011

- Spec `05-api-hub-and-mcp/03-local-mcp-server-and-agents.md:122` (CR-011): every
  data-returning tool/resource ≤ one page (default 50, max 500) **and a cursor**.
- Code: `McpResourceSurface.DefaultReadLimit = 200`, no cursor (`McpResourceSurface.cs:92,165-181`).
  All 15 resources still read live and shape-asserted.
- **Owner: phase-13 / CR-011 MCP surface rebuild.**

### 4.14 G-MCP-TEMPLATES — resources/templates/list unimplemented

- `McpJsonRpcDispatcher` returns `-32601 MethodNotFound` (static resources only).
- **Owner: phase-13 MCP surface.**

### 4.15 G-REST-IDEMPOTENCY — duplicate audit start not idempotent

- Spec `05-api-hub-and-mcp/01-rest-api-contract.md` §10.4: duplicate start idempotent while
  an identical run is active. Code: `AuditExecutionEngine.StartAuditAsync` (:334-368) always
  creates a new audit.
- **Owner: phase-09 wiring.**

### 4.16 G27 — redirect chains not persisted hop-by-hop (probe-2 carryover)

- Single final target stored (`url_records.redirect_target_url`); intermediate hops have no
  rows; `UrlDetailWire.redirectChain` not populated hop-by-hop. Shape-locked only; hop-count
  semantics measured by the fixture harness (redirect-chain = partial, matching probe-2).
- **Owner: crawler redirect persistence (orchestrator routing).**

### 4.17 G03 — rendered DOM artifacts absent (renderer inert, probe-2 carryover)

- http mode: no `content_artifacts` kind RenderedDom(2); body artifacts (kind 0) stored and
  feed the offline re-audit. js-mode rendering inertness DETECTED from evidence by the
  fixture harness (no rendered tree + js-only-target absent + word count ≈ raw markup).
- **Owner: rendering phase.**

### 4.18 G-NOINDEX-FLAG-QUESTION — noindex page reported indexable (semantics question)

- Fixture `/pages/noindex.html` serves `<meta name="robots" content="noindex, follow">`
  (`fixture-site/pages.mjs:46`), yet engine stores `is_indexable=1`, `indexability_flags=0`
  and REST reports `indexable: true` (`captured/trap-noindex-url-row.json`,
  `captured/storage-noindex-row.json`). Indexability FINDINGS are produced
  (countsByCategory.indexability=266; `indexability.noindex` emitted — the trap PASSES on
  the finding, consistent with probe-2).
- **Not asserted either way** (shape-only tests): may be a rule-engine flag defect OR a
  field-semantics ambiguity (`is_indexable` = HTTP-fetchable vs search-indexable).
- **Owner: decision needed** — route to the rule-engine workpiece or a spec CR clarifying
  the field semantics; flagged for phase-09 dispatch.

---

## 5. Spec ambiguities, notation divergences, interpretation decisions

1. **G-S — gcGen0/1/2Counts (spec plural) vs gcGen*Count (code singular).**
   Spec `05-api-hub-and-mcp/02-realtime-websocket-hub.md:47` writes plural; backend
   (`SystemHealthSampler.cs:21-29`), frontend contract (`system.ts:194-196`), guard
   (`guards.ts:126`) and UI (`StatusBar.tsx:122`) are ALL singular and consistent —
   **no functional defect**. (Builder's initial claim of a frontend plural-read defect was
   wrong and self-corrected; lead independently verified.) **Resolution: spec clarification
   via CR or a code rename — owner decision, NOT a phase-12 defect.**
2. **G-CRON-NAMING — schedule wire field "cron" carries interval:/calendar: rules.**
   `ScheduleCreateRequest.Cron` (`OperationsWireContracts.cs:109`) is parsed as a
   `RecurrenceRule` (`interval:<timespan>` / `calendar:HH:mm`, `RecurrenceRule.cs:90-121`,
   FS-CRAWL-001 §6.1), NOT a cron expression; null/empty fails firing ("Invalid recurrence
   rule", run-now 409). Field-name vs content-format divergence — spec-notation item for
   owner routing; no functional defect when the documented rule format is used.
3. **G-NOINDEX-FLAG-QUESTION** (§4.18) — semantics ambiguity, owner decision.
4. **Phase-08 gate vs KnownGap (interpretation, implemented).** The phase-08
   `after_each_wave` list contains unfiltered `dotnet test CrawlForge.sln`; with the
   conformance project wired in, that includes the single KnownGap failing test.
   AGENT-INSTRUCTIONS.md §12 (line 337) is explicit: *"phase-08 gates exclude exactly that
   gap, every later gate runs the full suite."* `run-phase-gates.ps1` therefore appends
   `--filter "KnownGap!=WP-09-06"` to every **phase-08** `dotnet test` command lacking a
   filter (logged per transformation); phases 09+ run verbatim. Self-tested (`-SelfTest` 6/6).
5. **Schema catalog citation.** WP-08-03 `contract_tests` cites
   `02-data-and-persistence/02-complete-schema-catalog.md` §9.2 + App A, which is absent
   from the workpiece's `context_files`. Resolved as part of the task definition (the
   storage-entity inventory derives from it); noted for plan hygiene at next revision.
6. **CODEMAP inaccuracies (app/CODEMAP.md, commit 11e6652 — not spec, recorded for correction).**
   - §2 step 7 claims `POST /api/system/shutdown`; the route does not exist
     (`SystemController` exposes only health + compact). Harnesses kill their own processes.
   - `CRAWLFORGE_DATA_DIRECTORY` env claim: the product binds `CrawlForge:DataDirectory`
     from configuration only (`Host/Configuration/CrawlForgePaths.cs:14`); harnesses pass
     the config argument (env set belt-and-braces).

---

## 6. Parallelism and coordination

**Yes — wave 8B ran two builders in parallel** (max_parallel_agents: 2), dispatched
2026-11-17T22:43:59Z with complete standalone briefs (full workpiece YAML + hard rules).

- **Disjoint ownership:** A (cf-wp-08-03) owned only
  `app/tests/CrawlForge.ContractConformance.Tests/`, a headless backend on an OS-assigned
  port with scratch data dir `%LOCALAPPDATA%\CrawlForge-conformance-wp0803`, and its own
  fixture on **47312**; B (cf-wp-08-04) owned only `app/tests/fixture-acceptance/`, the
  running Electron app, fixture **47311**, CDP **9222/9223**. `CF_AGENT_ID` MSBuild
  isolation for builders; gates + B's launcher run canonical (CF_AGENT_ID unset — the
  sanctioned launcher exception). Neither touched the other's processes or ports.
- **CODEMAP injection (user instruction):** `app/CODEMAP.md` (commit 11e6652) read in full;
  §6 (hub producer table incl. export-progress/local-notification mismatches) embedded in
  A's packet; §§2/9/10 (startup/handshake, frontend mounting, Electron) in B's. Both builders
  used it; A later recorded two CODEMAP inaccuracies (§5.6).
- **Review cycle — B send-back (one round):** lead's independent re-run of the exact
  verification command failed exit 2 (submit click never fired; no POST reached the
  backend). Root cause: `cdp.click/fill` returned `{clicked:false}`/`{ok:false}` which
  callers ignored; fixed sleeps masked misses. Send-back required assert+retry everywhere,
  evidence waits, 3 consecutive cold-start successes. B's fix also root-caused a second
  failure mode via live CDP probe: `HubDashboard.startAudit` (HubDashboard.tsx:213-246)
  navigates away after starting → the Start button legitimately vanishes mid-click
  (`vanishedAfterDispatch` semantics; REST audit row = source of truth). Hub refresh
  semantics + bounded 50-row project window documented. Re-review passed: 3 consecutive
  exit-0 runs + lead's 4th independent exit-0 run; 39 self-tests; verdict logic untouched.
- **Review cycle — A steers (no send-back):** (a) forbade defect-lock tests (would fail
  when the product is fixed; no later workpiece owns A's project) → contract-side passing
  tests + skipped-with-reason + captures; A applied the pattern to ALL drift tests.
  (b) Corrected A's probe-2 framing speculation (probe-2 re-audit FAILED on a REST-started
  target; MCP-started success is new refining evidence). (c) Re-routed A's initially-claimed
  frontend gcGen defect to spec-notation after A self-corrected and lead verified.
  (d) Approved G-MATRIX-SEED-RACE handling (results-endpoint conformance path) and routing.
  (e) Directed G-NOINDEX-FLAG-QUESTION to shape-only + record (semantics = B's domain).
- **Concurrent-run conflict (resolved by builder):** during lead's canonical verification
  re-run, A's redundant post-commit re-verification was also in flight on 47312. A detected
  the lead run, killed ONLY its own process tree (verified parent chains: dotnet test →
  vstest → testhost → backend), left the lead tree untouched, and stood by. Lead restored
  `captured/` to A's committed snapshot afterwards (live captures regenerate per run).
- **Session interruptions (two, harness crashes):**
  - ~9h10m gap after B's send-back dispatch: both builders' turns cut mid-work. Recovery:
    state re-established (git/tree/ports/processes/scratch), both resumed via self-contained
    send_message syncs; **no work lost** (B's fix partially on disk, A mid run-6). B
    completed the fix + 3-run acceptance; A completed runs 6–10 and reported ready-to-verify.
  - ~7h30m gap during the first full gate run: the run was killed mid-command-#4
    (`dotnet test sln`); no stale processes/ports remained; gate run relaunched cleanly from
    the start (the script is stateless/fail-fast). Partial log retained as evidence.
- **Environment ghosts:** intermittent ghost CDP listener on 9222 (PID 17600, no live
  process) → B's harness uses netstat-truth port selection + bind-failure fast-detect
  (9223 fallback); cleared after the first interruption. Orphan fixture PID 7568
  identity-verified then killed by B. Lead's first A-review run resolved the **.NET 11
  preview SDK** (invoked from repo root where `app/global.json` doesn't apply →
  StaticWebAssets build error); re-run from app/ with 10.0.404 passed — the gate script now
  enforces dotnet cwd=app root and verifies the SDK version before running.
- **check_traceability.py side effect:** rewrites `architecture/TRACEABILITY.md` CRLF vs
  committed LF (content identical; `--ignore-cr-at-eol` diff empty). Restored after every
  gate run; the gate script automates this iff the diff is CRLF-only (never discards content).
- **Data safety:** `%APPDATA%\CrawlForge` backed up to `CrawlForge-backup-2026-11-18` before
  dispatch; only clearly named harness projects created (B: projects 5–24 across runs;
  A: scratch-dir projects, never the real data dir); owner projects (incl. Airpower) never
  touched; harness-owned schedules deleted after use; Place bet/Deposit/Logout never clicked.
- **Ledger discipline:** builders never edited buildplan/architecture/register; orchestrator
  wrote only status fields + counts (plus the §11 amendment records); `check_buildplan.py`
  passed after every ledger change (54 workpieces during wave 8B, 56 after the amendments;
  9 phases, no ownership overlaps).
- **Gate-blocker triage (§5.5/§7.4/§11):** gate run 1's command #4 failed on 5 pre-existing
  stale tests (§1 WP-08-A1); run 2 then exposed 2 load-sensitive test-infrastructure defects
  (§1 WP-08-A2). The orchestrator did NOT reopen completed workpieces (the gates proved no
  defect in them), did NOT relax the gate list or skip commands, and did NOT edit outside
  an amendment's owned_paths: it created the serialized amendment workpieces WP-08-A1/A2
  (recorded in phase YAML + index per §11.4), repaired the stale/unsafe test infrastructure
  within their owned files, re-verified canonically, and re-ran the full gate list. The
  KnownGap mechanism was NOT extended — exactly one failing test remains allowed (§12.2).

---

## 7. Phase-08 gate run (canonical surface)

Executed via `app/tools/gates/run-phase-gates.ps1 -Phase phase-08` (CF_AGENT_ID unset,
SDK 10.0.404 verified from app/). Logs: `app/tools/gates/logs/phase-08-<timestamp>/`.

**Run 0 (interrupted):** `phase-08-20261118-131729` — killed mid-command-#4 by the second
session interruption (§6); #1–3 had passed (25s/9s/9s). Partial log retained as evidence.

**Run 1:** `phase-08-20261118-205122` — #1 Host build 0/0 (10s), #2 frontend vite build (8s),
#3 electron tsc (8s) all PASS; #4 `dotnet test sln --filter "KnownGap!=WP-09-06"` (filter
appended per §12) ran all 19 assemblies: every suite green EXCEPT the 5 pre-existing stale
tests (§1 WP-08-A1) — Acceptance 71/1, E2E 81/5; **ContractConformance itself passed inside
the sln run: 92/0/23 (17m32s)**; Scale.Benchmarks 10/10 (39m). Fail-fast stopped #5–9.
Triage → WP-08-A1 amendment (§6).

**Run 2 (post-A1):** `phase-08-20261118-221924` — #1–3 PASS (21s/2s/4s); #4 ran with heavier
assembly overlap than run 1: the A1 repairs held (Acceptance's Wave7A test passed; E2E's
AIMD test passed; the 4 MySQL facts skipped cleanly), but 2 NEW load-sensitive failures
appeared (Intelligence embed-call-count race; WAF E2E 90s wait starvation — §1 WP-08-A2).
Stopped once #4's verdict was determined; test tree killed by verified parent chain.
Triage → WP-08-A2 amendment.

**Run 3 (post-A2):** `phase-08-20261118-224122` — **PHASE GATES: PASS, all 9 commands exit 0**
(4209s total, canonical surface, SDK 10.0.404):

| # | Gate | Result | Duration |
|---|---|---|---|
| 1 | `dotnet build CrawlForge.Host -c Debug` | 0 warnings / 0 errors | 11s |
| 2 | `cd frontend && npm run build` | vite build OK (846 modules) | 2s |
| 3 | `cd electron && npx tsc -p tsconfig.build.json` | typecheck OK | 3s |
| 4 | `dotnet test CrawlForge.sln --filter "KnownGap!=WP-09-06"` (filter appended per §12) | **all 19 assemblies green** — Contracts 34, Governance 48, Security 92, Data.Sqlite 75, Data.Migrations 56/2 skip, Data.Repositories 145/1 skip, Data.MySql 154/9 skip, Intelligence 341, Audit 577, Mcp 87, Crawler 546, Export 40, Api 219, E2E 82/4 skip, Host 38, Boundary 27, Acceptance 72, Scale.Benchmarks 10 (44m25s), ContractConformance 92/23 skip | 2990s |
| 5 | `governance-analyzer --fail-on-violation` | 630 files, 0 violations, passed:true | 14s |
| 6 | `check_traceability.py` | PASS: 251 tagged + 24 accounted = 275; UI 246 bound / 5 exempt / 24 surfaces; CRLF-only rewrite auto-restored | 1s |
| 7 | `check_buildplan.py` | PASS: 56 workpieces / 9 phases, no ownership overlaps | 1s |
| 8 | `dotnet test ContractConformance.Tests --filter KnownGap!=WP-09-06` | **92 passed / 0 failed / 23 skipped** | 1037s |
| 9 | fixture harness `--mode http --traps noindex,canonical-conflict,soft-404-server,missing-alt` | **4 pass / 0 partial / 0 fail / 0 not_testable**; UI-started audit 36 (project 25, config 29 REST-bounded), nonGet==[], clean shutdown, exit 0; evidence `results/20261118T214902Z` | 150s |

The single KnownGap test (`RestCounters_MatchEngineCounters`, WP-09-06) remains the only
failing test in the unfiltered suite — excluded from phase-08 gates exactly per
AGENT-INSTRUCTIONS §12; phases 09+ run the full suite verbatim.

---

## 8. Commits and ledger

| Commit | Content |
|---|---|
| `93e07d3` | WP-08-03/WP-08-04 dispatch wave 8B builders (ledger claims) |
| `10a1e5a` | WP-08-04 fixture acceptance harness (55 files) |
| `0995114` | WP-08-04 deterministic UI entry (send-back fix, 28 files) |
| `b0068c0` | WP-08-04 acceptance (ledger completed 2026-11-18T10:05:25Z) |
| `4424c84` | WP-08-03 contract-conformance harness (88 files) |
| `d84304e` | WP-08-03 acceptance (ledger completed 2026-11-18T11:02:20Z) + lead WP-08-04 review evidence |
| `3c290f9` | WP-08-05 serial wiring (sln, README, gate runner) + verification evidence |
| (amendment) `3fd0198` | WP-08-A1 stale gate-test repairs + amendment ledger record (§11) + gate-run 0/1 summaries |
| (A2) `9a7a467` | WP-08-A2 thread-safe test double + load-robust E2E wait + ledger record + run-2 summary |
| (final) | WP-08-05 + WP-08-A1 + WP-08-A2 + phase-08 completion ledger + gate summary + this report |

All commits carry `Co-Authored-By: qwen4-max-preview <noreply@anthropic.com>`; nothing
pushed/forced/reset/rebased; one workpiece per commit (WP-08-04's review fix is a prefixed
follow-up, history intact). index.yaml at phase close: phases completed 1 / in_progress 0 /
pending 7 / unscheduled 1; workpieces completed 7 / pending 49 (56 total incl. WP-08-A1/A2).

## 9. Routing queue for phase-09 dispatch (carried by this report)

| Item | Suggested owner(s) |
|---|---|
| G-REAUDIT-SNAPSHOT (start race + re-audit 500) | WP-09-06 + WP-09-11 |
| G-MATRIX-SEED-RACE | WP-09-06 + WP-09-03 + WP-09-11 |
| G-COUNTERS / G-COMPLETED-COUNTERS / G-HUB-COUNTERS / G-PROGRESS keys | WP-09-06 |
| G-PROGRESS fields, G-G numeric ids, G-MATRIX-VERDICT | WP-09-11 |
| G-F export-delivery, G-I export-progress | WP-09-09 |
| G-REST-IDEMPOTENCY | phase-09 wiring |
| G-D / G-E schedule events | phase-14 WP-14-01 |
| G-MCP-PAGING / G-MCP-TEMPLATES | phase-13 (CR-011) |
| G05 no-producer events | respective wiring phases |
| G-N local-notification, G-S gcGen notation, G-CRON-NAMING, G-NOINDEX-FLAG-QUESTION, CODEMAP corrections | orchestrator/owner decisions (CR candidates) |
| G27 redirect persistence, G03 renderer | crawler/rendering phases |

The conformance suite's skipped-with-reason tests name these gaps; when an owner fixes a
defect, its skipped test is un-skipped and the KnownGap test (WP-09-06) turns green
automatically — the harness measures the fix without modification.