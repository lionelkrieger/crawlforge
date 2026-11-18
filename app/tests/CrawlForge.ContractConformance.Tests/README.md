# CrawlForge.ContractConformance.Tests (WP-08-03)

Contract-conformance harness for **every cross-layer payload** in CrawlForge:
REST envelopes, SignalR hub events, Electron IPC channels, the local MCP
surface, and storage-to-engine persistence. Each payload is exercised against
a **live loopback backend that the harness builds and starts itself** — no
mocks stand in for a producer/consumer boundary.

## Running

```powershell
# from the repository root (E:\Crawlforge), PowerShell (not Git Bash)
$env:CF_AGENT_ID = 'cf-wp-08-03'   # agent-scoped bin/obj isolation

# gate run: the ONE documented failure is excluded
dotnet test app\tests\CrawlForge.ContractConformance.Tests --filter "KnownGap!=WP-09-06"

# full run: exactly one failure is expected —
# AuditCountersWireConformanceTests.RestCounters_MatchEngineCounters
# [Trait("KnownGap","WP-09-06")] (REST counter-key mismatch, hotfix-01 §6)
dotnet test app\tests\CrawlForge.ContractConformance.Tests
```

Prerequisites: .NET SDK 10.0.x (pinned by `app/global.json`) and Node.js on
PATH (the fixture site runs `node app/tests/fixture-site/server.mjs`). The
harness uses fixture port **47312** (47311 belongs to the shared fixture /
cf-wp-08-04). First run builds the backend (~1 min) and runs a bounded
220-URL fixture audit, an MCP-started offline source audit + offline re-audit,
and redirect-matrix validation; expect several minutes for the whole suite.

The project is standalone-runnable (`dotnet test` on the project directory);
solution wiring is WP-08-05's job.

## What the harness does

`Harness/LiveBackendFixture.cs` (xUnit collection fixture, one per run):

1. Creates a fresh scratch data directory under
   `%LOCALAPPDATA%\CrawlForge-conformance-wp0803\run-<timestamp>-<id>`
   (never `%APPDATA%\CrawlForge`; the owner's real data is untouched).
2. Starts the fixture site on port 47312 and resets its recorder.
3. Builds `CrawlForge.Host` (Debug, agent-scoped) and starts it with
   `--CrawlForge:Port=0 --CrawlForge:DataDirectory=<scratch>`; discovers the
   port/token from `loopback-handshake.json` and the MCP endpoint from
   `mcp-endpoint.json`.
4. Connects a minimal SignalR JSON-protocol client (BCL `ClientWebSocket`,
   no extra packages) and subscribes to the fixture audits.
5. Seeds a project + configurations (http mode, rendering disabled), runs the
   main audit to a terminal state, and captures the **noindex** and
   **redirect-chain** trap payloads (url row, url detail, findings, summary).
6. Seeds operational surfaces: schedule (valid `interval:` recurrence rule;
   run-now), local-folder destination, export (csv) with explicit delivery,
   combined-log import, saved query.
7. Starts a bounded audit via **MCP** (keeps the engine DTO configuration
   snapshot — G-REAUDIT-SNAPSHOT breaks re-audit of REST-started audits),
   **stops the fixture site**, runs an offline re-audit (`refresh:false`) of
   that source to prove storage-to-engine reads with zero network, then
   restarts the fixture. The REST-start re-audit 500 is captured as evidence.
8. Uploads a redirect matrix (`autoSeedAudit:false`) and drives validation
   through `GET /api/audits/{id}/redirect-matrix/results` — the real consumer
   of `RedirectMatrixValidator`, emitting `matrix-row-validated` hub batches.
   The `autoSeedAudit:true` crawl/ingest race (G-MATRIX-SEED-RACE) is captured
   as evidence, not used as the conformance path.
9. On dispose: saves `hub-events.json`, kills its own backend and fixture
   processes, and keeps the scratch directory for review.

## Test classes

| Class | Boundary |
|---|---|
| `PayloadInventoryTests` | machine-readable inventory vs frozen sources (routes.ts, hub catalog, bridge/preload/main, SchemaNames, MCP surfaces) |
| `RestEnvelopeConformanceTests` | all 108 REST operations (88 groups) against the uniform envelope; pagination; error codes; binary exemptions; session gate |
| `HubEventWireConformanceTests` | live producer payloads for every catalog event + contract-side locks for drift/no-producer events |
| `AuditCountersWireConformanceTests` | engine counters → REST wire → hub (the WP-09-06 KnownGap failure lives here) |
| `IpcContractConformanceTests` | source-level cross-checks of bridge.ts / preload.ts / main.ts (frozen channels, descriptor validation, shell members) |
| `McpWireConformanceTests` | JSON-RPC initialize/tools/resources/prompts against the live MCP endpoint (8 tools, 15 resources, 5 prompts, error shapes) |
| `StorageEngineConformanceTests` | all 40 catalog entities vs EntityMap/SchemaNames; INTEGER enums/flags; TEXT timestamps; JSON columns; offline re-audit; G27/G03 shape locks |
| `FixtureTrapCaptureTests` | committed trap captures (noindex, redirect-chain) asserted for wire shape |

## Inventory and evidence

- `inventory/payload-inventory.json` — machine-readable payload inventory
  (REST operations, hub events, IPC channels, MCP tools/resources/prompts,
  storage entities). Every entry carries `coveredBy` references that resolve
  to real test methods (enforced by
  `PayloadInventoryTests.EveryInventoriedPayload_HasConformanceCoverage`).
- `captured/` — live wire captures written by the harness (trap payloads,
  envelope sweep results, hub events, MCP initialize, counter comparisons).
- `GAPS.md` — every observed drift with exact producer/consumer citations,
  spec citations, test coverage, and suggested owners (lead-confirmed
  routing). Only G-COUNTERS (WP-09-06) fails a test; all other gaps are
  contract-side assertions or skipped-with-reason tests.

## Conventions

- `CF_AGENT_ID` must be set on every `dotnet` invocation (build isolation).
- Scratch data lives under `%LOCALAPPDATA%\CrawlForge-conformance-wp0803`;
  runs are kept for lead review. The harness never touches `%APPDATA%\CrawlForge`.
- Trap assertions are **shape-only**; hop-count/finding/indexability semantics
  belong to cf-wp-08-04's fixture acceptance (see G-NOINDEX-FLAG-QUESTION).
- The sweep creates and deletes only its own disposable entities; deletes are
  driven last (children before parents) so reads keep their referents.
- **Ownership-safe test pattern (lead steer):** passing tests assert only
  fix-stable invariants (shared dimensions, spec disciplines, producer
  contracts); drift-specific assertions live in skipped-with-reason tests
  naming the gap + owners, with live captures as evidence. No test locks
  broken behavior as expected — the sole exception is the WP-09-06 KnownGap
  test, which asserts CONFORMANCE and turns green when the product is fixed.
- Storage-side DTO JSON is PascalCase (`PayloadJson` defaults, e.g.
  `limit_overrides_json = {"MaxUrls":220,...}`); the REST/hub wire is
  camelCase. Verified convention, not drift.
- The schedule wire field `cron` carries `interval:`/`calendar:` recurrence
  expressions (FS-CRAWL-001 §6.1), not cron syntax (G-CRON-NAMING).