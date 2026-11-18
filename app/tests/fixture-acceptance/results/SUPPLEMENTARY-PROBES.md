# Supplementary defect probes — 2026-11-18 ~02:12 (Africa/Johannesburg)

Agent-run probes OUTSIDE the acceptance runs (own cold-launched instance,
launch-debug.ps1 -CdpPort 9223, backend port 54746, MCP endpoint 54751;
instance closed via CDP Browser.close afterwards). Purpose: complete the
configuration-snapshot defect characterization on the CURRENT build.

## 1. re-audit of REST/UI-started audits — deterministic $.id failure

`node node/mcp_client.mjs reaudit 23` (audit 23 = UI-started http bulk, final run):

```json
{"cmd":"reaudit","endpoint":"http://127.0.0.1:54751/mcp","steps":[{"name":"re-audit","v":{"content":[{"type":"text","text":"The JSON value could not be converted to CrawlForge.Contracts.Entities.CrawlConfigurationDto. Path: $.id | LineNumber: 0 | BytePositionInLine: 10."}],"isError":true}}]}
```

`node node/mcp_client.mjs reaudit 24` (audit 24 = REST-started js bulk): identical
`$.id` error, `isError: true`.

## 2. re-audit of an MCP-started audit — SUCCEEDS

In the final acceptance run, `mcp-reaudit.json` recorded re-audit of audit 25
(MCP-started) returning `{"auditId":26,"sourceAuditId":25}` with `isError:false`;
GET /api/audits/26 afterwards: `{"id":"26","state":"completed","phase":"finalizing","errorMessage":null}`.

## Explanation (read-only source inspection)

`McpToolSurface.StartAuditAsync` (app/src/backend/CrawlForge.Mcp/McpToolSurface.cs:127)
calls `_execution.StartAuditAsync` DIRECTLY, bypassing
`AuditsController.StartAsync`'s WIRE-snapshot overwrite
(AuditsController.cs:146-160). MCP-started audits therefore keep the ENTITY
snapshot (numeric id) and re-audit deserializes cleanly; REST/UI-started audits
carry the WIRE snapshot (string id) and re-audit fails deterministically
(AuditExecutionEngine.cs:436-438, PayloadJson). The probabilistic START failure
(audit 18, results/20261117T235434Z) is the same overwrite racing the engine's
first snapshot read (AuditExecutionEngine.cs:497).

probe-2 §6 measured "re-audit returns an error" — consistent: its re-audit probe
ran against REST-started audits. The MCP-source success is a refinement of the
defect characterization, not a contradiction.

Routing (lead): WP-09-06 (CrawlForge.Api controllers) + WP-09-11 (Host execution).
No product code was changed. Audit 26 lives in the harness-owned
"Fixture harness MCP 20261118T000959Z" project (analysis-only re-audit pass).