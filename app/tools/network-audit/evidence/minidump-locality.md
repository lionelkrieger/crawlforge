# Criterion #21 — minidump locality, listing, purge

- Captured artifact: minidump-20261120-053241-436-q40ep3k1.k2c.dmp (4144738 bytes)
- Locality: under the local data directory (C:\Users\lione\AppData\Local\Temp\cf-boundary-1a9cf7ddcab84b7bb519e9bd71cb2c03)
- REST listing: GET /api/diagnostics/artifacts?kind=minidump → present
- Non-loopback connections during capture/list/purge: **0** (required: 0 — never transmitted)
- Purge: DELETE /api/diagnostics/artifacts?kind=minidump → purgedCount ≥ 1; file removed; listing empty