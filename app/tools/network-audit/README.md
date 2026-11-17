# WP-07-02 Network Boundary & Air-Gap Audit — Evidence Index

Builder: cf-wp-00-05 · Phase-07 wave-7A · Verification only (no product code edited).

## How the evidence was produced

- **Runtime harness**: `app/tests/CrawlForge.Boundary.Tests` — the REAL composition root
  (`CrawlForgeBackend.StartAsync`) runs in-process against fresh temp data directories;
  every product surface is exercised through its real API/service path.
- **Outbound monitoring**: process-level TCP-table polling (`iphlpapi GetExtendedTcpTable`,
  no elevation) with per-connection ownership, loopback classification, phase tags, and
  baseline attribution of pre-existing test-host plumbing. Both sides of in-process
  loopback connections are attributed correctly.
- **Identity monitoring**: process-lifetime assembly-load monitor started at module
  initialization, with load-stack capture and immediate-caller attribution.
- **Static layer**: governance analyzer (WP-00-05, WP-07-G1 amendment) full-tree scans.
- **Behavioral frontend layer**: vitest plugin suites (113 tests) run from `src/frontend`.

## Evidence artifacts (this directory)

| File | Criterion | Result |
|---|---|---|
| first-launch.md / first-launch-connections.csv | #1 first launch offline | PASS — init completes, hub ready, 0 non-loopback |
| idle-reporting.md / idle-reporting-connections.csv | #3 idle/reporting | PASS — 0 non-loopback (5s idle + 9 read routes) |
| audit-traffic.md / audit-traffic-connections.csv | #3 audit | PASS — real audit; loopback only; known product ports only |
| destination-transfer.md / destination-transfer-connections.csv | #14 / §2.6 | PASS — delivery only to user endpoint with user bearer |
| external-db-addressing.md | #3 / §2.6 | PASS — connection test only to configured server |
| identity-runtime-proof.md | #2 runtime | PASS — WMI/directory/automation never loaded; Registry load attributed to System.Net.Quic TLS-1.3 check (framework) |
| identity-static-proof.md | #2 static | PASS — NR-01 full tree, 0 violations |
| listener-sweep.md | #22 | PASS — Kestrel+hub/SSE+MCP+proxy all loopback |
| non-loopback-unreachability.md | #22/#16 | PASS — backend unreachable via non-loopback interfaces |
| mcp-boundary.md | #16 | PASS — 401 anon/wrong; 200 valid (HTTP+stdio); SSE gated; rotation immediate; non-loopback bind rejected |
| encryption-at-rest.md | #17 | PASS — five classes via real surfaces; full data-dir scan 0 plaintext leaks |
| secret-protector-properties.md | #17 | PASS — cfsec1 AES-GCM; tamper/cross-key rejection; independent RNG keys |
| handshake-lifecycle.md | #17 | PASS + orchestrator §9.1 ruling (runtime handshake ≠ durable store; strict-reading nuance + crash-residual note recorded) |
| minidump-locality.md | #21 | PASS — local capture, listing, purge; 0 outbound |
| metrics-boundary.md | #20 | PASS — no identity dimensions; pull-only; logs identity-free |
| no-entitlement.md | #5 | PASS — 14 API surfaces + MCP catalogs + settings DB, 0 concept hits |
| plugin-network-vitest.md | #39 | PASS — 9 suites / 113 tests (grant guard, sandbox CSP, bridge, registry) |
| dns-consent.md | E8 | PASS — tool-level consent refusal before network + REST gate (200, started=false) after DI fix |
| embedding-loopback.md | E7 | PASS — non-loopback rejected; localhost DNS-pinned |
| bundled-assets.md | §13 E2/E4/E8 | PASS — 10/10 catalogs local; no fetch path; packaging/ absent (packaging workpiece pending) |

Machine-readable governance scans: `app/tests/governance/wp-07-02-baseline-scan.json`
(before: 1006 files / 289 violations — all lockfile metadata + verification code) and
`wp-07-02-app-root-scan.json` (after: 636 files / 0 violations, final verification run).

## Blocker — RESOLVED

**Logs REST surface 500** — `LogsController` requires `ILogCorrelationReadModel`;
`EngineServicesModule.cs:265` registered only `ILogCorrelationEngine` although
`LogCorrelationEngine` implements both. Reported by WP-07-02, routed by the orchestrator,
and FIXED in cf-wp-00-04's composition batch (concrete singleton forwarded to BOTH
interfaces). The strict tracking test
`LogsRestSurfaceTests.LogsRest_VerifyDns_WithoutConsent_RefusesWithoutSending`
flipped green on the fix: /api/logs/{id}/verify-dns now returns 200 with started=false
and the consent message when consent is absent. Final verification: Boundary.Tests
27/27 exit 0; governance app-root 636 files / 0 violations exit 0.

## Known monitoring limitations (documented, not gaps in the product)

- DNS is UDP: the TCP monitor cannot observe queries; E8 is proven by the pre-network
  consent refusal + code path + default CidrOnly mode.
- ETW kernel-registry tracing requires elevation (unavailable); the identity proof is the
  lead-approved layered harness proof (assembly attribution + static NR-01).
- The embedding daemon is user-configured and not running by default; its loopback
  enforcement is proven by the validator + API rejection (E7), and any configured daemon
  endpoint is re-validated before every request.
