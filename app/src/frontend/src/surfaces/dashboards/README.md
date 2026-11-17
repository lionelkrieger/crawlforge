# Charts / Graphs / Dashboards — handoff notes (WP-06-04)

Owner: cf-wp-01-03. ECharts Canvas dashboards + Sigma.js/Graphology
force-directed networks, all offline from precomputed local aggregates
(FS-CRAWL-001 §8.3/§8.5, FS-CRAWL-002 §8.3–8.5/§7.4).

## Architecture (port-injection posture)

- **Pure transforms** (`charts/chartTransforms.ts`, `graphs/graphModel.ts`,
  `graphs/graphSampling.ts`, `graphs/layoutCore.ts`, `charts/crossFilter.ts`)
  are deterministic data → option/graph/query functions. No DOM, no renderer
  imports (echarts is a type-only import in transforms). Exhaustively
  unit-tested in the node environment.
- **Renderer ports**: `ChartRendererFactory` (charts/chartPorts.ts) and
  `GraphRendererFactory` (graphs/graphPorts.ts). Real adapters
  (`echartsRenderer.ts` — tree-shaken echarts/core canvas; `sigmaRenderer.ts`
  — sigma WebGL) are the ONLY runtime library imports. jsdom suites inject
  mocks — no WebGL/canvas needed.
- **Worker port**: `LayoutWorkerFactory` (graphs/layoutClient.ts). Layout
  runs in a Web Worker (`layoutWorker.ts` wrapping the pure `layoutCore.ts`,
  graphology-layout-forceatlas2). One-shot lifecycle: terminated in
  `finally`; hung-worker timeout; bounded synchronous fallback.
- **Lifecycle**: `EChart.tsx` and `useGraphRenderer.ts` dispose strictly on
  unmount — canvases, WebGL contexts, workers, ResizeObservers, and every
  listener released (criterion: no leaks).

## Offline / aggregates-only invariant

Dashboards consume ONLY precomputed aggregates through the typed loopback
client: `audits.summary`, `audits.scores`, `audits.chartSeries`
(pre-bucketed points), `waf.blocksSummary`, `hosts.list`, bounded
`ngrams.list`/`ngrams.gaps`, `logs.correlation`, `geo.llmsTxt`,
`geo.governance`, `analytics.overview`/`analytics.coverage`,
`semantics.clusters`/`semantics.linkOpportunities`, and the LOD-bounded
`audits.graph`. No client-side recomputation over raw rows; every loader
degrades to null offline. Live counters are read from the accepted
`liveProgress` store (read-only consumption; the store is never edited).

## Large-graph discipline (§8.5)

Deterministic LOD sampling (`DEFAULT_LOD_BUDGET` = 20k nodes / 60k edges):
top-K by link-equity percentile, edges capped by weight; honesty banner
"Showing X of Y nodes · E of F edges" from `GraphData` totals/truncated;
drill-down = induced neighborhood of the selected node. Weighted mode uses
the server's zone-weighted `equityWeight`/edge weight; unweighted counts
every link as 1 (dom_zone weights honored, §15.6). Node size = equity
percentile; color = status class or depth ramp. Semantic overlays: cluster
colors + similarity edges to centroids + violet dashed opportunity edges.

## Cross-filtering (§8.3)

`charts/crossFilter.ts` maps chart selections to locked ListQuery filters
(status/severity/category/waf-provider/ngram-zone) and drives the ACCEPTED
store actions (`loadUrlPage`/`loadFindingPage`/`loadLinkPage`/
`loadNgramPage`). Wire values are lowercase-no-hyphen; status classes keep
their `2xx` form. Export-to-image is a local data-URL download only.

## Shell wiring (for the integrator)

- Mount surfaces inside the existing `ShellProvider` + MantineProvider.
- `AuditDashboard`/`CrawlHealthPanel`/`ScoreTrendSurface`/intelligence
  panels take aggregate props; `useDashboardAggregates(api, auditId)` loads
  them. `VisualizationSurface` takes `api` + `auditId` directly.
- Pass `onCrossFilter={(sel) => applyChartCrossFilter(actions, auditId, sel)}`
  to wire chart clicks to the explorers.
- Tests inject `factory`/`rendererFactory`/`workerFactory` props; production
  defaults are lazy singletons (sigma loads via dynamic import on first real
  graph mount — jsdom never evaluates WebGL globals).

## Visualization control workbench

`VisualizationSurface` carries a permanently visible eight-tool control
region, all applied live: weighted equity toggle (dom_zone weights),
status/depth color mode, opportunity overlay toggle, label-density slider
(`labelRenderedSizeThreshold` via the renderer port — no remount), layout
iteration stepper (re-runs the worker layout), LOD node-budget select
(re-samples with the honesty banner), drill-radius select (neighborhood
hops), and reload aggregates. `LinkGraphSurface`/`SemanticGraphSurface`
expose the same knobs as props (`labelThreshold`, `iterations`,
`drillRadius`, `budget`) for direct composition.

