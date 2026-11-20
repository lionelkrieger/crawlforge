# Dispatch: WP-10-01 (wave-10A) — subagent prompt

You are builder `cf-wp-10-01` for phase 10 of CrawlForge (offline-first desktop crawler and SEO auditor at `E:\Crawlforge`: .NET 10 backend, Electron shell, React frontend). The coordinator has pasted your full workpiece YAML below this brief; it is your contract.

**Role.** You are a backend builder on rendering. You own `CrawlForge.Crawler\Rendering\`, `CrawlForge.Crawler\Proxy\`, `assets\browser\` and `app\tests\CrawlForge.Crawler.Tests\Rendering\`.

**Task.** Replace the PuppeteerSharp path with a direct CDP control layer (D-002): a WebSocket CDP client using the Target, Page, Network and Runtime domains, a navigation and wait rule, screenshots, console capture, infinite-scroll handling and a bounded concurrency pool with timeout and failure isolation. Resolve the pinned Chrome for Testing bundle only from packaged assets (D-003) and record the pinned version for the UI-21 browser section. Make the loopback intercepting proxy capture the waterfall for rendered traffic, apply resource blocking and upstream chaining, and record **transferred bytes per subresource** (CR-031). Make the render tester run on the new pool. Deliver every item in your YAML `deliverables`, with component tests that prove each defect below is fixed at component level. Your `done_when` fixture and UI checks run at the phase gate after the coordinator wires your work, so make your component tests strong enough to stand in for them until then.

**Defects routed to you.** G03: `IRenderingPool` is never injected, so the probe-2 JS audit was inert ('no rendered tree is available'). The proxy capture record has no size field, so image size is unmeasurable (phase-09 notation gap 7).

**Boundaries.** Injecting the pool into the engine is the coordinator's job. Give the exact pool interface, its registration and its lifetime in your handoff. Never download a browser or driver, and never open a non-loopback listener.

Your agent id is `cf-wp-10-01`; your private fixture port, if you need one, is 47341.

**Rules for this job.** Write only inside your owned paths (listed in your workpiece YAML). Never edit `architecture\`, `buildplan\`, `audit-work\register\`, `audit-work\decisions-log.md`, `audit-work\SOURCE-OF-TRUTH-LOOP.md`, `app\tests\fixture-site\expected-results.json`, `app\CODEMAP.md`, the solution file, DI or composition code, `Host\Execution\`, `Host\Composition\`, `Audit\Pipeline\`, `frontend\src\App.tsx`, `store\`, `shell\`, `CrawlForge.Contracts\` or `frontend\src\contracts\` (contracts are locked after the WP-10-A1 amendment); those belong to the coordinator, so anything you need there goes in your wiring handoff. If the spec looks wrong or ambiguous, stop that item and report it; do not invent requirements. Do not commit; the coordinator commits accepted work. Other builders may be working at the same time: set `CF_AGENT_ID=<your agent id>` for every `dotnet build` and `dotnet test` so your output folders do not collide, run only your own test projects filtered to your folders, and never run the whole solution. Do not start the CrawlForge app or the fixture site on port 47311; if a test needs a fixture server, start your own on the port given below and stop it afterwards. Never touch `%APPDATA%\CrawlForge` or the owner's real projects (for example "Airpower"); if you need a data folder, use `%LOCALAPPDATA%\CrawlForge-<your agent id>`. Exclude `bin`, `obj`, `node_modules`, `dist`, `tools-env` from searches; in Git Bash prefix `/api/...` arguments with `MSYS_NO_PATHCONV=1`. Read `E:\Crawlforge\CLAUDE.md` and `E:\Crawlforge\app\CODEMAP.md` first, then the `context_files` and `architecture_sections` in your YAML.

**When you finish, report:** the files you created or changed; how each deliverable is met; the exact verification commands you ran with their output; a **wiring handoff** listing every registration, DI line, pipeline call, route, navigation entry or contract addition the coordinator must make, with type names and call sites; and anything you could not do and why.

## Coordinator notes (authority: living architecture > locked contracts > brief/YAML)

FIRST read `E:\Crawlforge\audit-work\reports\phase-10\WAVE-10A-NOTES.md` — the locked-surface catalogue and your specific notes. Critical points:

1. **CR-006 amendment overrides the brief/YAML wording.** Architecture 03/03 §5.6.2 (living spec, applied CR-006) captures rendered traffic DIRECTLY in the rendering control layer via the CDP Network domain (waterfall + transferred bytes). The loopback proxy routes plain-HTTP NON-rendered traffic. Your brief's "proxy capture the waterfall for rendered traffic" and YAML deliverable line 84 ("Loopback intercepting proxy effective for rendered traffic") CONTRADICT the living spec. Follow the spec: CDP-direct waterfall for rendered captures; the proxy still records transferred bytes per subresource for non-rendered/http-mode traffic (CR-031). Record this deviation explicitly in your report (loop §8: spec wins; never edit the spec or YAML).
2. The EXISTING `HeadlessRenderingPool` in your folder is PuppeteerSharp-based; D-002 mandates direct CDP — your layer replaces it, no new PuppeteerSharp code. It is the ONLY PuppeteerSharp consumer in CrawlForge.Crawler; the csproj PackageReference removal is coordinator wiring (flag it). Keep an artifact-persistence path equivalent to the concrete `RenderAsync(request, RenderArtifactContext?, ct)` overload (RenderedDom + Screenshot today; add the NetworkWaterfall artifact, `ContentArtifactKind.NetworkWaterfall=3`).
3. Locked A1 surfaces you MUST use: `RenderedPage.NetworkWaterfall` (`IReadOnlyList<RenderedNetworkEntry>?`) with `RenderedNetworkEntry(Url, ResourceType, Method, StatusCode, MimeType, TransferredBytes, EncodedDataLength, TimingJson, FromCache, FromServiceWorker)`; `CaptureSettings` (renderWaitQuietMs=500, renderWaitHardCapMs=20000, renderingPoolSize null = `CaptureSettings.DefaultRenderingPoolSize` = half logical processors min 2). `IRenderingPool.RenderAsync(RenderRequest) → RenderedPage` signature is LOCKED.
4. Browser resolution chain (§5.6.3): pinned bundled Chrome for Testing (`app/assets/browser`, currently EMPTY) → user-configured path → local Chrome → Edge; none → pre-crawl validation failure. D-003: NO runtime downloads. Deliver resolver + pin manifest + fallback chain with component tests on a fake bundle layout; physically bundling the CfT binary is owner packaging item O-006 — do not download anything. Record the pinned version string for UI-21.
5. Wait rule: DOM + network quiet 500 ms, hard cap 20 s, both configurable; streaming responses excluded from the quiet test; no fixed delays. Fresh browser context per page; timeout/failure isolation. Service workers bypassed on every render (§7.19); expose the registration fact for WP-10-02's informational finding.
6. Governance gate runs `--fail-on-violation`: outbound client construction only in approved boundary scopes (Crawler namespace/path approved — see `CrawlForge.Crawler/Fetching/CrawlTargetTransportFactory.cs` for the pattern); no non-loopback listener (NET-01). A source-scan boundary test confines transport tokens in Crawler/Fetching — your Rendering/Proxy code is outside that scan but keep construction inside your approved scopes.
7. The working tree carries uncommitted coordinator ledger/evidence edits (`buildplan/`, `audit-work/reports/phase-10/`, `app/tools/network-audit/evidence/`) — ignore them in lane checks; not yours to commit or revert. A canonical gate run may be in progress: run ONLY your filtered test projects with CF_AGENT_ID set; never the whole solution; expect vitest/dotnet contention flakes — retry quietly before reporting a failure.
8. Contracts are RE-LOCKED (commits 2cb4311 + 0307fe8): no edits to CrawlForge.Contracts or frontend/src/contracts; any missing surface goes in your handoff as an amendment request with justification.

## Your workpiece YAML (verbatim)

```yaml
  - workpiece_id: "WP-10-01"
    title: "Direct CDP Rendering Layer, Pinned Bundled Browser & Loopback Proxy Capture"
    phase_id: "phase-10"
    status: "pending"
    parallel_group: "wave-10A"
    depends_on_workpieces: ["WP-08-03", "WP-08-04"]
    context_files:
      - "E:\\Crawlforge\\architecture\\03-crawl-engine\\03-rendering-and-proxy-routing.md"
      - "E:\\Crawlforge\\architecture\\01-foundation-and-governance\\03-network-boundary-and-security.md"
    architecture_sections:
      - file: "E:\\Crawlforge\\architecture\\03-crawl-engine\\03-rendering-and-proxy-routing.md"
        sections: ["5.6.2", "5.6.3", "5.9"]
      - file: "E:\\Crawlforge\\architecture\\01-foundation-and-governance\\03-network-boundary-and-security.md"
        sections: ["2.6"]
    capabilities:
      - { id: "CAP-A-X10", baseline_state: "missing_engine" }
      - { id: "CAP-A-X11", baseline_state: "partial" }
      - { id: "CAP-A-X04", baseline_state: "unwired" }
      - { id: "CAP-A-X03", baseline_state: "unwired" }
      - { id: "CAP-A-015", baseline_state: "unwired" }
    ui_surfaces: ["UI-19", "UI-21"]
    fixture_traps: ["js-only-link", "script-injected-content"]
    contract_tests:
      - payload: "render result: rendered DOM, screenshot, console log, network waterfall (storage content_artifacts + REST render-tester response)"
        test: "RenderResultConformanceTests"
      - payload: "CDP session lifecycle commands (Target/Page/Network domains) vs the rendering control-layer contract"
        test: "CdpControlLayerConformanceTests"
      - payload: "browser provisioning: pinned Chrome for Testing bundle resolution, no runtime download"
        test: "BrowserProvisioningConformanceTests"
    reopens:
      - workpiece_id: "WP-02-06"
        evidence: "G03: IRenderingPool never injected; probe-2 js audit inert ('no rendered tree is available'); D-002/CR-006 replaces PuppeteerSharp with direct CDP; D-003/CR-007 pins the browser bundle"
    owned_paths:
      - "E:\\Crawlforge\\app\\src\\backend\\CrawlForge.Crawler\\Rendering\\"
      - "E:\\Crawlforge\\app\\src\\backend\\CrawlForge.Crawler\\Proxy\\"
      - "E:\\Crawlforge\\app\\assets\\browser\\"
      - "E:\\Crawlforge\\app\\tests\\CrawlForge.Crawler.Tests\\Rendering\\"
    deliverables:
      - "Loopback proxy records transferred bytes per subresource so the oversized-image rule can measure images in rendered and full capture modes (CR-031)"
      - "Direct CDP control layer (D-002): WebSocket CDP client, Target/Page/Network/Runtime domains, navigation + wait rule, screenshot, console capture, infinite-scroll handling; PuppeteerSharp removed from the rendering path"
      - "Pinned Chrome for Testing bundle (D-003): local-only resolution from packaged assets, version pin recorded, no remote driver/browser download at runtime; browser settings section in UI-21 shows the pinned version"
      - "Stealth rendering profile applied; bounded concurrency pool; render timeout/failure isolation"
      - "Loopback intercepting proxy effective for rendered traffic: waterfall capture into the content store, resource blocking rules, upstream proxy chaining; no non-loopback listener"
      - "Render tester (UI-19) drives the new pool: rendered DOM + screenshot + waterfall for a fixture URL"
    verification_commands:
      - "dotnet test E:\\Crawlforge\\app\\tests\\CrawlForge.Crawler.Tests --filter FullyQualifiedName~Rendering"
    done_when: "Fixture harness render-tester entry: /pages/js-link.html rendered DOM contains /pages/js-only-target.html; /pages/js-content.html rendered word count ~200 vs raw ~0; waterfall captured through the loopback proxy; UI walk: UI-19 render tester shows the rendered result, UI-21 browser section shows the pinned bundle."
    acceptance_criteria_refs: [22, 50]
```