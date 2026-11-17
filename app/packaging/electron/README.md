# Electron Shell Binary — Local-Only Resolution (WP-07-04)

## Problem

The npm `electron` wrapper package downloads its platform binary in a
postinstall script (`install.js` → `@electron/get` → GitHub releases). In this
build environment the postinstall is blocked by the `allowScripts=false`
policy, so `node_modules/electron/dist` is absent and `path.txt` is empty.
A runtime download would also violate the product's offline-first invariant
(FS-CRAWL-001 §3: no runtime fetch of any runtime component).

## Resolution (product-owner ratified 2026-11-16)

**No runtime fetch. Local binary configuration only.**

1. **Rendering runtime (product requirement).** The backend's headless
   rendering pool resolves its Chromium-class browser through
   `LocalBrowserResolver` (WP-02-D1): packaged assets root → user-configured
   executable path → local Chrome/Edge installs. Runtime browser/driver
   download is prohibited by the resolver itself. On the reference machine
   both local sources exist:
   - `C:\Program Files\Google\Chrome\Application\chrome.exe`
   - `C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe`
   The acceptance harness proves live resolution (`ChromiumRenderingRuntime_ResolvesFromLocalSourcesOnly`)
   and that a missing configured path is a local configuration error, never a
   remote fallback. The scale benchmarks already captured CWV through the
   production `LabRenderingProbe` on local Chrome (benchmark-report.md).

2. **Electron desktop shell binary.** The wrapper natively honors
   `ELECTRON_OVERRIDE_DIST_PATH` (and `path.txt`). `resolve-electron-binary.ps1`
   configures a LOCAL dist through those channels, in order:
   - `override-env` — `ELECTRON_OVERRIDE_DIST_PATH` points at a local dist;
   - `bundled-vendor` — a user-provided dist dropped at
     `packaging/vendor/electron/` (containing `electron.exe`); the packaged
     launcher sets `ELECTRON_OVERRIDE_DIST_PATH` to it;
   - `system-path` — a local Electron installation on PATH;
   - `not-bundled` — no local dist present. The shell's unit suites
     (`src/electron/tests`, `tests/electron`) run on injected fakes and need
     no binary. Desktop launch requires a user-provided local dist via one of
     the channels above. **No download is attempted or permitted.**

3. **Feed policy.** Package feeds are nuget.org / npm registry only. The
   Electron dist zip is **not hosted on the npm registry** (the wrapper
   fetches GitHub releases), so bundling a dist into an installer is a
   **product-owner-reserved packaging decision** (binary distribution source).
   It is recorded as such in the final sign-off report — a distribution
   concern, not an acceptance-criterion failure: no criterion requires
   launching the Electron binary itself (criterion #1's main hub is the
   backend hub route, proven offline; the UI criteria are proven by the
   vitest suites and real-Chrome benchmarks).

## Verification

`packaging/verify-packaging.ps1` invokes the resolver and records the mode in
`packaging/artifacts/packaging-report.json`; the acceptance fact
`ElectronShellBinary_IsConfiguredLocalOnly_WithNoRuntimeFetch` asserts the
no-fetch policy and the documented vendor drop-in mechanism.
