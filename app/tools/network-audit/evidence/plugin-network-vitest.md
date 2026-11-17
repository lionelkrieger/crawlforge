# Criterion #39 — plugin network boundary: behavioral proof (vitest)

Run: `npx vitest run ../../tests/frontend/plugins` from `app/src/frontend` (vitest 5.1.0, node v26.5.1).
Result: **9 test files, 113 tests, ALL PASSED, exit 0** (2026-11-14).

| Suite | Tests | Coverage |
|---|---|---|
| networkGrantGuard.test.ts | 10 | deny-by-default; ungranted/empty-ref denial; exact host+scheme+port destination matching; non-http(s) denial; case-insensitive hosts; CSP origins empty without grant |
| sandboxFrame.test.ts | 8 | sandbox="allow-scripts" only (opaque origin); CSP default-src 'none'; connect-src 'none' without grant; granted destination origins only in connect-src |
| sandboxBridge.test.ts | (host dispatcher) | network capability gated by isNetworkAllowed under NO_NETWORK_GRANT default and granted states |
| pluginRegistry.test.ts | (grant lifecycle) | default NO_NETWORK_GRANT; grant/revoke transitions; mode resets to 'none' |
| pluginManager.test.ts | 11 | sandboxed loading wiring with grantedConnectOrigins |
| pluginManagement.test.tsx | 11 | management surface grant state UI |
| manifestValidator.test.ts | 19 | local manifest validation (no remote channel) |
| schemaAsset.test.ts | 3 | bundled plugin schema asset |

Companion static invariants pinned by CrawlForge.Boundary.Tests Plugins.PluginNetworkBoundaryTests
(sandbox attribute, CSP construction, NO_NETWORK_GRANT default, bridge enforcement wiring).

Boundary semantics proven: zero plugin network traffic without an explicit user grant;
with a grant, requests reach ONLY user-configured destination rules — never arbitrary
hosts, never vendor services (§2.6, E5/E6).
