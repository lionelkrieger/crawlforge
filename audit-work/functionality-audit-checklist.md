# CrawlForge: Functionality Audit Checklist

*Feature inventory for auditing what CrawlForge implements. Status values: Implemented, Partial, Backend only (no UI), Spec only, Missing, Not applicable. Evidence: file path, route, screen or test.*

## A. Crawl engine

| # | Feature | Status | Evidence / notes |
|---|---|---|---|
| 1 | HTTP and HTTPS crawling |  |  |
| 2 | HTML link discovery |  |  |
| 3 | Internal and external link analysis |  |  |
| 4 | JavaScript-rendered crawling (Chromium) |  |  |
| 5 | Configurable user agents, including Googlebot simulation |  |  |
| 6 | robots.txt handling |  |  |
| 7 | Meta robots handling |  |  |
| 8 | XML sitemap discovery and crawling |  |  |
| 9 | Canonical URL detection |  |  |
| 10 | Redirect chain detection |  |  |
| 11 | Redirect loop detection |  |  |
| 12 | Query parameter handling |  |  |
| 13 | URL normalisation |  |  |
| 14 | Pagination handling |  |  |
| 15 | Infinite-scroll handling |  |  |
| 16 | Crawl depth limit |  |  |
| 17 | URL count limit |  |  |
| 18 | Crawl time limit |  |  |
| 19 | Configurable concurrency |  |  |
| 20 | Configurable request rate |  |  |
| 21 | Authentication support |  |  |
| 22 | Cookie support |  |  |
| 23 | Custom request headers |  |  |
| 24 | Proxy support |  |  |
| 25 | Asset discovery: CSS |  |  |
| 26 | Asset discovery: JS |  |  |
| 27 | Asset discovery: images |  |  |
| 28 | Asset discovery: fonts |  |  |
| 29 | Asset discovery: PDFs and other documents |  |  |
| 30 | Crawl inclusion rules |  |  |
| 31 | Crawl exclusion rules |  |  |
| 32 | Crawl pause |  |  |
| 33 | Crawl resume |  |  |
| 34 | Interruption recovery |  |  |
| 35 | Original HTML response vs rendered DOM comparison (what the server delivered and what JavaScript changed) |  |  |

## B. Technical SEO analysis

| # | Feature | Status | Evidence / notes |
|---|---|---|---|
| 36 | Indexability: noindex |  |  |
| 37 | Indexability: robots restrictions |  |  |
| 38 | Indexability: canonical conflicts |  |  |
| 39 | Metadata: missing titles |  |  |
| 40 | Metadata: duplicate titles |  |  |
| 41 | Metadata: missing descriptions |  |  |
| 42 | Metadata: duplicate descriptions |  |  |
| 43 | Headings: H1/H2 hierarchy |  |  |
| 44 | Headings: missing headings |  |  |
| 45 | Headings: duplicated headings |  |  |
| 46 | Links: broken internal links |  |  |
| 47 | Links: broken external links |  |  |
| 48 | Links: orphan pages |  |  |
| 49 | Redirects: 3xx chains |  |  |
| 50 | Redirects: loops |  |  |
| 51 | Redirects: inappropriate redirects |  |  |
| 52 | Sitemaps: missing URLs |  |  |
| 53 | Sitemaps: non-indexable URLs listed |  |  |
| 54 | Sitemaps: redirecting URLs listed |  |  |
| 55 | Images: missing alt text |  |  |
| 56 | Images: oversized images |  |  |
| 57 | Images: format checks |  |  |
| 58 | International: hreflang validation |  |  |
| 59 | International: language declarations |  |  |
| 60 | International: reciprocal hreflang references |  |  |
| 61 | Content: thin content |  |  |
| 62 | Content: duplicate content |  |  |
| 63 | Content: near-duplicate content |  |  |
| 64 | URL structure: parameters |  |  |
| 65 | URL structure: inconsistent trailing slashes |  |  |
| 66 | URL structure: duplicate URLs |  |  |
| 67 | Structured data: Schema.org validation |  |  |
| 68 | Structured data: Google rich-result eligibility |  |  |

## B2. Structured data engine

| # | Feature | Status | Evidence / notes |
|---|---|---|---|
| 69 | Extract JSON-LD |  |  |
| 70 | Extract Microdata |  |  |
| 71 | Extract RDFa |  |  |
| 72 | Parse multiple entities per page |  |  |
| 73 | Identify entity types and relationships |  |  |
| 74 | Validate properties against Schema.org |  |  |
| 75 | Identify missing required properties |  |  |
| 76 | Identify missing recommended properties |  |  |
| 77 | Detect conflicting entity information |  |  |
| 78 | Compare structured data against visible page content |  |  |
| 79 | Identify inconsistent organisation identity across a domain |  |  |
| 80 | Group errors by template and entity type |  |  |

## C. Performance analysis

| # | Feature | Status | Evidence / notes |
|---|---|---|---|
| 81 | Lighthouse integration |  |  |
| 82 | Core Web Vitals: LCP |  |  |
| 83 | Core Web Vitals: INP |  |  |
| 84 | Core Web Vitals: CLS |  |  |
| 85 | Time to First Byte |  |  |
| 86 | Resource loading waterfalls |  |  |
| 87 | Large image detection |  |  |
| 88 | Unused CSS |  |  |
| 89 | Unused JavaScript |  |  |
| 90 | Render-blocking resources |  |  |
| 91 | Compression checks |  |  |
| 92 | Caching checks |  |  |
| 93 | Mobile viewport checks |  |  |
| 94 | Responsive checks |  |  |
| 95 | Page weight |  |  |
| 96 | Resource counts |  |  |
| 97 | Slow server response detection |  |  |
| 98 | Performance results at URL level |  |  |
| 99 | Performance results at template level |  |  |

## D1. Accessibility

| # | Feature | Status | Evidence / notes |
|---|---|---|---|
| 100 | WCAG 2.2 AA checks (where automatable) |  |  |
| 101 | Missing image alternatives |  |  |
| 102 | Form labels |  |  |
| 103 | Colour contrast |  |  |
| 104 | Heading structure |  |  |
| 105 | Landmark structure |  |  |
| 106 | Keyboard accessibility |  |  |
| 107 | Accessible names |  |  |
| 108 | Link purpose |  |  |
| 109 | ARIA misuse |  |  |
| 110 | Distinguish machine-detectable violations from issues needing human assessment |  |  |

## D2. Security

| # | Feature | Status | Evidence / notes |
|---|---|---|---|
| 111 | HTTPS usage |  |  |
| 112 | Certificate validity |  |  |
| 113 | HTTP security headers |  |  |
| 114 | Mixed content |  |  |
| 115 | Cookie security attributes |  |  |
| 116 | Insecure external resources |  |  |
| 117 | Information disclosure |  |  |
| 118 | Basic TLS configuration checks |  |  |

## E. Information architecture and site understanding

| # | Feature | Status | Evidence / notes |
|---|---|---|---|
| 119 | Internal link graph |  |  |
| 120 | Click depth |  |  |
| 121 | Crawl depth |  |  |
| 122 | Orphan page identification |  |  |
| 123 | Inlink counts |  |  |
| 124 | Outlink counts |  |  |
| 125 | Anchor-text analysis |  |  |
| 126 | Navigation analysis |  |  |
| 127 | Breadcrumb analysis |  |  |
| 128 | Page template identification |  |  |
| 129 | Content clustering |  |  |
| 130 | URL taxonomy |  |  |
| 131 | Topic and entity relationships |  |  |
| 132 | Internal PageRank / link equity modelling |  |  |
| 133 | Important pages with insufficient internal links |  |  |
| 134 | Pages receiving internal links from irrelevant sections |  |  |
| 135 | Navigation inconsistencies |  |  |
| 136 | Pages with no route from the main navigation |  |  |
| 137 | Primary content sections of the site |  |  |
| 138 | Site relationship graph (pages, links, entities, templates) with visual navigation |  |  |

## F. Content intelligence

| # | Feature | Status | Evidence / notes |
|---|---|---|---|
| 139 | Duplicate content detection |  |  |
| 140 | Near-duplicate content detection |  |  |
| 141 | Word count |  |  |
| 142 | Text density |  |  |
| 143 | N-gram analysis |  |  |
| 144 | Keyword and phrase extraction |  |  |
| 145 | Heading-to-content consistency |  |  |
| 146 | Topic clustering |  |  |
| 147 | Content freshness indicators |  |  |
| 148 | Thin content detection |  |  |
| 149 | Missing content coverage |  |  |
| 150 | Title and body-content alignment |  |  |
| 151 | Entity extraction |  |  |
| 152 | Content-template comparison |  |  |

## G. Content extraction

| # | Feature | Status | Evidence / notes |
|---|---|---|---|
| 153 | Custom extraction: CSS selectors |  |  |
| 154 | Custom extraction: XPath |  |  |
| 155 | Custom extraction: regex |  |  |
| 156 | Extraction output types: inner text, inner HTML, whole element, specific attribute |  |  |
| 157 | Extraction source selectable: raw HTML, rendered DOM, or both |  |  |
| 158 | Multiple matches per page per rule |  |  |
| 159 | Test extraction rule against a single URL before crawling |  |  |
| 160 | Custom search: pages containing a text or regex |  |  |
| 161 | Custom search: pages not containing a text or regex |  |  |
| 162 | Main content extraction (boilerplate removal of navigation, header, footer) |  |  |
| 163 | Analysis on main content (word count, duplicates, n-grams use extracted main content) |  |  |
| 164 | Full page text export (plain text) |  |  |
| 165 | Full page content export (Markdown) |  |  |
| 166 | Extracted values usable as filters |  |  |
| 167 | Extracted values included in exports |  |  |

## H. Reporting, data and integrations

| # | Feature | Status | Evidence / notes |
|---|---|---|---|
| 168 | Interactive dashboard |  |  |
| 169 | Searchable URL inventory |  |  |
| 170 | Advanced filtering |  |  |
| 171 | Segmentation |  |  |
| 172 | Custom issue rules |  |  |
| 173 | CSV export |  |  |
| 174 | XLSX export |  |  |
| 175 | JSON export |  |  |
| 176 | XML export |  |  |
| 177 | PDF reports |  |  |
| 178 | Crawl comparison |  |  |
| 179 | Historical audit storage |  |  |
| 180 | Issue trend over time (resolved, remaining, newly introduced) |  |  |
| 181 | Google Search Console integration |  |  |
| 182 | Google Analytics 4 integration |  |  |
| 183 | PageSpeed Insights integration |  |  |
| 184 | API access |  |  |
| 185 | MCP server access |  |  |
| 186 | Structured JSON output for other systems and agents |  |  |
| 187 | Webhooks |  |  |
| 188 | Scheduled crawls |  |  |
| 189 | Email reports |  |  |
| 190 | Multi-project management |  |  |
| 191 | Multi-user access and permissions |  |  |

## I. Findings and prioritisation

| # | Feature | Status | Evidence / notes |
|---|---|---|---|
| 192 | Prioritise findings by severity |  |  |
| 193 | Prioritise by number of affected URLs |  |  |
| 194 | Prioritise by business importance |  |  |
| 195 | Prioritise by confidence |  |  |
| 196 | Prioritise by remediation effort |  |  |
| 197 | Template-level issue grouping (shared problems across a template) |  |  |
| 198 | Root-cause grouping (underlying causes rather than individual warnings) |  |  |
| 199 | Change detection between crawls: structural changes |  |  |
| 200 | Change detection between crawls: newly introduced problems |  |  |
| 201 | Change detection between crawls: disappearing content |  |  |
| 202 | Change detection between crawls: deteriorating technical health |  |  |

## J. AI-assisted analysis

| # | Feature | Status | Evidence / notes |
|---|---|---|---|
| 203 | Website overview (structure, purpose, content, technical architecture) |  |  |
| 204 | Plain-language explanation of individual findings |  |  |
| 205 | Template-level pattern detection |  |  |
| 206 | Impact assessment (commercial, technical, search visibility) |  |  |
| 207 | Recommendations for interventions |  |  |
| 208 | Evidence references: every recommendation links to affected URLs and underlying data |  |  |
| 209 | Custom investigations: operator questions about the crawl |  |  |
| 210 | Change analysis between crawls |  |  |
| 211 | Developer implementation briefs |  |  |
| 212 | Machine-readable findings export |  |  |
| 213 | Constraint: AI only interprets collected crawl data; never invents findings |  |  |
