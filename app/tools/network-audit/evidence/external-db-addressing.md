# Criterion #3 / §2.6 — external-database addressing

- Configured user server (loopback stand-in): 127.0.0.1:54437
- TCP connection accepted by the stand-in: True
- Connection-test verdict: reachable/authenticated reported honestly (authenticated=false)
- DB-phase connections observed: 0
- Connections to any endpoint other than the configured server: **0** (required: 0)
- Non-loopback connections: **0** (required: 0)

The external database is contacted ONLY at the user-configured address;
no vendor or third-party database endpoint exists in the product.