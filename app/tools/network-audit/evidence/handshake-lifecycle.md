# Criterion #17 — handshake descriptor lifecycle

- mcp-endpoint.json + loopback-handshake.json: per-launch runtime bootstrap
  artifacts (local shell / stdio adapter discovery), present only while running.
- Both RETIRED (deleted) at graceful shutdown — no durable plaintext token at rest.
- Durable token store mcp-token.json survives shutdown, cfsec1:-encrypted.

## Orchestrator §9.1 ruling (WP-07-02, accepted)

Runtime handshake ≠ durable at-rest store. NOT a product-design blocker.
§9.1 (network-boundary-and-security.md:61) mandates encryption for the DURABLE
secret store; process-model-and-lifecycle.md:45 explicitly mandates "a port
descriptor written to the per-user application data directory, or an equivalent
spawn-time handshake" — the bootstrap descriptors are that spec-mandated
transient discovery mechanism.

STRICT-READING NUANCE (recorded for sign-off): "any local token" is broad
wording; a strict auditor could argue it covers the handshake. The spec's own
escape valve is "or an equivalent spawn-time handshake" — a future hardening
could pass the session token via spawn-time IPC (never on disk). Recorded as a
product-design option; WP-07-04 attestation carries the classification rationale.

CRASH RESIDUAL (defense-in-depth note): a crash may leave a stale descriptor;
its per-launch token is invalid after restart (new token) → dead artifact,
harmless. Optional hardening: startup purge of prior-launch descriptors.