# WP-10-01 — Direct CDP Rendering Control Layer + Browser Provisioning + Proxy Byte Capture

**Builder:** `cf-wp-10-01` · **Phase:** 10 (wave 10A) · **Repo HEAD at start:** `dc15529`
**Status:** IMPLEMENTATION COMPLETE + SEND-BACK RESOLVED — 173/173 Rendering tests AND 5/5 accepted Audit `LabProbeIntegration` tests pass; the strict loopback-only assertion holds **unrelaxed** (§5.3 corrected root cause + fix). Clean build (0 warnings/0 errors). Coordinator wiring items and the remaining spec-vs-reality deviation are handed off below.

Verification commands (run from `E:\Crawlforge\app`, `MSBUILDDISABLENODEREUSE=1`):

```
$env:CF_AGENT_ID='cf-wp-10-01'
dotnet test tests\CrawlForge.Crawler.Tests --filter FullyQualifiedName~Rendering
Passed!  - Failed:     0, Passed:   173, Skipped:     0, Total: 173, Duration: 40 s - CrawlForge.Crawler.Tests.dll (net10.0)
(run twice for stability: 40 s / 39 s, both 173/173)

# canonical Audit gate — CF_AGENT_ID unset:
dotnet test tests\CrawlForge.Audit.Tests --filter FullyQualifiedName~LabProbeIntegration
Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total: 5, Duration: 5 s - CrawlForge.Audit.Tests.dll (net10.0)
```

Source build: `dotnet build app\src\backend\CrawlForge.Crawler\CrawlForge.Crawler.csproj` → `Build succeeded. 0 Warning(s) 0 Error(s)`.

---

## 1. Files changed (owned folders only)

### Modified — `app\src\backend\CrawlForge.Crawler\`
| File | Change |
|---|---|
| `Rendering\HeadlessRenderingPool.cs` | Rewritten on direct CDP. **Class name + public ctor signature preserved** for DI compat. Adds `RenderDetailedAsync`, `ResolveBrowserInfo`, `TryLoadPinManifest`; waterfall persisted via shared writer. |
| `Rendering\LocalBrowserResolver.cs` | Fallback chain (bundled CfT → user path → Chrome → Edge → local failure); adds `ResolveDetailed()`, `DiscoverPackagedAssetsRoot()`, `ProbeLocalInstalls`. `Resolve()` compat kept. |
| `Rendering\RenderingPoolOptions.cs` | Wait-rule knobs (`RenderWaitQuietMs=500`, `RenderWaitHardCapMs=20000`), `MaxConcurrency` default = `CaptureSettings.DefaultRenderingPoolSize`, `FromCaptureSettings(...)`, `MaxNetworkEntries`, `StreamingPendingThresholdMs`, `BlockedResourceTypes`, `UserDataRootDirectory`. Removed obsolete `InfiniteScrollPauseMs`. |
| `Proxy\LoopbackInterceptingProxy.cs` | Per-subresource **transferred-byte counting** (response head + counted body); adds `DrainDetailedCapturedAsync`. Locked `DrainCapturedAsync` unchanged in shape. `_listener` field kept (structural tests). |

### New — `app\src\backend\CrawlForge.Crawler\Rendering\`
`CdpConnection.cs`, `CdpBrowserProcess.cs`, `CdpBrowser.cs`, `CdpPageSession.cs`, `CdpBrowserFactory.cs`, `CdpRenderingModels.cs`, `NetworkWaterfallRecorder.cs`, `RenderWaitRule.cs`, `BrowserPinManifest.cs`, `BrowserProvisioning.cs`, `NetworkWaterfallArtifactWriter.cs`, `AssemblyInfo.cs` (`InternalsVisibleTo("CrawlForge.Crawler.Tests")`).

### New — `app\src\backend\CrawlForge.Crawler\Proxy\`
`ProxyCapturedRequestDetail.cs` (size-bearing capture + `ToLockedRecord()` + `ToRenderedNetworkEntry()` mapper).

### New — `app\assets\browser\`
`browser-pin.json` (pinned **Chrome for Testing 131.0.6778.85**, per-platform layout slots, sha256 placeholders), `README.md` (resolution chain + O-006 bundling note).

### New/rewritten — `app\tests\CrawlForge.Crawler.Tests\Rendering\`
- `Cdp\CdpConnectionTests.cs`, `Cdp\CdpControlLayerConformanceTests.cs`, `Cdp\NetworkWaterfallRecorderTests.cs`, `Cdp\RenderWaitRuleTests.cs`
- `Harness\FakeCdpServer.cs` (scripted loopback CDP browser double), `Harness\FakeRenderHarness.cs` (fake factory + fake bundle layout)
- `Pool\BrowserProvisioningConformanceTests.cs`, `Pool\RenderResultConformanceTests.cs`
- `Pool\RenderingPoolUnitTests.cs` (rewritten), `Pool\RenderingPoolIntegrationTests.cs` (rewritten, real browser, graceful skip)
- `Proxy\ProxyCaptureSizeTests.cs`, `Proxy\ProxyWaterfallArtifactTests.cs`

### Kept unchanged (compat)
`Rendering\StealthProfile.cs`; all other `Proxy\*` sources; harness `LoopbackPageServer`/`CountingConnector`/`ScriptedTcpServer`/`RenderingTestDatabase`/`BrowserGuard`; `Proxy\ProxyStructuralTests`/`ProxyForwardTests`/`ProxyRoutingUnitTests`/`UpstreamChainingTests`; `Pool\LocalBrowserResolverTests`.

**No files outside the four owned folders were edited.** No commits made.

---

## 2. Deliverable-by-deliverable

### D-002 — Direct CDP control layer (replaces PuppeteerSharp)
- `CdpConnection`: `ClientWebSocket` CDP client, **loopback-only guard** (`ws://` + `IPAddress.IsLoopback`, else `ArgumentException`), id-correlated commands, flat-mode `sessionId` routing, event dispatch.
- `CdpBrowserProcess`: launches the resolved LOCAL exe `--headless=new --remote-debugging-port=0 --user-data-dir=… --hide-scrollbars about:blank` + `BoundaryHardeningArgs`; the inert `about:blank` startup tab suppresses the New Tab Page's vendor preconnect egress under a launch proxy (§5.3); parses the `DevTools listening on ws://127.0.0.1:…` endpoint; kills the process tree + cleans the profile on dispose. **No download code anywhere** (structural test asserts no `PuppeteerSharp`/`HttpClient`/`WebClient`/`DownloadFile`/`BrowserFetcher` tokens in `Rendering\`).
- `CdpBrowser`/`CdpPageSession`: Target/Page/Network/Runtime (+ServiceWorker/Fetch) domains on a flat session inside a **fresh browser context per page**; navigation + wait rule; PNG screenshot; console + runtime-exception capture; infinite scroll; **`Network.setBypassServiceWorker bypass:true` on every render** (§7.19) and registration facts exposed.
- `HeadlessRenderingPool`: bounded pool (default half logical processors, min 2), long-lived browsers leased per render, idle reuse, **timeout/failure isolation** (faulted browser retired, next render launches fresh). Public surface unchanged for DI.
- **No new PuppeteerSharp code.** The `CrawlForge.Crawler.csproj` PackageReference is now unused → removal is a handoff item (§4.2).

### CR-006 — CDP-direct network waterfall
`NetworkWaterfallRecorder` reduces `Network.*` events → locked `RenderedNetworkEntry` (URL, resourceType, method, status, mime, **transferredBytes**, encodedDataLength, timingJson, fromCache, fromServiceWorker). Redirect hops, cache/SW flags, `loadingFinished` authoritative size, streaming classification. The waterfall is **always** CDP-direct → `RenderedPage.NetworkWaterfall` → stored `NetworkWaterfall` artifact. The loopback proxy is **not** used for rendered traffic.

### CR-009 — Wait rule
`RenderWaitRule`: DOM + network quiet **500 ms** under a **20 s** hard cap (both configurable via `CaptureSettings`). Streaming (websocket/eventsource/long-poll/chunked-without-length) excluded from the quiet test. Hard cap captures current state (never fails). No fixed delays. Infinite-scroll steps gate on a DOM-mutation observation window so scroll-triggered lazy content is not missed (fixed a real settle race found during integration).

### D-003 / CR-007 — Browser provisioning
`BrowserPinManifest` + `LocalBrowserResolver.ResolveDetailed()`: bundled CfT (manifest platform layout, then legacy) → user path → local Chrome → local Edge → `BrowserResolutionException` (local error; "download is prohibited"; non-JS remains available). Pinned version recorded for UI-21. Component tests on **fake bundle layouts** prove the chain without a real binary. Physical bundling of the CfT binary = owner item **O-006** (assets/browser currently holds the pin manifest + README only).

### CR-031 — Transferred bytes per subresource
- **Rendered mode:** CDP waterfall `TransferredBytes` (encoded body + observed response head).
- **http (non-rendered) mode:** `LoopbackInterceptingProxy` counts response head + body per subresource → `ProxyCapturedRequestDetail` → `DrainDetailedCapturedAsync`. CONNECT tunnels/blocks/502 → 0 ("not measured", never "pass").
- **Artifact path (coordinator instruction `REPLY-TO-WP-10-01-PROXY-ARTIFACTS.md`):** `NetworkWaterfallArtifactWriter.PersistProxyCapturesAsync(store, auditId, urlId, captures)` maps proxy captures onto the **locked `RenderedNetworkEntry`** shape and persists a `ContentArtifactKind.NetworkWaterfall` artifact — **byte-identical camelCase JSON array** to the rendered path. Verified compatible with WP-10-05 `ImageRuleAnalyzer.LoadWaterfallBytesAsync` (case-insensitive array reader, `Url → TransferredBytes>0`). The locked `ProxyCapturedRequest` stays size-less (artifact carries bytes).

### UI-19 render tester
Runs on the new pool via the existing `IRenderingPool.RenderAsync` call site (`ToolsController` render-test). The pool returns `RenderedPage.NetworkWaterfall`; the REST response mapping is a handoff item (§4.3).

---

## 3. Verification evidence

- `dotnet build CrawlForge.Crawler.csproj` → **0 warnings, 0 errors**.
- `dotnet test CrawlForge.Crawler.Tests --filter FullyQualifiedName~Rendering` → **173 passed / 0 failed / 0 skipped** (run twice for stability; real-browser integration tests execute against local Chrome and gracefully skip when no browser resolves).
- Coverage includes: CDP loopback guard + correlation; domain lifecycle + SW bypass every render; fresh context per page + browser reuse; CDP-direct waterfall with transferred bytes; console/exceptions; screenshot PNG; wait-rule quiet/hard-cap/streaming/long-poll; infinite-scroll stabilization + iteration bound; bounded concurrency; timeout/navigation failure isolation; credential header split (in-scope only); fast resource-blocking profile; stealth on/off; browser identity recording; provisioning fallback chain on fake bundles + no-download structural proof; proxy byte counting (exact body, chunked, blocked/502/CONNECT=0, drain semantics); **http-mode proxy waterfall artifact end-to-end through the real `CompressedContentStore`** read back exactly as 05's reader does; rendered artifact persistence (RenderedDom + Screenshot + NetworkWaterfall) round-trip.
- Governance: all new code stays in approved `CrawlForge.Crawler.*` namespaces; the only socket is the loopback-guarded CDP client; no non-loopback listener / `IPAddress.Any`; no download API.

---

## 4. WIRING HANDOFF (coordinator — outside my owned folders)

### 4.1 G03 — inject the rendering pool into the engine
- Existing registration **works unchanged**: `Host\Composition\EngineServicesModule.cs:100`
  `services.AddSingleton<IRenderingPool>(p => new HeadlessRenderingPool(contentStore: …, wafEvaluator: …))` (singleton; ctor signature preserved).
- Coordinator: inject `IRenderingPool` into `AuditExecutionEngine` and set `RenderedDocument` on the rendered path (~`AuditExecutionEngine.cs:1510`). Per-audit options: `RenderingPoolOptions.FromCaptureSettings(snapshot.CaptureSettings)` (maps quiet/hard-cap/pool-size). For browser identity + SW facts per rendered URL use the concrete `HeadlessRenderingPool.RenderDetailedAsync(request, artifactContext, ct)` → `CdpRenderResult { Page, ServiceWorkerRegistrations, Browser }`.

### 4.2 Remove the unused PuppeteerSharp reference (orchestrator-managed csproj)
- Delete `<PackageReference Include="PuppeteerSharp" />` from `app\src\backend\CrawlForge.Crawler\CrawlForge.Crawler.csproj`. **`CrawlForge.Audit.csproj` keeps its own reference** (LabRenderingProbe / G04). `app\Directory.Packages.props` version 25.17.1 may stay until Audit no longer needs it.

### 4.3 UI-19 render-tester waterfall (REST + frontend contract)
- `ToolsController` render-test (~:202–233) already calls `_rendering.RenderAsync`. Add an **additive** `NetworkWaterfall` field to `RenderTestResponse` and map from `page.NetworkWaterfall`; add the matching frontend contract field. (Contracts are re-locked → additive amendment, §4.7.)

### 4.4 UI-21 browser section
- Bind `HeadlessRenderingPool.ResolveBrowserInfo()` (`ResolvedBrowser { ExecutablePath, Source, Product, Version, PinnedVersion }`) and `TryLoadPinManifest()` (`BrowserPinManifest`). **No REST surface exists today** (SettingsController/SettingsSurface have no browser section) → coordinator adds one. Pin manifest is discoverable via `LocalBrowserResolver.DiscoverPackagedAssetsRoot()` even before O-006 ships the binary.

### 4.5 Pre-crawl validation
- Rendering-dependent capture modes call the resolver; `BrowserResolutionException` → pre-crawl validation failure (clear local error). Non-JS capture unaffected.

### 4.6 CR-031 http-mode proxy artifact call site
- Engine plain-HTTP fetch path (Host\Execution / Capture): after fetching a URL through the loopback proxy, `var captures = await proxy.DrainDetailedCapturedAsync();` then `await NetworkWaterfallArtifactWriter.PersistProxyCapturesAsync(contentStore, auditId, urlId, captures, ct);`. Storage shape = camelCase JSON array of `RenderedNetworkEntry`, kind `NetworkWaterfall` (3), one artifact per URL — matches 05's reader. Engine fetcher proxy wiring (G09) remains coordinator's.

### 4.7 Additive contract amendments requested (contracts re-locked — do NOT edit in my lane)
1. `ProxyCapturedRequest`: additive `TransferredBytes` (pattern: `RenderedPage.NetworkWaterfall` `[AdditiveMember]`) so the locked drain carries sizes. Until then sizes flow via `DrainDetailedCapturedAsync` + the artifact path.
2. `RenderedPage`: additive `ServiceWorkerRegistrations` + browser-identity members. Until then the concrete `RenderDetailedAsync`/`CdpRenderResult` surface carries them.
3. `RenderTestResponse`: additive `NetworkWaterfall` (§4.3).

---

## 5. Deviations & blockers (loop §8 — reported, not papered over)

### 5.1 CR-006 deviation from the brief/YAML wording (spec wins)
The brief/YAML described proxy-captured rendered traffic. **03/03 §5.6/§5.6.2/§5.9 (CR-006) overrides**: rendered traffic is captured **directly by the CDP control layer** (`RenderedPage.NetworkWaterfall`); the loopback proxy routes **non-rendered plain-HTTP** traffic only. Implemented per spec; no spec/YAML edited.

### 5.2 `RouteThroughProxy` compatibility shim
The accepted `CrawlForge.Audit.Tests` `LabProbeIntegrationTests` sets `RouteThroughProxy=true` and asserts a non-empty proxy capture. To keep that opt-in working, the pool still routes the browser through the proxy when explicitly opted in (`--proxy-server` + `--proxy-bypass-list=<-loopback>`), **but the waterfall stays CDP-direct regardless** (CR-006). Default renders are not proxy-routed.

### 5.3 RESOLVED (send-back) — strict loopback assertion now passes; corrected root cause
The previous §5.3 conclusion (inherent, unsuppressible Chromium vendor egress; recommendation to relax `AssertCaptureStrictlyLoopback`) was **WRONG** and is retracted. Zero non-loopback capture is achievable on this machine and is now achieved: the canonical Audit gate passes **5/5** with the assertion **unrelaxed** and `LabProbeIntegrationTests.cs` **untouched**. Two distinct defects were found and fixed, both in owned folders. No assertion was relaxed; the `RouteThroughProxy` compat shim (§5.2) is kept (the accepted test still gets a non-empty proxy capture).

**(a) Vendor-egress root cause: the New Tab Page startup tab — NOT the headless mode.**
- The dispatch hypothesis (`HeadlessMode.True` = old headless vs the pool's `--headless=new`) was refuted by direct evidence:
  - The local browser is **Chrome 157.0.8103.79** (`C:\Program Files\Google\Chrome\Application\chrome.exe`), not 131; no CfT is bundled yet (O-006), so the probe and the pool resolve the **same** binary via the same resolver chain.
  - I captured the live `chrome.exe` command lines (`Win32_Process`) while running the PASSING probe test: PuppeteerSharp 25.17.1 `HeadlessMode.True` emits **`--headless=new`** — the accepted probe runs NEW headless, exactly like the pool. (Old headless was removed from the Chrome binary in 132; on 157 `--headless=old` is silently accepted with no distinct mode — verified by launch probe.) Switching headless modes therefore could not be, and was not, the fix.
  - The real launch delta: PuppeteerSharp always passes **`about:blank` as the initial startup URL** (captured command line: `… --headless=new --hide-scrollbars --mute-audio --disable-extensions about:blank …`). The pool passed **no** initial URL, so new-headless Chrome opened the **New Tab Page** at startup; the NTP's search-box preconnect emitted the vendor CONNECT tunnels (`www.google.com:443`) through the launch proxy. The probe's inert `about:blank` startup tab never loads the NTP → zero vendor egress.
- **Fix:** `CdpBrowserProcess.LaunchAsync` now includes the inert `about:blank` startup tab (`Rendering\CdpBrowserProcess.cs:103–111`), keeping `--headless=new` (probe parity). `BoundaryHardeningArgs` stays as the defense-in-depth superset.
- **Empirical confirmation (bisect):** with the startup URL temporarily removed, the pool's proxy-opt-in test captured exactly **3× `CONNECT https://www.google.com:443` (status 200 — really tunneled)**; with it, **zero** non-loopback captures across repeated full-suite and canonical runs. The earlier "unsuppressible across `--headless=new/old/shell`, `--host-resolver-rules`, Playwright PR #42909" evidence is superseded: that noise was NTP startup traffic, present in every mode because none of those launches suppressed the NTP.
- No CDP capability regression: headless mode is unchanged, and all capability tests (screenshot PNG, ServiceWorker domain + bypass, fresh browser contexts, waterfall, scroll, wait rule) pass 173/173.

**(b) Latent wait-rule defect exposed during re-verification (hard-cap flake) — fixed.**
- `Render_HardCap_CapturesState_WhenDomNeverQuiet` intermittently settled early (~1.3 s vs the 1.5 s cap) under full-suite parallel load. Temporary instrumented polls (env-gated, removed after diagnosis) showed `domQuiet=long.MaxValue` **while the mutation count was actively increasing** (`mut=27 … decision=Settled`).
- **Root cause:** `ReadDomQuietStateAsync` parsed the observer's `domQuietMs` — a **fractional** `DOMHighResTimeStamp` difference — with `TryGetInt64`, which **fails on fractional JSON numbers** → `-1` → mapped to `long.MaxValue` ("infinitely quiet"). Whenever such a poll coincided with ≥500 ms network quiet, the rule settled mid-mutation. Pre-existing since the CDP rewrite; unrelated to headless mode or the startup URL.
- **Fix:** parse as double (`Rendering\CdpPageSession.cs:714`), plus a soundness gate: when the quiet observer is absent from the current document (`count == -1`, i.e. the pre-commit `about:blank` predecessor), never accept `Settled` — keep waiting for the observer-bearing document; the hard cap still bounds the wait (`Rendering\CdpPageSession.cs:276–285`). Post-fix instrumentation shows `domQuiet` 0–24 ms throughout and `HardCapReached` at 1531 ms exactly as specified.
- **Test hardening (owned folder):** the busy page mutates every 25 ms (was 100 ms) for wide margin under load; `Render_ExplicitProxyOptIn_RoutesCrawlTraffic_LoopbackOnly` now asserts the **same strict loopback-only boundary as the accepted Audit test** — every capture including CONNECT must be loopback (replaces the former CONNECT-excluding assertion and its retracted "inherent noise" comment).

**Verification (verbatim, run from `E:\Crawlforge\app` with `MSBUILDDISABLENODEREUSE=1`):**

Rendering suite — `$env:CF_AGENT_ID='cf-wp-10-01'; dotnet test tests\CrawlForge.Crawler.Tests --filter FullyQualifiedName~Rendering` (run twice):

```
Passed!  - Failed:     0, Passed:   173, Skipped:     0, Total: 173, Duration: 40 s - CrawlForge.Crawler.Tests.dll (net10.0)
Passed!  - Failed:     0, Passed:   173, Skipped:     0, Total: 173, Duration: 39 s - CrawlForge.Crawler.Tests.dll (net10.0)
```

Canonical Audit gate — `CF_AGENT_ID` unset; `dotnet test tests\CrawlForge.Audit.Tests --filter FullyQualifiedName~LabProbeIntegration`:

```
Passed!  - Failed:     0, Passed:     5, Skipped:     0, Total: 5, Duration: 5 s - CrawlForge.Audit.Tests.dll (net10.0)
```

(Also passed 5/5 on two earlier runs during the bisect, including one with live command-line capture of the pool browser showing `… --hide-scrollbars about:blank … --proxy-server=127.0.0.1:64976 --proxy-bypass-list=<-loopback>`.)

Source build:

```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

**Files changed for the send-back (owned folders only):** `Rendering\CdpBrowserProcess.cs` (startup URL), `Rendering\CdpPageSession.cs` (fractional parse + observer gate), `tests\…\Rendering\Pool\RenderingPoolIntegrationTests.cs` (strict boundary assertion + busy-page cadence). No commits; no files outside the four owned folders edited (this report excepted).

### 5.4 Owner item O-006
Physical bundling of the pinned Chrome for Testing binary into `assets/browser` is pending (owner). Resolver + pin manifest + fallback chain + fake-bundle component tests are delivered; the pin (131.0.6778.85) is recorded for UI-21 now.

---

## 6. Notes
- `done_when` fixture/UI checks run at the phase gate after coordinator wiring; the 173 component/conformance/integration tests stand in until then.
- The CS0050 compile blocker flagged in `REPLY-TO-WP-10-01-PROXY-ARTIFACTS.md` (`FakeCdpBrowserFactory.LaunchAsync` returning internal `BrowserLease`) is **fixed** (harness factory + member made internal); the shared `CrawlForge.Crawler.Tests` project compiles and all Rendering tests pass — sibling builders' Crawler-side verification is unblocked.
- Private fixture port 47341 reserved; never 47311, never the app, never `%APPDATA%\CrawlForge`. Test data under `%LOCALAPPDATA%\CrawlForge-cf-wp-10-01`. No commits.
