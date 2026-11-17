# Criterion #16 — MCP boundary

- Endpoint: http://127.0.0.1:54526/mcp (loopback)
- Anonymous POST: 401; wrong token: 401; valid token: 200 (HTTP + stdio transports)
- SSE with token: 200 text/event-stream; anonymous SSE: 401
- Rotation: old token 401 immediately; new token 200
- Token at rest: protected (cfsec1: scheme), plaintext absent from mcp-token.json