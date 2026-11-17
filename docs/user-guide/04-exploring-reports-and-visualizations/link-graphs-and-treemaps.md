# Link Graphs and Treemaps

CrawlForge visualizes your site's structure and internal-link economy as interactive, GPU-accelerated graphics. This guide covers the internal-link network graph, the crawl-depth map, the site-structure tree, redirect-chain diagrams, and the section treemaps.

You will learn what the nodes, edges, zones, and equity encodings mean, how to navigate the views, and how to read link-equity concentration.

All graphs render locally from your audit's data:

- The network and depth graphs use WebGL force-directed rendering (Sigma.js + Graphology).
- The treemaps use canvas charting (Apache ECharts).
- Layout computation runs in a Web Worker, so the interface never freezes while a large graph settles.
- Nothing is fetched from a remote service.

## The Visualizations surface

The Visualizations surface collects the audit's graphical views:

| View | What it shows |
|---|---|
| Site-structure tree | The site organized by URL path |
| Crawl-depth map | Pages arranged by their crawl depth from the seeds |
| Internal-link network graph | Pages as nodes, internal links as edges, force-directed layout |
| Redirect-chain diagrams | Redirect hops and chains |
| Section treemap | Site sections sized by URL count or weight |

Each view is driven by the audit's graph data. The graph endpoint accepts a graph type (structure tree, link network, or redirect chains) plus depth/size limits, and returns nodes and edges. The same bounded-graph model also powers the Semantic cluster view's similarity-edge overlay.

## How the network graph is built

The internal-link network graph is a force-directed layout (ForceAtlas2-class):

- Connected pages attract.
- Unrelated pages repel.
- The simulation settles into clusters that reflect your site's linking structure.

The mechanics:

- **Nodes** are URLs (pages) in the audit.
- **Edges** are internal link edges between pages.
- The layout runs in a **Web Worker**, so dragging, zooming, and hovering stay responsive even while the simulation computes.
- Graphology holds a bounded in-memory graph model; the full dataset is never loaded into the page at once.

### What each edge carries

Every internal link edge records:

| Field | Meaning |
|---|---|
| Source URL | The page the link originates from |
| Target URL | The page the link points to (nullable for external targets) |
| Canonical target | The canonical form of the target |
| Anchor text | The visible text of the link |
| Relation flags | Link relation attributes |
| Internal/external marker | Whether the target is on-site or off-site |
| DOM zone | The zone of the source anchor (see [DOM zones](#dom-zones-and-default-weights)) |

This is the same dataset the link explorer shows in tabular form, so the graph and the explorer are two views of one underlying link model.

### Node and edge encodings

The graph encodes meaning in size and color:

- **Node size** reflects the page's **link-equity percentile** — higher-equity pages render larger.
- A **weighted/unweighted toggle** switches which equity computation drives the sizing.
- **Node color** reflects **status class or depth**, so you can spot error pages, redirects, or deep pages at a glance.
- **Hover and selection** are synchronized with the URL explorer: hovering a node highlights the corresponding URL, and selecting a node opens that URL's detail.

> **Tip:** Color by status class to find broken or blocked pages clustered in one section; color by depth to see how far pages sit from your seeds.

## Link equity: weighted and unweighted

Link equity estimates how internal linking distributes importance across your pages. CrawlForge computes an internal PageRank-style distribution and offers two variants.

### Unweighted equity

- Every internal link contributes equally.
- This is the classic distribution.
- It is retained for comparability.

### Weighted equity

- Each link contributes according to the **DOM zone** of its source anchor.
- Editorial in-content links transfer significantly more weight than boilerplate links.
- The computation is a standard iterative PageRank variant with **configurable damping**.

For each URL, the weighted computation produces:

- A **weighted equity score**.
- A **percentile** (its rank relative to all pages).
- A **concentration** measure.

These are persisted as the `link-equity-weighted` metric. In the graph, the weighted/unweighted toggle changes node sizing so you can compare how zone weighting reshapes which pages look important.

> **Note:** Because in-content links weigh 1.0 and footer links weigh 0.1, a page linked mainly from footers can look far less important under weighted equity than under unweighted equity. The toggle makes that difference visible.

## DOM zones and default weights

Every internal link edge is classified by the DOM zone of its source anchor into one of six zones. The built-in default weights are:

| DOM zone | Default weight | Meaning |
|---|---|---|
| `in-content` | 1.0 | Editorial links inside the scoped content region |
| `breadcrumb` | 0.6 | Breadcrumb navigation links |
| `navigation` | 0.3 | Primary navigation menus |
| `sidebar` | 0.3 | Sidebar / complementary regions |
| `header` | 0.2 | Header / banner links |
| `footer` | 0.1 | Footer links |

Weights are configurable per audit.

### How a link is classified

Classification uses landmark elements/roles and your Content Area Scoping rules:

1. Anchors inside the scoped content region classify as `in-content`.
2. Boilerplate landmarks classify by a deterministic precedence:
   - Breadcrumb markers (highest).
   - `nav` / `role=navigation`.
   - `header` / `role=banner`.
   - `footer` / `role=contentinfo`.
   - `aside` / `role=complementary` (sidebar).
3. Body anchors that match no boilerplate landmark default to `in-content`.

Classification runs during link inventory and is re-derived from stored bodies on re-audit, so it stays consistent offline.

### Zone badges and the zone filter

The link explorer surfaces the zone classification directly:

- A **zone badge** appears on each link edge.
- A **zone filter** lets you isolate, for example, only `in-content` links or only `footer` links.
- The link dataset also exposes the weighted-equity fields.
- The graph's node sizing follows the same weighted/unweighted choice.

## Reading link-equity concentration

Beyond individual page scores, CrawlForge computes link-graph statistics that describe the shape of your internal-link economy.

### The graph statistics

| Statistic | What it tells you |
|---|---|
| Orphan pages | Pages with no internal links pointing to them — discovered by other means but unreachable through the link graph |
| Hub pages | Pages with unusually high connectivity that concentrate links |
| Reachability | Which pages are reachable from the seeds by following internal links (standard graph traversal) |
| Link-equity distribution | Inlink counts, percentile ranking, and concentration across pages |

Concentration tells you whether equity is spread evenly or pooled in a few pages:

- A **highly concentrated** distribution (a handful of large nodes dominating the graph) often means important pages are starved of internal links while a few hubs hoard equity.
- A **more even** distribution suggests equity is broadly shared.

### A practical reading workflow

1. Open the internal-link network graph and size nodes by **weighted** equity.
2. Look for **large isolated clusters** — sections that link heavily internally but receive few links from the rest of the site.
3. Switch to **unweighted** sizing and compare; pages that shrink dramatically rely on boilerplate links rather than editorial ones.
4. Color by **status class** to check whether high-equity hubs are actually healthy, indexable pages.
5. Cross-reference **orphan pages** and **reachability**: a page with equity but no reachable path, or an indexable page with no inlinks, is a candidate for internal-link attention.
6. Select any node to open its URL detail and inspect its inbound/outbound links and redirect chain.

> **Tip:** Pair the graph with the Semantic surface's link-opportunities grid to find semantically related pages that are not yet linked — concrete candidates for redistributing equity.

## The crawl-depth map

The crawl-depth map arranges pages by their **depth** — the number of hops from the crawl seeds.

- It uses a **hierarchical/radial layout** (rather than the force-directed layout of the link network).
- Depth reads as distance from the center or root.
- Pages near the seeds sit at the shallow levels; deeply buried pages sit far out.
- Node coloring can reflect depth, making very deep pages easy to spot.

Depth is a useful proxy for crawl budget and discoverability: pages many hops from the seeds are harder for crawlers to reach.

Use the depth map to:

1. Find indexable pages buried too deep.
2. Note their depth level.
3. Verify their internal-link support in the network graph.
4. Prioritize shallow internal links to important deep pages.

## The site-structure tree

The site-structure tree organizes the site **by URL path**, showing the directory hierarchy of your URLs.

It is the structural complement to the link graph:

- The link graph shows how pages **connect**.
- The structure tree shows where pages **live** in the URL namespace.

Use it to spot:

- Sprawling directories.
- Inconsistent path patterns.
- Sections that have grown large.

## Section treemaps

Treemaps render site sections as nested rectangles whose area encodes a quantity. CrawlForge uses canvas-rendered treemaps (Apache ECharts) so they stay responsive at scale.

| Treemap | Sized by | Where |
|---|---|---|
| Section treemap | URL count or weight | Visualizations |
| Coverage treemap | Container coverage by directory | Analytics & Tagging |
| Topical treemap | Term density | N-Gram surface |

Treemap rectangles are interactive: clicking a section applies the corresponding filter to the underlying explorer, just like the dashboard charts.

> **Note:** Treemaps and charts use canvas/WebGL rendering, never per-datum SVG DOM. This is what keeps them smooth when a section contains tens of thousands of URLs.

## Redirect-chain diagrams

Redirect-chain diagrams visualize redirect hops and chains, letting you trace a URL through its redirects to the final target.

They complement:

- The URL detail's redirect chain.
- The migration redirect-matrix validator.

Use them to spot:

- Excessive redirect depth.
- Redirect loops.
- Chains that waste crawl budget.

## Level-of-detail budget

To stay interactive on very large sites, the graph applies a level-of-detail (LOD) budget:

- The default render cap is **20,000 nodes / 60,000 edges** (configurable).
- Beyond the cap, the server **aggregates or samples** the graph.
- The UI shows a **"showing X of Y" banner** so you always know you are viewing a bounded projection, not the whole graph.
- Graphology holds only the bounded in-memory model; the full graph is never loaded into the page.

You can adjust the depth/size limits when requesting graph data to focus on a shallower or smaller slice. The Semantic cluster overlay runs under the same LOD budgets.

> **Warning:** If you see the "showing X of Y" banner, conclusions about absent nodes or edges may reflect the sampling cap rather than the actual site. Raise the limit or narrow the depth before drawing conclusions about a specific page's connectivity.

### Working with large graphs

When an audit exceeds the LOD budget, use this approach to stay accurate:

1. Note the **"showing X of Y" banner** so you know a projection is in effect.
2. **Narrow the depth limit** to focus on the shallow, most-crawled part of the site.
3. **Raise the size limit** if your hardware handles it and you need broader coverage.
4. **Filter the link explorer** (for example, by DOM zone or internal/external) to investigate a specific slice tabularly, where pagination is exact.
5. **Select individual nodes** to open their full URL detail rather than inferring from the sampled graph.

The tabular link explorer always reflects the full dataset through server-side pagination, so it is the authoritative place to confirm a specific edge that the sampled graph may not show.

## Navigation controls

The graph views are interactive canvases:

- **Zoom and pan** to move around the layout and inspect dense clusters.
- **Hover** a node to highlight it and its connections; the matching URL is highlighted in the synchronized URL explorer.
- **Select** a node to open its URL detail side by side.
- **Toggle weighted/unweighted** equity to change node sizing.
- **Switch coloring** between status class and depth.
- **Adjust depth/size limits** to bound the graph and stay within the LOD budget.

Because layout runs in a Web Worker and rendering uses WebGL, these interactions remain smooth even on large graphs.

### The Semantic cluster overlay

The Semantic surface adds a cluster view with a similarity-edge graph overlay, built on the same Sigma.js + Graphology engine and the same LOD budgets. It visualizes semantically related pages (from local embeddings) rather than literal hyperlinks, so you can compare semantic similarity against the actual link graph.

## Choosing the right view

Each visualization answers a different question. Use this to pick the right starting point:

| Question | View |
|---|---|
| Which pages are important, and where is equity pooled? | Internal-link network graph (weighted sizing) |
| Which pages are buried too deep to crawl easily? | Crawl-depth map |
| How is the site organized by URL path? | Site-structure tree |
| Which sections dominate by page count or weight? | Section treemap |
| Where are redirect hops, loops, or long chains? | Redirect-chain diagrams |
| Which pages are semantically related but unlinked? | Semantic cluster overlay + link-opportunities grid |

A common investigation moves between views: spot a concentration in the network graph, check the depth map for buried pages, then confirm the section's size in the treemap.

## The link-equity chart

In addition to the graph, the weighted link-equity distribution is available as a chart metric (`link-equity-weighted`). Like the dashboard charts, it is canvas-rendered and pre-bucketed, and its filters propagate to the underlying explorer. Use the chart for a ranked overview of equity and the graph for the structural picture of how pages connect.

## Key terms

| Term | Meaning |
|---|---|
| Node | A URL (page) in the graph |
| Edge | An internal link from one page to another |
| Link equity | A PageRank-style estimate of a page's importance from internal linking |
| Percentile | A page's equity rank relative to all pages |
| Concentration | How evenly or unevenly equity is spread across pages |
| Orphan page | A page with no internal links pointing to it |
| Hub page | A page with unusually high connectivity |
| Reachability | Whether a page can be reached from the seeds via internal links |
| Depth | The number of hops from the crawl seeds to a page |
| DOM zone | The region of the source page where a link's anchor lives |
| LOD budget | The node/edge render cap that keeps large graphs interactive |

## Related guides

- [Dashboards and virtualized explorers](dashboards-and-virtualized-explorers.md) — the link explorer, URL detail, and the grid mechanics behind the synchronized explorer.
- [Exports and secure destinations](exports-and-secure-destinations.md) — export the link dataset and equity fields.
- [N-grams and topical density](../05-advanced-seo-and-intelligence-modules/n-grams-and-topical-density.md) — topical treemaps and the anchor-text demand behind link opportunities.

[← Documentation portal](../README.md)