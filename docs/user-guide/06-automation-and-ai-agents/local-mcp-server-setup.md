# Local MCP Server Setup

CrawlForge includes an **optional local Model Context Protocol (MCP) server** that lets local AI agents and automation clients start and control audits, read audit data, and compose agent-ready briefings — without any cloud service in the loop. The interface is **off by default**, enabled in settings, and reachable only through strictly local transports. Enabling it adds **no outbound traffic**.

This guide covers enabling the server, choosing a transport, token authentication and rotation, configuring an MCP client step by step, and the security boundaries that always apply. For the full catalog of tools, resources, and prompts — plus end-to-end agent workflows — see [Agent tools and workflows](agent-tools-and-workflows.md).

> **Note:** CrawlForge is local-first and offline-first. There is zero telemetry, no account or license service, and no update checking. The MCP server works fully on an air-gapped machine; it never phones home, enrolls, or reports liveness to anyone.

## What the local MCP server is

The local automation interface implements a **standardized MCP server**, conforming to the published protocol's capability negotiation, tool invocation, resource reading, and prompt semantics. It is a protocol-conformant surface, not an ad-hoc remote-procedure layer.

Key properties:

- **Semantic projection of the local API.** Every tool maps one-to-one onto the corresponding local API operation with identical semantics. The same validation, limits resolution, audit lifecycle rules, and global concurrency caps apply to agent-invoked actions as to actions you take in the UI.
- **No management surface.** The MCP interface exposes **no** settings, destination, credential, proxy, or database-provider management. Agents consume data and control audit lifecycle; they never reconfigure the product.
- **Optional and local-only.** The interface is off by default and enabled in settings. When enabled, it is reachable only through the local transports described below; it is never bridged, relayed, or exposed remotely.
- **No elevated capability.** Agent-invoked actions grant no extra privileges — everything remains subject to local validation, limits resolution, and the global concurrency cap.

### The surface at a glance

| Surface | Count | Purpose |
|---|---|---|
| Tools | 8 | Audit lifecycle control and progress reads |
| Resources | 15 | Read-only data views (7 baseline + 8 analysis) |
| Prompts | 5 | Parameterized briefing templates (3 baseline + 2 optional) |

The complete catalog, with exact inputs, outputs, and filter fields, is in [Agent tools and workflows](agent-tools-and-workflows.md).

## Where the server lives and how the endpoint is exposed

The MCP server runs inside the **local backend service** — the same self-contained process that hosts the crawl engine, audit engine, REST API, and real-time hub. The desktop shell spawns and supervises this backend; the MCP surface is available while the application is running.

```
Desktop shell (window host)
        │  HTTP (JSON) + WebSocket hub — loopback only
Local backend service
  crawl engine · audit engine · scheduler · local MCP server · hub
        │  stdio / loopback SSE (optional, off by default)
Local AI / automation clients (user-owned)
```

### Loopback-only binding

- The backend binds to the **loopback interface only**. It is never reachable from other machines.
- Both MCP transports are strictly local. The enabled interface is unreachable from non-loopback addresses.
- There is **no remote or cloud bridging, enrollment, heartbeat, or remote credential issuance** of any kind.

### Port discovery

The backend's loopback HTTP listener — which the SSE / streamable HTTP transport uses — follows the product's port strategy:

1. A configured default loopback port is attempted first.
2. If that port is occupied, the backend falls back to an **operating-system-assigned ephemeral port**.
3. The shell discovers the actual port through a local handshake: a port descriptor written to the per-user application data directory, or an equivalent spawn-time handshake.

No literal port number is guaranteed by the product. Plan for the actual port to differ between launches, and re-check the endpoint your client points at after a restart.

> **Warning:** The MCP interface provides programmatic control **while the application is running**. Unattended, headless command-line execution without opening any window is not part of this version — do not build automation that assumes CrawlForge can be driven with no running session.

## Transports

The interface offers two local transports. Select the transport in settings alongside the interface toggle.

| Transport | Binding | Client class | Authentication |
|---|---|---|---|
| stdio | Local process pipes | An agent spawns the packaged **stdio adapter** as a child process; the adapter bridges protocol messages to the backend's loopback API | Local process-spawn trust; the local token may optionally be required |
| SSE / streamable HTTP | Loopback HTTP endpoint | Resident local agents connecting over the loopback interface | Local token (required) |

### stdio

With stdio, your MCP client launches the **packaged stdio adapter** as a child process and speaks the protocol over the child's standard input/output pipes. The adapter is a thin, packaged local bridge:

- It holds **no domain logic** — all work happens in the backend.
- It opens **no non-loopback sockets**.
- It **terminates when its parent client or the backend exits**.

Authentication rests on local process-spawn trust: only a process you launch locally can attach. You may optionally require the local token for stdio as an extra hardening step.

### SSE / streamable HTTP

With SSE / streamable HTTP, a resident local agent connects to the backend's **loopback HTTP endpoint** and authenticates with the **local token, which is required** on this transport. This suits long-lived local agents that stay connected while CrawlForge runs, rather than being spawned per task.

### Choosing a transport

- Choose **stdio** when your agent framework launches MCP servers as child processes per session. The adapter's lifecycle is tied to the client and backend automatically.
- Choose **SSE / streamable HTTP** when a resident local agent connects over loopback and can present the local token.

> **Tip:** Both transports expose the identical tool, resource, and prompt surface with identical semantics. The choice is about how your client attaches locally — never about capability.

### Settings controls at a glance

| Control | What it does |
|---|---|
| MCP automation-interface toggle | Enables or disables the whole surface; disabling closes all listeners and terminates any running stdio adapter |
| Transport selection | Chooses stdio or SSE / streamable HTTP |
| Token display | Shows the locally generated random token |
| Token rotation | Generates a new token; existing clients are invalidated immediately |

## Enabling the server

The MCP automation-interface toggle lives in **settings**, together with transport selection, token display, and rotation.

1. Open **Settings** in CrawlForge.
2. Locate the **MCP automation-interface** toggle. On a fresh installation the interface is disabled.
3. Enable the interface.
4. Select the transport you want to use: **stdio** or **SSE / streamable HTTP**.
5. Use the **token display** to view the locally generated token, and copy it if your chosen transport requires it (always required for SSE / streamable HTTP; optional for stdio).
6. Configure your MCP client as described in [Configuring an MCP client](#configuring-an-mcp-client).
7. Verify the connection as described in [Verifying the connection](#verifying-the-connection).

> **Note:** Enabling the interface adds no outbound traffic. All listeners remain loopback-only, and the token never leaves the machine.

## Token authentication

Authentication uses a **locally generated random token**:

- The token is **viewable and rotatable by you in settings**.
- It is **stored encrypted at rest**, using app-generated entropy persisted locally. Key material is never derived from hardware or OS identity.
- It **never leaves the machine**.
- **Invalid tokens are rejected.** The enabled interface completes the standard protocol handshake only for authenticated clients on the token-required transport.
- **Rotation invalidates existing clients immediately.**

There is no enrollment, heartbeat, liveness reporting, or remote credential issuance. Revocation is entirely local: rotate the token, or disable the interface.

### How this token differs from other local credentials

CrawlForge has exactly two local credential concepts, and they are separate:

| Credential | Purpose | Scope |
|---|---|---|
| Automation-interface token | Authenticates MCP clients on the token-required transport; optionally required for stdio | The optional local MCP interface |
| Per-launch session token | Optional local hardening: a random token that may be generated at spawn and shared between shell and backend | Shell ↔ backend communication only |

The shell-facing REST API requires **no authentication headers**. The per-launch session token is explicitly not derived from hardware, OS stores, or any persistent machine identity, and it never leaves the machine. Neither token is a license, account, or identity credential — no such concepts exist in the product.

### Rotating the token

Rotate the token whenever you want to revoke access for previously configured clients — for example after decommissioning an agent, or as routine hygiene.

1. Open **Settings** and locate the MCP automation-interface section.
2. Trigger **token rotation**. A new locally generated random token replaces the old one.
3. Existing clients are **invalidated immediately** — their next request with the old token is rejected.
4. Update each client you want to keep using with the new token (see the client configuration steps below).
5. Reconnect the clients and verify the handshake.

> **Warning:** Rotation takes effect immediately; there is no grace period. Any agent mid-workflow with the old token will be rejected until you update its configuration.

## Configuring an MCP client

Any MCP-capable local client can attach. The configuration fields below are generic — map them to your client's own MCP server configuration mechanism. CrawlForge does not require a specific third-party client.

### Before you configure

1. Confirm CrawlForge is **running** and the MCP interface is **enabled** in settings.
2. Confirm the **transport selection** in settings matches the transport you are about to configure.
3. Have the **local token** available from the token display in settings if your transport requires it.

### Configuring a stdio client

1. In your client's MCP server configuration, register a new server entry using the **stdio** transport type.
2. Set the launch command to the **packaged stdio adapter** that ships with CrawlForge. The adapter is a packaged asset of the installation; point the command at that executable. Do not substitute a custom bridge — the packaged adapter is the supported local bridge.
3. If you choose to require the local token for stdio, supply the token through your client's credential/secret configuration for that server entry, using the same token shown in CrawlForge settings.
4. Save the configuration and let the client **spawn the adapter as a child process**.
5. The client performs the standard MCP handshake; the server advertises its **tools, resources, and prompts**.
6. Confirm the connection by checking that the advertised surface appears in your client, and that invocations show up in the local automation action log.

> **Note:** The adapter terminates automatically when your client exits or when the CrawlForge backend exits. If the backend restarts, your client must spawn a fresh adapter — this is expected behavior, not a fault.

### Configuring an SSE / streamable HTTP client

1. In your client's MCP server configuration, register a new server entry using the **SSE / streamable HTTP** transport type.
2. Set the endpoint URL to the **loopback HTTP endpoint of the running CrawlForge backend**. Remember the port strategy: the configured default loopback port is attempted first, with an ephemeral fallback if occupied — so confirm the actual port of the current session rather than assuming a fixed number.
3. Configure the client to present the **local token** with its requests, using the client's authentication/credential field for MCP servers. The token is **required** on this transport; copy it from the token display in settings.
4. Save the configuration and connect. The client performs the standard MCP handshake over loopback; the server advertises its tools, resources, and prompts.
5. Verify the connection and check the automation action log for the handshake and subsequent invocations.

> **Tip:** Keep the token inside your local client configuration only. It never needs to leave the machine, and it must never be sent to any remote service.

> **Note:** After a CrawlForge restart, re-check the loopback endpoint before reconnecting a resident SSE client: the backend may have fallen back to a different ephemeral port.

## Security boundaries

The MCP interface is deliberately bounded. These boundaries hold regardless of which client attaches or what it requests:

- **Loopback-only.** All listeners bind to the loopback interface. The interface is unreachable from non-loopback addresses and is never bridged, relayed, or exposed remotely.
- **No remote control plane.** No installation enrollment, no cloud relay, no heartbeat or liveness reporting, no remote credential issuance or rotation, no remote revocation channel.
- **No elevated capability.** Agent actions pass through the same validation, limits resolution, lifecycle rules, and global concurrency cap as UI actions. Invalid requests are rejected with protocol error objects mirroring the API error envelope.
- **No management access.** No settings, destination, credential, proxy, or database-provider management is exposed — and no signature, stop-word, plugin, log-import, embedding-provider, migration-matrix, GEO-tool, or analytics-configuration management either.
- **Outbound traffic stays user-authorized.** Enabling MCP adds no outbound traffic. The product's only outbound traffic remains: requests to crawl targets you configured (optionally through your proxy), transfers to export destinations you explicitly configured and initiated or pre-authorized, and connections to an external database server you configured. None of it consults any vendor service.
- **Everything stays local and transparent.** Every invocation is recorded in the local automation action log, which is user-viewable and purgeable. Diagnostics, metrics, and the action log never leave the machine.
- **Local revocation.** Rotate the token to invalidate clients immediately, or disable the interface to shut the surface down entirely.

### Disabling the server

1. Open **Settings** and turn the MCP automation-interface toggle **off**.
2. The interface **closes all of its listeners** and **terminates any running stdio adapter**.
3. Connected clients lose access immediately; no listener remains open.

> **Tip:** Disabling the interface is the cleanest way to pause agent access without touching client configurations. Rotation and disabling are the product's only revocation mechanisms — both are local and immediate.

## Verifying the connection

After setup, confirm the following behaviors — they are the product's acceptance-level guarantees for the interface:

1. With the interface enabled, the **standard protocol handshake completes** over the transport you configured (the product validates both loopback SSE and stdio).
2. The server **advertises its tools, resources, and prompts** to the client.
3. **Invalid tokens are rejected** on the token-required transport.
4. The interface is **unreachable from non-loopback addresses**.
5. **Token rotation invalidates prior clients** immediately.
6. Every invocation appears in the **automation action log** with its timestamp, transport, tool/resource name, argument summary, and outcome.

### If something does not connect

- **Token rejected:** confirm the client presents the current token from settings. If you rotated recently, the old token is invalid immediately — update the client.
- **SSE connection refused after a restart:** the backend may be on a different ephemeral port. Re-check the running session's loopback endpoint and update the client URL.
- **stdio adapter exits unexpectedly:** the adapter terminates when its parent client or the backend exits. Restart CrawlForge if needed, then let the client spawn a fresh adapter.
- **No surface advertised:** confirm the interface toggle is enabled and the transport selection matches your client configuration.

### What happens on restart and shutdown

- **Graceful shutdown:** the backend checkpoints any running crawl, drains workers and background services, closes the database, and exits. Any running stdio adapter terminates when the backend exits.
- **Backend crash and restart:** the shell restarts the backend with bounded backoff. Audits that were running are marked *interrupted* and can be resumed from their checkpoints — an agent can recover them with `resume-audit`. The loopback port may differ after a restart because of the ephemeral-port fallback.
- **Client exit:** a stdio adapter terminates when its parent client exits; an SSE client simply disconnects. Neither case affects running audits.

## Setup checklist

- Interface enabled in settings; transport selected.
- Token copied from the token display (required for SSE / streamable HTTP; optional for stdio).
- Client configured: the packaged stdio adapter as a child process, or the loopback SSE endpoint plus the token.
- Standard handshake completes; tools, resources, and prompts are advertised.
- A first invocation is visible in the automation action log.
- You know your revocation path: rotate the token, or disable the interface.

## Related guides

- [Agent tools and workflows](agent-tools-and-workflows.md) — the full tool, resource, and prompt catalog, the delegated flow, and practical SEO agent workflows.
- [Lifecycle and checkpointing](../03-running-and-scheduling-audits/lifecycle-and-checkpointing.md) — the audit states and checkpoint behavior that agent lifecycle tools control.
- [Crawl limits and AIMD politeness](../03-running-and-scheduling-audits/crawl-limits-and-aimd-politeness.md) — the limits resolution and politeness rules that apply unchanged to agent-invoked audits.
- [Logs, minidumps, and metrics](../07-diagnostics-and-troubleshooting/logs-minidumps-and-metrics.md) — local diagnostics, including the user-viewable, purgeable automation action log.

[← Documentation portal](../README.md)
