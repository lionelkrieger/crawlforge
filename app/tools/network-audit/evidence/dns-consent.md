# Exclusion E8 — DNS verification consent gate

- Default bot verification mode: CidrOnly (bundled CIDR + agent profiles, zero outbound).
- LogDnsVerificationTool.VerifySampleAsync(importId, consent:false) throws
  InvalidOperationException ("DNS verification requires explicit per-import user
  consent") BEFORE any verifier invocation — proven at runtime via DI.
- LogsController.VerifyDns additionally returns started=false without invoking
  the tool when consent is absent (source-verified).
- DNS verification is the ONLY optional network action of the log analyzer;
  it is user-triggered, per-import, off by default, and locally logged.
- Monitoring note: DNS is UDP; the harness TCP monitor cannot observe queries.
  The gate proof is the pre-network refusal + code path.

## API-surface defect — RESOLVED

Previously all /api/logs/* routes returned 500: LogsController requires
ILogCorrelationReadModel, which lacked a DI forwarding registration
(EngineServicesModule registered only ILogCorrelationEngine, although
LogCorrelationEngine implements both interfaces). Reported as a WP-07-02
blocker; fixed in cf-wp-00-04's composition batch (concrete singleton
forwarded to BOTH interfaces). The REST consent gate is now verified by
LogsRestSurfaceTests (200 + started=false without consent).