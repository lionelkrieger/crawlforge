# Content Scoping and Boilerplate Stripping

Word counts, readability scores, spelling checks, and near-duplicate detection
are only as good as the text they measure. On a typical site, every page repeats
the same navigation, mega-menu, cookie banner, sidebar, newsletter signup, and
footer — boilerplate that inflates word counts, dilutes readability, and makes
distinct pages look like duplicates.

**Content scoping** tells CrawlForge which regions of a page are *content*.
You define ordered **Include** and **Exclude** selector lists (CSS or XPath);
CrawlForge derives a **Scoped Visible Text Tree** from each parsed document and
runs every text-sensitive pass over that scoped view. Titles, links, images,
structured data, and the heading outline are never affected — scoping changes
**text metrics only**.

> **Note:** Nothing is stripped automatically. With no scope configured,
> CrawlForge uses the full visible text (baseline behavior). Applying the
> bundled boilerplate preset is an explicit action you take.

---

## What scoping changes — and what it never touches

| Pass | Operates on scoped text when a scope is configured |
|---|---|
| Visible word count / text statistics | Yes |
| Flesch-class readability, grade level, reading time, text-to-HTML ratio | Yes |
| Spelling checks | Yes |
| Shingle near-duplicate detection | Yes |
| Custom content search — visible-text target | Yes |
| N-gram analysis — `body` and `headings` zones | Yes |

**Unchanged by scoping:** document-head facts (title, meta description,
canonical, alternates), the full heading outline structure, link discovery,
anchors, images, structured data, resources, and tables.

Two important details:

- The **text-to-HTML ratio** keeps the **full markup size as denominator**
  (scoped visible-text length relative to the full document markup). Removing
  boilerplate therefore *raises* the ratio, as intended.
- **Raw-markup search targets** in custom content search are never scoped.
  Custom **extraction** rules also continue to run against the full parsed
  document — scoping and extraction are orthogonal.

---

## The configuration model

### Two levels, one priority order

| Level | Where | Behavior |
|---|---|---|
| Project default | Project settings | Reusable scope profiles applied to audits of the project |
| Audit override | Audit configuration | Overrides the project default for that audit |

Merge rules:

1. An **audit-level scope overrides** the project-level default.
2. An absent audit scope **inherits** the project default.
3. No configured scope anywhere → **baseline behavior** (full visible text).

### What a scope consists of

- An ordered **Include list** of selectors.
- An ordered **Exclude list** of selectors.
- A per-entry **selector language**: CSS selector or XPath, declared per
  entry. Both languages may be mixed within one scope.

### Evaluation semantics (deterministic)

1. Include list **empty** → the whole document body is the candidate region.
2. Include list **non-empty** → the candidate region is the union of subtrees
   matched by the include selectors, in document order.
3. Exclude selectors are applied **after** inclusion, subtracting matched
   subtrees from the candidate region. **Exclude wins** on any conflict.
4. Nested matches are deduplicated: an ancestor match subsumes its
   descendants — text is never counted twice.
5. The result preserves document order.

> **Tip:** Start with an empty Include list (whole body) and a solid Exclude
> list. Add Include selectors only when the content lives in a well-defined
> container (for example `article` or `main`) and you want everything outside
> it gone.

---

## The bundled boilerplate preset

CrawlForge ships a **named, read-only preset** of common boilerplate selectors.
Applying it is an explicit user action (a preset chip in the scope editor);
the preset itself cannot be edited, but you can extend the resulting lists
with your own entries.

| Preset selector | Targets |
|---|---|
| `nav` | Navigation blocks |
| `header` | Page headers |
| `footer` | Page footers |
| `aside` | Sidebars / complementary regions |
| `.cookie-banner` | Cookie consent banners |
| `.mega-menu` | Mega-menu navigation |
| `[role=navigation]` | ARIA navigation landmarks |
| `[role=banner]` | ARIA banner landmarks |
| `[role=contentinfo]` | ARIA contentinfo (footer) landmarks |
| `.sidebar` | Sidebar containers |
| `.newsletter-signup` | Newsletter signup blocks |
| `.social-share` | Social sharing widgets |

---

## Step-by-step: build your first scope

Work through these steps in order; each one builds on the previous.

### Step 1 — Open the scope editor

1. Open **project settings** (for a reusable default) or the **audit
   configuration** (for a one-off override).
2. Locate the **content scope** editor: two lists (Include / Exclude), each
   entry with a CSS/XPath language toggle, drag handles for reordering, preset
   chips, and inline syntax validation.

### Step 2 — Apply the boilerplate preset

1. Click the bundled **boilerplate preset chip**. Its selectors populate the
   Exclude list.
2. Review the entries; remove any that would strip real content on your site
   (for example, if your articles legitimately live inside `<aside>`).

### Step 3 — Add site-specific selectors

1. Add entries for your template's boilerplate: cookie overlays, related-posts
   widgets, comment forms, breadcrumb bars, promo strips.
2. Choose the language per entry: CSS for class/attribute selectors, XPath
   when you need axis-based conditions.
3. Invalid syntax is rejected **at configuration time** with a descriptive
   local error — fix entries before saving.

### Step 4 — (Optional) define the content region

1. If content lives in a known container, add it to the **Include** list
   (for example `main`, `article`, or `[role=main]`).
2. Remember: include first, exclude after — **exclude wins** on overlap, and
   nested matches never double-count.

### Step 5 — Preview against a sample URL

1. Use the **live preview** against a sample URL before the crawl starts.
2. Inspect the matched-region summary, word counts before/after, sample
   scoped text, and **per-selector match counts** (see the next section).
3. Adjust selectors until the preview shows your content — and only your
   content.

### Step 6 — Save and verify

1. Save the scope (as a project profile or the audit override).
2. Start the audit. The **audit header** displays the active scope state —
   for example `Scoped: 2 include / 7 exclude` — so reports are never silently
   misread.
3. After the run, check the URL explorer's **scoped vs full word-count
   columns** and the scope-diagnostics panel in URL detail.

> **Tip:** Point-and-click capture: the **Finder selector picker** can send a
> captured selector directly into the Include or Exclude list — useful when
> you can see the boilerplate but don't know its selector.

---

## Match-count preview

The scope preview is a stateless tool (also embedded in the single-page
analyzer) that evaluates a scope against a target URL **or a stored artifact
reference** — no crawl required.

**Inputs:** target URL (or stored artifact), Include list, Exclude list.

**Outputs:**

| Output | What it tells you |
|---|---|
| Matched-region summary | Which subtrees survived inclusion/exclusion |
| Word counts before/after | Full vs scoped visible word count |
| Sample scoped text | The actual text the passes will see |
| Per-selector match counts | How many regions each individual selector matched |

Per-selector match counts are the tuning instrument: a selector with **zero**
matches on representative pages is dead weight; a selector matching
unexpectedly many regions may be eating content.

> **Note:** Previewing against a stored artifact works offline — the same
> bodies the audit uses, with no network access.

---

## How scoping cleans your metrics

- **Word counts.** The visible word count now measures content only. The
  inventory keeps both numbers — full and scoped — plus the removed count, so
  the effect of scoping is always auditable.
- **Readability, grade level, reading time.** Flesch-class formulas run over
  scoped text: boilerplate sentence fragments and menu labels no longer skew
  the score.
- **Spelling checks.** Menu labels, legal footers, and cookie-banner copy no
  longer produce spelling noise on every page.
- **Near-duplicate detection.** Shingle fingerprints are computed over scoped
  text, so pages that differ only in boilerplate are no longer flagged as
  near-duplicates — and genuinely similar content stands out.
- **Custom content search (visible-text target).** Matches are evaluated
  against scoped visible text.
- **N-grams.** The `body` and `headings` zones use the scoped tree; `title`
  and `inbound-anchor` zones are unaffected (see the n-gram guide).

Text-based findings note the **scoped basis in their evidence**, so every
judgment is reproducible.

---

## The Scoped Visible Text Tree

The scoped tree is a **derived view, not a persisted artifact**:

- It is produced in the audit pipeline **after DOM parsing and before
  text-sensitive inventory and rule passes**.
- In **rendering mode** it is applied to the **post-render** tree, using
  computed visibility when available.
- Visibility extraction follows standard semantics: `script`, `style`,
  `noscript`, and `template` content is excluded; elements hidden by standard
  markup/attribute semantics are excluded.
- On **re-audit** it is re-derived from stored bodies — no network access,
  no re-fetching.

---

## Diagnostics and reporting

### Per-URL scope diagnostics

The element inventory gains a `content_scope` payload per URL:

| Field | Meaning |
|---|---|
| Include-match count | Regions matched by Include selectors |
| Exclude-match count | Regions subtracted by Exclude selectors |
| Full word count | Visible words before scoping |
| Scoped word count | Visible words after scoping |
| Removed word count | Words removed by scoping |

### Surfaces

- **URL explorer:** scoped and full word-count columns; a scope-diagnostics
  panel in URL detail (matched regions, removed counts).
- **Audit header:** active scope state (`Scoped: N include / M exclude`).
- **Content comparison:** each audit uses its own snapshotted scope; the
  comparison surface labels which scope produced each side.
- **Dashboard metric:** `scoped-word-count` distribution.
- **MCP:** the `url-detail` resource carries the `content_scope` diagnostics
  payload.

---

## Worked example: evaluating a scope

Consider a page with this structure:

```text
body
├── header.site-header        (menu, logo)
├── nav.breadcrumbs           (breadcrumb trail)
├── main
│   ├── article.post          (the actual content)
│   │   └── aside.related     (related-posts widget inside the article)
│   └── div.newsletter-signup
└── footer.site-footer        (legal links, copyright)
```

Scope: Include `article.post`; Exclude `aside.related`, `.newsletter-signup`,
`header`, `footer`, `nav`.

Evaluation, step by step:

1. Include list is non-empty → the candidate region is the `article.post`
   subtree (rule 2). The header, breadcrumbs, newsletter block, and footer
   are outside the candidate region already.
2. Exclude selectors subtract matched subtrees **inside** the candidate
   region: `aside.related` is removed from the article (rule 3 — exclude wins
   even though the article was included).
3. No nested double-counting: if you also excluded a child of
   `aside.related`, the ancestor match subsumes it (rule 4).
4. The remaining article text, in document order, is the scoped visible text
   (rule 5).

The URL's diagnostics would show the include-match count (1 region), the
exclude-match count (regions actually subtracted), and full / scoped / removed
word counts — so you can verify the widget was stripped and the content was
kept.

> **Tip:** The whole-body candidate region applies **only when the Include
> list is empty** (rule 1). A **non-empty** Include list that matches nothing
> on some template yields an **empty** candidate region (rule 2) — there is no
> whole-body fallback. Preview across your main templates to catch template
> drift before a full run.

---

## Scope profiles and storage

Reusable project-level scopes are stored as **scope profiles**
(`content_scope_profiles`):

| Field | Meaning |
|---|---|
| Profile name | Human-readable identifier for the editor |
| Include selectors | Ordered entries: language, expression, order |
| Exclude selectors | Ordered entries: language, expression, order |
| Preset reference | Nullable — which bundled preset seeded the profile |
| Created/updated timestamps | Configuration history |

Profiles are **configuration data** — they are never pruned by audit
retention. Audit configurations reference a profile or carry an inline scope
override; the **resolved** scope is snapshotted into the audit itself.

---

## Re-audit, snapshots, and bounds

- The **resolved content scope is snapshotted into every audit**. Later edits
  to profiles or defaults never mutate history.
- **Re-audit** re-derives scoped trees and all dependent metrics from the
  content store under the audit's snapshotted scope — no re-fetching.
- **Changing the scope and re-auditing** recomputes findings; the deltas
  surface through audit comparison.
- Selector syntax is validated at configuration time; invalid selectors are
  rejected with a descriptive local error.
- Selector evaluation is **time-bounded per page**. Evaluation failures are
  isolated, logged as rule-level diagnostics, and **never fail the audit**.

> **Warning:** An **empty Include list** uses the whole document body as the
> candidate region (rule 1). A **non-empty Include list that matches nothing**
> on a page produces an **empty candidate region** (rule 2) — no whole-body
> fallback exists. Always preview against representative templates before a
> full run.

---

## REST quick reference

| Operation | Method & path |
|---|---|
| Scope preview (URL or stored artifact; include/exclude lists) | POST `/api/tools/scope-preview` |
| Create configuration with scope | POST `/api/projects/{id}/configurations` (additive `contentScope` field) |
| Update configuration scope | PUT `/api/configurations/{id}` |
| Project-level scope defaults / profile references | PUT `/api/projects/{id}` |
| Single-page analysis with scope diagnostics | POST `/api/analysis/single-page` |

Reusable project-level scopes are stored as `content_scope_profiles`
(configuration data — never pruned by audit retention).

---

## Related guides

- [WAF and soft-block detection](waf-and-soft-block-detection.md)
- [N-grams and topical density](n-grams-and-topical-density.md)
- [Semantic internal linking](semantic-internal-linking.md)
- [Dashboards and virtualized explorers](../04-exploring-reports-and-visualizations/dashboards-and-virtualized-explorers.md)
- [Exports and secure destinations](../04-exploring-reports-and-visualizations/exports-and-secure-destinations.md)

[← Documentation portal](../README.md)
