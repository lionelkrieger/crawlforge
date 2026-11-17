# Criterion #21 — minidump locality, listing, purge

- Captured artifact: minidump-20261114-200921-169-lixtohi4.ysz.dmp (4480757 bytes)
- Locality: under the local data directory (C:\Users\lione\AppData\Local\Temp\cf-boundary-3a40b9475ed8419fb366e8f2f07c5596)
- REST listing: GET /api/diagnostics/artifacts?kind=minidump → present
- Non-loopback connections during capture/list/purge: **0** (required: 0 — never transmitted)
- Purge: DELETE /api/diagnostics/artifacts?kind=minidump → purgedCount ≥ 1; file removed; listing empty