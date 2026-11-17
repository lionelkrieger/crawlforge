# Proxies and Authenticated Crawls

Some sites sit behind a corporate egress proxy. Others admit crawlers only with the right headers, cookies, or a cryptographic signature. CrawlForge supports both — with strict rules about where your credentials travel and how they are stored.

This guide covers upstream and local proxy routing, custom headers and cookies, signed-request access, the credential classes and their encrypted-at-rest handling, and the authorization boundaries that define what CrawlForge will never do with your secrets.

> **Note:** CrawlForge is local-first and air-gap capable. The backend binds to the loopback interface only and is never reachable from other machines. There is no telemetry, no remote crash reporting, no update checking, no account service, and no vendor-operated cloud service. Every credential you supply is a user-owned secret used solely for its respective outbound connection — nothing else.

## How crawl traffic is routed

Outbound traffic is limited to an explicit set of user-authorized paths:

- HTTP(S) requests to **crawl targets**, including their robots files, sitemaps, and page resources — optionally routed through a user-configured proxy.
- File writes for user-initiated exports (local filesystem).
- Transfers to **export destinations you explicitly configured**, only when you initiate them or pre-authorize them on a schedule. No destinations exist by default.
- Connections to a **user-configured external database server**, only when that provider is selected. None is configured by default.
- **User-triggered DNS bot verification** in the offline log analyzer — the single optional exception: DNS queries only, off by default, explicit per-import consent, never automatic, locally logged. With it disabled, the log module generates zero outbound traffic.

Your credentials ride only the connections they belong to: site credentials go to the crawl target, proxy credentials to the proxy, destination credentials to the destination, database credentials to the database server. Diagnostic artifacts (logs, minidumps) and metrics never leave the machine.

## Rendering mode and why routing matters

Proxy and credential behavior applies to both fetch modes, but rendering mode is where routing choices matter most:

- The optional headless-browser mode renders JavaScript-heavy pages and captures the **post-render DOM**, a **network-waterfall summary**, and **console messages** (errors and warnings).
- Optional rendering behaviors include **infinite-scroll expansion** (scroll until content stabilizes or a bounded limit) and a **stealth profile** that reduces automation fingerprints. Device, viewport, and locale emulation are configurable per audit.
- Rendering mode is selectable **per audit or per URL pattern**.
- Rendered fetches obey the **same limits and politeness rules** as plain fetches.
- When the local intercepting proxy is enabled, **all rendered page traffic** routes through it, and the network waterfall is built from its capture.

A rendered page loads scripts, styles, images, and beacons from many origins. The credential split (below) is what keeps your site credentials confined to in-scope origins while the rest of that traffic loads normally.

> **Warning:** The stealth profile is a user-configured rendering option, not a WAF-bypass tool. WAF and challenge detection is diagnostic, not adversarial — no CAPTCHA solving, challenge bypass, evasion automation, or credential abuse is specified or permitted.

## Upstream proxy routing

Crawl requests may be routed through a user-configured **upstream proxy** — an HTTP-connect or SOCKS class proxy you operate or trust.

### Configuration

| Field | Default | Bounds | Notes |
|---|---|---|---|
| Proxy class | None (direct connection) | HTTP-connect or SOCKS | User-configured; no remote auto-configuration or discovery channel exists |
| Scope of setting | — | Global or per audit | Set it once in settings, or override for a single audit |
| Proxy credentials | None | Optional, user-supplied | Encrypted at rest; used only for the proxy connection |
| Per-host bypass list | Empty | Host entries | Chosen hosts are exempted from proxying |
| Fallback to direct | Not specified | Per-audit option | When enabled, an unreachable proxy falls back to a direct connection for that audit |

All proxy configuration is local and user-supplied. There is no remote proxy auto-configuration (PAC) fetch and no discovery channel of any kind.

### Failure behavior

- With **fallback enabled** (per audit): an unreachable proxy falls back to a direct connection.
- With **fallback disabled**: proxy failures are classified like any other fetch failure — `proxy failure` is one of the permanent failure classes stored with the URL record (alongside name resolution, TLS, timeout, HTTP status, oversize, and robots-blocked).
- Transient failures elsewhere are retried within the retry budget using exponential backoff; the proxy setting itself is never silently changed mid-audit.

> **Tip:** Use the bypass list for hosts that must be reached directly — for example, an internal staging host that the corporate proxy cannot resolve. Keep fallback off for audits where egress *must* go through the proxy (compliance scenarios): a silent direct connection would defeat the purpose.

## The built-in local intercepting proxy

Separately from the upstream proxy, the backend can run a **forward proxy bound to the loopback interface only**. When enabled, the headless rendering engine routes all page traffic through it.

What it provides:

- **Request/response capture** feeding the network-waterfall summary of rendering mode.
- **User-defined local resource-blocking rules** — for example, suppressing selected third-party resource classes during rendering.
- **Deterministic local traffic routing** for advanced crawl scenarios.

What it never does:

- It is a **crawl-traffic tool, not a tunnel**: it never bridges local services to remote clients.
- It opens **no non-loopback listener**.
- It performs **no enrollment or liveness reporting**.

> **Note:** The two proxies compose: the local intercepting proxy captures and shapes rendered page traffic, and the upstream proxy (when configured) carries crawl requests onward to their targets. Rendered fetches obey the same limits and politeness rules as plain fetches regardless of routing.

### Choosing a routing setup

| Scenario | Upstream proxy | Local intercepting proxy |
|---|---|---|
| Direct audit of a public site | Off | Off (plain fetches need neither) |
| Corporate egress required | On, with credentials; bypass list for internal hosts; fallback off for compliance | Optional |
| Rendered audit with network-waterfall detail | As required by your network | On — the waterfall is built from its capture |
| Suppress third-party resource classes during rendering | As required | On, with local resource-blocking rules |
| Staging host the corporate proxy cannot resolve | On, with the staging host in the bypass list | Optional |

All configuration is local and user-supplied in every case; no remote auto-configuration or discovery channel exists.

## Custom headers and cookies

Custom request headers and cookies are an optional audit input: credentials for the target site, stored locally in the configuration. Sensitive optional values are encrypted at rest.

### The credential split

Custom request headers are split into two groups with different routing rules:

| Header group | Where it is sent |
|---|---|
| Credential-bearing headers | **In-scope origins only** |
| Non-credential headers (for example, a language preference) | All requests |

This split is what keeps your site credentials from leaking to third-party origins that appear in the crawl (external links are recorded, never fetched — but resource requests during rendering still respect the split).

> **Warning:** Supply credentials only for sites you are authorized to audit. CrawlForge's WAF and challenge detection is diagnostic, not adversarial: no CAPTCHA solving, challenge bypass, evasion automation, or credential abuse is specified or permitted. The optional stealth rendering profile is a user-configured rendering option, not a WAF-bypass tool.

### Storage and return behavior

- Headers/cookies live in the crawl configuration; sensitive values are encrypted at rest (see [Credential classes](#credential-classes-and-encrypted-at-rest-handling)).
- The settings surface guarantees defaults but **never returns secrets in clear** (`GET /api/settings`).
- The local automation interface exposes **no** credential, proxy, or settings management — secrets cannot be read or mutated through it.
- UI plugins cannot read backend credentials; the sandboxed plugin context has no access to them at all.

## Signed-request crawler access

Some targets admit automated clients only when requests carry a valid cryptographic signature. CrawlForge provides a **generic signed-access mechanism** — it is not tied to any particular platform.

### How it works

1. You supply the **signature material as custom request headers**, together with an **expiry**.
2. The engine **extracts the signature-bearing headers** from the general header set and retains the non-signature headers.
3. It **re-applies the signature headers to in-scope requests automatically** — you do not manage per-request signing yourself.
4. Signature material is a user-owned secret, **encrypted at rest**. It is never derived from hardware or OS identity, and it is never transmitted anywhere except the crawl target.

### Expiry and rejection handling

- The engine **tracks signature expiry** and **warns before and after it lapses**, so you can renew the material before a run degrades.
- **Signature-rejection responses are classified distinctly** from other failures, so you can tell "the signature expired/was rejected" apart from "the page is broken" and renew accordingly.

The full lifecycle in order:

1. Supply signature headers and expiry in the configuration (encrypted at rest).
2. The engine extracts the signature-bearing headers from the general header set; non-signature headers are retained as usual.
3. Signature headers are re-applied automatically to in-scope requests throughout the audit.
4. Expiry is tracked; warnings fire before and after lapse.
5. Rejected responses surface under the distinct signature-rejection classification.
6. Renew the material in the configuration; the next audit uses the renewed snapshot.

> **Tip:** Pair signed access with a sample crawl first. If the sample shows signature-rejection classifications, renew the material before committing to a full audit — the distinct classification makes this check cheap.

## Credential classes and encrypted-at-rest handling

Secret protection is mandatory and uniform across every credential class:

| Credential class | Used for | Where it lives |
|---|---|---|
| Site credentials (custom headers/cookies) | Authenticating to the crawl target | Crawl configuration |
| Signature material / signing secrets | Signed-request access | Crawl configuration (signature headers + expiry) |
| Proxy credentials | Authenticating to the upstream proxy | Proxy configuration (global or per audit) |
| Destination credentials | Export delivery to user-configured destinations | Destination record (encrypted credential payload) |
| External-database credentials | The user-configured external database provider | Application settings (connection settings, credentials encrypted) |
| Local tokens (session token, automation-interface token) | Local shell↔backend hardening and the local automation interface | Locally generated; never leave the machine |

The storage contract for all of them:

- **Encrypted at rest** using **app-generated entropy persisted locally**.
- Key material is **never derived from hardware or OS identity** — no machine fingerprint, no OS key store dependency, no persistent machine identity.
- Secrets are **never returned in clear** by the settings or destination surfaces.
- The optional per-launch **session token** is generated at spawn, shared between shell and backend, explicitly not derived from hardware or OS stores, and never leaves the machine. The shell-facing API requires no authentication headers; these local tokens are the only local credential concepts.

> **Note:** Because encryption entropy is app-generated and persisted locally (not bound to a machine identity), your encrypted data travels with your installation data — and nothing about your hardware is ever encoded into key material.

### The credential lifecycle

Every credential class follows the same lifecycle:

1. **Supply.** You enter the secret in the relevant configuration surface (crawl configuration, proxy configuration, destination record, or database settings). Nothing is seeded by default — no destination, no external database, no proxy exists until you create it.
2. **Store.** The value is encrypted at rest immediately, using app-generated entropy persisted locally. Key material is never derived from hardware or OS identity.
3. **Use.** The secret is applied only to its respective outbound connection: site credentials and signature material to in-scope crawl origins, proxy credentials to the proxy, destination credentials to the destination, database credentials to the database server.
4. **Read back.** Settings and destination surfaces never return secrets in clear. Connection-test actions report reachability and credential validity without echoing the secret.
5. **Rotate.** Update the configuration with new material. The change applies to subsequent audits; a running audit keeps the configuration snapshot it started with.
6. **Revoke.** Remove the value from the configuration. Subsequent audits run without it; the encrypted payload is gone from the active configuration.

> **Tip:** For signed access, rotation is routine rather than exceptional — signature material carries an expiry by design. Renew before the expiry warning fires, and use the distinct signature-rejection classification to confirm the new material is accepted.

## Authorization boundaries: what CrawlForge will never do

These are product invariants, not preferences:

- **Never send credentials anywhere except their intended connection.** Site credentials and signature material go only to in-scope crawl origins; proxy credentials only to the proxy; destination and database credentials only to their user-configured endpoints.
- **Never derive secrets or key material from hardware or OS identity.**
- **Never transmit diagnostics or metrics off the machine.** Logs, minidumps, and local metrics stay local.
- **Never phone home.** No telemetry, no remote crash reporting, no update checking, no account service, no vendor cloud, no enrollment, no heartbeat or liveness reporting, no remote credential issuance or rotation.
- **Never expose secrets through the automation interface or plugins.** The local automation interface manages no credentials, proxy, or settings; plugins cannot read backend credentials.
- **Never bypass access controls.** WAF/challenge detection is detection, classification, and reporting only. Remediation guidance is to verify crawler access permissions with the site owner, or adjust scope or supplied credentials — never to evade.
- **Never open a non-loopback listener.** The backend, the local intercepting proxy, and the automation transports are all loopback-only (or stdio for locally spawned clients).

If a crawl target blocks you with a challenge page, the correct next steps are: confirm you are authorized to crawl it, obtain proper access (credentials or signed material from the site owner), or narrow the scope. A WAF-blocked URL is recorded as `blocked` with sub-classification `waf-challenge`, excluded from content-quality passes, and surfaced as a diagnostic — it is never re-fetched or worked around.

## Local API and automation security

The surfaces that could touch your secrets are themselves bounded:

- **Loopback-only binding.** The backend binds to the loopback interface only and is never reachable from other machines. A configured default loopback port is attempted first; if occupied, the backend falls back to an OS-assigned ephemeral port, discovered by the shell through a local handshake.
- **No authentication headers for the shell API.** The shell-facing API requires none. The only local credential concepts are the optional per-launch session token and the automation-interface token — both local, both never leaving the machine.
- **Per-launch session token (optional hardening).** Generated at spawn, shared between shell and backend, explicitly not derived from hardware, OS stores, or any persistent machine identity.
- **Automation interface boundary.** The local automation interface exposes no destination, credential, proxy, or settings management. Secrets cannot be read or mutated through it; its transports (stdio for spawned clients, loopback SSE/streamable HTTP for resident clients) are strictly local with no remote bridging.
- **Plugin isolation.** UI plugins run in a sandboxed renderer context with no same-origin access to the host UI, no Node integration, no filesystem access, and no backend credentials. Plugins cannot read host storage, backend credentials, or other plugins' state.
- **Settings return behavior.** `GET /api/settings` guarantees defaults but never returns secrets in clear. Connection-test actions (for the external database provider and for export destinations) report reachability and credential validity without echoing the secrets back.

## Workflow: auditing an authenticated site

1. **Confirm authorization** with the site owner. Obtain the credentials or signature material they issue for automated access.
2. **Create the project and configuration** (see [Creating projects and scopes](creating-projects-and-scopes.md)); define the scope so credential-bearing headers reach only the origins you intend.
3. **Add the custom headers/cookies** to the configuration. Non-credential headers (language preferences, for example) may be added separately — they apply to all requests.
4. **For signed access**, supply the signature headers and their expiry. The engine handles extraction and in-scope re-application.
5. **Configure egress** if required: upstream proxy class, credentials, bypass list, and the per-audit fallback decision. Enable the local intercepting proxy only if you need rendered-traffic capture or resource blocking.
6. **Enable pre-crawl validation** and probe the start URLs. A login-gate or WAF-challenge blocker here means the credentials are missing, wrong, or out of scope — fix before crawling.
7. **Run a sample crawl.** Check for signature-rejection classifications and the WAF block rate; renew material or resolve access as needed.
8. **Run the full audit.** Watch expiry warnings (signed access) and the WAF-block diagnostic; both tell you when access degrades mid-run.
9. **Rotate or revoke** by updating the configuration. Secrets are stored encrypted; removing them removes them from subsequent audits. Running audits keep their configuration snapshot.

## Troubleshooting authenticated and proxied crawls

| Symptom | What it means | What to do |
|---|---|---|
| URLs classified `proxy failure` | The upstream proxy was unreachable or refused the connection, and fallback is off | Check proxy reachability and credentials; enable per-audit fallback only if direct egress is acceptable |
| Audit fell back to direct unexpectedly | Per-audit fallback is enabled and the proxy was unreachable | Disable fallback for compliance-sensitive audits |
| TLS blocker on the start URL | Certificate or handshake problem detected by pre-crawl validation | Resolve the certificate issue; verify whether the proxy terminates TLS differently |
| Login-gate blocker | The probe landed on authentication instead of content | Add the site's headers/cookies or signed access; confirm the scope covers the authenticated origin |
| Signature-rejection classifications | The target rejected the signature material | Renew the material; the distinct classification separates this from ordinary fetch failures |
| Expiry warning before a run | Signature material is about to lapse | Renew before starting; the engine warns before and after lapse |
| WAF-challenge blocks | The target fingerprinted a challenge page | Verify crawler access with the site owner; adjust scope or credentials — never bypass |
| Credentials not reaching a resource origin | The credential split confines credential-bearing headers to in-scope origins | Expected behavior; only non-credential headers apply to all requests |

## Related guides

- [Creating projects and scopes](creating-projects-and-scopes.md)
- [List Mode and the migration matrix](list-mode-and-migration-matrix.md)
- [Crawl limits and AIMD politeness](../03-running-and-scheduling-audits/crawl-limits-and-aimd-politeness.md)
- [WAF and soft-block detection](../05-advanced-seo-and-intelligence-modules/waf-and-soft-block-detection.md)
- [Exports and secure destinations](../04-exploring-reports-and-visualizations/exports-and-secure-destinations.md)
- [Local MCP server setup](../06-automation-and-ai-agents/local-mcp-server-setup.md)

[← Documentation portal](../README.md)
