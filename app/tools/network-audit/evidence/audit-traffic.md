# Criterion #3 — audit-time traffic classification

- Crawl target: http://127.0.0.1:58188/ (loopback), requests served: 7
- Known product ports: target=58188, backend=58189, mcp=58190
- Audit 1: status=Completed, phase=Finalizing
- Observed connections during run: 44
- Non-loopback observations: **0** (required: 0)
- Connections to unknown ports: **0** (required: 0 — no tunneling of local services)

Raw observations: audit-traffic-connections.csv