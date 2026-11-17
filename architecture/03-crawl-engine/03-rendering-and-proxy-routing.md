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
> Capabilities: CAP-A-004, CAP-A-015, CAP-A-X04, CAP-C-086

- A headless-browser mode renders JavaScript-heavy pages and captures the post-render DOM, a network-waterfall summary, and console messages (errors and warnings).
- Optional rendering behaviors: **infinite-scroll** expansion (scroll until content stabilizes or a bounded limit) and a **stealth** profile that reduces automation fingerprints. Device, viewport, and locale emulation are configurable per audit.
- Custom request headers are split so that credential-bearing headers are sent only to in-scope origins while non-credential headers (e.g. language preference) apply to all requests.
- When the local intercepting proxy is enabled (section 5.9), all rendered page traffic is routed through it, and the network waterfall is built from its capture.
- Rendered fetches obey the same limits and politeness rules; the mode is selectable per audit or per URL pattern.

---

> **Verbatim source — FS-CRAWL-001 v1.4: 5.9 Local proxy and traffic routing — verbatim and unabridged**

### 5.9 Local proxy and traffic routing
> Capabilities: CAP-A-024, CAP-A-X03

- **Upstream proxy (optional):** crawl requests may be routed through a user-configured upstream proxy — HTTP-connect or SOCKS class — set globally or per audit. Optional proxy credentials are supplied by the user and encrypted at rest (section 9.1). A per-host bypass list exempts chosen hosts from proxying. A per-audit option allows fallback to direct connection when the proxy is unreachable; proxy failures are otherwise classified like any other fetch failure.
- **Built-in local intercepting proxy (optional):** the backend can run a forward proxy bound to the **loopback interface only**. When enabled, the headless rendering engine routes all page traffic through it, providing:
  - request/response capture feeding the network-waterfall summary (section 5.6);
  - user-defined local resource-blocking rules (for example, suppressing selected third-party resource classes during rendering);
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
