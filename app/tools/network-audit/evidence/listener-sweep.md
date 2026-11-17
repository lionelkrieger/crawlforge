# Criterion #22 — loopback-only listener sweep

Active surfaces: Kestrel REST + SignalR hub/SSE (port 54462),
MCP loopback server, intercepting proxy (port 54464).

| Local address | Port | Loopback |
|---|---|---|
| 127.0.0.1 | 54462 | True |
| 127.0.0.1 | 54464 | True |

- Listeners observed: 2
- Non-loopback listeners: **0** (required: 0)
- Backend port present: True
- Proxy port present: True