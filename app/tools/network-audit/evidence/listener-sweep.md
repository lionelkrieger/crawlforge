# Criterion #22 — loopback-only listener sweep

Active surfaces: Kestrel REST + SignalR hub/SSE (port 58112),
MCP loopback server, intercepting proxy (port 58116).

| Local address | Port | Loopback |
|---|---|---|
| 127.0.0.1 | 58112 | True |
| 127.0.0.1 | 58116 | True |

- Listeners observed: 2
- Non-loopback listeners: **0** (required: 0)
- Backend port present: True
- Proxy port present: True