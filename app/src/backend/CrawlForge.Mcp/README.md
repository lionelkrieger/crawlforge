# CrawlForge.Mcp — local MCP server (WP-05-04)

Implements FS-CRAWL-001 §11 (complete tool/resource/prompt surface) and
Appendix D additive prompts over the locked `IMcpToolSurface` /
`IMcpResourceSurface` / `IMcpPromptSurface` contracts. Local-only by
construction: loopback listeners, encrypted local tokens, no remote/cloud
bridge, no settings/credential management tools (exclusion E7, criterion #16).

## Protocol decision (lead-approved)

Hand-rolled **JSON-RPC 2.0** over the locked surfaces with **zero new .NET
packages**. The official `ModelContextProtocol` NuGet package (2.4.0 stable)
was evaluated and rejected: it would require an amendment-weight dependency
and cannot express the custom loopback-only binding + local-token rotation
posture directly. Protocol conformance is instead proven by an interop suite
that runs the **official TypeScript SDK client** (`@modelcontextprotocol/sdk`,
npm registry) against this server on both transports — handshake, all 8
tools, all 15 resources, all 5 prompts, invalid-token rejection, and rotation
invalidation (`McpInteropTests` + `src/mcp-stdio-adapter/test/interop.test.ts`).

- Supported MCP protocol versions: `2025-06-18` (latest), `2025-03-26`,
  `2024-11-05`; the server echoes a supported client version or counters with
  the latest.
- Server identity: `crawlforge` / `1.0.0`. Capabilities: tools, resources,
  prompts (all `listChanged: false` — surfaces are static per build).
- Tool execution errors (including invalid arguments) are returned inside the
  MCP result envelope with `isError: true`; protocol errors use JSON-RPC error
  codes (`-32700`, `-32600`, `-32601`, `-32602`, `-32603`).

## Transports

### 1. Loopback streamable HTTP + SSE (`McpLoopbackServer`)

- `HttpListener` bound **only** to `IPAddress.Loopback` / `IPAddress.IPv6Loopback`;
  any other bind address throws before a socket opens. Non-loopback remote
  endpoints are rejected with 403 as defense in depth.
- `POST /mcp` — JSON-RPC requests. `Authorization: Bearer <token>` required
  (401 + `WWW-Authenticate` otherwise). The initialize response assigns
  `Mcp-Session-Id`; subsequent requests must carry it (400 otherwise).
  Notifications return 202. `DELETE /mcp` terminates the session (204).
- `GET /mcp` — SSE keep-alive channel (`text/event-stream`). Server-initiated
  streams are not used (`listChanged: false`); the channel exists for SDK
  compatibility.
- `X-MCP-Transport: stdio` header (local process-spawn trust) maps adapter
  traffic to `AutomationTransportKind.Stdio` in the action log; direct HTTP
  clients log as `Sse`/`Http` per the server's transport argument.

### 2. Packaged stdio adapter (`src/mcp-stdio-adapter`)

Thin TypeScript child-process bridge using the official SDK on both sides
(`StdioServerTransport` facing the agent, `StreamableHTTPClientTransport`
facing the backend). No domain logic. Configuration via
`--endpoint/--token` or `CF_MCP_ENDPOINT`/`CF_MCP_TOKEN`. Loopback-only
endpoints are enforced before any socket opens (`src/loopback.ts`, no DNS).
The adapter terminates when its parent closes stdin or the backend exits.

Build: `npm run build --workspace src/mcp-stdio-adapter` (emits `dist/`).
Entry: `dist/index.js` (`bin: crawlforge-mcp-stdio`).

## Surface inventory

**8 lifecycle tools** (`McpToolSurface`): `start-audit`, `pause-audit`,
`resume-audit`, `cancel-audit`, `re-audit`, `analyze-single-page`,
`run-schedule-now`, `get-audit-progress`. No configuration, settings,
credential, or key-management tools exist.

**15 resources** (`McpResourceSurface`, scheme `crawlforge://`):
baseline 7 — `audits/{id}/summary`, `audits/{id}/high-severity-findings`,
`audits/{id}/urls/{urlId}`, `audits`, `schedules`, `audits/{id}/progress`,
`audits/{id}/urls/{urlId}/content`; additive 8 — `audits/{id}/ngrams`,
`audits/{id}/waf-blocks`, `audits/{id}/host-throttle`,
`audits/{id}/redirect-matrix`, `audits/{id}/log-correlation?import={id}`,
`audits/{id}/semantic-links`, `audits/{id}/geo-readiness?agent={name}`,
`audits/{id}/analytics-coverage?vendor={name}`. Additive parent fields are
carried on summary (WAF counts), url-detail (WAF/scope/top n-grams/SERP
metrics/header conflicts/semantic relations), and progress (throttle summary,
redirect-matrix counters). All reads are bounded (default page 200).

**5 prompts** (`McpPromptSurface`, side-effect free): `audit-health-briefing`,
`high-severity-triage`, `audit-delta-briefing`, `ngram-topical-briefing`,
`semantic-topical-briefing`. Each materializes one `user` message composed
from read-only resources / the comparison reader.

## Phase-06 handoff: `IAuditLifecyclePort`

Contracts contain no audit-scoped lifecycle facade, so this project defines a
**local composition port** (`AuditLifecyclePorts.cs`). The Host (phase 06)
must bind it to the real engine services:

```csharp
public interface IAuditLifecyclePort
{
    Task<AuditLifecycleSnapshot> PauseAsync(long auditId, CancellationToken ct);
    Task<AuditLifecycleSnapshot> ResumeAsync(long auditId, CancellationToken ct);
    Task<AuditLifecycleSnapshot> CancelAsync(long auditId, CancellationToken ct);
    Task<long> ReAuditAsync(long auditId, bool refresh, CancellationToken ct);
    Task<AuditProgressSnapshot> GetProgressAsync(long auditId, CancellationToken ct);
}

public sealed record AuditLifecycleSnapshot(long AuditId, AuditStatus Status, AuditPhase Phase);

public sealed record AuditProgressSnapshot(
    long AuditId, AuditStatus Status, AuditPhase Phase,
    long Discovered, long Fetched, long Analyzed, long Queued, double? PercentComplete);
```

Semantics expected of the Host adapter: pause/resume/cancel act on the running
engine and return the resulting state; `ReAuditAsync` clones the audit's
configuration snapshot into a new queued audit (`refresh` re-resolves dynamic
inputs) and returns the new audit id; progress reads live engine counters.
Tests use `FakeAuditLifecyclePort` (see `CrawlForge.Mcp.Tests/TestDoubles.cs`).

## Composition guide (Host)

```csharp
var tokens = new McpTokenService(secretProtector, new FileMcpTokenStore(tokenFilePath));
await tokens.InitializeAsync(ct);                    // generates on first run
var actionLog = new AutomationActionLog(readRepo, writeRepo); // AutomationActionLogDto
var tools = new McpToolSurface(executionService, lifecyclePort, scheduledAuditService, singlePageAnalyzer, auditRepo);
var resources = new McpResourceSurface(new McpResourcePorts(/* 24 read ports — see record */));
var prompts = new McpPromptSurface(resources, comparisonReader);
var dispatcher = new McpJsonRpcDispatcher(tools, resources, prompts, actionLog);
using var server = new McpLoopbackServer(dispatcher, tokens); // loopback-only
server.Start();                                       // server.MessageEndpoint for the adapter
```

`McpResourcePorts` consumes accepted read models directly: `NGramReadModel`,
`GeoReadinessAnalyzer`, `LogCorrelationEngine`, `AuditComparisonReader`, plus
`IReadRepository<T>` for the persisted read models and `IContentStore` for
extracted content. No schema or contract changes are required.

## Tokens and rotation (criterion #16)

- 256-bit random tokens (64 hex chars), validated with fixed-time comparison.
- Encrypted at rest through `ISecretProtector` (`cfsec1:` AES-GCM scheme);
  plaintext is never persisted. `IMcpTokenStore` is the storage abstraction —
  `FileMcpTokenStore` ships here; the Host may bind a DB-backed store later
  without contract changes.
- `RotateAsync` issues a new token and immediately invalidates every prior
  client on both transports (proven over the wire by the interop suite).

## Automation action log (criterion #23)

Every JSON-RPC request (initialize, ping, lists, calls, reads, gets) is logged
to `automation_action_log` with transport, target method, bounded arguments
summary (512 chars; tools/call includes the tool name), and outcome. Error
envelopes log as `Failed`. Notifications are not invocations and are not
logged. The log is user-viewable (`ListAsync`) and purgeable (`PurgeAsync`).

## Verification

- `dotnet test E:\Crawlforge\app\tests\CrawlForge.Mcp.Tests` — 86 tests,
  including the real-HTTP loopback suite and the TS SDK interop gate.
- `npm run typecheck --workspace src/mcp-stdio-adapter` (from `E:\Crawlforge\app`).
- Governance: `dotnet run --project tools/governance-analyzer -- --root src/backend/CrawlForge.Mcp --fail-on-violation` — clean.
