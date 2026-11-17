# Technology Fingerprint Catalogue

Bundled, versioned fingerprint catalogue for CrawlForge technology detection
(FS-CRAWL-001 §7.15, acceptance criterion #29). Consumed by
`CrawlForge.Audit.StructuredData.TechnologyFingerprintCatalogue` /
`TechnologyDetector`.

## Offline posture (mandatory)

- This asset is **bundled with the product** and loaded exclusively via local
  file read (`File.ReadAllText`). There is **no runtime fetch path**, no remote
  catalogue service, and no auto-update mechanism (clean-room exclusions E2/E4).
- The catalogue updates **only with product updates** (version/revision fields
  below are bumped by the maintainer, never at runtime).

## Provenance

- **Authorship:** curated in-house for CrawlForge (WP-03-05). No third-party
  fingerprint dataset was copied or imported; every signature was written from
  the publicly documented, observable output of each technology:
  - CMS/framework `generator` meta tags and asset paths as emitted by the
    platforms themselves (e.g. WordPress `wp-content`/`wp-includes`, Next.js
    `/_next/static/`, Nuxt `/_nuxt/`).
  - Vendor-documented response headers (e.g. Cloudflare `cf-ray`, Shopify
    `x-shopid`, Vercel `x-vercel-id`, Fastly `x-fastly-request-id`).
  - Vendor-documented cookie names (e.g. `PHPSESSID`, `laravel_session`,
    `__cf_bm`, `_shopify_*`).
  - Well-known script endpoints of analytics/marketing vendors as documented in
    their public installation guides (e.g. `googletagmanager.com/gtm.js`,
    `connect.facebook.net/.../fbevents.js`, `static.hotjar.com`).
- **License:** original work product of the CrawlForge build; no external
  dataset license applies. Technology names are nominative references to the
  detected products, not endorsements.

## Versioning

| Field | Value | Meaning |
|---|---|---|
| `version` | `2026.1` | Product-update cadence identifier. |
| `revision` | `r1` | Revision within the version; bumped on any signature change. |

## Schema

```jsonc
{
  "version": "2026.1",
  "revision": "r1",
  "technologies": [
    {
      "key": "stable-key",          // technology_detections.technology_key
      "name": "Human Name",
      "category": "CMS",            // CMS | Ecommerce | Framework | JavaScript Library |
                                    // Analytics | Tag Manager | Marketing | CDN |
                                    // Web Server | Language | Hosting
      "headers": [ { "name": "server", "pattern": "nginx", "weight": 0.9 } ],
      "cookies": [ { "name": "PHPSESSID", "isPrefix": false, "weight": 0.7 } ],
      "meta":    [ { "name": "generator", "pattern": "WordPress", "weight": 1.0 } ],
      "scripts": [ { "pattern": "/wp-(content|includes)/", "weight": 0.9 } ],
      "html":    [ { "pattern": "wp-content", "weight": 0.3 } ]
    }
  ]
}
```

- All patterns are case-insensitive regular expressions with a 100 ms match
  timeout (tolerant posture: a timeout counts as no match).
- `headers.name` / `meta.name` are case-insensitive exact names; `pattern`
  matches the value/content. `cookies.isPrefix: true` matches any cookie whose
  name starts with `name`.
- `html` patterns match the raw response body; `scripts` patterns match
  `script[src]` attribute values.

## Confidence rules

- Each matched signature contributes its `weight` once per technology.
- `confidence = min(1.0, Σ matched weights)` — reported per URL in
  `technology_detections.confidence`.
- Weight convention (author-assigned, documented here for maintainers):
  - `1.0` — definitive single marker (platform-emitted generator meta,
    unique vendor script endpoint).
  - `0.7–0.9` — strong marker (characteristic asset path, vendor header).
  - `0.4–0.6` — moderate marker (cookie name, secondary script, inline marker).
  - `0.2–0.3` — weak corroborating marker (generic HTML substring); never
    sufficient alone for high confidence.
- Detections are emitted for any confidence > 0, ordered by confidence
  descending then key; the UI explorer surfaces the confidence indicator.

## Maintenance

1. Add/adjust entries in `tech-catalogue.json`; keep keys stable (never
   repurpose an existing key — persisted detections reference it).
2. Bump `revision` (and `version` on product updates).
3. Run the WP-03-05 suite:
   `dotnet test app/tests/CrawlForge.Audit.Tests --filter FullyQualifiedName~StructuredData`.
