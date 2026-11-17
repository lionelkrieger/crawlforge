# Accessibility & Core Web Vitals Performance

> **CrawlForge modular architecture — area of concern: `04-audit-and-analysis-engine`.**
> Every source section below is reproduced **verbatim and unabridged** from the immutable master documents. No summarization, paraphrase, or omission has been applied.

| Source document | Version | Sections reproduced in this file |
|---|---|---|
| FS-CRAWL-001 — `baseline/functional-specification.md` | v1.4 | §7.10, §7.11 |

**Notation.** Cross-references inside the reproduced text retain the source convention: a bare `§n` refers to **FS-CRAWL-001**; `FS-CRAWL-002 §n` refers to the extension specification. The [README traceability matrix](../README.md) maps every source section to its file(s) in this hierarchy.

---

> **Verbatim source — FS-CRAWL-001 v1.4: 7.10 Accessibility auditing (optional category) — verbatim and unabridged**

### 7.10 Accessibility auditing (optional category)
> Capabilities: CAP-D1-100, CAP-D1-101, CAP-D1-102, CAP-D1-103, CAP-D1-104, CAP-D1-105, CAP-D1-107, CAP-D1-108, CAP-D1-109, CAP-D1-110, CAP-D1-X01

- An automated accessibility rule engine evaluates each page's document tree against a bundled, versioned rule set. When rendering mode is enabled the rendered tree is used; otherwise the parsed tree is used, and checks that require layout or computed style are marked as requiring rendering.
- Rule families include: image alternative text; ARIA attribute and role validity; form-label association; landmark and heading structure; link purpose; document language; and color-contrast signals.
- Each violation emits a finding with severity, the implicated element (a locator), and remediation guidance. Rules are individually enable-able and severity-mappable.
- Results aggregate into an accessibility report surface and the URL detail view, and contribute to audit scoring (section 8.1). The rule set ships as a bundled asset and updates only with product updates.

---

> **Verbatim source — FS-CRAWL-001 v1.4: 7.11 Rendered performance and Core Web Vitals (optional category) — verbatim and unabridged**

### 7.11 Rendered performance and Core Web Vitals (optional category)
> Capabilities: CAP-C-082, CAP-C-084, CAP-C-085, CAP-C-088, CAP-C-089, CAP-C-X02, CAP-C-X03, CAP-C-X04

- Requires rendering mode (section 5.6). For each rendered page the engine captures standard lab metrics — largest contentful paint, cumulative layout shift, total blocking time, first contentful paint, and time to first byte — using their publicly documented definitions.
- **Performance budgets:** the user defines per-metric thresholds; exceedance emits a finding. Budgets are stored with the audit configuration and applied per audit.
- **Code coverage:** the fraction of fetched stylesheet and script bytes actually used during render is measured; large unused payloads emit findings.
- **Page composition metrics:** total DOM nodes, maximum tree depth/width, image counts and sizes, and smallest rendered font size are captured for mobile and front-end heuristics.
- Per-request network timing already captured in the rendering network-waterfall summary (section 5.6) is reused here.
- Aggregations: metric distribution histograms, worst-performing pages, and budget pass-rate. Optional field-data enrichment from an external provider is an open scope decision (section 15) and is not required by this version.
