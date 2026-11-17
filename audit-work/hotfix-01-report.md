# HOTFIX-01 — blocker fix report

| Item | Value |
|---|---|
| Date | 2026-11-17 |
| Scope | Two runtime blockers found by `audit-work/runtime-probe/RUNTIME-PROBE.md` (repo HEAD at probe time `18c53da`, working HEAD `603cc83`) |
| Rules obeyed | Bounded fix only; no refactor, no other missing features, no spec change; read-only w.r.t. prior audit work |

## 1. Root causes

**Blocker 1 — every audit failed at start.** `ProjectWireMapper.SerializeAimd` writes the stored politeness payload as the nested REST shape `{"aimd":{"initialRateR0":…,"alpha":…,"beta":…,"kappa":…,"ewmaTtfbWeight":…}}`. `AuditExecutionEngine` deserialized `PolitenessJson` directly as the flat `AimdSettings` DTO (`InitialRate`, …). The nested JSON has none of those properties, so `AimdSettings.InitialRate` was `0`, `AimdControllerOptions.Validate()` threw `ArgumentOutOfRangeException`, and `RunAuditAsync` finalized the audit as Failed within ~40 ms with 0 URLs. The spec (`03-crawl-engine/01 §3.3`) fixes the parameter defaults but does not pin a JSON property layout; the REST shape is the producer used by every write path. A secondary defect made this invisible: the failure message was only logged (never persisted or published), the engine emitted `audit-completed` for every terminal status, `stage-changed` used payload names the frontend guard does not read, and `hubDemux`'s `crawl-progress` handler unconditionally reset state to `running`.

**Blocker 2 — run view blanked the renderer.** `HubHostThrottleSink` serialized the hub event with `previousRate/newRate/previousConcurrency/newConcurrency/ttfbSampleMilliseconds/rollingBaselineMilliseconds`. The frontend hub contract (`contracts/hub/events.ts` `HostThrottlePayload`, mirrored by `contracts/api/hosts.ts` and the guards) requires `rateBefore/rateAfter/concurrencyBefore/concurrencyAfter/ttfbSampleMs/rollingBaselineRate`. `HubDemux` therefore set `HostState.ttfbEwmaMs = undefined`; `AuditViews` tested `!== null` and called `undefined.toFixed(0)`, throwing an uncaught `TypeError` that unmounted the entire React tree.

## 2. Files changed

Production:
- `app/src/backend/CrawlForge.Crawler/Politeness/AimdSettingsParser.cs` **(new)** — total parser for both stored shapes, spec defaults, per-field diagnostics.
- `app/src/backend/CrawlForge.Host/Execution/AuditExecutionEngine.cs` — uses the parser instead of raw `Deserialize<AimdSettings>`; logs fallbacks; threads failure message to finalize; emits contract-shaped `audit-failed` / `audit-completed` / `stage-changed`.
- `app/src/backend/CrawlForge.Host/Execution/HostThrottleSinks.cs` — emits the frontend hub field names.
- `app/src/backend/CrawlForge.Api/Controllers/AuditWireContracts.cs`, `AuditWireMapper.cs` — expose optional `errorMessage` from persisted counters.
- `app/src/backend/CrawlForge.Api/CrawlForge.Api.csproj` — `InternalsVisibleTo("CrawlForge.Host.Tests")` for the real-producer contract test.
- `app/src/frontend/src/contracts/api/audits.ts` — `errorMessage?: string | null` on `AuditSummary`.
- `app/src/frontend/src/store/hubDemux.ts` — terminal states survive late `crawl-progress`; `audit-failed` builds a base entity and stores the message; `audit-completed` maps `reportReady:false` to `cancelled`.
- `app/src/frontend/src/shell/AuditViews.tsx`, `format.ts` — `formatMs()` renders numeric-missing as `—`; no `toFixed` on undefined.
- `app/src/frontend/src/shell/HubDashboard.tsx` — failed rows show the message (`data-testid=audit-error-<id>`).
- `app/src/frontend/src/shell/ErrorBoundary.tsx` **(new)**, `CrawlForgeShell.tsx` — inline per-view boundary with inline error + Retry; one failing panel cannot blank the window.

Tests:
- `app/tests/CrawlForge.Host.Tests/Contracts/WireShapeContractTests.cs` **(new)** — 5 tests (both contract halves + fallback cases).
- `app/tests/CrawlForge.Host.Tests/FailedAuditVisibilityTests.cs` **(new)** — real audit failure → persisted message + `audit-failed` event.
- `app/tests/CrawlForge.Host.Tests/ReopenBatchWiringTests.cs` — existing assertion updated to the corrected hub field names.
- `app/tests/frontend/contracts/host-throttle-wire.test.ts` **(new)**, `app/tests/frontend/shell/errorBoundary.test.tsx` **(new)**.

## 3. Which side of each contract was changed, and why

| Contract | Side changed | Why |
|---|---|---|
| Configuration politeness JSON (storage → engine) | **Consumer (engine).** Kept the REST/storage shape unchanged. | The spec's §3.3 defines defaults, not property names; `ProjectWireMapper` is the only production writer and the REST contract is specified. Changing the producer would break every stored row in existing databases. The engine now accepts both shapes with safe defaults. |
| Host-throttle hub event (backend → frontend) | **Producer (backend).** Kept the frontend contract unchanged. | `contracts/hub/events.ts`, `contracts/api/hosts.ts`, the guards, the demux and the REST host wire all use `rateBefore/rateAfter/…`; the backend adapter's names matched no consumer. One small producer change fixes every consumer without touching the shipped UI contract. |
| Terminal-state visibility | **Both producers and UI demux.** | Backend now publishes `audit-failed` with `{errorCode,message}` and `audit-completed` with `{finalCounters,reportReady}`; UI stops clobbering terminal states and renders the persisted message. |

Existing rows need no manual edit: `AimdSettingsParser` accepts the legacy flat rows written by first-run seeding (verified by `LegacyFlatPolitenessPayload_IsStillParsed`). Invalid or missing values fall back to the §3.3 defaults and are recorded as local warnings (`Audit {id}: politeness configuration fallback (…) - …`), never crashing an audit.

## 4. Contract tests added (fail on old code, pass on new)

- `RestPolitenessPayload_IsParsedByEngineWithoutFallback` — serializes the **real** `ProjectWireMapper.SerializeAimd` payload and parses it with the **real** engine parser; old code produced `InitialRate=0` and `Validate()` threw.
- `LegacyFlatPolitenessPayload_IsStillParsed` — guards existing user rows.
- `InvalidPolitenessPayload_FallsBackToDefaultsAndReportsDiagnostic` (2 cases) — old code threw on `initialRateR0:0`; new code defaults and reports.
- `HostThrottleHubPayload_UsesFrontendContractFieldNames` — captures the **real** `HubHostThrottleSink` payload and asserts `rateAfter`/`ttfbSampleMs`; old code emitted `newRate`/`ttfbSampleMilliseconds` and fails.
- `host-throttle-wire.test.ts` — feeds the producer's exact payload to the real hub guard + demux and checks the run-view formatter; old payload fails the guard and leaves `ttfbEwmaMs` undefined.
- `FailedAuditVisibilityTests` — real audit forced to fail via an invalid concurrency ceiling: asserts status Failed, persisted `errorMessage`, contract-shaped `audit-failed` event, and no `audit-completed`.

## 5. Acceptance evidence (steps 3–6)

**Setup.** Fixture site already running at `http://127.0.0.1:47311/` (`206`/`200` on `/robots.txt`; `/__fixture/reset` called before the UI audit). App launched with `launch-crawlforge.ps1` equivalent (fresh built DLLs, CDP on 9223 because the probe's old process left 9222 in `CLOSE_WAIT`).

**Step 3 — UI.** Created project **3 "HOTFIX verification"** with start URL `http://127.0.0.1:47311/`, pressed **Start audit** on the main hub → **audit 6**, state RUNNING. After 3 s: **2 fetched**; after ~40 s: **105 fetched**; host-throttle updates arrived repeatedly (`127.0.0.1`, 2.5→6.0/s, TTFB EWMA 1 ms) and the run view **stayed mounted** (no `surface-error`, no blank window). Cancelled via UI → final **CANCELLED**, **126–127 URLs fetched**; main hub showed `CANCELLED` after refresh. Screenshots: `audit-work/hotfix-01-runview.png`, `hotfix-01-runview-after-20s.png`, `hotfix-01-hub-after-cancel.png`.
Failure visibility: MCP-started **audit 10** (invalid `maxConcurrency:0`) failed at start; REST returned `state=failed` and `errorMessage="The worker concurrency ceiling must be at least 1. (Parameter 'WorkerConcurrencyCeiling') …"`; after reload the main hub showed badge **`failed`** and the full message (`data-testid=audit-error-10`). Screenshot: `audit-work/hotfix-01-failed-visible.png`.

**Step 4 — REST and MCP.**
- REST `POST /api/audits` with `configurationId=6`, `maxUrls=12` → **audit 7**, crawled **12 URLs**, reached `completed`.
- MCP `start-audit` on project 3 / config 6 with `maxUrls=10` → **audit 8**, crawled **10 URLs**, reached `completed`.
- MCP `start-audit` with `maxConcurrency:0` → **audit 10**, reached `failed` with the persisted message (used for the visibility check above).

**Step 5 — click safety.** `GET http://127.0.0.1:47311/__fixture/requests` → `nonGet: []` (0 entries), `tagHits: []`. Vacuous as a click-safety proof because rendering/clicking is still inert in full audits (noted below), but the requested assertion holds.

**Step 6 — tests.**
- `CrawlForge.Host.Tests`: **38 passed** (was 36 + 1 failing before the fix; includes all new contract and failure-visibility tests).
- `CrawlForge.Crawler.Tests`: **546 passed**.
- `CrawlForge.Api.Tests`: **219 passed**.
- Frontend focused: `host-throttle-wire`, `hubDemux`, `hub-events` — **34 passed**; `errorBoundary` — **1 passed**.
- Full frontend suite: **637 passed / 1 failed** — the single failure is a pre-existing, unrelated 5 s timeout in `tools/queryBuilder.test.tsx` ("disables deeper grouping once the depth cap is reached"), untouched by this hotfix.

Build note: the only installed SDK is .NET 11.0.100-rc preview; `dotnet build` needs `-p:StaticWebAssetsEnabled=false` to bypass a pre-existing BlazorWeb `dotnet-watch` static-web-assets validation error. All C# projects compile and tests run with that flag.

## 6. Noticed but not fixed (out of scope)

- **Rendering is still inert in full audits** (`AuditExecutionEngine` never injects `IRenderingPool`; `RenderedDocument` is hardcoded `null`). Fixture JS traps remain undetected; `nonGet` is empty vacuously. (UNDERBUILD gap G03.)
- Crawler traps (calendar/filter), sitemap ingestion, redirect-hop persistence, postback/cookieless/ViewState checks, hreflang return tags, in-audit structured-data findings and thin-page detection are still missing. (G06–G08, G11, G27.)
- Report/explorer surfaces remain unmounted; only the run view and live panels are reachable. (G01.)
- MCP action log has no read endpoint; the REST audit `counters_json` keys (`discovered/fetched/analyzed/failed`) do not match `AuditWireMapper.ParseCounters`' expected keys (`queued/fetched/error/blocked/wafBlocked`), so several dashboard counters read as 0. Neither is caused by this hotfix.
- A transient run-view badge could show `COMPLETED` before a cancel was reconciled; the demux now maps `reportReady:false` to `cancelled`, but no dedicated test was added for that one-line mapping.
- Governance/negative-requirement enforcement remains static-analysis based; no runtime capability denial was added.
- Stale probe processes held CDP port 9222; the acceptance session used 9223. No source change was made for this environment issue.
