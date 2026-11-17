# CrawlForge Packaging Verification (WP-07-04)

Verification-only workpiece artifacts. No product code is modified from this
directory; the scripts publish and probe the REAL composition root.

## Contents

| Path | Purpose |
|---|---|
| `verify-packaging.ps1` | Release publish + asset bundling + licensing absence + update-feed absence + offline first launch from the published layout + local Electron resolution. Emits `artifacts/packaging-report.json`. |
| `electron/resolve-electron-binary.ps1` | Local-only Electron dist resolution (override env → vendor drop-in → system PATH → not-bundled). Never downloads. |
| `electron/README.md` | The ratified local-binary approach + feed-policy record. |
| `vendor/electron/` | User-provided local Electron dist drop-in point (optional; absent by default — see electron/README.md). |
| `artifacts/publish/` | Release publish output with the bundled `assets/` tree (generated). |
| `artifacts/packaging-report.json` | Machine-readable check results (generated; consumed by `CrawlForge.Acceptance.Tests`). |

## What is verified

1. **Release publish** of `CrawlForge.Host` (the full module composition) —
   exit 0, executable present.
2. **Every required asset family bundled** next to the published executable
   (FS-CRAWL-001 §13 asset manifest; `AssetLocator` resolves `assets/` by
   walking up from the executing directory — never the network):
   WAF signatures, stop-words, dictionaries, font metrics, technology
   fingerprints, bot CIDR, AI agent profiles, ONNX model, export templates,
   plugin schema.
3. **Chromium rendering runtime** resolves from LOCAL sources only
   (`LocalBrowserResolver`: packaged → configured → local Chrome/Edge).
4. **Licensing manifest ABSENT** — no license/entitlement/activation manifest
   files or licensing-shaped product assemblies in the packaged build
   (criterion #5; the boundary audit's no-entitlement proof covers the live
   surfaces).
5. **No account-bound update feed** — no update/account endpoint in any
   packaged configuration (open scope #2 remains product-owner reserved;
   no update checking of any kind exists in this version).
6. **First-launch offline initialization from the PUBLISHED layout** — the
   published executable runs against a fresh temporary data directory:
   migrations apply, deterministic settings defaults exist, the hub reports
   ready (`/api/health` 200, `/api/settings` 200 with the per-launch session
   token from the local `loopback-handshake.json`). Zero external endpoints
   are involved; the boundary audit (WP-07-02) proves zero non-loopback
   connections at first launch with process-level TCP monitoring.
7. **Electron shell binary** configured local-only with no runtime fetch
   (see `electron/README.md`).

## Running

```powershell
pwsh -NoProfile -File E:\Crawlforge\app\packaging\verify-packaging.ps1
```

The acceptance harness (`tests/CrawlForge.Acceptance.Tests`) runs the script
once per test session via its shared fixture and re-verifies the critical
invariants independently against the published layout.

## Feed policy

nuget.org / npm registry only. NU1902/NU1903 and npm high-severity
vulnerability warnings are never suppressed (bump, not silence). The Electron
dist zip binary source is a product-owner-reserved packaging decision
(recorded in `docs/acceptance/`), never a runtime fetch.
