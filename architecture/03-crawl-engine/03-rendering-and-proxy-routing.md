# Rendering & Proxy Routing

> **CrawlForge modular architecture — area of concern: `03-crawl-engine`.**
> Every source section below is reproduced **verbatim and unabridged** from the immutable master documents. No summarization, paraphrase, or omission has been applied.

| Source document | Version | Sections reproduced in this file |
|---|---|---|
| FS-CRAWL-001 — `baseline/functional-specification.md` | v1.4 | §5.6, §5.9, §5.10 |

**Notation.** Cross-references inside the reproduced text retain the source convention: a bare `§n` refers to **FS-CRAWL-001**; `FS-CRAWL-002 §n` refers to the extension specification. The [README traceability matrix](../README.md) maps every source section to its file(s) in this hierarchy.

---

> **Verbatim source — FS-CRAWL-001 v1.4: 5.6 Rendering mode (optional) — verbatim and unabridged**

### 5.6 Rendering mode (optional)
> Capabilities: CAP-A-004, CAP-A-015, CAP-A-X04, CAP-C-086 · Decisions: D-002 · Change records: CR-006

- A headless-browser mode renders JavaScript-heavy pages and captures the post-render DOM, a network-waterfall summary, and console messages (errors and warnings).
- Optional rendering behaviors: **infinite-scroll** expansion (scroll until content stabilizes or a bounded limit) and a **stealth** profile that reduces automation fingerprints. Device, viewport, and locale emulation are configurable per audit.
- Custom request headers are split so that credential-bearing headers are sent only to in-scope origins while non-credential headers (e.g. language preference) apply to all requests.
- Rendered page traffic is captured directly by the rendering control layer (section 5.6.2); the network waterfall is built from that capture. The local intercepting proxy (section 5.9) is not used for rendered traffic. **[Amended by CR-006, D-002]**
- Rendered fetches obey the same limits and politeness rules; the mode is selectable per audit or per URL pattern.

### 5.6.1 Capture modes
> Capabilities: CAP-A-X09 · Decisions: D-001 · Change records: CR-005

**[Added by CR-005, D-001]** Both non-JS and JS capture are first-class. A configuration selects exactly one capture mode:

| Mode | Behaviour |
|---|---|
| Non-JS | Every in-scope URL is fetched over plain HTTP and parsed without executing scripts. Default for new configurations. |
| JS | Every in-scope URL is rendered in the headless browser (section 5.6); the post-render document is the analysed document. |
| Dual | Every in-scope URL is captured both ways. Both documents are stored and the response-vs-rendered comparison (section 7.18) is computed for every URL. Above a configurable storage threshold the user is warned; there is no hard cap. |
| Adaptive | Every in-scope URL is fetched over plain HTTP. URLs are grouped by URL path pattern (for example `/blog/*`, `/product/*`) until template identification is available; a sample per group (configurable, default 3 to 5 URLs) is also rendered and compared. If rendering materially changes the sample, the group switches to rendered capture for the rest of the audit. Each switch is recorded with its evidence and reported as a "JavaScript-dependent template" finding. |

- **Material change** (adaptive mode): any difference in title, canonical, meta robots or hreflang; a different set of links; or a visible word-count change above 20% (threshold configurable).
- **Manual override:** in the configuration the user may set the capture for any URL pattern explicitly (rendered or non-rendered); explicit settings take precedence over adaptive switching. Per-URL-pattern overrides (section 5.6) apply on top of every mode.
- Every URL record states which capture produced it (raw, rendered, or both). Report surfaces and exports show the capture source per record; comparison views are available whenever both captures exist for a URL.
- Custom extraction (section 7.9) and custom search (section 7.16) can target the raw document, the rendered document, or both, when both exist.
- The default for new configurations is reviewed once adaptive mode has passed acceptance on the fixture site (`07-quality-and-verification`).

### 5.6.2 Rendering control layer
> Capabilities: CAP-A-X10 · Decisions: D-002 · Change records: CR-006

**[Added by CR-006, D-002]**

- The headless browser is controlled directly through the **Chrome DevTools Protocol**. No third-party browser-automation library sits between the engine and the browser.
- **Process model:** a bounded pool of long-lived browser processes, sized by default to half the logical processors (minimum 2) and configurable, within the limits resolver (section 4) and the global concurrency cap. Each page is rendered in a **fresh browser context** (isolated cookies, storage and cache, except the shared resource cache below).
- **Network capture:** request and response events (URL, method, status, headers, timing, size, initiator) feed the network-waterfall summary (section 5.6), WAF evaluation (FS-CRAWL-002 §2) and analytics/consent inspection (FS-CRAWL-002 §14.2).
- **Console messages and runtime exceptions** are captured per page.
- **Emulation:** device, viewport, user agent and locale are applied per configuration.
- **Wait rule:** a page is complete when the DOM and network have been quiet for 500 ms, subject to a hard cap of 20 seconds; both values are configurable. Fixed delays are not used.
- **Resource blocking** is a configuration profile. The default profile blocks nothing, so Core Web Vitals and tag checks are not skewed; an opt-in "fast" profile blocks selected resource classes (for example fonts and media).
- **Shared resource cache:** static resources are cached for the duration of one audit so each site bundle is fetched once.
- Infinite-scroll expansion, the stealth profile, custom JavaScript snippets (section 7.17) and the Core Web Vitals lab probe (section 7.11) run on this layer.
- In full audits the engine supplies the rendered document to the audit pipeline whenever the capture mode calls for rendering; rendering is not limited to single-page analysis.

### 5.6.3 Browser provisioning
> Capabilities: CAP-A-X11 · Decisions: D-003 · Change records: CR-007

**[Added by CR-007, D-003, D-009]**

- The product bundles a **pinned Chrome for Testing build** (full build, run headless) inside the installer as its default rendering browser. The pinned version is updated with each product release and at least quarterly, and is recorded in the release notes.
- Browser resolution order: (1) bundled browser; (2) a user-configured executable path set in Settings; (3) a locally installed Chrome, then Edge. Runtime download of any browser or driver is prohibited (offline-first).
- Every audit and every rendered URL record records the browser product and version used. Audit comparison flags a browser-version change between the compared audits.
- If no browser resolves, rendering-dependent capture modes fail at pre-crawl validation with a clear local error; non-JS capture remains available.
- Redistribution terms of the bundled browser are confirmed by the licence audit (`09-distribution`) before public release.

---

> **Verbatim source — FS-CRAWL-001 v1.4: 5.9 Local proxy and traffic routing — verbatim and unabridged**

### 5.9 Local proxy and traffic routing
> Capabilities: CAP-A-024, CAP-A-X03 · Decisions: D-002 · Change records: CR-006

- **Upstream proxy (optional):** crawl requests may be routed through a user-configured upstream proxy — HTTP-connect or SOCKS class — set globally or per audit. Optional proxy credentials are supplied by the user and encrypted at rest (section 9.1). A per-host bypass list exempts chosen hosts from proxying. A per-audit option allows fallback to direct connection when the proxy is unreachable; proxy failures are otherwise classified like any other fetch failure.
- **Built-in local intercepting proxy (optional):** the backend can run a forward proxy bound to the **loopback interface only**. When enabled, plain-HTTP (non-rendered) crawl traffic is routed through it, providing: **[Amended by CR-006, D-002]**
  - request/response capture feeding the network-waterfall summary (section 5.6);
  - user-defined local resource-blocking rules for non-rendered traffic (resource blocking during rendering is a configuration profile of the rendering control layer, section 5.6.2); **[Amended by CR-006, D-002]**
  - deterministic local traffic routing for advanced crawl scenarios.
- The local proxy is a **crawl-traffic tool, not a tunnel**: it never bridges local services to remote clients, opens no non-loopback listener, and performs no enrollment or liveness reporting (section 3, row 4).
- All proxy configuration is local and user-supplied; no remote proxy auto-configuration or discovery channel exists.

---

> **Verbatim source — FS-CRAWL-001 v1.4: 5.10 Signed-request crawler access (optional) — verbatim and unabridged**

### 5.10 Signed-request crawler access (optional)

- Some targets admit automated clients only when requests carry a valid cryptographic signature. The user may supply signature material (as custom request headers, section 5.1) together with an expiry; the engine extracts the signature-bearing headers from the general header set, retains the non-signature headers, and re-applies the signature headers to **in-scope** requests automatically.
- The engine tracks signature expiry, warns before and after it lapses, and classifies signature-rejection responses distinctly so the user can renew the material.
- Signature material is a user-owned secret, encrypted at rest (section 9.1); it is never derived from hardware or OS identity and never transmitted anywhere except the crawl target. This is a generic signed-access mechanism, not tied to any particular platform.

---

---

## Amendment history

### CR-006 (2026-11-17; D-002)

Replaced text in §5.6 (intercepting-proxy bullet):

> - When the local intercepting proxy is enabled (section 5.9), all rendered page traffic is routed through it, and the network waterfall is built from its capture.
### CR-006 (2026-11-17; D-002)

Replaced text in §5.9 (built-in proxy bullet):

> - **Built-in local intercepting proxy (optional):** the backend can run a forward proxy bound to the **loopback interface only**. When enabled, the headless rendering engine routes all page traffic through it, providing:
### CR-006 (2026-11-17; D-002)

Replaced text in §5.9 (resource-blocking sub-bullet):

>   - user-defined local resource-blocking rules (for example, suppressing selected third-party resource classes during rendering);
