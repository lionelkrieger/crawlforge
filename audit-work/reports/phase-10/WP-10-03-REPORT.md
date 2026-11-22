# WP-10-03 report — Rendered Comparison UI (wave-10B)

Built by a builder whose session ended before reporting; report reconstructed from code on 2026-11-22. Coordinator fix round: 11 typecheck errors in tests/frontend/rendered/helpers.tsx and renderedDiff.test.ts fixed (fixtures aligned to current contract types, no assertions changed). Verified: typecheck 0, rendered tests 7 files / 65 passed, build 0.

## 1. Status / verification

See the status line above. The verification figures come from the coordinator fix round; this reconstruction re-read the delivered code but did not re-run the suites.

## 2. Files created / changed (owned paths only)

### Production — `app/src/frontend/src/surfaces/explorers/rendered/` (new folder, 14 files)

| File | What it is |
|---|---|
| `index.ts` | Barrel: all pure logic + all nine components with their props types. |
| `renderedKeys.ts` | Locked `render.*` key groups (`RENDER_DELTA_RULE_KEYS`, `RENDER_COMPARISON_RULE_KEYS`), field labels, severity ranks (`severityRank`, `comparisonSortRank`), capture-mode/source labels. |
| `renderedDiff.ts` | `extractRenderedDiff` + per-finding readers over the WP-10-02 comparer dual evidence (title/canonical/meta-robots/noindex/word count + every delta field). |
| `renderedComparison.ts` | `buildRenderedComparisonRows` (per-URL grouping, critical-aware severity sort) + `loadRenderedComparison` (drains `audits.listUrls` + one `audits.listFindings` query per locked `render.*` key). |
| `renderedArtifacts.ts` | `RenderedArtifactLoader` / `RenderedUrlArtifacts` interfaces, `parseNetworkWaterfall` (tolerant of bare array / `{ networkWaterfall \| entries }` / JSON string), `totalTransferredBytes`. |
| `RenderedTab.tsx` | UI-13 rendered tab: fetches `audits.getUrl`, runs the diff from stored findings, hosts all artifact viewers. |
| `RenderedDiffPanel.tsx` | Dual-evidence diff table with dedicated title/canonical/noindex/word-count rows and generic rows for every other delta field. |
| `RenderedComparisonSurface.tsx` | UI-16 surface: virtualized grid over the aggregated comparison rows, drill-down via `onOpenUrl`. |
| `RenderedOutputPanel.tsx` | Real rendered output panel bound to the locked `RenderTestResponse` (UI-18/UI-19). |
| `CaptureModeIndicator.tsx` | UI-10 capture-mode / per-URL capture-source badge cluster. |
| `NetworkWaterfallViewer.tsx` | Waterfall table over `RenderedNetworkEntry` (+ exported `formatBytes`). |
| `ConsoleLogViewer.tsx` | Console list over `ConsoleMessage`. |
| `RenderedDomViewer.tsx` | Post-render DOM source block (reuses `tools/shared/CodeBlock`). |
| `ScreenshotViewer.tsx` | Base64 PNG viewer (local data URL only). |

### Production — `app/src/frontend/src/surfaces/single-page/` (changed)

| File | What it is |
|---|---|
| `SinglePageAnalyzer.tsx` | +3 imports and +7 lines in the "Rendered artifact" tab: mounts `NetworkWaterfallViewer`, `ConsoleLogViewer`, `RenderedDomViewer` over the live `tools.renderTest` response (`artifact.networkWaterfall ?? null`, `artifact.consoleMessages`, `artifact.renderedHtml`), testids `sp-waterfall` / `sp-console` / `sp-dom`. The only WP-10-03 component actually mounted in app code. |

### Tests — `app/tests/frontend/rendered/` (new folder, 8 files)

| File | What it proves |
|---|---|
| `helpers.tsx` | Shared fixtures: the actual WP-10-02 comparer evidence shapes (late-meta title/canonical/critical rendered-only noindex, js-content word counts 3→203, js-link, status, structured-data, resources, service-worker), fixture URL records with `captureSource`/`discoverySource`, URL-detail wires, waterfall/console artifacts, a locked `RenderTestResponse`, and fake `audits.getUrl` / `audits.listUrls` / `audits.listFindings` (ruleKey/urlId/category/severity + offset/limit) routes over the real typed loopback client. |
| `wireConformance.test.tsx` | The two YAML contract tests. `RenderedTabWireConformanceTests`: parses the producer waterfall JSON (exact wire keys), sums `transferredBytes`, tolerates wrapper shapes, renders locked waterfall fields, binds `RenderTestResponse.networkWaterfall`, projects comparer dual-evidence keys, reads findings via the locked rule-key constant. `RenderedComparisonSurfaceTests`: aggregates comparer findings + `captureSource` into rows, orders by locked severity tokens, queries the issue-explorer surface with the locked `render.*` tokens. |
| `renderedTab.test.tsx` | UI-13 acceptance at component level: raw vs rendered title, canonical dual evidence, critical rendered-only noindex with direction, capture-source badge, js-content ~0 vs ~200 word counts, all four artifact viewers bound, honest empty artifacts with diff still working, null loader = baseline, clean page / offline / error states. |
| `renderedOutput.test.tsx` | UI-19 `RenderedOutputPanel` binds screenshot, DOM, console, waterfall and the WAF verdict (detected and not); null/absent waterfall = honest baseline. UI-18 `SinglePageAnalyzer` shows waterfall, console and DOM after rendering. |
| `renderedDiff.test.ts` | Pure `extractRenderedDiff`: title/canonical dual evidence, both noindex critical directions, word counts with `totalWordCount`/`words` alias fallback, every delta field projected (service-worker excluded), per-finding readers return null off-key, evidence helpers coerce correctly. |
| `renderedComparisonSurface.test.tsx` | UI-16 acceptance at component level: lists both acceptance pages, severity-sorted (critical noindex page first), page/finding/critical counts, drills into the URL detail rendered tab on row activation, mounts ≤ `MAX_DOM_ROWS` rows with hundreds of differing pages, honest empty and offline states. |
| `renderedComparison.test.ts` | Pure grouping: only delta-bearing pages get rows (service-worker-only excluded), critical-first severity sort, capture-source + changed-field label join, `loadRenderedComparison` queries every `render.*` key, offline and envelope-error degrade without fabricating rows. |
| `captureModeIndicator.test.tsx` | All four `CAPTURE_MODE` tokens with labels and `data-mode`, baseline state for absent/unknown tokens, all three `CAPTURE_SOURCE` tokens with `data-source`, source badge omitted when absent. |

## 3. Deliverables

### Deliverable 1 — UI-13 rendered tab: DOM viewer, screenshot, console, waterfall, raw-vs-rendered diff with dual evidence

Met at component level by `RenderedTab` (composes `RenderedDiffPanel`, `ScreenshotViewer`, `RenderedDomViewer`, `ConsoleLogViewer`, `NetworkWaterfallViewer`, `CaptureModeIndicator`). The diff covers title, canonical, noindex and word count with dedicated rows carrying both raw and rendered values, plus generic dual-evidence rows for every other `render.*-delta` field; the evidence is read from the stored findings on the `audits.getUrl` wire, so it works offline and from re-audit (§9.6).

Partly done, honestly flagged: the four artifact viewers are bound but have **no data source yet** — `renderedArtifacts.ts` documents that the frozen REST route catalog exposes no per-URL content-artifact read route at this amendment, so `RenderedTab` accepts an injected `RenderedArtifactLoader` and without one renders honest "not captured" states. The component is also **not mounted** anywhere in the app (coordinator wiring).

### Deliverable 2 — UI-16 rendered comparison surface, all pages with response-vs-rendered differences, severity-sorted

Met at component level by `RenderedComparisonSurface` + `loadRenderedComparison`/`buildRenderedComparisonRows`: one findings query per locked `render.*` key merged with `audits.listUrls`, one row per page carrying at least one delta, sorted critical rendered-only/raw-only noindex first, then error, warning, notice (ties on canonical URL). It reuses the accepted grid engine (virtualized, tested at ≤ `MAX_DOM_ROWS` DOM rows) and drills into the URL detail via `onOpenUrl`. **Not mounted**: no route or nav entry exists yet.

### Deliverable 3 — UI-18/UI-19 render tester shows real rendered output; capture-mode indicator in UI-10

- **UI-18 (single-page analyzer): done and mounted.** `SinglePageAnalyzer`'s "Rendered artifact" tab now shows the network waterfall, console log and post-render DOM from the real `tools.renderTest` response (the screenshot was already shown). Waterfall is the WP-10-A1 additive field; absent/null renders an honest baseline state.
- **UI-19 (render tester): component delivered, not mounted.** `RenderedOutputPanel` binds the full locked `RenderTestResponse` (screenshot, rendered DOM, console, waterfall, post-render WAF verdict). The standalone render tester tool is coordinator-owned; nothing mounts this panel yet.
- **UI-10 capture-mode indicator: component delivered, not placed.** `CaptureModeIndicator` renders the locked `CAPTURE_MODE` (non-js/js/dual/adaptive) and optional per-URL `CAPTURE_SOURCE` tokens with honest baseline fallback for absent/unknown values. Placement in the audit run view is coordinator wiring.

## 4. WIRING HANDOFF (coordinator applies; nothing here was edited by this builder)

### 4.1 Exported components / functions

| Export (exact name) | File | Props / signature | Data / API calls | Key testids |
|---|---|---|---|---|
| `RenderedTab` | `surfaces/explorers/rendered/RenderedTab.tsx` | `RenderedTabProps { readonly api: LoopbackApiClient \| null; readonly auditId: string \| null; readonly urlId: string \| null; readonly loadArtifacts?: RenderedArtifactLoader; readonly height?: number; }` | `api.request('audits.getUrl', { params: { id: auditId, urlId } })` → `UrlDetail`; reads `detail.findings` (diff) and `detail.captureSource` (badge); optional `loadArtifacts(auditId, urlId, signal)` for DOM/screenshot/console/waterfall | `rendered-tab` (data-url-id), `rendered-tab-offline`, `rendered-tab-empty`, `rendered-tab-loading`, `rendered-tab-error`, `rendered-tab-capture` |
| `RenderedDiffPanel` | `…/RenderedDiffPanel.tsx` | `RenderedDiffPanelProps { readonly diff: RenderedDiff; readonly testId?: string; }` (default `testId='rendered-diff'`) | none (pure projection of `RenderedDiff`) | `rendered-diff`, `rendered-diff-table`, `rendered-diff-empty`, `rendered-diff-critical`, headline + generic rows (see §5) |
| `RenderedComparisonSurface` | `…/RenderedComparisonSurface.tsx` | `RenderedComparisonSurfaceProps { readonly api: LoopbackApiClient \| null; readonly auditId: string \| null; readonly onOpenUrl?: (urlId: string) => void; readonly height?: number; }` (default `height=480`) | via `loadRenderedComparison`: `audits.listUrls` `{ params:{id}, query:{offset,limit} }` drained (page size 200) + one `audits.listFindings` `{ params:{id}, query:{ruleKey,offset,limit} }` per key in `RENDER_COMPARISON_RULE_KEYS` | `rendered-comparison`, `rendered-comparison-count`, `rendered-comparison-finding-count`, `rendered-comparison-url-<urlId>` (data-critical), `rendered-comparison-loading`, `rendered-comparison-error`, `rendered-comparison-empty`, `rendered-comparison-offline` |
| `loadRenderedComparison` | `…/renderedComparison.ts` | `(api: LoopbackApiClient \| null, auditId: string \| null, signal?: AbortSignal) => Promise<RenderedComparisonResult>` | as above | — |
| `buildRenderedComparisonRows` | `…/renderedComparison.ts` | `(findings: readonly Finding[], urlRecords: readonly UrlRecordSummary[]) => RenderedComparisonRow[]` | none (pure) | — |
| `extractRenderedDiff` | `…/renderedDiff.ts` | `(findings: readonly Finding[]) => RenderedDiff` | none (pure; reads `finding.evidence.details`) | — |
| `RenderedOutputPanel` | `…/RenderedOutputPanel.tsx` | `RenderedOutputPanelProps { readonly result: RenderTestResponse; readonly testId?: string; readonly showDom?: boolean; }` (default `testId='rendered-output'`, `showDom=true`) | none — binds a `RenderTestResponse` the caller already holds (from `tools.renderTest`) | `rendered-output`, `-waf` (data-detected/data-provider), `-waterfall-count`, `-console-count`, `-screenshot`, `-waterfall`, `-console`, `-dom` |
| `CaptureModeIndicator` | `…/CaptureModeIndicator.tsx` | `CaptureModeIndicatorProps { readonly mode?: CaptureModeWire \| null; readonly captureSource?: CaptureSourceWire \| null; readonly testId?: string; }` (default `testId='capture-mode-indicator'`) | none (pure props) | `capture-mode-indicator` (data-mode), `-value`, `-source` (data-source) |
| `NetworkWaterfallViewer` (+ `formatBytes`) | `…/NetworkWaterfallViewer.tsx` | `NetworkWaterfallViewerProps { readonly entries: readonly RenderedNetworkEntry[] \| null \| undefined; readonly testId?: string; }` | none | `<root>`, `<root>-empty`, `<root>-total`, `<root>-row-<i>`, `<root>-url-<i>`, `<root>-status-<i>`, `<root>-bytes-<i>` |
| `ConsoleLogViewer` | `…/ConsoleLogViewer.tsx` | `ConsoleLogViewerProps { readonly messages: readonly ConsoleMessage[] \| null \| undefined; readonly testId?: string; }` | none | `<root>`, `<root>-empty`, `<root>-row-<i>` (data-level), `<root>-text-<i>` |
| `RenderedDomViewer` | `…/RenderedDomViewer.tsx` | `RenderedDomViewerProps { readonly html: string \| null \| undefined; readonly testId?: string; readonly maxHeight?: number; }` | none | `<root>`, `<root>-empty` |
| `ScreenshotViewer` | `…/ScreenshotViewer.tsx` | `ScreenshotViewerProps { readonly screenshotBase64: string \| null \| undefined; readonly testId?: string; readonly maxHeight?: number; }` | none | `<root>`, `<root>-empty`, `<root>-img` |
| `RenderedArtifactLoader` (type) | `…/renderedArtifacts.ts` | `(auditId: string, urlId: string, signal?: AbortSignal) => Promise<RenderedUrlArtifacts \| null>` | coordinator-supplied | — |
| `parseNetworkWaterfall`, `totalTransferredBytes`, `EMPTY_RENDERED_ARTIFACTS` | `…/renderedArtifacts.ts` | pure helpers over persisted waterfall JSON | none | — |

All of the above are re-exported from `surfaces/explorers/rendered/index.ts`.

### 4.2 What the coordinator must do to make each reachable

1. **UI-13 rendered tab** — mount `<RenderedTab api={api} auditId={auditId} urlId={urlId} loadArtifacts={loader} />` inside the URL detail surface (`surfaces/explorers/urls/UrlDetailPanel.tsx`; today it is a flat `url-detail-*` section list with no tab strip, so a "Rendered" tab/section must be added there — report window navigation / drill-down path per `04-ui-surface-map.md` §1 line 31). To satisfy the acceptance walk the tab must be reachable from the URL row drill-down for `/pages/late-meta.html` and `/pages/js-content.html`.
2. **Artifact loader** — implement `RenderedArtifactLoader` bound to per-URL `ContentArtifactKind.RenderedDom` / `Screenshot` / `NetworkWaterfall` **when a read route exists**; the frozen REST catalog (`contracts/api/routes`) has none at this amendment. Until then omit `loadArtifacts`; viewers show honest empty states and the diff still works from finding evidence. Adding a route is a contracts change record (contracts are re-locked).
3. **UI-16** — add a report-window navigation entry labelled "Rendered comparison" (`04-ui-surface-map.md` §1 line 34) mounting `<RenderedComparisonSurface api={api} auditId={auditId} onOpenUrl={(urlId) => openUrlDetailRenderedTab(urlId)} />`. Note: `ExplorerId` (`surfaces/explorers/explorerTypes.ts:21-32`) is the fixed FS-CRAWL-002 §8.3 eleven-member union with no rendered-comparison member, and `ExplorerHost` only renders `ExplorerDefinition`s over frozen routes; this component drives `useGridEngine`/`DataGrid` itself (`explorerId: 'rendered-comparison'` is a plain string for grid cache keying), so mount it directly — extending the union is the coordinator's call and may need a change record.
4. **UI-19** — mount `<RenderedOutputPanel result={renderTestResponse} />` in the standalone render tester (`tools/RenderTesterTool`, coordinator-owned; its current file/shape not confirmed in this reconstruction) alongside/instead of its current output; the data is the existing `tools.renderTest` response, no new call.
5. **UI-10** — place `<CaptureModeIndicator mode={captureMode} captureSource={perUrlCaptureSource} />` in the audit run view (UI-10, `04-ui-surface-map.md` §1 line 28). `mode` = the running audit's configuration snapshot capture mode (`CaptureModeWire`: non-js/js/dual/adaptive); `captureSource` is optional per-URL. Where the run view reads the snapshot from was not confirmed here.
6. **Store / shell changes** — none required by these components: they take `api`/`auditId` as props, fetch through the locked routes and manage their own loading/error state. Only the mounting shells change.
7. **Contract fields to verify the backend actually serves** (all present in the locked frontend mirrors, live serving not confirmed):
   - `UrlDetail.captureSource` — declared on `UrlRecordSummary` (`contracts/api/audits.ts:125`, `UrlDetail extends UrlRecordSummary` at :245). `RenderedTab` reads `detail?.captureSource`.
   - `audits.listFindings` with a `ruleKey` query filter — `loadRenderedComparison` issues one query per `render.*` key; the dispatch coordinator note says the issue-explorer query surface supports ruleKey/category filters, and the fake routes emulate it.
   - `RenderTestResponse.networkWaterfall` (`contracts/api/tools.ts:142`, optional additive per WP-10-A1) — must be populated by the new-pool wiring or the viewers show the baseline empty state.
   - The per-URL content-artifact read route itself (absent from the frozen catalog; see item 2).

## 5. Testid list for the acceptance harness (exact strings)

**done_when walk — URL detail rendered tab (root `rendered-diff`, inside `rendered-tab`):**

| Check | Testids |
|---|---|
| raw vs rendered title (`/pages/late-meta.html`) | `rendered-diff-title-raw` → "Raw title before script", `rendered-diff-title-rendered` → "Rendered title after script", row `rendered-diff-field-title` |
| canonical change | `rendered-diff-canonical-raw`, `rendered-diff-canonical-rendered`, row `rendered-diff-field-canonical` |
| critical rendered-only noindex | row `rendered-diff-field-noindex` (data-direction), `rendered-diff-noindex-raw`, `rendered-diff-noindex-rendered`, `rendered-diff-noindex-direction`, `rendered-diff-noindex-critical`, `rendered-diff-noindex-raw-directives`, `rendered-diff-noindex-rendered-directives`, header badge `rendered-diff-critical` |
| word counts (`/pages/js-content.html`, ~0 vs ~200) | `rendered-diff-wordcount-raw`, `rendered-diff-wordcount-rendered`, `rendered-diff-wordcount-delta`, row `rendered-diff-field-text` |
| diff shell / generic rows | `rendered-diff`, `rendered-diff-table`, `rendered-diff-empty` (clean page); per-field `rendered-diff-field-<field>` with `-raw`, `-rendered`, `-critical` children |

**Rendered tab shell and viewers:** `rendered-tab` (data-url-id), `rendered-tab-offline`, `rendered-tab-empty`, `rendered-tab-loading`, `rendered-tab-error`, `rendered-tab-capture` (+ `-value`, `-source`), `rendered-screenshot` (`-img`, `-empty`), `rendered-dom` (`-empty`), `rendered-console` (`-empty`, `-row-<i>`, `-text-<i>`), `rendered-waterfall` (`-empty`, `-total`, `-row-<i>`, `-url-<i>`, `-status-<i>`, `-bytes-<i>`).

**Comparison surface rows (done_when: both pages listed):** `rendered-comparison`, `rendered-comparison-count`, `rendered-comparison-finding-count`, `rendered-comparison-url-<urlId>` (data-critical=true/false — expect entries for the late-meta and js-content urlIds), `rendered-comparison-loading`, `rendered-comparison-error`, `rendered-comparison-empty`, `rendered-comparison-offline`.

**Render tester / single-page output:** `rendered-output` (`-waf` with data-detected/data-provider, `-waterfall-count`, `-console-count`, `-screenshot`, `-waterfall`, `-console`, `-dom`); UI-18 `sp-waterfall`, `sp-console`, `sp-dom` (with `sp-screenshot`, `sp-artifact` pre-existing).

**UI-10 indicator:** `capture-mode-indicator` (data-mode = `non-js|js|dual|adaptive|baseline`), `capture-mode-indicator-value`, `capture-mode-indicator-source` (data-source = `raw|rendered|both`).

## 6. Gaps and questions

1. **No artifact read route (blocks full deliverable 1).** `renderedArtifacts.ts` states the frozen REST catalog exposes no per-URL content-artifact read route at this amendment, so the DOM/screenshot/console/waterfall viewers of `RenderedTab` are inert without a coordinator-supplied loader. Against the brief's deliverable 1 wording ("rendered DOM viewer, screenshot, console log, network waterfall … with dual evidence") the viewer components exist and are bound, but their data path is a handoff item, not delivered end to end. This is the same asymmetry the dispatch itself anticipates (coordinator note 3 promises the render-tester response carries artifacts "once the coordinator wires the new pool" — that covers UI-19, not the URL-detail tab).
2. **Nothing is mounted except UI-18.** Per `04-ui-surface-map.md` §2 line 54 ("Every view binding must reach the actual data") and §3 line 60 (acceptance requires reaching the bound surface through its navigation path), none of the CAP-A-004 / CAP-A-015 / CAP-A-035 view bindings are satisfied until items 4.2.1–4.2.5 are applied — consistent with the dispatch (mounting is coordinator's), but worth stating plainly for the phase gate.
3. **UI-16 registration shape.** `02-virtualization-and-explorers.md` §8.2 line 41 requires a "Rendered comparison | Response-vs-rendered differences per URL (section 7.18)" report surface, and `04-ui-surface-map.md` §1 line 34 lists "Rendered comparison" under UI-16 specialist surfaces; neither mandates the `ExplorerDefinition` registry. Since `ExplorerId` (explorerTypes.ts:21-32) is a closed eleven-member union (FS-CRAWL-002 §8.3), question for the coordinator: mount the surface standalone (as built) or extend the union via a change record? Not a spec conflict — a wiring decision.
4. **URL detail has no tab strip today.** The brief calls UI-13 a "rendered tab" and `04-ui-surface-map.md` §1 line 31 names "Report › URL explorer and URL detail"; `UrlDetailPanel.tsx` currently renders a flat section list (`url-detail-*` sections). Adding the tab is a shell decision; the `RenderedTab` component itself is tab-agnostic. Spec §8.2 line 30 lists "optional rendered screenshot" among URL-detail content, which the tab delivers once artifacts are loadable.
5. **Word-count evidence aliasing.** `renderedDiff.ts:157-159` reads `renderedWordCount ?? totalWordCount ?? words` for the rendered count (with `rawWordCount` read directly). The test fixtures carry all three keys as the comparer emits them; the comparer source (WP-10-02 backend) was not re-read in this reconstruction, so exact live evidence keys are **not confirmed** beyond the fixtures and the module's documented wire shapes. If the live key differs the row shows "—", never a fabricated number.
6. **Backend serving of contract fields not confirmed** — `UrlDetail.captureSource`, `audits.listFindings?ruleKey=…`, `RenderTestResponse.networkWaterfall`: all typed in the locked frontend mirrors and exercised by the fake routes; whether the current backend serializes them was not verified here (see §4.2.7).
7. **Minor: unused prop.** `RenderedTabProps` declares `readonly height?: number` but `RenderedTab` does not consume it (the stack scrolls with `overflowY: 'auto'`). Harmless; either wire it or drop it at mounting time.
8. **Fixture traps coverage.** The brief's `fixture_traps` (late-meta-change, script-injected-content, js-only-link) are all represented in the test fixtures and asserted at component level; the phase-gate UI walk itself is pending coordinator mounting (done_when).
