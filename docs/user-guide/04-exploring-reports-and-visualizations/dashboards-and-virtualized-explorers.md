# Dashboards and Virtualized Explorers

CrawlForge turns a crawl into a set of interactive, fully offline report surfaces. This guide covers the report workspace, the dashboard, and the virtualized data explorers that let you sort, filter, pin, and query hundreds of thousands of rows without slowing down.

Everything here runs against the local backend on your own machine:

- There are no accounts, no licenses, and no entitlement screens.
- There are no cloud calls and no authorization checks gating report access.
- Every report is viewable at any time, fully offline.
- The first-run experience proceeds directly to project creation.

The explorers are built on a virtualized grid engine (TanStack Table + TanStack Virtual) inside the Mantine UI shell. The practical result: you can scroll a 500,000-row URL list as smoothly as a 50-row one, because the grid only ever keeps a small window of rows in the page at once.

## The report workspace

The report window uses a dark-first enterprise layout (dark by default; a light theme is optional and your choice is persisted). The shell is organized into three persistent regions:

- **Header** — the audit switcher (move between audits of a project) and global search.
- **Navbar** — navigation to projects, reports, tools, and settings.
- **Bottom status bar** — live local memory and crawl telemetry (see [The status bar](#the-status-bar)).

### The shell windows

The desktop shell hosts a catalog of windows, each with a distinct role:

| Window | Role |
|---|---|
| Splash | Startup |
| Main hub | Projects, dashboards, and schedules |
| Report window | The audit report surfaces and explorers |
| Visualization window | Graphs and treemaps |
| Single-page analyzer | Analyze one URL without a full audit |
| Standalone tools | Self-contained diagnostic and authoring tools |
| Settings | Configuration surfaces |
| Recovery | Backend-failure diagnostics and recovery |

The shell spawns, monitors, and restarts the backend, discovers its port, loads only local UI assets, provides native file dialogs for export/import, surfaces OS taskbar progress and local notifications, and persists window state.

### Dockable split panes

Explorers and the URL detail view live in resizable, dockable panes:

- Place an explorer and a URL detail side by side.
- Resize the split to give either side more room.
- Rearrange panes to suit your workflow.
- Each window's layout persists, so your arrangement is restored next time.

A typical layout is the URL explorer on the left and the selected URL's detail on the right: click a row and the detail pane updates without leaving the list.

### Dark and light themes

- The theme is dark by default.
- Switch to light in settings if you prefer.
- The choice is saved to your application settings and applied across the shell.
- Charts and graphs synchronize their palette with the active theme.

## Ctrl+K Spotlight

Press `Ctrl+K` anywhere in the shell to open the Spotlight command palette. Spotlight is a fuzzy search over everything you can navigate to or run:

- Navigation targets (surfaces, windows, and views).
- Audits (jump straight to an audit's reports).
- Saved queries (apply a query-builder definition to an explorer).
- Standalone tools.
- Settings actions.
- Commands registered by installed plugins.

Spotlight is fully keyboard-operable:

1. Press `Ctrl+K` to open it.
2. Type to fuzzy-filter the matches.
3. Use the arrow keys to move through results.
4. Press `Enter` to activate the highlighted item.

It is the fastest way to move around a large project without touching the mouse.

> **Tip:** Save a frequently used filter as a named query (see [The interactive query builder](#the-interactive-query-builder)), then recall it from Spotlight by name instead of rebuilding it each time.

## The dashboard

The dashboard is the landing surface for an audit. It summarizes the crawl at a glance:

- **Summary cards** — counts by status class, severity, and category, plus crawl-health indicators.
- **Severity and category charts** — stacked bars and pie/donut breakdowns of findings.
- **Crawl-health panel** — status distribution and crawl completion signals.
- **Top-issue lists** — the most frequent or most severe findings, ready to drill into.

Charts are rendered on canvas (Apache ECharts), not as per-point SVG, so they stay responsive even when an audit contains very large series. Chart data arrives pre-bucketed from the backend; the dashboard does not hold the full dataset in the page.

### Interactive chart filters

Charts are not static pictures. Clicking a segment applies that filter to the underlying explorer lists:

1. Click a chart segment (for example, a severity band or a status class).
2. The filter propagates to the matching explorer.
3. The issue explorer or URL explorer opens already narrowed to that segment.

This is the quickest path from "something looks wrong in the chart" to "here are the affected URLs".

> **Note:** Dashboard reads are designed to stay under one second at target scale. If a chart offers a filter, the matching explorer supports the same filter server-side.

## Virtualized explorers at scale

Every tabular explorer renders only the rows visible in the viewport plus a small overscan buffer:

- The mounted DOM row count never exceeds **50 rows**.
- The target band is **30–50 rows** including overscan.
- Rows are recycled as you scroll, so the DOM stays small.

### What "sustained 60 FPS at 500k+ rows" means for you

It means scrolling stays smooth no matter how large the audit is:

- A 500,000-row URL explorer scrolls at the same fluid 60 frames per second as a tiny list, on ordinary desktop hardware.
- You never wait for "all rows to load" before you can scroll, sort, or filter.
- The grid shows a window immediately and fetches more as you move.

This works because the full dataset is **never held in the page**:

- Rows are fetched from the backend in pages (offset/limit pagination). The default page size is **200 rows**.
- The page keeps a bounded resident cache of up to **20 pages** (about 4,000 rows) with least-recently-used eviction.
- Scrolling back to recently seen rows is instant; older windows are fetched again on demand.
- The total row count comes from pagination metadata, so the scrollbar reflects the true size of the dataset even though only a window is mounted.

The same virtualization applies to the URL explorer, issue explorer, link explorer, custom extraction explorer, N-Gram explorer, WAF detection lists, and any plugin report tab with tabular content.

### Wide datasets and horizontal virtualization

Custom extraction datasets can be very wide (many per-rule columns). The grid virtualizes horizontally too:

- Only the visible columns plus a small overscan are rendered.
- Pinned columns are always mounted (exempt from horizontal virtualization).
- The pin count is bounded (default **≤ 4**) so the frozen region never crowds out the scrollable area.

## The explorer surfaces

CrawlForge ships a broad catalog of report surfaces. The tabular ones all obey the same virtualization, sorting, filtering, and accessibility contract.

| Surface | What it shows |
|---|---|
| Dashboard | Summary cards, severity/category charts, crawl-health panel, top-issue lists |
| URL explorer | Per-URL records: status and timing, indexability, depth, content type; drill-down to full detail |
| Issue explorer | Filterable, sortable, groupable findings; drill-down to affected URLs; bulk export |
| Link explorer | The internal/external link dataset with source/target, anchor text, DOM-zone badges, and equity fields |
| Custom extraction explorer | Your user-defined extraction dataset with dynamic per-rule columns; per-rule match counts and diagnostics |
| Visualizations | Site-structure tree, crawl-depth map, internal-link network graph, redirect-chain diagrams, section treemap |
| Audit comparison | Any two audits of a project: new/resolved findings and metric deltas |
| Content comparison | Per shared URL: side-by-side title, heading, and visible-text differences plus status/metadata changes |
| Accessibility | Violations grouped by rule family and severity, with implicated elements and remediation |
| Performance / Core Web Vitals | Lab-metric distributions, worst pages, budget pass-rate, code-coverage summary |
| Structured data | Every block with type, parsed fields, and validation/rich-result outcome |
| Technology | Detected technologies by category with confidence and affected-URL counts |
| Custom search | Per-item match overview and per-URL matched snippets |
| Snippet results | Custom JavaScript snippet outputs joined to URLs, with dynamic columns |
| Rendered comparison | Response-vs-rendered differences per URL |
| Scores | Per-category and overall audit scores with cross-audit trend |
| Suppressed findings | User-suppressed findings and rules, reversible |
| N-Gram explorer | Zone-tabbed term frequencies (TF/DF), cannibalization clusters, topical gaps |
| Semantic | Link-opportunities grid, cluster view, per-URL semantically related panel |
| GEO Readiness | `llms.txt` status cards and the bot-governance matrix grid |
| Analytics & Tagging | Container inventory, per-URL tag/consent timeline, coverage treemap |
| Log File Analyzer | True Orphans / Crawl Budget Waste / Uncrawled Indexable correlation tabs |

> **Note:** The N-Gram, Semantic, GEO Readiness, Analytics & Tagging, and Log File Analyzer surfaces are covered in depth in their own guides; this guide focuses on the shared grid mechanics they all use.

## Sorting

Sorting is multi-column and executes **server-side**, so you can sort the entire dataset — not just the rows currently on screen.

### Sorting a single column

1. Click a column header to sort by that column.
2. Click again to reverse the direction.
3. Click a third time to remove the sort.

The cycle is **ascending → descending → unsorted**. A header indicator shows the current direction.

### Chaining multiple columns

1. Click the first column to establish the primary sort.
2. **Shift-click** additional headers to add them to the chain.
3. The order you add them is the sort priority: the first column is the primary key, the next breaks ties, and so on.
4. The header indicator shows each column's position in the chain.

Because sorting runs on the backend, the result is correct across all rows and pages, and the grid re-windows the sorted result automatically.

> **Tip:** Sort by severity descending, then shift-click a category column to group the most severe issues by category in one pass.

### Which columns sort

Sortable columns are those the backend can order server-side for that dataset:

- URLs: record fields such as status and depth.
- Findings: severity, category, and rule key.
- Links: source and target.

Columns the backend does not support ordering on are shown as **static headers without a sort control**, so you are never offered a sort that would silently do nothing.

## Column pinning

Pin important columns to the left or right edge so they stay visible while you scroll horizontally through a wide dataset.

1. Open a column's header menu.
2. Choose pin left or pin right.
3. The column freezes to that edge and stays mounted at all times.

Details:

- Pinned columns are exempt from horizontal virtualization.
- The number of pinned columns is bounded (default **≤ 4**) to keep the frozen region usable.
- Unpin from the same header menu to return the column to the scrollable area.

Pinning is ideal for keeping the URL or status column in view while you inspect many extraction columns to the right.

## Instant filtering

Type into an explorer's filter box and the list narrows as you type. Filtering is **instant but debounced (150–300 ms)**:

- The grid waits a fraction of a second after you stop typing before issuing the server-side filter.
- Rapid keystrokes do not flood the backend.
- The result feels immediate while staying efficient.

Filters execute server-side over the whole dataset, and they compose with sorting and with the query builder. Common filters include:

| Dataset | Example filters |
|---|---|
| URLs | Status class, content type, depth, indexability, text search |
| Findings | Severity, category, rule key, URL |
| Links | Source/target, internal/external, DOM zone |

> **Note:** "Instant" describes the responsiveness you see, not a client-side scan of all rows. The filter is always evaluated by the backend against the full dataset.

## Dynamic columns

The custom extraction explorer (and snippet results) build their columns dynamically from your rules:

- Each extraction rule contributes its own column.
- Multi-valued rules expand to the **busiest page's column count** — if one page yields five values for a rule, the dataset shows five columns for that rule so no value is hidden.
- Per-rule match counts and diagnostics (pages matched, values captured, discards, truncations) are available from the extraction summary.

These wide datasets use horizontal virtualization, so even a large number of dynamic columns scrolls smoothly. Exports of custom extraction use the same dynamic-column expansion as the explorer.

## The interactive query builder

For filters beyond a single text box, use the interactive query builder (a standalone tool, also reachable from Spotlight). It composes **nested AND/OR filter rules** over URL columns, including dynamic custom-extraction and snippet columns.

### Supported operators

| Operator | Use |
|---|---|
| contains / starts-with / ends-with | Text matching on a column |
| equals | Exact match |
| in-set | Match any value in a list |
| between | Range match (numeric or ordered values) |
| regex | Regular-expression match |
| numeric comparison | Greater than / less than and related comparisons |

### Building a nested query

1. Open the query builder from Spotlight or the standalone tools window.
2. Add a rule: choose a column, an operator, and a value.
3. Combine rules with AND or OR.
4. Nest groups to express complex logic.
5. Apply the query to an explorer, or save it for reuse.

Rules nest, so you can build groups such as:

`(status equals 200 AND depth between 0 and 2) OR (indexability equals indexable AND title contains "product")`

The rule tree composes into the same server-side filters the explorers use.

### Saved queries

- Save a query under a name.
- Saved queries apply to both the explorers and the export flow.
- Filter a list, then export exactly that filtered slice.
- Recall saved queries from Spotlight or the query-builder tool.

> **Tip:** Build an "indexable pages with errors" query once, save it, and reuse it across audits and exports instead of re-filtering each time.

## Keyboard navigation and accessibility

Virtualization never compromises accessibility. The grid preserves:

- **Keyboard navigation** — move through rows and cells from the keyboard without a mouse.
- **Focus retention** — focus stays stable as rows recycle during scrolling, so you do not lose your place.
- **ARIA row semantics** — the grid exposes `aria-rowcount` (the true total row count) and `aria-rowindex` (each row's absolute position).

Assistive technology therefore announces the correct position within the full dataset even though only a window is mounted. Spotlight, forms, and the surrounding shell are likewise fully keyboard-operable.

> **Note:** Because `aria-rowindex` reflects the absolute row position, a screen reader reports "row 4,213 of 500,000" correctly — not "row 13 of 30" — despite only ~30 rows being present in the DOM.

## Density, columns, and selection

The grid supports the everyday interactions you expect from a data table:

- **Density modes** — switch between comfortable and compact row spacing to fit more rows on screen.
- **Column resize** — drag column edges to resize.
- **Column reorder** — drag headers to reorder.
- **Row selection** — select rows for drill-down or bulk actions.

Your selection and filter state are handed to the export flow, so an export can target exactly the rows you selected or filtered (see [Exports and secure destinations](exports-and-secure-destinations.md)).

## URL detail side by side

Selecting a URL opens its detail in a dockable pane. The detail view includes:

- Status and timing.
- The extracted element inventory.
- Findings for the URL.
- Inbound and outbound links.
- The redirect chain.
- An optional rendered screenshot (when rendering was captured).
- A **SERP & Social Card Preview** panel with a desktop/mobile toggle and live truncation highlighting.
- A header/HTML conflict panel with a side-by-side directive diff (where header and HTML directives disagree).

Because the detail pane docks beside the explorer, you can step through rows with the keyboard and read each URL's detail without losing your place in the list.

## Standalone tools

A set of self-contained tools operates without requiring a full audit. Each is reachable from the shell (and, where stateless, from the automation interface):

| Tool | What it does |
|---|---|
| Robots tester | Returns the allow/disallow verdict per test URL under the crawler's own parser, including the matched rule |
| URL checker | Fetches a single URL and reports status, full redirect chain, selected headers, and indexability signals |
| Structured-data checker | Parses pasted markup or a URL into blocks with validation and rich-result outcome |
| Render tester | Renders a single URL and returns a screenshot, the post-render document, and console messages |
| XML sitemap generator | Builds a sitemap from an audit's URL set, filterable by indexability and status |
| Interactive query builder | Composes nested AND/OR filter rules; saved queries apply to explorers and exports |
| `llms.txt` Generator & Validator | Generates and validates `llms.txt` / `llms-full.txt` as a local export |

The render tester and single-page analyzer also embed a point-and-click selector picker that generates resilient CSS selectors (and an XPath alternative) from the rendered DOM, with "send to" actions into custom extraction rules or content-scoping lists.

## Settings surfaces

Settings gather the configuration surfaces, each with appropriate feedback:

- Database provider selection with connection-test feedback.
- Export-destination management with test-connection feedback.
- Proxy configuration.
- User-dictionary management.
- The MCP automation-interface toggle with transport selection, token display, and rotation.
- Diagnostics management (open, inspect, and purge local logs and crash artifacts).
- UI language selection.

## The status bar

A persistent bottom status bar in the main hub and report windows shows live local telemetry, refreshed every 2 seconds:

| Field | Meaning |
|---|---|
| Backend working set | Backend process working-set RAM |
| Managed heap | Managed heap size under the server-GC profile |
| GC generation counts | GC generation collection counts |
| WAL size | SQLite write-ahead log file size (embedded provider) |
| Frontend JS heap | Measured locally in the renderer; never sent to the backend |
| Crawl throughput | Appended while an audit is running |

Advisory color thresholds are locally configurable:

- Working-set advisory default: **2 GiB**.
- WAL advisory default: **256 MiB**.

Advisories are informational only — they never trigger automatic destructive actions.

### Compact / Trim Working Set

The status bar (and Spotlight) offers a manual **Compact / Trim Working Set** action. It performs:

- Managed GC compaction.
- A SQLite WAL checkpoint (truncate mode, embedded provider).
- An OS working-set trim where the platform supports it.

Behavior:

- Returns before/after snapshots and the actions performed.
- Rate-limited (at least 10 seconds between invocations).
- Never interrupts an in-flight audit or transaction.
- Logged locally.
- Typical completion under five seconds.

> **Tip:** If the status bar shows a large WAL after a heavy audit, run Compact / Trim to checkpoint it and reclaim working set.

## Export handoff

Whatever you have selected or filtered in an explorer flows into the export workflow:

- Export the current filtered slice or selection rather than the whole dataset.
- Choose any supported format (workbook, CSV, HTML bundle, PDF summary).
- Optionally deliver to a destination you have configured and authorized.

See [Exports and secure destinations](exports-and-secure-destinations.md) for formats, the export job workflow, and optional encrypted delivery destinations.

## Related guides

- [Link graphs and treemaps](link-graphs-and-treemaps.md) — navigate the internal-link network, crawl-depth map, and section treemaps.
- [Exports and secure destinations](exports-and-secure-destinations.md) — generate spreadsheet, CSV, HTML, and PDF exports and configure encrypted delivery.
- [N-grams and topical density](../05-advanced-seo-and-intelligence-modules/n-grams-and-topical-density.md) — the N-Gram explorer's TF/DF analysis, cannibalization, and topical gaps.

[← Documentation portal](../README.md)