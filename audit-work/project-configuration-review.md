# CrawlForge: Project Configuration Review

*Date: 17 November 2026. Discussion notes, no code changes proposed here.*

## 1. Why a project cannot be run from the UI today

The main hub had no way to start an audit after a project was created. A minimal **Start audit** button has since been added to each project row in `src/frontend/src/shell/HubDashboard.tsx`. It starts an audit from the project's first configuration. The deeper issue is that configurations themselves are invisible in the UI.

There are three separate gaps, and they need different kinds of work.

### 1.1 UI gap (front-end work)

- The backend exposes full configuration CRUD: `GET/POST /api/projects/{id}/configurations` and `GET/PUT/DELETE /api/configurations/{id}`.
- The frontend already has the route definitions (`contracts/api/routes.ts`) and types (`ConfigurationSummary`, `AuditConfiguration`, `ConfigurationCreateRequest`, `ConfigurationUpdateRequest` in `contracts/api/projects.ts`).
- No screen uses them. The only place a configuration is touched is the New project dialog, which silently creates a "Default configuration" with the start URL and `respectRobotsTxt: true`. That configuration is never shown again.
- Result: whatever the backend supports on a configuration cannot currently be viewed or changed by the user.

### 1.2 Spec gap (product decisions needed)

Several useful per-project settings are not in the functional specification at all, so there is nothing for a UI to expose yet. See section 3 for the full list. Examples: per-project issue thresholds, client context (expected GA4/GTM IDs, consent tool, brand variants), GSC/GA export import as crawl sources, form-based login, and explicit directive-handling toggles.

Special case: analysis-category toggles appear to exist but are resolved app-wide. In `AuditsController.StartAuditAsync`, `ResolveEnabledCategoriesAsync` takes no project or configuration input, so every project shares the same enabled categories. This should be revisited as a design choice.

### 1.3 Wiring question (unverified)

A field existing on the configuration contract does not prove the crawl engine honours it. The build was split into work packages by different agents, so some fields (for example query-parameter policy or rendering mode) may be stored on the configuration but never read by the engine. This has not been checked yet.

## 2. Recommended order

1. **Wiring audit (read-only).** For every configuration field, confirm: (a) the API accepts and persists it, (b) the crawl/audit engine actually reads and applies it. Produce a field-by-field table.
2. **Configuration editor.** Build the UI only for fields confirmed as working in step 1, so no switch is cosmetic.
3. **Spec additions.** Decide one by one which items from sections 3 and 5 belong in CrawlForge, update the spec, then implement. Section 5.7 gives a priority order for the site-type items.
4. **Rendering rebuild.** Implement the rendering decisions in section 4: four capture modes, direct CDP layer, pinned bundled browser. The wiring audit in step 1 should also record which rendering-related fields exist today so they map onto the new modes.

Suggested first sections for the configuration editor, in priority order:

1. Start URLs and scope
2. "Crawl as" preset (user agent, viewport, rendering)
3. Limits
4. Include/exclude patterns
5. Directive handling

Then analysis toggles and thresholds, then the client-context block.

## 3. Per-project configuration: lessons from LibreCrawl, Sitebulb and SiteOne

The functional spec (sections 4.2 and 5.1) already covers most crawl-behaviour inputs: seeds, scope, include/exclude, query-parameter policy, rendering, limits, headers and cookies, sitemaps, proxy, custom extraction, custom search, JS snippets, virtual robots, sample mode, signed requests and pre-crawl validation. What the other tools add is mostly the layer around the crawl: how a project describes the client, what gets analysed, what counts as an issue, and what comes out at the end.

### 3.1 Structure: three layers

Keep the existing model of **project → configurations → per-audit overrides**. Sitebulb keeps audit settings with the project so re-audits are repeatable, which is the right habit.

- Treat configurations as **named crawl profiles** within a project, for example "Full crawl", "Quick sample", "Mobile rendered", "URL list check".
- Add **app-level configuration templates** that are copied into new projects, so standard setups are not rebuilt per client.

### 3.2 Crawl sources

Sitebulb treats these as separate, combinable inputs: the website, XML sitemaps, an uploaded URL list, and URLs from Search Console and Analytics. CrawlForge has seeds, sitemaps and list mode.

- **Add:** import of GSC and GA4 page exports (CSV), consistent with the offline-first design. This enables orphan-page detection (pages with traffic that the crawl never reaches).

### 3.3 Directive handling as explicit toggles

The spec says robots.txt is obeyed. Make the rest explicit per configuration:

- Follow nofollow links (page-level and link-level, separately)
- Crawl noindexed pages
- Follow canonicals as discovery
- Use hreflang and rel next/prev as discovery
- Follow iframes and meta refresh

These change results significantly between clients.

### 3.4 Resource and external scope

SiteOne is strongest here: toggles for fetching JS, CSS, fonts, images and other files, plus separate allowed-domain lists for assets versus crawling. LibreCrawl exposes "follow external links" as a core crawler setting.

- **Add:** "check external link status without crawling further" toggle.
- **Add:** per-project list of asset domains (CDNs) treated as internal.
- **Add:** per-resource-type fetch toggles.

### 3.5 Authentication

Headers and cookies exist.

- **Add:** HTTP basic auth as a first-class field.
- **Later:** recorded form login executed in the rendering browser (Sitebulb has this). Staging sites behind login pages are common in agency work.

### 3.6 "Crawl as" presets

Bundle user agent, viewport and rendering mode into presets instead of separate fields: Googlebot Smartphone, Googlebot Desktop, standard mobile Chrome, standard desktop Chrome. SiteOne's desktop/mobile/tablet switch is the simple version of this.

### 3.7 Analysis modules

Sitebulb's "audit data" concept: choose per project whether to run accessibility, Core Web Vitals, structured data, spelling, readability, technology detection, GEO and llms.txt checks, and so on.

- **Change:** move category toggles from app-wide to per configuration (see 1.2).

### 3.8 Issue tuning

LibreCrawl supports URL patterns excluded from issue detection; Sitebulb lets users hide hints. CrawlForge has a suppressions API.

- **Add:** per-project thresholds: title and meta description lengths, thin-content word count, slow-response cut-off, readability target. An ecommerce client and a law firm should not share these.

### 3.9 URL rewriting

- Regex rewrite rules
- Lowercase and https normalisation
- Per-project parameter removal
- Host mapping, so a staging crawl can be compared against production

### 3.10 Client context (potential differentiator)

None of the three tools do this well, and it fits agency SEO and analytics work directly:

- Expected GA4 measurement IDs and GTM container IDs, so analytics coverage checks flag pages missing them
- Expected consent tool
- Brand name variants
- Target locale and language (drives spelling dictionary and expected hreflang)
- Client name and logo for report branding

### 3.11 Outputs

SiteOne can produce an offline website copy, a Markdown export and a generated sitemap. LibreCrawl lets users pick export formats and which fields to export.

- **Add per project:** default export field set, default export destination, baseline audit for comparisons.
- **Consider:** offline copy, Markdown export, sitemap generation as optional outputs.

### 3.12 Retention

- Per project: how many audits to keep, and whether to store full HTML or only extracted data. Large client sites will otherwise consume disk quickly.

## 4. Rendering and capture

### 4.1 Current state (as built)

- **Non-JS capture:** plain HTTP fetch, HTML parsed with AngleSharp. No browser involved.
- **JS capture:** headless Chromium-class browser driven through PuppeteerSharp (`CrawlForge.Crawler/Rendering/HeadlessRenderingPool.cs`). The Core Web Vitals lab probe (`CrawlForge.Audit/Performance/LabRenderingProbe.cs`) uses the same browser.
- **Browser resolution** (`LocalBrowserResolver.cs`): bundled assets, then a user-configured path, then a local install (Chrome first, Edge as fallback on Windows). Runtime download is prohibited (offline-first).
- **Gap:** no browser is bundled (`app/assets` and `packaging/vendor` contain none), and the host never passes a bundled or configured path to the resolver. In practice rendering uses whatever Chrome or Edge is installed locally. Consequences: results can drift silently when Chrome auto-updates, and rendering fails on machines without Chrome or Edge.

### 4.2 Decision: both JS and non-JS crawls are required

Both capture modes are first-class and must be available for every project and configuration. A configuration chooses one of:

1. **Non-JS only.** Plain HTTP fetch for all URLs. Fastest; shows what the server sends.
2. **JS only.** Every URL rendered in the browser. Slowest; shows the post-render DOM.
3. **Dual.** Every URL captured both ways, with raw-versus-rendered comparison (spec section 7.18) stored per URL. This is the full-fidelity audit mode.
4. **Adaptive.** HTTP for everything; render a sample per template or URL pattern; compare on SEO-relevant elements (links, title, meta, canonical, headings, word count, structured data); switch a pattern to rendered for the rest of the crawl only where rendering changes something material. Produces a "JavaScript-dependent templates" finding as a by-product.

Per-URL-pattern overrides (spec section 5.6) apply on top of any of these modes.

Reporting must show which mode produced each record, and comparison views must be available whenever both captures exist for a URL.

### 4.3 Decision: drive Chrome DevTools Protocol directly

Replace PuppeteerSharp with a direct Chrome DevTools Protocol (CDP) client in the backend, done now as part of getting rendering right, not deferred.

Rationale:

- Tightest control over network capture (request/response events, timing, headers, bodies) for the waterfall, WAF detection and analytics/consent checks.
- Can replace the local intercepting proxy route for rendered pages, removing a moving part.
- Precise control over wait conditions, resource blocking, cache sharing and context lifecycle.
- No dependency on a library's browser-management assumptions, which fits the offline-first rule.

Trade-off accepted: more development and maintenance work, since CDP domains change between Chrome versions. This is mitigated by pinning the browser version (4.4).

Scope to cover in the CDP layer:

- Browser process lifecycle and a small pool of long-lived browser processes.
- A fresh browser context per page (isolation without per-page launch cost).
- Network domain capture feeding the waterfall, headers, status, timing and WAF signals.
- Console and runtime exception capture.
- Device, viewport, user agent and locale emulation ("crawl as" presets, section 3.6).
- Wait rule: DOM stable and network quiet, with a hard cap. No fixed delays.
- Resource blocking as part of the crawl profile (fonts, media, third-party scripts). A tracking/consent profile blocks nothing so GA4, GTM and consent banners execute.
- A shared resource cache per audit so site JS/CSS bundles are fetched once.
- Infinite-scroll expansion and custom JS snippets (spec sections 5.6 and 7.17).
- The Core Web Vitals lab probe moved onto the same CDP layer.

### 4.4 Decision: bundle a pinned browser

- Bundle a pinned **Chrome for Testing** build (full build, run in headless mode) in packaging, and wire the host to pass the bundled path to the resolver. The resolver already probes `chrome-win64\chrome.exe` under the bundled-assets root.
- Prefer the full build over the lighter headless-shell variant: fidelity matters more for an audit tool, and throughput gains come mainly from adaptive rendering.
- Update the pinned version deliberately with CrawlForge releases, so rendered results are reproducible between audits and stay close to current Googlebot rendering.
- Keep a **browser path setting** in Settings and the local Chrome/Edge install as fallbacks.

### 4.5 Rejected options

- **Rendering through Electron's bundled Chromium.** Tempting because it already ships, but it couples the crawler to the UI process and differs enough from real Chrome to undermine trust in results.
- **Switching to Playwright.** Friendlier API, but brings its own browser-management assumptions that conflict with offline-first, and does not address render cost.

## 5. Site-type handling: server-rendered, ASP.NET and client-side apps

### 5.1 Method and caveat

The functional specification was searched for the behaviours each site type depends on (fragments, hash routing, postbacks, ViewState, cookie handling, crawler traps, soft 404, client-side redirects, service workers, history-API routing, consent, user-agent variation). Most returned no matches. Only the functional spec was searched; `crawlforge-extensions-and-ui-stack.md` and the build plan may cover some items (consent analytics exists in the code but not in the spec). Treat every gap below as **not in the spec** until the wiring audit (section 2, step 1) confirms what the code actually does.

### 5.2 Overall verdict

- **Server-rendered sites:** mostly covered. Three gaps.
- **ASP.NET WebForms:** not covered. Four specific traps unaddressed.
- **Client-side web apps:** content side covered by dual/adaptive rendering (section 4.2); discovery and routing have significant gaps, one of which (fragment discarding) actively breaks hash-routed apps today.
- **Consent banners:** unaddressed in rendered mode for all site types.

The rendering decisions in section 4 are sound, and the direct CDP layer (4.3) is what makes most of the client-side recommendations possible.

### 5.3 Server-rendered sites (WordPress, most CMS builds, ASP.NET Core Razor/MVC)

Covered: robots, sitemaps, redirect chains, canonicals, soft-404 detection, session query-parameter stripping.

| Gap | Recommendation |
|---|---|
| No cookie persistence during a crawl. Custom cookies can be supplied, but cookies the site sets are not specified as retained. Some sites redirect or vary content until a session cookie exists. | Add a per-audit **cookie jar**, on by default, with a configuration toggle to crawl cookieless. Scope cookies by host as a browser would. Record when a URL's response changed after a cookie was set. |
| No crawler-trap detection. Limits stop runaway crawls but do not explain them. Calendars, faceted navigation and sort/filter combinations create endless URL spaces. | Add **trap detection**: flag URL patterns whose count grows without new content (parameter-combination explosion, repeating path segments, incrementing date paths, near-duplicate content hashes). Throttle the pattern and raise a finding naming it. Valuable on ecommerce sites. |
| No user-agent comparison. Dynamic rendering, cloaking and accidental bot blocking go undetected. | Add an optional **UA comparison** check: fetch a sample (or all URLs) as Googlebot and as a standard browser; compare status, redirects, meta robots, canonical, content hash and links; raise findings on material differences. |

### 5.4 ASP.NET WebForms

Nothing ASP.NET-specific is in the spec. Older WebForms sites have well-known crawler traps.

| Gap | Recommendation |
|---|---|
| **Postback navigation.** Links using `javascript:__doPostBack(...)` are form POSTs and cannot be followed. | Detect postback links (and `WebForm_DoPostBackWithOptions`) during link discovery. Record them as **uncrawlable navigation** with source URL and control ID; raise a finding with a count per page and per template. Do not attempt to execute postbacks. One of the most useful findings for legacy .NET clients. |
| **ViewState weight.** The hidden `__VIEWSTATE` field can be hundreds of KB per page. | Measure `__VIEWSTATE` (and `__EVENTVALIDATION`) size per page; raise a performance finding above a configurable threshold (default suggestion: 100 KB). |
| **Cookieless sessions in the path,** e.g. `/(S(abc123))/page.aspx`. Every visit produces a new URL, causing a duplicate explosion. Current canonicalization only strips session *query* parameters. | Add path-segment normalization for ASP.NET cookieless session tokens (`(S(...))`, `(A(...))`, `(F(...))`) to canonicalization (spec section 5.2). Raise a finding when detected, since search engines index these URLs too. |
| **Session-dependent redirects.** | Covered by the cookie jar recommendation in 5.3. |

Also note: **Blazor Server** apps render over a live SignalR connection and behave like client-side apps; they need the section 5.5 handling and rendered mode.

### 5.5 Client-side web apps (React, Vue, Angular, Blazor Server)

Covered: content differences between raw and rendered HTML via dual and adaptive modes (section 4.2); infinite-scroll expansion (spec section 5.6).

| Gap | Recommendation |
|---|---|
| **Hash routing breaks today.** Canonicalization discards fragments (spec section 5.2), so apps routed through `#/products` or `#!/products` collapse into one URL. | Add a per-configuration **hash-route mode** that preserves fragments beginning with `#/` or `#!` as distinct routes in rendered mode. Always raise a finding when hash routing is detected, since Google does not index hash routes. |
| **Navigation without crawlable links.** Click handlers, buttons and router components without a real `href`. Google will not follow these; the crawl silently finds fewer pages. | In rendered mode, use CDP to inventory elements with click listeners or ARIA link/button roles that change the route without a crawlable `<a href>`. Record them and raise a **non-crawlable navigation** finding. Optional later: a bounded "click discovery" mode that triggers them to find routes, clearly labelled as beyond what Google does. |
| **Client-side redirects.** JavaScript redirects after load are not HTTP redirects, so redirect-chain logic never sees them. | Capture navigations in the CDP layer (frame navigation and history events) and record a distinct **client-side redirect** type in the redirect chain, with timing. Raise a finding, since these are weaker signals than HTTP redirects. |
| **Soft 404s.** Client-side apps often return 200 for everything, including "not found" screens. | Run soft-404 detection (spec section 7.12) on the **rendered** version whenever it exists, and give it priority over the raw version for client-side apps. |
| **Late meta changes.** Titles, canonicals and robots tags set by JavaScript after load. | In dual mode, treat a raw-versus-rendered difference in title, canonical, meta robots or hreflang as **high severity**. A rendered `noindex` that is absent from raw HTML (or the reverse) should be critical. |
| **Service workers and app-shell caching** can serve stale content in rendered mode. | Bypass service workers in the CDP layer for every render. Record whether a service worker is registered as an informational finding. |

### 5.6 Consent banners (all site types, rendered mode)

Not in the spec, although consent analytics exists in the code.

| Gap | Recommendation |
|---|---|
| Banners can cover content and hold back scripts until interaction, changing both content and tracking results. | Add a configuration option for **consent state** in rendered mode: no interaction, consent accepted, consent rejected. Support known consent platforms (including Agency Support Consent) by selector, with a custom-selector fallback. For tracking audits, allow capturing more than one state per URL and comparing which tags and requests fire in each. Flag tags that fire before consent. |

### 5.7 Additions to the recommended order

These items join step 3 (spec additions) in section 2, in this priority:

1. Hash-route handling and fragment exception (fixes an active breakage).
2. Cookie jar.
3. Postback and non-crawlable navigation detection (both ASP.NET and client-side).
4. Client-side redirect capture and rendered-first soft-404 (depend on the CDP layer).
5. Consent-state rendering.
6. Crawler-trap detection.
7. ASP.NET cookieless-session normalization and ViewState weight.
8. User-agent comparison.

Items 2 to 5 should be designed alongside the CDP layer (section 4.3), since they depend on it or shape its interfaces.

## 6. Notes on sources

- LibreCrawl settings were checked against its current README (settings categories: Crawler, Requests, JavaScript rendering, Filters, Export, Issue exclusion, Custom CSS).
- Sitebulb and SiteOne points are based on general knowledge of those tools and may not match their latest option names exactly.

Sources:
- [LibreCrawl on GitHub](https://github.com/phialsbasement/librecrawl)
